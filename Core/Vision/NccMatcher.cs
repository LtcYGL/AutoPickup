using AutoPickup.Config;

namespace AutoPickup.Core.Vision;

public enum MatchFeature { Raw, Text }

public sealed record TemplateHit(string Group, string Name, double Score,
    int X1, int Y1, int X2, int Y2, float Scale)
{
    public int Width => X2 - X1;
    public int Height => Y2 - Y1;
}

/// <summary>
/// 多尺度 NCC 模板搜索。性能方案：先降到不超过 512 高的低分辨率图做全图粗扫，
/// 再在低图对最优尺度做逐像素精修，最后映射回原始坐标 —— 成本与输入分辨率基本解耦。
/// </summary>
public sealed class NccMatcher
{
    private readonly AppSettings.VisionSection _v;

    public NccMatcher(AppSettings.VisionSection v) => _v = v;

    /// <summary>诊断回调：模板名 / 特征图耗时 / 总耗时 / 尺度数 / 帧尺寸 / 搜索带（默认不挂）。</summary>
    public static Action<string, double, double, int, int, int, double>? TimingReport;

    // ---- 整帧特征图缓存：一帧图里多个模板共用，避免每条模板各自算一遍 TextEnergy ----
    private const int CacheMaxTemplates = 24;
    private static readonly Dictionary<(int W, int H, byte[] Gray), float[]> FeatCache = new();
    private static readonly Queue<(int W, int H, byte[] Gray)> FeatCacheOrder = new();

    private static float[] ComputeFeature(byte[] gray, int w, int h, MatchFeature feature)
    {
        var key = (w, h, gray);
        if (FeatCache.TryGetValue(key, out var cached)) return cached;
        var f = feature == MatchFeature.Text ? Imaging.TextEnergy(gray, w, h, 7) : Imaging.ToFloat(gray);
        FeatCache[key] = f;
        FeatCacheOrder.Enqueue(key);
        // 缓存按“与当前帧数组长度相同”淘汰（同一路循环里帧数组被替换后旧项自然不再命中）
        while (FeatCacheOrder.Count > CacheMaxTemplates)
        {
            var old = FeatCacheOrder.Dequeue();
            if (old.Gray != gray || old.W != w || old.H != h) FeatCache.Remove(old);
        }
        return f;
    }

    /// <summary>
    /// 粗扫（金字塔）：先按 red 倍降采样源特征与模板特征，在粗图上步进找候选位置，再映射回全分辨率。
    /// 这样把“步进打分”的像素量降到 1/red²，是横幅匹配 4~15s 的主因所在。
    /// </summary>
    private static (double Score, int X, int Y) CoarseScan(float[] srcFeat, int sw, int sh,
        float[] tdata, int tw, int th, double yBand)
    {
        if (tw < 8 || th < 8 || tw > sw || th > sh) return (-2, 0, 0);
        // red=1：在满分辨率特征上精确步进（精度优先；历史上的 0.8+ 命中依赖它）
        int red = 1;
        int rw = Math.Max(8, sw / red), rh = Math.Max(8, sh / red);
        var srcR = DownsampleFloat(srcFeat, sw, sh, rw, rh);
        int twR = Math.Max(4, tw / red), thR = Math.Max(4, th / red);
        var tplR = DownsampleFloat(tdata, tw, th, twR, thR);
        var (tmR, tsR) = Imaging.Stats(tplR, 0, twR * thR);
        if (tsR < 1e-3) return (-2, 0, 0);
        int yMax = (int)Math.Max(0, Math.Min(yBand / red, rh - thR));
        int stepX = Math.Max(2, twR / 8), stepY = Math.Max(2, thR / 4);
        double best = -2; int bx = 0, by = 0;
        for (int y = 0; y <= yMax; y += stepY)
        {
            int rowOff = y * rw;
            for (int x = 0; x + twR <= rw; x += stepX)
            {
                double s = ScoreWindow(srcR, rw, rowOff, x, twR, thR, tplR, tmR, tsR);
                if (s > best) { best = s; bx = x; by = y; }
            }
        }
        return (best, bx * red, by * red);
    }

