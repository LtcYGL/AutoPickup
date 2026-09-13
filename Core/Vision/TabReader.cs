using AutoPickup.Config;
using AutoPickup.Core.Capture;
using AutoPickup.Core.Menus;
using AutoPickup.Core.Vision.Ocr;
using AutoPickup.Logging;

namespace AutoPickup.Core.Vision;

/// <summary>顶部标签条读取：选中 tab = 纯白底块（黑字），未选中 = 深底灰字。</summary>
public sealed record TabRead(string? Selected, int WhiteX0, int WhiteX1, string StripWords, int BandY0, int BandY1)
{
    public int WhiteCenter => (WhiteX0 + WhiteX1) / 2;
    public int BandYBottom => BandY1;
}

public sealed class TabReader
{
    private readonly IOcrEngine _ocr;
    private readonly LogBus _log;
    private readonly AppSettings _settings;
    private readonly int _refH = 768;

    public TabReader(IOcrEngine ocr, LogBus log, AppSettings settings)
    {
        _ocr = ocr;
        _log = log;
        _settings = settings;
    }

    private static readonly string[] StripTabNames =
        { "地图", "在线", "职业", "好友", "信息", "商店", "设置", "统计", "相册", "简讯", "游戏" };

    /// <summary>快速“暂停菜单开否”：只对 tab 行条带做 2x 词OCR（不走 Read 的整帧词OCR），
    /// 条带内命中词表 ≥2 判开。供手势前置等只需要“菜单开/关”的低成本场景，约 0.2~1s/帧。</summary>
    public bool IsTabStripPresent(Frame frame)
    {
        if (!frame.IsValid || !_ocr.Available) return false;
        try
        {
            double k = frame.Height / (double)_refH;
            // 百分比区域：tab 条上下边界按帧高比例
            int y0 = Math.Max(0, (int)Math.Round(frame.Height * _settings.Vision.TabStripTopPercent / 100.0));
            int y1 = Math.Min(frame.Height - 1, (int)Math.Round(frame.Height * _settings.Vision.TabStripBottomPercent / 100.0) + 4);
            if (y1 - y0 < (int)(12 * k)) return false;
            var words = OcrStrip(frame, y0, y1);
            if (words.Count < 2) return false;
            int hits = 0;
            foreach (var wd in words)
            {
                string c = TextMatcher.Clean(wd.Text);
                if (c.Length == 0) continue;
                foreach (var tn in StripTabNames)
                {
                    if (TextMatcher.FuzzyEqual(c, tn)) { hits++; break; }
                }
            }
            return hits >= 2;
        }
        catch
        {
            return false;
        }
    }

    public TabRead? Read(Frame frame)
    {
        if (!frame.IsValid || !_ocr.Available) return null;
        // 整帧词 OCR 也走工作像素预算（默认 ≤0.98M，只缩不放，保持长宽比），
        // 词框再按同一系数还原回原帧——高分辨率下这是 tab 识别最大的开销点。
        var v0 = _settings.Vision;
        var fullGray = Imaging.BgraToGray(frame.Bgra, frame.Width, frame.Height);
        var fullPrep = OcrPrep.ForFullFrame(fullGray, frame.Width, frame.Height, v0);
        var raw = _ocr.RecognizeWords(BgraFromGray(fullPrep.Gray), fullPrep.Width, fullPrep.Height);
        if (v0.OcrLogInput)
            _log.Info(string.Format("OCR[tab整帧] 输入 {0} 缩放 {1:F2} 词数 {2}",
                fullPrep.SizeNote, fullPrep.Resized ? fullPrep.Scale : 1.0, raw.Count), "Ocr");
        double fb = fullPrep.Resized ? fullPrep.Scale : 1.0;
        IReadOnlyList<OcrWord> words = new List<OcrWord>(raw.Count);
        var remapped = (List<OcrWord>)words;
        foreach (var wd in raw)
            remapped.Add(new OcrWord(wd.Text,
                (int)Math.Round(wd.X1 / fb), (int)Math.Round(wd.Y1 / fb),
                (int)Math.Round(wd.X2 / fb), (int)Math.Round(wd.Y2 / fb)));
        if (words.Count == 0) return null;

        double k = frame.Height / (double)_refH;
        // ---- 自适应：找“顶部、横跨最宽、单行小字”的簇 = tab 条 ----
        var candidates = words
            .Where(w => (w.Y2 - w.Y1) <= 36 * k && w.Y1 >= 8 * k && w.Y1 <= 0.42 * frame.Height)
            .OrderBy(w => w.Y1).ThenBy(w => w.X1)
            .ToList();
        var bands = new List<(int y0, int y1, int xMin, int xMax, int count, int yMid)>();
        foreach (var wd in candidates)
        {
            bool merged = false;
            for (int i = 0; i < bands.Count; i++)
            {
                var b = bands[i];
                if (wd.Y1 <= b.y1 + 14 * k && wd.Y2 >= b.y0 - 14 * k)
                {
                    bands[i] = (Math.Min(b.y0, wd.Y1), Math.Max(b.y1, wd.Y2),
                        Math.Min(b.xMin, wd.X1), Math.Max(b.xMax, wd.X2),
                        b.count + 1, (Math.Min(b.y0, wd.Y1) + Math.Max(b.y1, wd.Y2)) / 2);
                    merged = true;
                    break;
                }
            }
            if (!merged)
            {
                bands.Add((wd.Y1, wd.Y2, wd.X1, wd.X2, 1, (wd.Y1 + wd.Y2) / 2));
            }
        }
        var tabNames = new[]
        {
            "地图", "在线", "职业", "好友", "信息", "商店", "设置", "统计", "相册",
            "简讯", "游戏", "Rockstar编辑器", "GTA加",
        };
        // 候选簇：词数>=4 且横向铺开；评分=簇内词命中 tab 名称的数量，取最高者
        var bestBand = bands
            .Where(b => b.count >= 4 && (b.xMax - b.xMin) > 0.35 * frame.Width)
            .Select(b =>
            {
                int score = words.Count(w =>
                    w.Y2 >= b.y0 - 2 && w.Y1 <= b.y1 + 2 &&
                    tabNames.Any(tn => TextMatcher.FuzzyEqual(w.Text, tn)));
                return (b, score);
            })
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.b.xMax - x.b.xMin)
            .FirstOrDefault();
        var tab = bestBand.b;
        if (tab.count == 0 || bestBand.score < 3)
        {
            return null;
        }

