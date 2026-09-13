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
        var bgra = new byte[width * height * 4];
        for (int i = 0, j = 0; i < gray.Length && j < bgra.Length; i++, j += 4)
        {
            byte v = gray[i];
            bgra[j] = v; bgra[j + 1] = v; bgra[j + 2] = v; bgra[j + 3] = 255;
        }
        return Recognize(bgra, width, height);
    }
}