using AutoPickup.Config;
using AutoPickup.Core.Vision.Ocr;
using AutoPickup.Logging;

namespace AutoPickup.Core.Vision;

/// <summary>
/// OCR 输入的“工作像素”统一预处理：把任意分辨率/任意裁剪的图，等比缩到一个
/// **固定像素预算**（整帧兜底）或 **固定目标字高**（区域识别），再用双三次或最近邻重采样，
/// 最后一律以**灰度直送** OCR（引擎内展开 BGRA，省一块 4B 缓冲）。
///
/// 设计要点（2026-09-12 实测定稿）：
/// - 只做等比缩放，永不拉伸压扁；输出尺寸 = round(输入尺寸 * 同一个 k)。
/// - 整帧：k = min(1, sqrt(预算像素 / 实际像素))，只缩不放。
/// - 区域：k = clamp(目标字高 / 实际行高, 1, 放大上限)，配合像素预算做上限。
/// - 每次调用可打印“站点 | 输入 | 缩放比 | 耗时”，供日志与覆盖层核对。
/// </summary>
public static class OcrPrep
{
    public sealed record Result(byte[] Gray, int Width, int Height, double Scale, bool Resized)
    {
        public string SizeNote => Resized
            ? string.Format("{0}x{1} ×{2:F2}", Width, Height, Scale)
            : string.Format("{0}x{1} 原样", Width, Height);
    }

    /// <summary>整帧兜底用：按像素预算等比缩小（只缩不放，保持长宽比）。</summary>
    public static Result ForFullFrame(byte[] gray, int w, int h, AppSettings.VisionSection v)
    {
        double maxPx = Math.Max(0.05, v.OcrWorkPixelsM) * 1_000_000.0;
        double cur = (double)w * h;
        double k = cur > maxPx ? Math.Sqrt(maxPx / cur) : 1.0;
        return Resize(gray, w, h, k, v);
    }

    /// <summary>各识别站点的固定放大倍率（面积帧字号的硬性基线；0=原样，不放大不缩小）。</summary>
    public static double SiteUpscale(string site, AppSettings.VisionSection v) => site switch
    {
        "toast" => Math.Max(0, v.ToastUpscale),
        "tab" => Math.Max(0, v.TabUpscale),
        "dialog" => Math.Max(0, v.DialogUpscale),
        "focusrow" => Math.Max(0, v.FocusRowUpscale),
        "list" => Math.Max(0, v.ListUpscale),
        _ => Math.Max(0, v.RegionUpscale),
    };

    /// <summary>区域识别用：按站点固定倍率等比放大（倍率来自实测表，不受人肉裁剪百分比影响）。</summary>
    public static Result ForRegionSite(byte[] gray, int w, int h, string site, AppSettings.VisionSection v)
    {
        double k = SiteUpscale(site, v);
        if (k <= 1.0001) return new Result(gray, w, h, 1.0, false);
        return Resize(gray, w, h, k, v);
    }

    /// <summary>按 k 等比重采样（k≈1 直接返回原图）。</summary>
    public static Result Resize(byte[] gray, int w, int h, double k, AppSettings.VisionSection v)
    {
        if (w <= 0 || h <= 0 || gray is null || gray.Length < w * h) return new Result(gray ?? Array.Empty<byte>(), w, h, 1.0, false);
        if (k >= 0.995 && k <= 1.005) return new Result(gray, w, h, 1.0, false);
        int dw = Math.Max(8, (int)Math.Round(w * k));
        int dh = Math.Max(4, (int)Math.Round(h * k));
        if (dw == w && dh == h) return new Result(gray, w, h, 1.0, false);
        try
        {
            var dst = Resample(gray, w, h, dw, dh, v.OcrUseBicubic);
            return new Result(dst, dw, dh, dw / (double)w, true);
        }
        catch
        {
            return new Result(gray, w, h, 1.0, false);
        }
    }

    /// <summary>灰度重采样（GDI+），双三次或最近邻，始终等比。</summary>
    private static byte[] Resample(byte[] gray, int sw, int sh, int dw, int dh, bool bicubic)
    {
        using var src = GrayToBitmap(gray, sw, sh);
        using var dst = new System.Drawing.Bitmap(dw, dh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(dst))
        {
            g.InterpolationMode = bicubic
                ? System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic
                : System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = bicubic
                ? System.Drawing.Drawing2D.PixelOffsetMode.HighQuality
                : System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(src, new System.Drawing.Rectangle(0, 0, dw, dh));
        }
        var data = dst.LockBits(new System.Drawing.Rectangle(0, 0, dw, dh),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var tmp = new byte[dw * dh * 4];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, tmp, 0, tmp.Length);
        dst.UnlockBits(data);
        var outGray = new byte[dw * dh];
        for (int i = 0, j = 0; i < outGray.Length; i++, j += 4)
            outGray[i] = (byte)((tmp[j + 2] * 77 + tmp[j + 1] * 150 + tmp[j] * 29) >> 8);
        return outGray;
    }

    private static System.Drawing.Bitmap GrayToBitmap(byte[] gray, int w, int h)
    {
        var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var tmp = new byte[w * h * 4];
        for (int i = 0, j = 0; i < tmp.Length; i += 4, j++)
        {
            byte v = gray[j];
            tmp[i] = v; tmp[i + 1] = v; tmp[i + 2] = v; tmp[i + 3] = 255;
        }
        System.Runtime.InteropServices.Marshal.Copy(tmp, 0, data.Scan0, tmp.Length);
        bmp.UnlockBits(data);
        return bmp;
    }

    /// <summary>对比度线性拉伸（白字/灰字都保笔画，不做二值化）。</summary>
    public static byte[] ApplyContrast(byte[] gray, double k)
    {
        if (Math.Abs(k - 1.0) < 1e-6) return gray;
        var o = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++)
        {
            int val = (int)((gray[i] - 128) * k + 128);
            o[i] = (byte)Math.Clamp(val, 0, 255);
        }
        return o;
    }

    /// <summary>识别 + 打印“站点 | 输入尺寸 | 缩放比 | 耗时”（受 Vision.OcrLogInput 控制）。</summary>
    public static string? Recognize(IOcrEngine ocr, AppSettings.VisionSection v, LogBus log,
        string site, byte[] gray, int w, int h, Result r)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? text = ocr.RecognizeGray(r.Gray, r.Width, r.Height);
        long ms = sw.ElapsedMilliseconds;
        if (v.OcrLogInput)
        {
            log.Info(string.Format("OCR[{0}] 输入 {1} 缩放 {2:F2} 耗时 {3}ms", site, r.SizeNote, r.Resized ? r.Scale : 1.0, ms), "Ocr");
        }
        return text;
    }
}
