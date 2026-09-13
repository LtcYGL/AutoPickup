using System.Runtime.InteropServices;
using System.Text;

namespace AutoPickup.Core.Native;

/// <summary>
/// 命令行工具（GUI 子系统 exe）在 PowerShell/cmd 下输出：
/// 挂接父控制台后，把 stdout 重定向到控制台句柄并以 UTF-8 流写入，
/// 避免 WriteConsoleW 的 UTF-16 与控制台管道/捕获双重编码导致的乱码。
/// </summary>
public static class NativeConsole
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
    private const int STD_OUTPUT_HANDLE = -11;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    private static bool _attempted;
    private static bool _ready;
    private static StreamWriter? _writer;

    private static bool Ensure()
    {
        if (_attempted) return _ready;
        _attempted = true;
        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) return false;
            IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
            var fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(h, false), FileAccess.Write);
            _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(_writer);
            try { Console.SetError(_writer); } catch { }
            _ready = true;
        }
        catch { _ready = false; }
        return _ready;
    }

    public static void Ln(string text)
    {
        if (Ensure())
        {
            try { _writer!.WriteLine(text); _writer.Flush(); return; }
            catch { }
        }
        try { Console.WriteLine(text); } catch { }
    }
}
