using System.Runtime.InteropServices;

namespace AutoPickup.Core.Native;

/// <summary>
/// ViGEmClient.dll 原生封装（P/Invoke）。驱动 = ViGEmBus（内核驱动，需单独安装）。
/// 本工具只作为普通客户端使用公开 API，无任何注入。
/// </summary>
public static class ViGEmNative
{
    public const uint VIGEM_ERROR_OK = 0x20000000;

    // XUSB 按键位
    public const ushort DPAD_UP = 0x0001;
    public const ushort DPAD_DOWN = 0x0002;
    public const ushort DPAD_LEFT = 0x0004;
    public const ushort DPAD_RIGHT = 0x0008;
    public const ushort START = 0x0010;
    public const ushort BACK = 0x0020;
    public const ushort LEFT_SHOULDER = 0x0100;
    public const ushort RIGHT_SHOULDER = 0x0200;
    public const ushort A = 0x1000;
    public const ushort B = 0x2000;
    public const ushort X = 0x4000;
    public const ushort Y = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct XusbReport
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("ViGEmClient.dll")]
    public static extern IntPtr vigem_alloc();

    [DllImport("ViGEmClient.dll")]
    public static extern uint vigem_connect(IntPtr client);

    [DllImport("ViGEmClient.dll")]
    public static extern void vigem_free(IntPtr client);

    [DllImport("ViGEmClient.dll")]
    public static extern uint vigem_target_add(IntPtr client, IntPtr target);

    [DllImport("ViGEmClient.dll")]
    public static extern uint vigem_target_remove(IntPtr client, IntPtr target);

    [DllImport("ViGEmClient.dll")]
    public static extern IntPtr vigem_target_x360_alloc();

    [DllImport("ViGEmClient.dll")]
    public static extern void vigem_target_free(IntPtr target);

    [DllImport("ViGEmClient.dll")]
    public static extern uint vigem_target_x360_update(IntPtr client, IntPtr target, ref XusbReport report);

    private static bool _loadAttempted;

    /// <summary>确保原生 DLL 已加载（输出目录自带一份；发布单文件时会先解压到临时目录再加载）。</summary>
    public static void EnsureDll()
    {
        if (_loadAttempted) return;
        _loadAttempted = true;
        try
        {
            string[] candidates =
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "ViGEmClient.dll"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "native", "ViGEmClient.dll"),
            };
            foreach (var c in candidates)
            {
                if (System.IO.File.Exists(c)) { LoadLibraryW(c); break; }
            }
        }
        catch { }
    }
}

/// <summary>Xbox360 虚拟手柄最小封装。</summary>
public sealed class ViGEmX360Pad : IDisposable
{
    private IntPtr _client;
    private IntPtr _target;
    public bool IsReady { get; private set; }
    public string? LastError { get; private set; }

    public ViGEmX360Pad() => Init();

    private void Init()
    {
        ViGEmNative.EnsureDll();
        try
        {
            _client = ViGEmNative.vigem_alloc();
            if (_client == IntPtr.Zero) { LastError = "vigem_alloc 失败"; return; }
            uint err = ViGEmNative.vigem_connect(_client);
            if (err != ViGEmNative.VIGEM_ERROR_OK)
            {
                LastError = "vigem_connect 失败 0x" + err.ToString("X8");
                Cleanup();
                return;
            }
            _target = ViGEmNative.vigem_target_x360_alloc();
            if (_target == IntPtr.Zero) { LastError = "target 分配失败"; Cleanup(); return; }
            err = ViGEmNative.vigem_target_add(_client, _target);
            if (err != ViGEmNative.VIGEM_ERROR_OK)
            {
                LastError = "vigem_target_add 失败 0x" + err.ToString("X8");
                Cleanup();
                return;
            }
            IsReady = true;
        }
        catch (Exception e)
        {
            LastError = "ViGEm 初始化异常: " + e.Message;
            Cleanup();
        }
    }

    /// <summary>尝试断开并重建连接（驱动/总线重枚举后自愈）。</summary>
    public bool TryReconnect()
    {
        Cleanup();
        Init();
        return IsReady;
    }

    public bool Update(ushort buttons)
        => Update(buttons, 0, 0);

    /// <summary>带右摇杆轴的更新（快捷切换轮盘方向）。左右摇杆 X / 扳机暂不暴露，需要时再扩。</summary>
    public bool Update(ushort buttons, short thumbRX, short thumbRY)
    {
        if (!IsReady) return false;
        var rep = new ViGEmNative.XusbReport
        {
            wButtons = buttons,
            sThumbRX = thumbRX,
            sThumbRY = thumbRY,
        };
        return ViGEmNative.vigem_target_x360_update(_client, _target, ref rep) == ViGEmNative.VIGEM_ERROR_OK;
    }

    private void Cleanup()
    {
        if (_target != IntPtr.Zero)
        {
            if (_client != IntPtr.Zero) ViGEmNative.vigem_target_remove(_client, _target);
            ViGEmNative.vigem_target_free(_target);
            _target = IntPtr.Zero;
        }
        if (_client != IntPtr.Zero)
        {
            ViGEmNative.vigem_free(_client);
            _client = IntPtr.Zero;
        }
        IsReady = false;
    }

    public void Dispose() => Cleanup();
}