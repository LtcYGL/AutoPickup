using AutoPickup.Logging;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace AutoPickup.Core.Vision.Ocr;

/// <summary>Windows 内置 OCR（WinRT）。零外部文件；需要语言包（中文系统自带）。</summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly OcrEngine? _engine;
    private readonly LogBus _log;

    public string Name => "WindowsOCR";
    public bool Available => _engine is not null;

    private static readonly string[] LanguageTags =
    {
        "zh-Hans-CN", "zh-CN", "zh-Hans", "en-US", "zh-Hant-CN", "en-GB",
    };

    public WindowsOcrEngine(LogBus log)
    {
        _log = log;
        _engine = TryCreateEngine();
        if (_engine is null) _log.Warn("Windows OCR 不可用（缺少语言包或系统过旧），将退回纯模板识别", "Ocr");
        else _log.Info("Windows OCR 可用", "Ocr");
    }

    private static OcrEngine? TryCreateEngine()
    {
        foreach (var tag in LanguageTags)
        {
            try
            {
                var lang = new Language(tag);
                var e = OcrEngine.TryCreateFromLanguage(lang);
                if (e is not null) return e;
            }
            catch { }
        }
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch { return null; }
    }

    public string? Recognize(byte[] bgra, int width, int height)
    {
        if (_engine is null || width <= 0 || height <= 0) return null;
        try
        {
            var buf = CryptographicBuffer.CreateFromByteArray(bgra);
            var sbmp = SoftwareBitmap.CreateCopyFromBuffer(buf,
                BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
            try
            {
                var op = _engine.RecognizeAsync(sbmp);
                var res = Wait(op);
                if (res is null) return null;
                var sb = new System.Text.StringBuilder();
                foreach (var line in res.Lines)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(line.Text);
                }
                return sb.ToString();
            }
            finally { sbmp.Dispose(); }
        }
        catch (Exception e)
        {
            _log.Warn("OCR 失败: " + e.Message, "Ocr");
            return null;
        }
    }


    /// <summary>灰度直送：1B/像素就地展开成 BGRA8 再交给 WinRT（省一整块 4B 缓冲）。</summary>
    public string? RecognizeGray(byte[] gray, int width, int height)
    {
        if (_engine is null || gray is null || width <= 0 || height <= 0) return null;
        try
        {
            int n = width * height;
            if (gray.Length < n) return null;
            var bgra = new byte[n * 4];
            for (int i = 0, j = 0; i < n; i++, j += 4)
            {
                byte v = gray[i];
                bgra[j] = v; bgra[j + 1] = v; bgra[j + 2] = v; bgra[j + 3] = 255;
            }
            return Recognize(bgra, width, height);
        }
        catch (Exception e)
        {
            _log.Warn("OCR 灰度识别失败: " + e.Message, "Ocr");
            return null;
        }
    }

    public IReadOnlyList<OcrWord> RecognizeWords(byte[] bgra, int width, int height)
    {
        var list = new List<OcrWord>();
        if (_engine is null || width <= 0 || height <= 0) return list;
        try
        {
            var buf = CryptographicBuffer.CreateFromByteArray(bgra);
            var sbmp = SoftwareBitmap.CreateCopyFromBuffer(buf,
                BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
            try
            {
                var op = _engine.RecognizeAsync(sbmp);
                var res = Wait(op);
                if (res is null) return list;
                foreach (var line in res.Lines)
                {
                    foreach (var word in line.Words)
                    {
                        var t = word.Text.Trim();
                        if (t.Length == 0) continue;
                        var r = word.BoundingRect;
                        list.Add(new OcrWord(t,
                            (int)Math.Round(r.X), (int)Math.Round(r.Y),
                            (int)Math.Round(r.X + r.Width), (int)Math.Round(r.Y + r.Height)));
                    }
                }
            }
            finally { sbmp.Dispose(); }
        }
        catch (Exception e)
        {
            _log.Warn("OCR words 失败: " + e.Message, "Ocr");
        }
        return list;
    }


    private static OcrResult? Wait(IAsyncOperation<OcrResult> op)
    {
        while (op.Status == AsyncStatus.Started)
            Thread.Sleep(2);
        if (op.Status != AsyncStatus.Completed) return null;
        return op.GetResults();
    }
}