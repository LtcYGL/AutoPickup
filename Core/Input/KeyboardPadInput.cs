using System.Runtime.InteropServices;
using AutoPickup.Logging;

namespace AutoPickup.Core.Input;

/// <summary>
/// 键盘输入层（PC 键位）：Esc=暂停开合、Q/E=切 tab、方向键=列表移动、Enter=确认/进入。
/// 与手柄语义一一对应（PadButton→虚拟键），供状态机无差别使用。
/// </summary>
public sealed class KeyboardPadInput : IInputLayer
{
    private readonly LogBus _log;

    public string Name => "键盘(Q/E/Enter/方向键)";
    public bool IsAvailable => true;

    private const byte VK_ESCAPE = 0x1B, VK_RETURN = 0x0D, VK_UP = 0x26, VK_DOWN = 0x28,
        VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_Q = 0x51, VK_E = 0x45, VK_BACK = 0x08, VK_MENU = 0x12;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MOUSEEVENTF_MOVE = 0x0001;

    public KeyboardPadInput(LogBus log) => _log = log;

    private static byte ToVk(PadButton button) => button switch
    {
        PadButton.A => VK_RETURN,
        PadButton.B => VK_ESCAPE,
        PadButton.Start => VK_ESCAPE,
        PadButton.DPadUp => VK_UP,
        PadButton.DPadDown => VK_DOWN,
        PadButton.DPadLeft => VK_LEFT,
        PadButton.DPadRight => VK_RIGHT,
        PadButton.LeftShoulder => VK_Q,
        PadButton.RightShoulder => VK_E,
        _ => 0,
    };

    public void Tap(PadButton button, int holdMs = 150)
    {
        byte vk = ToVk(button);
        if (vk == 0) return;
        try
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            Thread.Sleep(Math.Max(20, holdMs));
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch (Exception e)
        {
            _log.Warn("键盘按键失败 " + button + " : " + e.Message, "Input");
        }
    }

    public void TapTimes(PadButton button, int times, int holdMs = 150, int gapMs = 150)
    {
        for (int i = 0; i < times; i++)
        {
            Tap(button, holdMs);
            if (gapMs > 0) Thread.Sleep(gapMs);
        }
    }

    /// <summary>PC 快捷切换：按住 Alt 打开角色/模式轮盘，鼠标向 dir 方向甩动后松开 Alt（同刻松）。</summary>
    public bool TryQuickLook(QuickLookDir dir, int wheelHoldMs, int lookHoldMs, double magnitude)
    {
        if (magnitude <= 0.01) return false;
        try
        {
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);   // 按住 Alt（轮盘）
            Thread.Sleep(Math.Max(40, wheelHoldMs));
            int sign = dir == QuickLookDir.Up ? -1 : 1; // 鼠标坐标 y 向下为正，向上为负
            int per = Math.Max(4, (int)Math.Round(80.0 * Math.Clamp(magnitude, 0.1, 1.0)));
            int span = Math.Max(2, lookHoldMs);
            int n = Math.Max(1, span / 25);
            for (int i = 0; i < n; i++)
            {
                mouse_event(MOUSEEVENTF_MOVE, 0, sign * per, 0, UIntPtr.Zero);
                Thread.Sleep(25);
            }
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            _log.Info("快捷手势(键盘)完成：Alt 按住 + 鼠标" + (dir == QuickLookDir.Up ? "上" : "下")
                + " 约" + (sign * per * n) + " 相对单位后松开", "Input");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("快捷手势(键盘)失败: " + ex.Message, "Input");
            return false;
        }
    }

    public void Reset() { }
    public void Dispose() { }
}
