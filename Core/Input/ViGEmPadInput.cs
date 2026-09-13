using AutoPickup.Core.Native;
using AutoPickup.Logging;

namespace AutoPickup.Core.Input;

/// <summary>基于原生 ViGEmClient 的虚拟 Xbox360 手柄：游戏轮询 XInput，窗口失焦也能后台按键。</summary>
public sealed class ViGEmPadInput : IInputLayer
{
    private readonly LogBus _log;
    private readonly ViGEmX360Pad? _pad;
    private readonly object _lock = new();
    private ushort _mask;
    private int _consecFails;
    private int _reconnects;
    public int Failures => _consecFails;
    public int Reconnects => _reconnects;

    public string Name => "ViGEm 手柄";
    public bool IsAvailable => _pad is { IsReady: true };

    public ViGEmPadInput(LogBus log)
    {
        _log = log;
        _pad = new ViGEmX360Pad();
        if (_pad.IsReady) _log.Okay("ViGEm 虚拟手柄已连接", "Input");
        else _log.Warn("ViGEm 不可用: " + (_pad.LastError ?? "未知"), "Input");
    }

    private static ushort ToMask(PadButton b) => b switch
    {
        PadButton.A => ViGEmNative.A,
        PadButton.B => ViGEmNative.B,
        PadButton.X => ViGEmNative.X,
        PadButton.Y => ViGEmNative.Y,
        PadButton.DPadUp => ViGEmNative.DPAD_UP,
        PadButton.DPadDown => ViGEmNative.DPAD_DOWN,
        PadButton.DPadLeft => ViGEmNative.DPAD_LEFT,
        PadButton.DPadRight => ViGEmNative.DPAD_RIGHT,
        PadButton.Start => ViGEmNative.START,
        PadButton.Back => ViGEmNative.BACK,
        PadButton.LeftShoulder => ViGEmNative.LEFT_SHOULDER,
        PadButton.RightShoulder => ViGEmNative.RIGHT_SHOULDER,
        _ => 0,
    };

    private void Send()
    {
        if (_pad is null) return;
        bool ok = false;
        try { ok = _pad.Update(_mask); } catch { ok = false; }
        if (ok) { _consecFails = 0; return; }

        // 更新失败（设备被重新枚举/断开）→ 尝试自愈重连一次
        _consecFails++;
        _log.Warn("ViGEm 上报失败（第 " + _consecFails + " 次，疑似被 Steam/DS4Windows 重枚举），尝试重连…", "Input");
        try
        {
            if (_pad.TryReconnect())
            {
                _reconnects++;
                _consecFails = 0;
                _log.Okay("ViGEm 已重连（累计 " + _reconnects + " 次）", "Input");
                try { _pad.Update(_mask); } catch { }
            }
            else if (_consecFails >= 3)
            {
                _log.Error("ViGEm 重连失败且无法恢复——请检查是否被 Steam/DS4Windows 抢占或驱动异常；后续按键将失效", "Input");
            }
        }
        catch (Exception e)
        {
            _log.Warn("ViGEm 重连异常: " + e.Message, "Input");
        }
    }

    public void Tap(PadButton button, int holdMs = 150)
    {
        if (!IsAvailable) { _log.Warn("忽略按键 " + button + "（ViGEm 不可用）", "Input"); return; }
        ushort bit = ToMask(button);
        if (bit == 0) return;
        lock (_lock)
        {
            _mask |= bit;
            Send();
            Thread.Sleep(Math.Max(20, holdMs));
            _mask &= (ushort)~bit;
            Send();
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

    public bool TryQuickLook(QuickLookDir dir, int wheelHoldMs, int lookHoldMs, double magnitude)
    {
        if (!IsAvailable) return false;
        ushort wheel = ToMask(PadButton.DPadDown); // GTAV 快捷轮盘：按住 下方向键
        lock (_lock)
        {
            _mask |= wheel;
            Send();                                   // 按住 下方向键
            Thread.Sleep(Math.Max(40, wheelHoldMs));  // 等轮盘出现
            short ry = (short)Math.Round(Math.Clamp(magnitude, 0.0, 1.0) * short.MaxValue);
            if (dir == QuickLookDir.Down) ry = (short)-ry;
            _pad?.Update(_mask, 0, ry);               // 右摇杆推向上/下目标
            Thread.Sleep(Math.Max(40, lookHoldMs));
            ushort release = (ushort)(_mask & ~wheel);
            _pad?.Update(release, 0, 0);              // 摇杆归中 + 松开 下方向键（同帧同时松）
            _mask = release;
            Send();
            _mask = 0;
            Thread.Sleep(40);
            _log.Info("快捷手势完成：按住↓ " + wheelHoldMs + "ms + 右摇杆" + (dir == QuickLookDir.Up ? "上" : "下")
                + " " + lookHoldMs + "ms（同帧松开）", "Input");
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _mask = 0;
            Send();
        }
    }

    public void Dispose() => _pad?.Dispose();
}