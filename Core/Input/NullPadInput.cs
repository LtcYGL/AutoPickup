using AutoPickup.Logging;

namespace AutoPickup.Core.Input;

/// <summary>未接入手柄驱动时的占位实现：只记录日志，不真正按键。</summary>
public sealed class NullPadInput : IInputLayer
{
    private readonly LogBus _log;
    public string Name => "NullPad(未安装驱动)";
    public bool IsAvailable => false;

    public NullPadInput(LogBus log) => _log = log;

    public void Tap(PadButton button, int holdMs = 150)
        => _log.Warn($"忽略按键 [{button}]：没有可用的输入层（需 ViGEmBus 或前台键鼠降级）", "Input");

    public void TapTimes(PadButton button, int times, int holdMs = 150, int gapMs = 150)
    {
        for (int i = 0; i < times; i++)
        {
            Tap(button, holdMs);
            if (gapMs > 0) Thread.Sleep(gapMs);
        }
    }

    public bool TryQuickLook(QuickLookDir dir, int wheelHoldMs, int lookHoldMs, double magnitude)
        => false;

    public void Reset() { }
    public void Dispose() { }
}
