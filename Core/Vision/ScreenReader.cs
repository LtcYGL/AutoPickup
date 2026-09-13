using AutoPickup.Config;
using AutoPickup.Core.Capture;
using AutoPickup.Core.Vision.Ocr;
using AutoPickup.Logging;

namespace AutoPickup.Core.Vision;

public enum UiKind
{
    Unknown,
    TitleHome,      // 主菜单/开始页
    StoryTitle,     // 故事横幅（暂停菜单顶部）
    OnlineTitle,    // 在线横幅（暂停菜单顶部）
    Loading,        // 云加载/白圈/正在加入
    ExitDialog,     // 退出/切换确认弹窗
    DisconnectAlert,// 断连警报
    SaveFail,       // 保存失败提示
}

public sealed class ReadResult
{
    public UiKind Kind { get; set; } = UiKind.Unknown;
    public double Confidence { get; set; }
    public List<string> DebugLines { get; } = new();
    public string? OcrText { get; set; }
    public List<string> OcrHits { get; } = new();
    public TemplateHit? StoryBest { get; set; }
    public TemplateHit? OnlineBest { get; set; }
    public TemplateHit? HomeBest { get; set; }
    /// <summary>横幅模板实际搜索的高度比例（故事/在线=0.35，主菜单=1.0）；覆盖层用来画搜索带。</summary>
    public double BannerSearchRatio { get; set; }
    public int FrameWidth { get; set; }
    public int FrameHeight { get; set; }
    /// <summary>本次识别实际送进 OCR 的图，对应到原帧的矩形（x,y,w,h）——覆盖层 F6 画的就是它。</summary>
    public (int X, int Y, int W, int H)? OcrInputRect { get; set; }
    /// <summary>本次送进 OCR 的词框（原帧坐标），供覆盖层实时画框。</summary>
    public IReadOnlyList<OcrWord> OcrWords { get; set; } = Array.Empty<OcrWord>();
    /// <summary>本次送 OCR 的尺寸与缩放说明（日志/覆盖层显示用）。</summary>
    public string OcrSizeNote { get; set; } = "";
    /// <summary>横幅 OCR 复核结果（仅在 NCC 分数含糊时产生）。</summary>
    public string? BannerOcrText { get; set; }
    public bool BannerOcrStory { get; set; }
    public bool BannerOcrOnline { get; set; }
    public double StoryScore => StoryBest?.Score ?? 0;
    public double OnlineScore => OnlineBest?.Score ?? 0;
    public double HomeScore => HomeBest?.Score ?? 0;
}

/// <summary>
/// 屏幕阅读器 v0.2：横幅模板投票 + OCR 关键词证据分层判定。
/// 优先级：退出弹窗 > 加载 > 断连/保存失败 > 横幅(故事/在线) > 主菜单词证。
/// 阈值将随更多真实截图微调。
/// </summary>
public sealed class ScreenReader
{
    private readonly TemplateBank _bank;
    private readonly NccMatcher _matcher;
    private readonly IOcrEngine _ocr;
    private readonly LogBus _log;
    private readonly AppSettings.VisionSection? _vision;
    /// <summary>覆盖层 F6 开启时置位：Read 额外取一次词框（原帧坐标）供画框。</summary>
    public bool PreviewWords { get; set; }

    public ScreenReader(TemplateBank bank, NccMatcher matcher, IOcrEngine ocr, LogBus log,
        AppSettings.VisionSection? vision = null)
    {
        _bank = bank;
        _matcher = matcher;
        _ocr = ocr;
        _log = log;
        _vision = vision;
        // 预热一次（冷启动 0.3~0.4s），让后续计时反映真实稳态
        try { _ocr.RecognizeGray(new byte[16 * 16], 16, 16); } catch { }
    }

    /// <summary>把全分辨率帧归一到基准工作高度（与 LocateBest 同一套归一化，坐标仍以原帧返回）。</summary>
    private (byte[] Gray, int W, int H, double K) NormalizeForMatch(byte[] gray, int w, int h)
    {
        int workH = Math.Max(240, _vision?.BannerWorkHeight ?? 768);
        if (h <= workH + 8) return (gray, w, h, 1.0);
        double k = workH / (double)h;
        int dw = Math.Max(64, (int)Math.Round(w * k));
        var small = Imaging.GrayDownsample(gray, w, h, dw, workH);
        return (small, dw, workH, k);
    }

