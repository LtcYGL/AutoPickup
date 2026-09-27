using AutoPickup.Logging;

namespace AutoPickup.Core.Input;

/// <summary>未接入手柄驱动时的占位实现：只记录日志，不真正按键。</summary>
public sealed class NullPadInput : IInputLayer
{
    private readonly LogBus _log;
    public string Name => "NullPad(未安装驱动)";
    public bool IsAvailable => false;

    public NullPadInput(LogBus log) => _log = log;

    private bool _warned;

    public void Tap(PadButton button, int holdMs = 150)
    {
        // 只提示一次：不可用时每个按键都报会把日志淹没
        if (_warned) return;
        _warned = true;
        _log.Warn("没有可用的输入层，按键全部忽略。两条路选一条（改完都重启本程序）："
            + "① 装 ViGEmBus 驱动（官网 " + AutoPickup.Core.Native.ViGEmNative.DownloadUrl
            + "，自检页 [手柄驱动下载] 可直接打开）；"
            + "② 参数页「5 自动化 · 输入方式」改成 Keyboard（Esc/Q/E/方向键/Enter）", "Input");
    }

    public bool TryQuickLook(QuickLookDir dir, int wheelHoldMs, int lookHoldMs, double magnitude)
        => false;

    public void Dispose() { }
}
