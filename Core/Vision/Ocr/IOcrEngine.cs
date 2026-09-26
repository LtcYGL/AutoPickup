namespace AutoPickup.Core.Vision.Ocr;

public interface IOcrEngine
{
    string Name { get; }
    bool Available { get; }

    /// <summary>整图 OCR，返回归一化文本（空白折叠）。不可用/失败返回 null。</summary>
    string? Recognize(byte[] bgra, int width, int height);

    /// <summary>逐词 OCR（含像素框），失败返回空列表。</summary>
    IReadOnlyList<OcrWord> RecognizeWords(byte[] bgra, int width, int height);

    /// <summary>灰度直送（每像素 1B）——默认实现复制成 BGRA 后走 Recognize，省去调用方自己建 4B 缓冲。</summary>
    string? RecognizeGray(byte[] gray, int width, int height)
    {
        if (gray is null || width <= 0 || height <= 0) return null;
        int n = width * height;
        if (gray.Length < n) return null;
        // 展开逻辑统一在 Imaging.GrayToBgra（原来接口默认实现与 WindowsOcrEngine 各抄了一份）
        return Recognize(Imaging.GrayToBgra(n == gray.Length ? gray : gray[..n]), width, height);
    }
}