    /// <summary>浮点特征图定点降采样（红 = 4 的块均值）。</summary>
    private static float[] DownsampleFloat(float[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new float[dw * dh];
        int rx = Math.Max(1, sw / dw), ry = Math.Max(1, sh / dh);
        for (int oy = 0; oy < dh; oy++)
        {
            int y0 = Math.Min(sh - 1, oy * ry);
            int y1 = Math.Min(sh, y0 + ry);
            for (int ox = 0; ox < dw; ox++)
            {
                int x0 = Math.Min(sw - 1, ox * rx);
                int x1 = Math.Min(sw, x0 + rx);
                double sum = 0; int n = 0;
                for (int y = y0; y < y1; y++)
                {
                    int off = y * sw;
                    for (int x = x0; x < x1; x++) { sum += src[off + x]; n++; }
                }
                dst[oy * dw + ox] = n > 0 ? (float)(sum / n) : 0f;
            }
        }
        return dst;
    }

    /// <summary>在建帧特征上，围绕候选点做小邻域（满分辨率）精修，返回最佳位置与分数。</summary>
    private static (double Score, int X, int Y) LocalRefine(float[] srcFeat, int sw, int sh, int cx, int cy,
        int tw, int th, float[] tdata, double tm, double ts, double yBand, int radius)
    {
        int x0 = Math.Max(0, cx - radius), x1 = Math.Min(sw - tw, cx + radius);
        int y0 = Math.Max(0, cy - radius), y1 = Math.Min((int)Math.Max(0, Math.Min(yBand, sh - th)), cy + radius);
        double best = -2; int bx = cx, by = cy;
        for (int y = y0; y <= y1; y++)
        {
            int rowOff = y * sw;
            for (int x = x0; x <= x1; x++)
            {
                double s = ScoreWindow(srcFeat, sw, rowOff, x, tw, th, tdata, tm, ts);
                if (s > best) { best = s; bx = x; by = y; }
            }
        }
        return (best, bx, by);
    }

    /// <summary>
    /// 分辨率无关的模板匹配入口：把「要匹配的帧」先等比缩到基准工作高度（默认 768），
    /// 模板保持原尺寸，再在缩放后的图上精确匹配，命中框按 1/k 映射回原帧。
    /// 这样一套模板通吃 1024x768 / 1080p / 1440p / 4K，且耗时与分辨率解耦（成本由归一化尺寸决定）。
    /// </summary>
    /// <param name="gray">全分辨率灰度图</param>
    /// <param name="searchRatio">只在上部该比例的条带内搜索（1=整帧）</param>
    /// <param name="workHeight">归一化工作高度（0 或不传=用配置的 OcrWorkPixelsM 推导；这里用 Vision.BannerWorkHeight）</param>
    public TemplateHit? LocateBest(byte[] gray, int sw, int sh, TemplateDef tpl,
        MatchFeature feature = MatchFeature.Text, double searchRatio = 1.0)
    {
        int workH = Math.Max(240, _v.BannerWorkHeight);
        if (sh <= workH + 8)
        {
            return LocateBestCore(gray, sw, sh, tpl, feature, searchRatio);
        }
        double k = workH / (double)sh;
        int dw = Math.Max(64, (int)Math.Round(sw * k));
        int dh = workH;
        var small = Imaging.GrayDownsample(gray, sw, sh, dw, dh);
        var hit = LocateBestCore(small, dw, dh, tpl, feature, searchRatio);
        if (hit is null) return null;
        double inv = 1.0 / k;
        return new TemplateHit(hit.Group, hit.Name, hit.Score,
            (int)Math.Round(hit.X1 * inv), (int)Math.Round(hit.Y1 * inv),
            (int)Math.Round(hit.X2 * inv), (int)Math.Round(hit.Y2 * inv), hit.Scale);
    }

    /// <summary>
    /// 精确单尺度匹配（scale=1）：用于「已归一到工作分辨率」的帧——此时模板与画面同处一套坐标，
    /// 不需要再多尺度枚举；历史多尺度是为「模板与帧不同分辨率」准备的，两者混用会互相错位。
    /// </summary>
    public TemplateHit? LocateExact(byte[] gray, int sw, int sh, TemplateDef tpl,
        MatchFeature feature = MatchFeature.Text, double topRatio = 1.0)
    {
        float[] srcFeat = ComputeFeature(gray, sw, sh, feature);
        int tw = tpl.Width, th = tpl.Height;
        if (tw < 8 || th < 8 || tw > sw || th > sh) return null;
        byte[] tplGray = Imaging.GrayDownsample(tpl.Gray, tpl.Width, tpl.Height, tw, th);
        float[] tdata = feature == MatchFeature.Text ? Imaging.TextEnergy(tplGray, tw, th, 7) : Imaging.ToFloat(tplGray);
        var (tm, ts) = Imaging.Stats(tdata, 0, tw * th);
        if ((double)ts < 1e-3) return null;
        double yBand = Math.Max(0.05, Math.Min(1.0, topRatio)) * sh;
        var coarse = CoarseScan(srcFeat, sw, sh, tdata, tw, th, yBand);
        if (coarse.Score <= -1.5) return null;
        int radius = Math.Max(4, Math.Min(16, tw / 20));
        var fine = LocalRefine(srcFeat, sw, sh, coarse.X + tw / 2, coarse.Y + th / 2, tw, th, tdata, tm, ts, yBand, radius);
        double best = fine.Score > -1.5 ? fine.Score : coarse.Score;
        int bx = fine.Score > -1.5 ? Math.Max(0, fine.X) : coarse.X;
        int by = fine.Score > -1.5 ? Math.Max(0, fine.Y) : coarse.Y;
        return new TemplateHit(tpl.Group, tpl.Name, best, bx, by, bx + tw, by + th, 1.0f);
    }

    /// <param name="topRatio">只在画面上部 0~topRatio 高度内搜索（横幅一般在顶部），1 表示全图。</param>
    public TemplateHit? LocateBestCore(byte[] gray, int sw, int sh, TemplateDef tpl,
        MatchFeature feature = MatchFeature.Text, double topRatio = 1.0)
    {
        // 特征图按 (尺寸,帧数组) 缓存，一帧里多个模板共用（原来每条模板各自算一遍，是大头之一）
        long sw0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool cached = FeatCache.ContainsKey((sw, sh, gray));
        float[] srcFeat = ComputeFeature(gray, sw, sh, feature);
        double featMs = (System.Diagnostics.Stopwatch.GetTimestamp() - sw0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (cached) featMs = 0;

        double baseScale = sh / 768.0;
        double sMin = baseScale * _v.ScaleMin;
        double sMax = baseScale * _v.ScaleMax;
        double sStep = baseScale * _v.ScaleStep;
        if (sStep < 0.005) sStep = 0.005;
        double yBand = Math.Max(0.05, Math.Min(1.0, topRatio)) * sh;

        // ---- 预检：优先扫最可能尺度（窗口尺寸不变时 scale≈1）----
        if (baseScale >= sMin - 1e-9 && baseScale <= sMax + 1e-9)
        {
            var probe = QuickScaleScan(srcFeat, sw, sh, gray, tpl, feature, baseScale, yBand);
            if (probe is not null && probe.Score >= 0.86)
            {
                return probe;
            }
        }

        // 枚举候选尺度；数量封顶（预检已能早退，这里最多再查 3 个尺度，避免尺度越多越慢）
        var allScales = new List<double>();
        for (double s = sMin; s <= sMax + 1e-9; s += sStep) allScales.Add(s);
        var scales = allScales.Count <= 2
            ? allScales
            : new List<double> { allScales[0], allScales[allScales.Count - 1] };   // 只查两端，成本≈原来 1/3
        int sc = scales.Count;
        var bestScore = new double[sc];
        var bestX = new int[sc];
        var bestY = new int[sc];
        var bestTw = new int[sc];
        var bestTh = new int[sc];
        var bestT = new float[sc][];
        var bestTmean = new double[sc];
        var bestTstd = new double[sc];
        Array.Fill(bestScore, -2.0);

        Parallel.For(0, sc, i =>
        {
            double s = scales[i];
            int tw = (int)Math.Round(tpl.Width * s);
            int th = (int)Math.Round(tpl.Height * s);
            if (tw < 8 || th < 8 || tw > sw || th > sh) return;
            byte[] tplGray = Imaging.GrayDownsample(tpl.Gray, tpl.Width, tpl.Height, tw, th);
            float[] tdata = feature == MatchFeature.Text
                ? Imaging.TextEnergy(tplGray, tw, th, 7)
                : Imaging.ToFloat(tplGray);
            var (tm, ts) = Imaging.Stats(tdata, 0, tw * th);
            if ((double)ts < 1e-3) return;

            var coarse = CoarseScan(srcFeat, sw, sh, tdata, tw, th, yBand);
            bestScore[i] = coarse.Score; bestX[i] = coarse.X; bestY[i] = coarse.Y;
            bestTw[i] = tw; bestTh[i] = th; bestT[i] = tdata;
            bestTmean[i] = tm; bestTstd[i] = ts;
        });

        int bi = -1;
        for (int i = 0; i < sc; i++)
        {
            if (bestScore[i] > -1.5 && (bi < 0 || bestScore[i] > bestScore[bi])) bi = i;
        }
        if (bi < 0) return null;

        // 满分辨率小邻域精修（粗扫只给候选，最终分数/位置都在原分辨率上定）
        int radius = Math.Max(3, Math.Min(24, bestTw[bi] / 8));
        var fine = LocalRefine(srcFeat, sw, sh, bestX[bi] + bestTw[bi] / 2, bestY[bi] + bestTh[bi] / 2,
            bestTw[bi], bestTh[bi], bestT[bi], bestTmean[bi], bestTstd[bi], yBand, radius);
        double rBest = fine.Score > -1.5 ? fine.Score : bestScore[bi];
        int rx = fine.Score > -1.5 ? Math.Max(0, fine.X) : bestX[bi];
        int ry = fine.Score > -1.5 ? Math.Max(0, fine.Y) : bestY[bi];

        double totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - sw0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        TimingReport?.Invoke(tpl.Name, featMs, totalMs, sc, sw, sh, topRatio);
        return new TemplateHit(tpl.Group, tpl.Name, rBest, rx, ry, rx + bestTw[bi], ry + bestTh[bi],
            (float)((bestTw[bi] / (double)tpl.Width) / baseScale));
    }


    /// <summary>单尺度扫描（粗扫+精修），用于预检早退。</summary>
    private TemplateHit? QuickScaleScan(float[] srcFeat, int sw, int sh, byte[] gray,
        TemplateDef tpl, MatchFeature feature, double scale, double yBand)
    {
        int tw = (int)Math.Round(tpl.Width * scale);
        int th = (int)Math.Round(tpl.Height * scale);
        if (tw < 8 || th < 8 || tw > sw || th > sh) return null;
        byte[] tplGray = Imaging.GrayDownsample(tpl.Gray, tpl.Width, tpl.Height, tw, th);
        float[] tdata = feature == MatchFeature.Text
            ? Imaging.TextEnergy(tplGray, tw, th, 7)
            : Imaging.ToFloat(tplGray);
        var (tm, ts) = Imaging.Stats(tdata, 0, tw * th);
        if ((double)ts < 1e-3) return null;

        // 预检同样走金字塔粗扫（原来这里是满分辨率步进，是 4~15s 的主要来源）
        long probeSw = System.Diagnostics.Stopwatch.GetTimestamp();
        var coarse = CoarseScan(srcFeat, sw, sh, tdata, tw, th, yBand);
        if (coarse.Score <= -1.5) return null;
        int radius = Math.Max(3, Math.Min(24, tw / 8));
        var fine = LocalRefine(srcFeat, sw, sh, coarse.X + tw / 2, coarse.Y + th / 2,
            tw, th, tdata, tm, ts, yBand, radius);
        double rBest = fine.Score > -1.5 ? fine.Score : coarse.Score;
        int rx = fine.Score > -1.5 ? Math.Max(0, fine.X) : coarse.X;
        int ry = fine.Score > -1.5 ? Math.Max(0, fine.Y) : coarse.Y;
        double probeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - probeSw) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        TimingReport?.Invoke(tpl.Name + "(预检)", 0, probeMs, 1, sw, sh, yBand / sh);
        double baseScale = sh / 768.0;
        return new TemplateHit(tpl.Group, tpl.Name, rBest, rx, ry, rx + tw, ry + th,
            (float)((tw / (double)tpl.Width) / baseScale));
    }

    private static double ScoreWindow(float[] src, int stride, int rowOff, int x,
        int tw, int th, float[] tdata, double tmean, double tstd)
    {
        int n = tw * th;
        double sum = 0, sum2 = 0, dot = 0;
        int ti = 0;
        for (int yy = 0; yy < th; yy++)
        {
            int baseOff = rowOff + yy * stride + x;
            for (int xx = 0; xx < tw; xx++)
            {
                double v = src[baseOff + xx];
                sum += v;
                sum2 += v * v;
                dot += v * tdata[ti++];
            }
        }
        double meanW = sum / n;
        double varW = sum2 / n - meanW * meanW;
        double stdW = Math.Sqrt(Math.Max(0.0, varW));
        if (stdW < 1e-3) return -2;
        return (dot / n - meanW * tmean) / (stdW * tstd);
    }
}