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


    /// <summary>等待 WinRT 异步 OCR。**带超时**：以前是纯忙等，OCR 一旦卡住会把整个流程冻死
    /// （步骤超时是在两次观察之间检查的，卡在里面就永远不会超时）。</summary>
    private OcrResult? Wait(IAsyncOperation<OcrResult> op, int timeoutMs = 20000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (op.Status == AsyncStatus.Started)
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                _log.Warn("OCR 超时（>" + timeoutMs + "ms），本帧按“无文本”处理", "Ocr");
                return null;
            }
            Thread.Sleep(2);
        }
        if (op.Status != AsyncStatus.Completed) return null;
        try { return op.GetResults(); } catch { return null; }
    }
}