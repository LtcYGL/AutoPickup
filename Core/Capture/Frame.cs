namespace AutoPickup.Core.Capture;

/// <summary>BGRA32 帧（每像素 4 字节，行序 top-down）。</summary>
public sealed class Frame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    public Frame(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public static Frame Empty { get; } = new(0, 0, Array.Empty<byte>());
    public bool IsValid => Width > 0 && Height > 0 && Bgra.Length == Width * Height * 4;
}