    /// <summary>
    /// 横幅/主菜单模板：在归一化后的帧上精确匹配（1:1）。
    /// 只把「搜索带」那一块拿去算特征图——整幅归一帧算 TextEnergy 是当前 Read 的主要耗时，
    /// 而横幅只在画面上部，裁带后特征点数按带高比例下降。
    /// </summary>
    private TemplateHit? BestOfGroupNormalized(byte[] smallGray, int sw, int sh, double k,
        string group, double topRatio, int bandOffsetY, int bandH)
    {
        var list = _bank.ByGroup(group);
        TemplateHit? best = null;
        foreach (var t in list)
        {
            // 已归一化 → 模板与画面同坐标，用精确单尺度（1:1）；传入的是“带图”，故 topRatio=1
            var hit = _matcher.LocateExact(smallGray, sw, bandH, t, MatchFeature.Text, 1.0);
            if (hit is null) continue;
            // 带内坐标 → 原帧坐标
            double inv = k != 1.0 ? 1.0 / k : 1.0;
            hit = new TemplateHit(hit.Group, hit.Name, hit.Score,
                (int)Math.Round(hit.X1 * inv), (int)Math.Round((hit.Y1 + bandOffsetY) * inv),
                (int)Math.Round(hit.X2 * inv), (int)Math.Round((hit.Y2 + bandOffsetY) * inv), hit.Scale);
            if (best is null || hit.Score > best.Score) best = hit;
        }
        return best;
    }

    // ---------- 横幅定点缓存（快速复查，避免每次全图扫描） ----------
    private sealed record BannerCache(int W, int H, string Group, double Scale,
        int X, int Y, int Tw, int Th);
    private BannerCache? _bannerCache;

    public bool HasBannerCache(Frame frame)
        => _bannerCache is not null && _bannerCache.W == frame.Width && _bannerCache.H == frame.Height;

    /// <summary>在记忆框 ± 小邻域复查横幅是否仍在（约几十毫秒）。</summary>
    public bool FastBannerPresent(Frame frame)
    {
        if (_bannerCache is null) return false;
        var c = _bannerCache;
        if (c.W != frame.Width || c.H != frame.Height) return false;
        var gray = Imaging.BgraToGray(frame.Bgra, frame.Width, frame.Height);
        var tplList = _bank.ByGroup(c.Group);
        var tpl = tplList.FirstOrDefault(t => t.Name.Length > 0);
        if (tpl is null) return false;
        int tw = c.Tw, th = c.Th;
        if (tw < 8 || th < 8) return false;
        var feat = Imaging.TextEnergy(gray, frame.Width, frame.Height, 7);
        byte[] tplGray = Imaging.GrayDownsample(tpl.Gray, tpl.Width, tpl.Height, tw, th);
        var tdata = Imaging.TextEnergy(tplGray, tw, th, 7);
        var (tm, ts) = Imaging.Stats(tdata, 0, tw * th);
        if ((double)ts < 1e-3) return false;
        double best = -2;
        int x0 = Math.Max(0, c.X - 4), x1 = Math.Min(frame.Width - tw, c.X + 4);
        int y0 = Math.Max(0, c.Y - 2), y1 = Math.Min(frame.Height - th, c.Y + 2);
        for (int y = y0; y <= y1; y++)
        {
            int rowOff = y * frame.Width;
            for (int x = x0; x <= x1; x++)
            {
                double sc = ScoreWindowLocal(feat, frame.Width, rowOff, x, tw, th, tdata, tm, ts);
                if (sc > best) best = sc;
            }
        }
        return best >= 0.78;
    }

