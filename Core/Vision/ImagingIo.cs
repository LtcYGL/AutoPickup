using System.Drawing;
using System.Drawing.Imaging;
using AutoPickup.Core.Capture;

namespace AutoPickup.Core.Vision;

/// <summary>Frame 与磁盘/图片的互转（调试、截图校准用）。</summary>
public static class ImagingIo
{
    public static Frame? LoadImage(string path)
    {
        try
        {
            using var src = new Bitmap(path);
            int w = src.Width, h = src.Height;
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.DrawImage(src, 0, 0, w, h);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var bytes = new byte[w * h * 4];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            bmp.UnlockBits(data);
            return new Frame(w, h, bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool SaveFrame(Frame frame, string path, long quality = 85)
    {
        try
        {
            if (!frame.IsValid) return false;
            using var bmp = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, frame.Width, frame.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(frame.Bgra, 0, data.Scan0, frame.Bgra.Length);
            bmp.UnlockBits(data);
            var codec = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            bmp.Save(path, codec ?? throw new InvalidOperationException("no jpeg codec"), ep);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
