using AutoPickup.Config;
using AutoPickup.Core.Capture;
using AutoPickup.Core.Vision.Ocr;
using AutoPickup.Logging;

namespace AutoPickup.Core.Vision;

/// <summary>焦点行识别结果。</summary>
public sealed record FocusRow(int X0, int Y0, int X1, int Y1, string? Label)
{
    public int Height => Y1 - Y0;
    public int Width => X1 - X0;
}

/// <summary>
/// 暂停菜单“焦点行”识别：选中行在 GTAV 暂停菜单里是整行纯白背景+黑字，
/// 未选行是深底浅字。做法：列表 x 区间的行亮度剖面找“白带”，对白带做放大 OCR 读文字。
/// 分辨率无关：列表面板几何按帧高 / 768 等比缩放。
/// </summary>
public sealed class FocusRowReader
{
    private readonly IOcrEngine _ocr;
    private readonly LogBus _log;
    private readonly AppSettings _settings;
    private readonly int _refH = 768;

    public FocusRowReader(IOcrEngine ocr, LogBus log, AppSettings settings)
    {
        _ocr = ocr;
        _log = log;
        _settings = settings;
    }

    /// <param name="yFrom">跳过 tab 条：从该 y 开始扫列表（null 用配置默认）。</param>
    public FocusRow? FindFocusRow(Frame frame, int? yFrom = null)
    {
        if (!frame.IsValid || !_ocr.Available) return null;
        double k = frame.Height / (double)_refH;
        // 百分比区域（相对帧宽/帧高各自换算）：列表为竖条，X 用宽度比例、Y 用高度比例，避免超宽屏被拉偏
        var v = _settings.Vision;
        int xL = (int)Math.Round(frame.Width * v.ListLeftPercent / 100.0);
        int xR = (int)Math.Round(frame.Width * v.ListRightPercent / 100.0);
        int yTop = yFrom ?? (int)Math.Round(frame.Height * v.ListTopPercent / 100.0);
        int yBot = Math.Min(frame.Height - 1, (int)Math.Round(frame.Height * v.ListBottomPercent / 100.0));
        xL = Math.Max(0, xL); xR = Math.Min(frame.Width, xR); yTop = Math.Max(0, yTop);
        if (xR <= xL || yBot <= yTop) return null;

        var gray = Imaging.BgraToGray(frame.Bgra, frame.Width, frame.Height);
        var white = new bool[frame.Height];
        double thWhite = _settings.Vision.RowWhiteThreshold;
        for (int ry = yTop; ry <= yBot; ry++)
        {
            long sum = 0;
            int n = 0;
            for (int x = xL; x < xR; x++)
            {
                sum += gray[ry * frame.Width + x];
                n++;
            }
            white[ry] = n > 0 && sum / (double)n >= thWhite;
        }

        // 找“行高合理 + 最白”的近白色带（高亮行纯白窄带；右侧说明面板等宽块会被行高过滤）
        double minRowH = 14 * k, maxRowH = 56 * k;
        int bestY0 = -1, bestLen = 0;
        double bestWhite = -1;
        int y = yTop;
        while (y <= yBot)
        {
            if (!white[y]) { y++; continue; }
            int start = y;
            int gap = 0;
            while (y <= yBot && gap <= 2)
            {
                if (white[y]) gap = 0;
                else gap++;
                y++;
            }
            int len = y - gap - start;
            if (len >= minRowH && len <= maxRowH)
            {
                // 平均亮度 = 白度
                long sumG = 0;
                for (int yy = start; yy < start + len; yy++)
                    sumG += gray[yy * frame.Width + xL];
                double w = sumG / (double)len;
                if (w > bestWhite) { bestWhite = w; bestY0 = start; bestLen = len; }
            }
        }
        if (bestY0 < 0 || bestLen < 8) return null;
        int y1 = Math.Min(yBot, bestY0 + bestLen);
        // 行宽：白带内实际白色像素的左/右边界（跳过边缘阴影）
        int xMin = xR, xMax = xL;
        for (int yy = bestY0; yy < y1; yy++)
        {
            for (int x = xL; x < xR; x++)
            {
                if (gray[yy * frame.Width + x] >= (int)thWhite)
                {
                    if (x < xMin) xMin = x;
                    if (x > xMax) xMax = x;
                }
            }
        }
        if (xMax < xMin) return null;
        // 列表焦点行整行宽（≈300px）；顶部 tab 选中白块只有 ~60-75px，按宽度过滤掉
        if (xMax - xMin < 150 * k) return null;

        string? label = ReadRowLabel(frame, xMin, bestY0, xMax, y1);
        return new FocusRow(xMin, bestY0, xMax, y1, label);
    }

    private string? ReadRowLabel(Frame frame, int x0, int y0, int x1, int y1)
    {
        try
        {
            int w = x1 - x0, h = y1 - y0;
            if (w < 8 || h < 4) return null;
            var full = Imaging.BgraToGray(frame.Bgra, frame.Width, frame.Height);
            var crop = new byte[w * h];
            for (int y = 0; y < h; y++)
                Array.Copy(full, (y0 + y) * frame.Width + x0, crop, y * w, w);
            var v = _settings.Vision;
            // 行裁很小，按站点固定倍率放大（默认 3）后灰度直送
            var prep = OcrPrep.ForRegionSite(crop, w, h, "focusrow", v);
            var px = OcrPrep.ApplyContrast(prep.Gray, 1.6);
            var text = OcrPrep.Recognize(_ocr, v, _log, "焦点行", crop, w, h, prep);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception e)
        {
            _log.Warn("焦点行 OCR 失败: " + e.Message, "FocusRow");
            return null;
        }
    }
}