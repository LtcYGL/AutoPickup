namespace AutoPickup.Core.Input;

/// <summary>输入层：后台可用优先用虚拟手柄（游戏轮询 XInput 与焦点无关）；SendInput 为前台降级。</summary>
public interface IInputLayer : IDisposable
{
    string Name { get; }
    bool IsAvailable { get; }

    /// <summary>点按（按下 holdMs 后松开）。</summary>
    void Tap(PadButton button, int holdMs = 150);

    /// <summary>多次点按，间隔默认与 holdMs 相同。</summary>
    void TapTimes(PadButton button, int times, int holdMs = 150, int gapMs = 150);

    /// <summary>彻底复位（松开一切）。</summary>
    void Reset();

    /// <summary>GTAV 快捷切换手势：按住“下方向键/Alt”打开角色/模式轮盘，同时把右摇杆/鼠标推向 dir 方向，
    /// 持续 lookHoldMs 后与按键同帧松开（触发 线上/故事 快速切换，随后游戏弹确认框）。
    /// 不支持该手势的输入层返回 false，由调用方回退传统暂停菜单流程。</summary>
    bool TryQuickLook(QuickLookDir dir, int wheelHoldMs, int lookHoldMs, double magnitude);
}

/// <summary>快捷轮盘推向的物理方向（Up=上、Down=下）。目标语义实机定稿：上=回故事(线下)、下=进线上；
/// 由 NetmodeMachine 按 AppSettings.QuickSwitch.OnlineIsUp=false 解析为 进线上=Down / 回故事=Up。</summary>
public enum QuickLookDir { Up, Down }
