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

    public bool IsForeground => Handle != IntPtr.Zero && Win32.GetForegroundWindow() == Handle;

    public bool IsProcessRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName(
                System.IO.Path.GetFileNameWithoutExtension(_processName)).Length > 0;
        }
        catch { return false; }
    }

    /// <summary>把窗口恢复到前台（游戏失焦可能忽略手柄输入/自动暂停）。</summary>
    private const uint WM_ACTIVATE = 0x0006, WM_NCACTIVATE = 0x0086, WM_SETFOCUS = 0x0007;
    private const int WA_ACTIVE = 1;

    /// <summary>“假激活”：向游戏窗口投递 WM_ACTIVATE / WM_NCACTIVATE / WM_SETFOCUS，让游戏以为处于激活状态，
    /// 但**不改变真实前台**（后台模式用，避免打断用户正在用的程序）。窗口需可见；最小化时无效。</summary>
    public bool FakeActivate()
    {
        var h = Handle;
        if (h == IntPtr.Zero) return false;
        Win32.PostMessageW(h, WM_ACTIVATE, (IntPtr)WA_ACTIVE, h);
        Win32.PostMessageW(h, WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero);
        Win32.PostMessageW(h, WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    public bool BringToFront()
    {
        var h = Handle;
        if (h == IntPtr.Zero) return false;
        if (Win32.IsIconic(h)) Win32.ShowWindow(h, Win32.SW_RESTORE);
        if (Win32.GetForegroundWindow() == h) return true;
        Win32.SetForegroundWindow(h);
        var fg = Win32.GetForegroundWindow();
        if (fg != h)
        {
            var fgThread = Win32.GetWindowThreadProcessId(fg, out _);
            var curThread = Win32.GetCurrentThreadId();
            if (fgThread != curThread)
            {
                Win32.AttachThreadInput(curThread, fgThread, true);
                Win32.SetForegroundWindow(h);
                Win32.AttachThreadInput(curThread, fgThread, false);
            }
        }
        return Win32.GetForegroundWindow() == h;
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
            return pw;
        }

        var bb = CaptureViaScreenBlt(h, w, ht);
        if (bb is not null)
        {
            LastMethod = "ScreenBitBlt";
            return bb;
        }
        LastMethod = "None";
        return Frame.Empty;
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