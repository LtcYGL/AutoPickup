namespace AutoPickup.Core.Capture;

/// <summary>BGRA32 帧（每像素 4 字节，行序 top-down）。</summary>
public sealed class Frame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    /// <summary>本次抓帧的序号（同一帧=同一序号，每次新抓帧递增）。观察缓存用它当钥匙：
    /// 以前缓存按 <c>Bgra</c> 数组引用判等，而每次抓帧都是新数组 → 缓存永远不命中、同一帧被反复 OCR。
    /// 0 = 未标记（不缓存）。</summary>
    public long Seq { get; set; }

    public Frame(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public static Frame Empty { get; } = new(0, 0, Array.Empty<byte>());
    public bool IsValid => Width > 0 && Height > 0 && Bgra.Length == Width * Height * 4;
}
