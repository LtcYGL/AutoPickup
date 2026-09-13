namespace AutoPickup.Core.Vision;

/// <summary>一张模板：分类组 + 名称 + 灰度像素。组用于按需选取匹配对象。</summary>
public sealed class TemplateDef
{
    public string Group { get; init; } = "";
    public string Name { get; init; } = "";
    public byte[] Gray { get; init; } = Array.Empty<byte>();
    public int Width { get; init; }
    public int Height { get; init; }

    public override string ToString() => Group + "/" + Name + " " + Width + "x" + Height;
}
