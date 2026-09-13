namespace AutoPickup.Core.Input;

/// <summary>手柄按键抽象（与游戏内操作一一对应）。</summary>
public enum PadButton
{
    A, B, X, Y,
    DPadUp, DPadDown, DPadLeft, DPadRight,
    Start,        // 暂停菜单开关（游戏内 Start）
    Back,         // 互动菜单/长按（Select）
    LeftShoulder, // 向左切换标签页
    RightShoulder,// 向右切换标签页
}
