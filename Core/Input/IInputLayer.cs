namespace AutoPickup.Core.Input;

/// <summary>输入层：优先虚拟手柄（游戏轮询 XInput，与窗口焦点无关）；键盘层（keybd_event）为备选。</summary>
public interface IInputLayer : IDisposable
{
    string Name { get; }
    bool IsAvailable { get; }

    /// <summary>点按（按下 holdMs 后松开）。</summary>
    void Tap(PadButton button, int holdMs = 150);

    /// <summary>多次点按，间隔默认与 holdMs 相同。</summary>
    // TapTimes 已删除：从无调用点（引擎的 tap 原子自己循环按）

    /// <summary>GTAV 快捷切换手势：按住“下方向键/Alt”打开角色/模式轮盘，同时把右摇杆/鼠标推向 dir 方向，
    /// 持续 lookHoldMs 后与按键同帧松开（触发 线上/故事 快速切换，随后游戏弹确认框）。
    /// 不支持该手势的输入层返回 false，由调用方回退传统暂停菜单流程。</summary>
    bool TryQuickLook(QuickLookDir dir, int wheelHoldMs, int lookHoldMs, double magnitude);
}

/// <summary>快捷轮盘推向的物理方向（Up=上、Down=下）。语义：上=回故事(线下)、下=进线上（与流程 JSON 的 gesture dir 一致）。</summary>
public enum QuickLookDir { Up, Down }