        // 条带内词改用 2x 放大重读：整帧原生 OCR 会把选中 tab 的小字整字漏读
        // （地图→地/图都丢，导致读到相邻/白块误判），2x 后小字识别可靠很多。
        var bandWords = OcrStrip(frame, Math.Max(0, tab.y0 - 6), Math.Min(frame.Height - 1, tab.y1 + 6));
        if (bandWords.Count >= 4) words = bandWords;

        // tab 条内词（按 x 排序）
        int ty0 = tab.y0, ty1 = tab.y1;
        var inTab = words
            .Where(w => w.Y2 >= ty0 - 2 && w.Y1 <= ty1 + 2)
            .OrderBy(w => w.X1)
            .ToList();
        string strip = "";
        foreach (var wd in inTab) strip += (strip.Length > 0 ? " " : "") + wd.Text;

        // 选中白块：在 tab 行中段按列找白色 run
        int midRow = (ty0 + ty1) / 2;
        var gray = Imaging.BgraToGray(frame.Bgra, frame.Width, frame.Height);
        int th = (int)_settings.Vision.TabWhiteThreshold;
        int minW = Math.Max(8, (int)(_settings.Vision.TabMinWidthPx * k));
        int bestX0 = -1, bestLen = 0; double bestWhite = -1;
        int x = 0;
        while (x < frame.Width)
        {
            bool whiteCol = false;
            int cnt = 0;
            for (int yy = Math.Max(0, midRow - 4); yy <= Math.Min(frame.Height - 1, midRow + 4); yy++)
                if (gray[yy * frame.Width + x] >= th) cnt++;
            whiteCol = cnt >= 5;
            if (!whiteCol) { x++; continue; }
            int start = x;
            while (x < frame.Width)
            {
                int c2 = 0;
                for (int yy = Math.Max(0, midRow - 4); yy <= Math.Min(frame.Height - 1, midRow + 4); yy++)
                    if (gray[yy * frame.Width + x] >= th) c2++;
                if (c2 < 5) break;
                x++;
            }
            int len = x - start;
            if (len >= minW)
            {
                long sumG = 0;
                for (int xx = start; xx < x; xx++) sumG += gray[midRow * frame.Width + xx];
                double wv = sumG / (double)len;
                if (wv > bestWhite) { bestWhite = wv; bestX0 = start; bestLen = len; }
            }
        }

        // 选中 tab 识别 = 亮度优先 + “词表约束相邻字重组”兜底
        // （Windows OCR 常把中文切成单字：设+置→设置，在+线→在线）
        string? selected = null;
        double bestB = -1;
        foreach (var wd in inTab)
        {
            double b = EdgeBrightness(gray, frame.Width, frame.Height, wd);
            if (b > bestB) { bestB = b; selected = TextMatcher.Clean(wd.Text); }
        }
        if (bestB < 150) selected = null;

