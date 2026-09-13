namespace AutoPickup.Core.Vision.Ocr;

/// <summary>OCR 词（含在原图中的像素框，坐标以送入识别的图像为准）。</summary>
public sealed record OcrWord(string Text, int X1, int Y1, int X2, int Y2);
