using System.Runtime.InteropServices;
using AutoPickup.Core.Native;
using AutoPickup.Logging;

namespace AutoPickup.Core.Capture;

/// <summary>
/// 定位并抓取 GTAV 增强版窗口客户区。
/// 抓屏策略：先 PrintWindow(PW_RENDERFULLCONTENT)（窗口被遮挡/非前台也能拿到游戏画面），
/// 失败再退回屏幕 BitBlt。分辨率无关：以真实客户区像素为准。
/// </summary>
public sealed class GtaWindowSource
{
    private readonly LogBus _log;
    private readonly string _processName;
    private readonly string _windowClass;
    private readonly string _windowTitle;
    private IntPtr _hwnd;

    public string LastMethod { get; private set; } = "";
    public string? LastCaptureError { get; private set; }

    public GtaWindowSource(LogBus log, string processName, string windowClass, string windowTitle)
    {
        _log = log;
        _processName = processName;
        _windowClass = windowClass;
        _windowTitle = windowTitle;
    }

    public IntPtr Handle
    {
        get
        {
            if (_hwnd != IntPtr.Zero && Win32.IsWindow(_hwnd)) return _hwnd;
            _hwnd = Win32.FindWindowW(_windowClass, _windowTitle);
            return _hwnd;
        }
    }

    public bool IsWindowValid => Handle != IntPtr.Zero;

    public bool IsProcessRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName(
                System.IO.Path.GetFileNameWithoutExtension(_processName)).Length > 0;
        }
        catch { return false; }
    }


    public Frame CaptureClient()
    {
        var h = Handle;
        if (h == IntPtr.Zero) return Frame.Empty;
        if (!Win32.GetClientRect(h, out var client)) return Frame.Empty;
        int w = client.Right - client.Left, ht = client.Bottom - client.Top;
        if (w <= 0 || ht <= 0) return Frame.Empty;

        var pw = CaptureViaPrintWindow(h, w, ht);
        if (pw is not null)
        {
            LastMethod = "PrintWindow";
            return Stamped(pw);
        }

        var bb = CaptureViaScreenBlt(h, w, ht);
        if (bb is not null)
        {
            LastMethod = "ScreenBitBlt";
            return Stamped(bb);
        }
        LastMethod = "None";
        return Frame.Empty;
    }

    private long _seq;

    /// <summary>给这一帧打上序号：同一次抓帧的所有观察共用一份 OCR 结果。</summary>
    private Frame Stamped(Frame f)
    {
        f.Seq = Interlocked.Increment(ref _seq);
        return f;
    }

    /// <summary>PrintWindow 全内容渲染：即使被遮挡/不在前台也能拿到游戏自身画面。</summary>
    private Frame? CaptureViaPrintWindow(IntPtr hwnd, int w, int ht)
    {
        IntPtr hdcW = Win32.GetDC(hwnd);
        IntPtr mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            if (hdcW == IntPtr.Zero) return null;
            mem = Win32.CreateCompatibleDC(hdcW);
            bmp = Win32.CreateCompatibleBitmap(hdcW, w, ht);
            old = Win32.SelectObject(mem, bmp);
            bool ok = Win32.PrintWindow(hwnd, mem, Win32.PW_RENDERFULLCONTENT);
            if (!ok) return null;
            var bytes = ReadBitmapPixels(mem, bmp, w, ht);
            if (bytes is null) return null;
            return new Frame(w, ht, bytes);
        }
        catch (Exception e)
        {
            LastCaptureError = "PrintWindow: " + e.Message;
            return null;
        }
        finally
        {
            if (old != IntPtr.Zero) Win32.SelectObject(mem, old);
            if (bmp != IntPtr.Zero) Win32.DeleteObject(bmp);
            if (mem != IntPtr.Zero) Win32.DeleteDC(mem);
            if (hdcW != IntPtr.Zero) Win32.ReleaseDC(hwnd, hdcW);
        }
    }

    /// <summary>回退：从屏幕 DC 拷贝客户区（要求窗口可见且不被完全遮挡）。</summary>
    private Frame? CaptureViaScreenBlt(IntPtr hwnd, int w, int ht)
    {
        var origin = new Win32.POINT { X = 0, Y = 0 };
        Win32.ClientToScreen(hwnd, ref origin);
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        IntPtr mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            if (screenDc == IntPtr.Zero) return null;
            mem = Win32.CreateCompatibleDC(screenDc);
            bmp = Win32.CreateCompatibleBitmap(screenDc, w, ht);
            old = Win32.SelectObject(mem, bmp);
            bool ok = Win32.BitBlt(mem, 0, 0, w, ht, screenDc, origin.X, origin.Y, Win32.SRCCOPY);
            if (!ok) return null;
            var bytes = ReadBitmapPixels(mem, bmp, w, ht);
            if (bytes is null) return null;
            return new Frame(w, ht, bytes);
        }
        catch (Exception e)
        {
            LastCaptureError = "ScreenBitBlt: " + e.Message;
            return null;
        }
        finally
        {
            if (old != IntPtr.Zero) Win32.SelectObject(mem, old);
            if (bmp != IntPtr.Zero) Win32.DeleteObject(bmp);
            if (mem != IntPtr.Zero) Win32.DeleteDC(mem);
            if (screenDc != IntPtr.Zero) Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static byte[]? ReadBitmapPixels(IntPtr memDc, IntPtr bmp, int w, int ht)
    {
        try
        {
            int stride = w * 4;
            var bytes = new byte[stride * ht];
            var bmi = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -ht,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Win32.BI_RGB,
            };
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                int lines = Win32.GetDIBits(memDc, bmp, 0, (uint)ht, ptr, ref bmi, Win32.DIB_RGB_COLORS);
                if (lines <= 0) return null;
                Marshal.Copy(ptr, bytes, 0, bytes.Length);
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return bytes;
        }
        catch
        {
            return null;
        }
    }
}