    private static double ScoreWindowLocal(float[] src, int stride, int rowOff, int x,
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
                sum += v; sum2 += v * v; dot += v * tdata[ti++];
            }
        }
        double meanW = sum / n;
        double varW = sum2 / n - meanW * meanW;
        double stdW = Math.Sqrt(Math.Max(0.0, varW));
        if (stdW < 1e-3) return -2;
        return (dot / n - meanW * tmean) / (stdW * tstd);
    }

    /// <summary>横幅 OCR 复核：裁画面上部搜索带（整宽），按工作像素预算识别一次，返回去空白文本。</summary>
    private string? ReadBannerByOcr(Frame frame, byte[] gray, double ratio)
    {
        try
        {
            int y1 = (int)Math.Round(frame.Height * ratio);
            if (y1 < 24) return null;
            var (crop, cw, ch) = CropGray(frame, 0, 0, frame.Width, y1);
            var v = _vision ?? new AppSettings.VisionSection();
            var prep = OcrPrep.ForFullFrame(crop, cw, ch, v);
            // 只试 2 次且“首次有字就停”：这条兜底每次 Read 都可能触发，试多了纯浪费（实测 3 次会把 Read 从 3s 拖到 13s）
            string? best = null;
            for (int i = 0; i < 2 && best is null; i++)
            {
                var txt = _ocr.RecognizeGray(prep.Gray, prep.Width, prep.Height);
                if (!string.IsNullOrWhiteSpace(txt))
                    best = new string(txt.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
            }
            return best;
        }
        catch (Exception e)
        {
            _log.Warn("横幅 OCR 复核失败: " + e.Message, "Ocr");
            return null;
        }
    }

    public ReadResult Read(Frame frame, bool withOcr, bool includeHome = true, bool withBanner = true)
    {
        var res = new ReadResult();
        if (!frame.IsValid)
        {
            res.DebugLines.Add("无效帧");
            return res;
        }
        var gray = Imaging.BgraToGray(frame.Bgra, frame.Width, frame.Height);

        // 模式横幅不是固定 OCR 区域，而是模板匹配：故事/在线只在上部 X% 高搜索（参数可调），主菜单用整帧。
        // withBanner=false 时完全跳过（模板匹配是 Read 里最贵的一段，测试/已确认菜单状态时不需要）。
        double bannerRatio = Math.Clamp((_vision?.BannerSearchPercent ?? 35.0) / 100.0, 0.05, 1.0);
        res.FrameWidth = frame.Width;
        res.FrameHeight = frame.Height;
        res.BannerSearchRatio = bannerRatio;
        var bannerSw = System.Diagnostics.Stopwatch.StartNew();
        var norm = withBanner
            ? NormalizeForMatch(gray, frame.Width, frame.Height)
            : (Gray: gray, W: frame.Width, H: frame.Height, K: 1.0);
        // OCR 优先：模板库与新版渲染不对版时，NCC 分数（0.1~0.35）本来就不参与定案，
        // 那就别再每帧花 2~4s 跑它——只做横幅带 OCR 词证，判定与耗时都更好。
        bool ocrFirst = withBanner && withOcr && _ocr.Available && _vision?.BannerPreferOcr == true;
        if (withBanner && !ocrFirst)
        {
            res.StoryBest = BestOfGroupNormalized(norm.Gray, norm.W, norm.H, norm.K, "mode_story", bannerRatio, 0, norm.H);
            res.OnlineBest = BestOfGroupNormalized(norm.Gray, norm.W, norm.H, norm.K, "mode_online", bannerRatio, 0, norm.H);
            // 主菜单横幅实际也在画面上部；默认只搜上部 HomeSearchPercent%（原来整帧是 15s+ 的大头）
            if (includeHome)
            {
                double homeRatio = Math.Clamp((_vision?.HomeSearchPercent ?? 60.0) / 100.0, 0.1, 1.0);
                res.HomeBest = BestOfGroupNormalized(norm.Gray, norm.W, norm.H, norm.K, "mode_home", homeRatio, 0, norm.H);
                res.BannerSearchRatio = homeRatio;
            }
        }
        // 横幅 OCR 复核（兜底）：只在上部搜索带里找“在线模式 / 故事”等词证。
        // 用途：NCC 分数处在“说不清”的区间时用文字证据定案（也回答了“横幅能不能用 OCR 判”）。
        if (withBanner && withOcr && _ocr.Available && (_vision?.BannerOcrFallback == true || ocrFirst))
        {
            double best = ocrFirst ? 0.0 : Math.Max(res.StoryScore, res.OnlineScore);
            // NCC 不确定（<0.60）就跑一次——原模板与新版渲染不对版时分数常年在 0.1~0.35，
            // 旧的 best>=0.20 门槛反而不触发，等于兜底失效。
            if (best < 0.60)
            {
                var ocr = ReadBannerByOcr(frame, gray, bannerRatio);
                if (ocr is not null)
                {
                    res.BannerOcrText = ocr;
                    res.BannerOcrOnline = ocr.Contains("在线") || ocr.Contains("职业") || ocr.Contains("在线模式");
                    // 故事横幅是英文大标题 "Grand Theft Auto V"（OCR 常把 V 并进词里）；
                    // 在线横幅是 "Grand Theft Auto 在线模式" —— 有“在线”就算在线，没有则算故事。
                    bool grand = ocr.Contains("grand") || ocr.Contains("theft");
                    res.BannerOcrStory = ocr.Contains("故事") || ocr.Contains("简讯")
                        || (grand && !res.BannerOcrOnline);
                    res.DebugLines.Add("横幅OCR复核: 在线=" + res.BannerOcrOnline + " 故事=" + res.BannerOcrStory
                        + " 文本=" + (ocr.Length > 60 ? ocr.Substring(0, 60) : ocr));
                }
            }
        }
        if (_vision?.BannerTimingLog == true)
            _log.Info(string.Format("横幅模板匹配 上部{0:F0}% 耗时 {1}ms（故事 {2:F3} / 在线 {3:F3}）",
                bannerRatio * 100, bannerSw.ElapsedMilliseconds, res.StoryScore, res.OnlineScore), "Ocr");

        // 记录强横幅位置，供后续定点复查
        var strong = res.StoryBest is { Score: >= 0.75 } ? res.StoryBest
                   : res.OnlineBest is { Score: >= 0.75 } ? res.OnlineBest : null;
        if (strong is not null)
        {
            _bannerCache = new BannerCache(frame.Width, frame.Height,
                strong.Group, strong.Scale, strong.X1, strong.Y1, strong.Width, strong.Height);
        }
        else if (Math.Max(res.StoryScore, res.OnlineScore) < 0.60)
        {
            // 全图扫描确实看不到横幅（世界/加载/已关菜单）→ 清除陈旧缓存，避免误导定点复查
            _bannerCache = null;
        }

        double s = res.StoryScore;
        double o = res.OnlineScore;
        double h = res.HomeScore;
        res.DebugLines.Add(string.Format("横幅投票: 故事={0:F3} 在线={1:F3} 主菜单={2:F3}", s, o, h));

        bool ocrExit = false, ocrLoading = false, ocrDisconnect = false, ocrSaveFail = false, ocrHomeBar = false;
        if (withOcr && _ocr.Available)
        {
            // 退出/切换确认、加载、断连类弹窗都居中显示 → 先中央裁剪快速OCR（0.2~0.6s/帧），命中即定案，免整帧 4~9s。
            // 保存失败 toast 走左下 ScanToastStrip；主菜单顶栏在顶部 → 这两类不进中央快速通道（仍走整帧兜底）。
            string? quick = ScanCenterDialog(frame);
            bool quickHit = false;
            if (!string.IsNullOrWhiteSpace(quick))
            {
                res.OcrText = quick;
                string q = CleanNorm(quick);
                ocrLoading = ContainsAny(q, "正在加入", "正在初始化", "正在进入", "joininggtaonline");
                ocrExit = q.Contains("退出")
                          && (q.Contains("取消") || q.Contains("确认") || q.Contains("确定")
                              || q.Contains("保存") || q.Contains("故事")
                              || q.Contains("是否") || q.Contains("进度") || q.Contains("丢失"));
                ocrDisconnect = ContainsAny(q,
                    "无法保持与游戏服务器的连接", "无法从游戏服务下载", "无法连接服务进行验证", "连接丢失");
                if (ocrExit || ocrLoading || ocrDisconnect)
                {
                    quickHit = true;
                    foreach (var hit in CollectHits(ocrLoading, ocrExit, ocrDisconnect, false, false))
                        res.OcrHits.Add(hit);
                    res.DebugLines.Add("OCR 中央裁剪命中: " + string.Join(", ", res.OcrHits));
                }
            }
            if (!quickHit)
            {
                // 整帧兜底：先按工作像素预算等比缩小（保持长宽比），再灰度直送 OCR
                var fullSw = System.Diagnostics.Stopwatch.StartNew();
                var fullPrep = OcrPrep.ForFullFrame(gray, frame.Width, frame.Height, _vision ?? new AppSettings.VisionSection());
                long prepMs = fullSw.ElapsedMilliseconds;
                var txt = _ocr.RecognizeGray(fullPrep.Gray, fullPrep.Width, fullPrep.Height);
                double fk = fullPrep.Resized ? fullPrep.Scale : 1.0;
                res.OcrSizeNote = fullPrep.SizeNote;
                res.OcrInputRect = (0, 0, frame.Width, frame.Height);
                if (PreviewWords)
                {
                    var rawWords = _ocr.RecognizeWords(BgraFromGray(fullPrep.Gray), fullPrep.Width, fullPrep.Height);
                    var mapped = new List<OcrWord>(rawWords.Count);
                    foreach (var wd in rawWords)
                        mapped.Add(new OcrWord(wd.Text,
                            (int)Math.Round(wd.X1 / fk), (int)Math.Round(wd.Y1 / fk),
                            (int)Math.Round(wd.X2 / fk), (int)Math.Round(wd.Y2 / fk)));
                    res.OcrWords = mapped;
                }
                if (_vision?.OcrLogInput == true)
                    _log.Info(string.Format("OCR[整帧] 输入 {0} 缩放 {1:F2} 预处理 {2}ms 识别 {3}ms",
                        fullPrep.SizeNote, fullPrep.Resized ? fullPrep.Scale : 1.0, prepMs, fullSw.ElapsedMilliseconds - prepMs), "Ocr");
                if (txt is not null)
                {
                    res.OcrText = txt;
                    string full = CleanNorm(txt);
                    ocrLoading = ContainsAny(full, "正在加入", "正在初始化", "正在进入", "joininggtaonline");
                    ocrExit = full.Contains("退出")
                              && (full.Contains("取消") || full.Contains("确认") || full.Contains("确定")
                                  || full.Contains("保存") || full.Contains("故事")
                                  || full.Contains("是否") || full.Contains("进度") || full.Contains("丢失"));
                    ocrDisconnect = ContainsAny(full,
                        "无法保持与游戏服务器的连接", "无法从游戏服务下载", "无法连接服务进行验证", "连接丢失");
                    ocrSaveFail = full.Contains("保存失败");
                    ocrHomeBar = full.Contains("gta+")
                                 || (full.Contains("在线") && full.Contains("故事") && full.Contains("gta"));
                    foreach (var hit in CollectHits(ocrLoading, ocrExit, ocrDisconnect, ocrSaveFail, ocrHomeBar))
                        res.OcrHits.Add(hit);
                    res.DebugLines.Add("OCR 命中: " + string.Join(", ", res.OcrHits));
                }
                else
                {
                    res.DebugLines.Add("OCR 无结果");
                }
            }
        }


        // ---- 分层判定 ----
        double maxB = Math.Max(s, Math.Max(o, h));
        double[] sorted = { s, o, h };
        Array.Sort(sorted);
        double margin = sorted[2] - sorted[1];

        if (ocrExit)
        {
            res.Kind = UiKind.ExitDialog;
            res.Confidence = Math.Max(0.6, maxB);
            res.DebugLines.Add("OCR 证据: 退出/切换确认弹窗");
        }
        else if (ocrLoading)
        {
            res.Kind = UiKind.Loading;
            res.Confidence = Math.Max(0.55, maxB);
            res.DebugLines.Add("OCR 证据: 正在加入/加载");
        }
        else if (ocrDisconnect)
        {
            res.Kind = UiKind.DisconnectAlert;
            res.Confidence = Math.Max(0.6, maxB);
            res.DebugLines.Add("OCR 证据: 断连警报");
        }
        else if (ocrSaveFail)
        {
            res.Kind = UiKind.SaveFail;
            res.Confidence = Math.Max(0.6, maxB);
            res.DebugLines.Add("OCR 证据: 保存失败提示");
        }
        // 横幅词证优先于“分数太低 => 加载/未知”的兜底：模板与新版渲染不对版时，
        // NCC 常年只有 0.1~0.35，若先走 maxB<0.30 分支会把已经读到的横幅文字白白判成 Loading。
        else if (res.BannerOcrOnline && !res.BannerOcrStory)
        {
            res.Kind = UiKind.OnlineTitle;
            res.Confidence = 0.55;
            res.DebugLines.Add("OCR 证据: 横幅带词证 -> 在线模式");
        }
        else if (res.BannerOcrStory && !res.BannerOcrOnline)
        {
            res.Kind = UiKind.StoryTitle;
            res.Confidence = 0.55;
            res.DebugLines.Add("OCR 证据: 横幅带词证 -> 故事模式");
        }
        else if (res.BannerOcrText is not null)
        {
            res.DebugLines.Add("横幅词证未定案: online=" + res.BannerOcrOnline + " story=" + res.BannerOcrStory
                + " text=" + res.BannerOcrText);
        }
        else if (maxB >= 0.50 && margin >= 0.08)
        {
            if (maxB == s) res.Kind = UiKind.StoryTitle;
            else if (maxB == o) res.Kind = UiKind.OnlineTitle;
            else res.Kind = UiKind.TitleHome;
            res.Confidence = maxB;
        }
        else if (ocrHomeBar && h <= 0.5)
        {
            res.Kind = UiKind.TitleHome;
            res.Confidence = 0.5 + h * 0.2;
            res.DebugLines.Add("OCR 证据: 主菜单顶部栏(在线/GTA+/故事)");
        }
        else if (maxB < 0.30)
        {
            res.Kind = UiKind.Loading;
            res.Confidence = maxB;
            res.DebugLines.Add("无横幅命中 -> 加载/黑屏/未知");
        }
        else
        {
            res.Kind = UiKind.Unknown;
            res.Confidence = maxB;
            res.DebugLines.Add("横幅分数接近或过低（歧义），等待下一帧");
        }
        return res;
    }

    /// <summary>
    /// 左下“提示条”快速扫描：整帧 OCR 把 toast 小字“保存失败”读碎成“保 存 灾”且每帧耗 5-9s，
    /// 这里只裁左下条带并按高度比例 2x 放大再 OCR（约 0.1-0.3s/帧），用于等待“保存失败”提示。
    /// 返回原始文本（字符间可能带空格）；调用方自行规整匹配。
    /// </summary>
    public string? ScanToastStrip(Frame frame)
    {
        if (!frame.IsValid || !_ocr.Available) return null;
        try
        {
            int W = frame.Width, H = frame.Height;
            // 百分比区域（Vision·提示条可调）：左下 toast 条带
            double rp = _vision?.ToastRightPercent ?? 35.0;
            double tp = _vision?.ToastTopPercent ?? 45.0;
            double bp = _vision?.ToastBottomPercent ?? 90.0;
            int x1 = (int)Math.Round(W * rp / 100.0);
            int y0 = (int)Math.Round(H * tp / 100.0);
            int y1 = (int)Math.Round(H * bp / 100.0);
            if (x1 <= 16 || y1 <= y0 || y0 < 0 || y1 > H || x1 > W) return null;
            var (crop, cw, ch) = CropGray(frame, 0, y0, x1, y1);
            var v = _vision ?? new AppSettings.VisionSection();
            // 站点固定倍率（实测表）：与人的裁剪百分比解耦，只决定“怎么识”
            var prep = OcrPrep.ForRegionSite(crop, cw, ch, "toast", v);
            var px = OcrPrep.ApplyContrast(prep.Gray, 1.3);
            var text = OcrPrep.Recognize(_ocr, v, _log, "左下toast", crop, cw, ch, prep);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception e)
        {
            _log.Warn("提示条 OCR 失败: " + e.Message, "Ocr");
            return null;
        }
    }

    /// <summary>诊断用：对指定区域走一遍“裁剪→实测行高→工作像素缩放→OCR”，返回文本与尺寸说明。</summary>
    public (string? Text, string Note) PrepareRegion(Frame frame, string site)
    {
        if (!frame.IsValid || !_ocr.Available) return (null, "(无帧/无OCR)");
        var v = _vision ?? new AppSettings.VisionSection();
        int x0, y0, x1, y1;
        if (site == "左下toast")
        {
            x0 = 0;
            y0 = (int)Math.Round(frame.Height * v.ToastTopPercent / 100.0);
            x1 = (int)Math.Round(frame.Width * v.ToastRightPercent / 100.0);
            y1 = (int)Math.Round(frame.Height * v.ToastBottomPercent / 100.0);
        }
        else
        {
            x0 = (int)Math.Round(frame.Width * v.DialogCropLeft);
            x1 = (int)Math.Round(frame.Width * v.DialogCropRight);
            y0 = (int)Math.Round(frame.Height * v.DialogCropTop);
            y1 = (int)Math.Round(frame.Height * v.DialogCropBottom);
        }
        var (crop, cw, ch) = CropGray(frame, x0, y0, x1, y1);
        string key = site == "左下toast" ? "toast" : "dialog";
        var prep = OcrPrep.ForRegionSite(crop, cw, ch, key, v);
        var text = OcrPrep.Recognize(_ocr, v, _log, site + "·诊断", crop, cw, ch, prep);
        return (text, string.Format("{0} {1}x{2}→{3}x{4} k={5:F2}",
            site, cw, ch, prep.Width, prep.Height, prep.Resized ? prep.Scale : 1.0));
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

    /// <summary>按原帧像素取一块灰度（x0/y0/x1/y1 为闭开区间）。</summary>
    private static (byte[] Gray, int W, int H) CropGray(Frame frame, int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, Math.Min(frame.Width - 1, x0));
        y0 = Math.Max(0, Math.Min(frame.Height - 1, y0));
        x1 = Math.Max(x0 + 1, Math.Min(frame.Width, x1));
        y1 = Math.Max(y0 + 1, Math.Min(frame.Height, y1));
        int w = x1 - x0, h = y1 - y0;
        var full = Imaging.BgraToGray(frame.Bgra, frame.Width, frame.Height);
        var crop = new byte[w * h];
        for (int y = 0; y < h; y++)
            Array.Copy(full, (y0 + y) * frame.Width + x0, crop, y * w, w);
        return (crop, w, h);
    }

    /// <summary>中央区域快速 OCR：退出/切换确认、加载、断连等弹窗都居中（黑底白字/提示条），
    /// 只裁中央框(默认 x 0.18..0.82W、y 0.26..0.74H) 2x 放大+轻微对比 → 约 0.2~0.6s/帧，
    /// 替代整帧 OCR 4~9s。区域可用 AppSettings.VisionSection.DialogCrop* 调。空结果返回 null（调用方回退整帧）。</summary>
    public string? ScanCenterDialog(Frame frame)
    {
        if (!frame.IsValid || !_ocr.Available) return null;
        try
        {
            int W = frame.Width, H = frame.Height;
            double l = _vision?.DialogCropLeft ?? 0.18, r = _vision?.DialogCropRight ?? 0.82;
            double t = _vision?.DialogCropTop ?? 0.26, b = _vision?.DialogCropBottom ?? 0.74;
            int x0 = (int)Math.Round(W * l), x1 = (int)Math.Round(W * r);
            int y0 = (int)Math.Round(H * t), y1 = (int)Math.Round(H * b);
            if (x1 - x0 < 40 || y1 - y0 < 24 || x0 < 0 || y0 < 0 || x1 > W || y1 > H) return null;
            var (crop, cw, ch) = CropGray(frame, x0, y0, x1, y1);
            var v = _vision ?? new AppSettings.VisionSection();
            var prep = OcrPrep.ForRegionSite(crop, cw, ch, "dialog", v);
            var text = OcrPrep.Recognize(_ocr, v, _log, "中央弹窗", crop, cw, ch, prep);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception e)
        {
            _log.Warn("中央OCR 失败: " + e.Message, "Ocr");
            return null;
        }
    }

    private static string CleanNorm(string text)
        => new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    private static bool ContainsAny(string text, params string[] kws)
        => kws.Any(k => text.Contains(k));

    private static IEnumerable<string> CollectHits(bool loading, bool exit, bool disconnect, bool saveFail, bool home)
    {
        if (loading) yield return "加载/加入中";
        if (exit) yield return "退出弹窗";
        if (disconnect) yield return "断连警报";
        if (saveFail) yield return "保存失败";
        if (home) yield return "主菜单词证";
    }
}