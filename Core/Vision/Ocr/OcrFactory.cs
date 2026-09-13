using AutoPickup.Logging;

namespace AutoPickup.Core.Vision.Ocr;

public static class OcrFactory
{
    public static IOcrEngine Create(LogBus log)
    {
        try
        {
            return new WindowsOcrEngine(log);
        }
        catch (Exception e)
        {
            log.Warn("OCR 初始化失败: " + e.Message, "Ocr");
            return new NoOcr(log);
        }
    }
}

/// <summary>兜底：无 OCR 时行为等价于“整图无文本”。</summary>
public sealed class NoOcr : IOcrEngine
{
    private readonly LogBus _log;
    public string Name => "NoOcr";
    public bool Available => false;

    public NoOcr(LogBus log) => _log = log;

    public string? Recognize(byte[] bgra, int width, int height) => null;

    public IReadOnlyList<OcrWord> RecognizeWords(byte[] bgra, int width, int height)
        => Array.Empty<OcrWord>();
}