        // 词表重组：按 x 序在 inTab 中找“连续单字=已知 tab 名”，选最接近白块/最亮字的候选
        var known = new[] { "地图", "在线", "职业", "好友", "信息", "商店", "设置", "统计", "相册", "简讯", "游戏" };
        var tokens = inTab.OrderBy(w => w.X1).ToList();
        string? assembled = null;
        double bestScore2 = double.MaxValue;
        int cx = bestX0 >= 0 ? bestX0 + bestLen / 2 : -1;
        if (cx < 0 && selected is not null)
        {
            var sw2 = tokens.FirstOrDefault(x => TextMatcher.FuzzyEqual(TextMatcher.Clean(x.Text), selected!));
            if (sw2 is not null) cx = (sw2.X1 + sw2.X2) / 2;
        }
        foreach (var name in known)
        {
            int n = TextMatcher.Clean(name).Length;
            if (n < 2) continue;
            for (int i = 0; i + n <= tokens.Count; i++)
            {
                var seg = tokens.Skip(i).Take(n).ToList();
                string joined = TextMatcher.Clean(string.Join("", seg.Select(s => s.Text)));
                if (TextMatcher.FuzzyEqual(joined, name))
                {
                    int mid = (seg.First().X1 + seg.Last().X2) / 2;
                    double d = cx >= 0 ? Math.Abs(mid - cx) : 0;
                    double sc2 = d;
                    if (sc2 < bestScore2) { bestScore2 = sc2; assembled = name; }
                }
            }
        }
        if (assembled is not null && (bestB < 150 || bestScore2 <= 60 * k)) selected = assembled;

        return new TabRead(string.IsNullOrEmpty(selected) ? null : selected,
            bestX0, bestX0 + bestLen, strip, ty0, ty1);
    }

    private static double EdgeBrightness(byte[] gray, int w, int h, OcrWord wd)
    {
        long sum = 0; int n = 0;
        int x0 = Math.Max(0, wd.X1), x1 = Math.Min(w - 1, wd.X2);
        int yTop = Math.Min(h - 1, wd.Y1 + 2);
        int yBot = Math.Max(0, wd.Y2 - 2);
        for (int x = x0; x < x1; x++)
        {
            int yt = yTop, yb = yBot;
            if (wd.Y2 - wd.Y1 > 6)
            {
                yt = wd.Y1 + 1;
                yb = wd.Y2 - 1;
            }
            sum += gray[yt * w + x];
            sum += gray[yb * w + x];
            n += 2;
        }
        return n == 0 ? 0 : (double)sum / n;
    }

    private int CountWhiteCol(byte[] gray, int w, int h, int x, int ya, int yb, int th)
    {
        int n = 0;
        for (int y = Math.Max(0, ya); y <= Math.Min(h - 1, yb); y++)
            if (gray[y * w + x] >= th) n++;
        return n;
    }

    /// <summary>tab 条带识别：按“目标字高”等比缩放到工作像素后灰度直送 OCR，
    /// 词框坐标再按同一系数还原回原帧（EdgeBrightness 等按原帧取样）。</summary>
    private IReadOnlyList<OcrWord> OcrStrip(Frame frame, int y0, int y1)
    {
        int w = frame.Width;
        int yt = Math.Max(0, y0);
        int yb = Math.Min(frame.Height, Math.Max(yt + 4, y1));
        int h = yb - yt;
        if (w <= 0 || h <= 4) return Array.Empty<OcrWord>();
        var full = Imaging.BgraToGray(frame.Bgra, frame.Width, frame.Height);
        var crop = new byte[w * h];
        for (int y = 0; y < h; y++)
            Array.Copy(full, (yt + y) * frame.Width, crop, y * w, w);
        var v = _settings.Vision;
        // 站点固定倍率（tab 条实测原生 1x 最佳）
        var prep = OcrPrep.ForRegionSite(crop, w, h, "tab", v);
        var words = _ocr.RecognizeWords(BgraFromGray(prep.Gray), prep.Width, prep.Height);
        if (v.OcrLogInput)
            _log.Info(string.Format("OCR[tab条] 裁剪 {0}x{1} → {2} k={3:F2} 词数 {4}",
                w, h, prep.SizeNote, prep.Resized ? prep.Scale : 1.0, words.Count), "Ocr");
        double k = prep.Resized ? prep.Scale : 1.0;
        var res = new List<OcrWord>();
        foreach (var wd in words)
        {
            res.Add(new OcrWord(wd.Text,
                (int)Math.Round(wd.X1 / k), (int)Math.Round(wd.Y1 / k) + yt,
                (int)Math.Round(wd.X2 / k), (int)Math.Round(wd.Y2 / k) + yt));
        }
        return res;
    }

    private static byte[] BgraFromGray(byte[] gray)
    {
        var bgra = new byte[gray.Length * 4];
        for (int i = 0, j = 0; i < gray.Length; i++, j += 4)
        {
            byte v = gray[i];
            bgra[j] = v; bgra[j + 1] = v; bgra[j + 2] = v; bgra[j + 3] = 255;
        }
        return bgra;
    }
}