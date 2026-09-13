namespace AutoPickup.Core.Vision;

/// <summary>基础图像算子（纯托管，无第三方依赖）。输入统一为 BGRA32 top-down。</summary>
public static class Imaging
{
    /// <summary>BGRA -> 灰度（BT.601 亮度，用整数近似）。</summary>
    public static byte[] BgraToGray(byte[] bgra, int width, int height)
    {
        var gray = new byte[width * height];
        int idx = 0, gi = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int b = bgra[idx], g = bgra[idx + 1], r = bgra[idx + 2];
                gray[gi++] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
                idx += 4;
            }
        }
        return gray;
    }

    /// <summary>区域平均降采样（更稳，避免走样）。</summary>
    public static byte[] GrayDownsample(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (dw >= sw && dh >= sh)
        {
            var copy = new byte[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int sy = Math.Min(y, sh - 1);
                Array.Copy(src, sy * sw, copy, y * dw, Math.Min(sw, dw));
                if (dw > sw)
                    for (int x = sw; x < dw; x++) copy[y * dw + x] = src[sy * sw + sw - 1];
            }
            return copy;
        }
        var outB = new byte[dw * dh];
        for (int oy = 0; oy < dh; oy++)
        {
            int y0 = oy * sh / dh, y1 = Math.Max(y0 + 1, (oy + 1) * sh / dh);
            if (y1 > sh) y1 = sh;
            for (int ox = 0; ox < dw; ox++)
            {
                int x0 = ox * sw / dw, x1 = Math.Max(x0 + 1, (ox + 1) * sw / dw);
                if (x1 > sw) x1 = sw;
                long sum = 0;
                int n = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        sum += src[y * sw + x];
                        n++;
                    }
                outB[oy * dw + ox] = n == 0 ? (byte)0 : (byte)(sum / n);
            }
        }
        return outB;
    }

    /// <summary>盒式模糊（积分图）。</summary>
    public static byte[] BoxBlur(byte[] src, int w, int h, int radius)
    {
        var integral = new long[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            long row = 0;
            var rowBase = (y + 1) * (w + 1);
            var srcBase = y * w;
            for (int x = 0; x < w; x++)
            {
                row += src[srcBase + x];
                integral[rowBase + x + 1] = integral[rowBase - (w + 1) + x + 1] + row;
            }
        }
        var dst = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - radius), y1 = Math.Min(h - 1, y + radius);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(w - 1, x + radius);
                long a = integral[y0 * (w + 1) + x0];
                long b = integral[y0 * (w + 1) + x1 + 1];
                long c = integral[(y1 + 1) * (w + 1) + x0];
                long d = integral[(y1 + 1) * (w + 1) + x1 + 1];
                long area = (long)(y1 - y0 + 1) * (x1 - x0 + 1);
                dst[y * w + x] = (byte)((d - b - c + a) / area);
            }
        }
        return dst;
    }

    /// <summary>文字能量图：abs(gray - localMean)。黑白两极性文字都高响应，供模板匹配前处理。</summary>
    public static float[] TextEnergy(byte[] gray, int w, int h, int radius = 7)
    {
        var mean = BoxBlur(gray, w, h, radius);
        var f = new float[gray.Length];
        for (int i = 0; i < gray.Length; i++)
            f[i] = Math.Abs(gray[i] - mean[i]);
        return f;
    }

    /// <summary>灰度浮点图：直接用于 NCC 匹配（原始像素）。</summary>
    public static float[] ToFloat(byte[] gray)
    {
        var f = new float[gray.Length];
        for (int i = 0; i < gray.Length; i++) f[i] = gray[i];
        return f;
    }

    /// <summary>计算 NCC（归一化互相关系数）的模板均值与标准差。</summary>
    public static (float mean, float std) Stats(float[] data, int offset, int count)
    {
        double sum = 0, sum2 = 0;
        for (int i = 0; i < count; i++)
        {
            double v = data[offset + i];
            sum += v;
            sum2 += v * v;
        }
        double mean = sum / count;
        double var = Math.Max(0, sum2 / count - mean * mean);
        return ((float)mean, (float)Math.Sqrt(var));
    }
}
