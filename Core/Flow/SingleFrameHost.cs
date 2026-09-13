using AutoPickup.Core.Capture;

namespace AutoPickup.Core.Flow;

/// <summary>只提供固定一帧的最小宿主（用于 --obs-check / 单帧判据验收）。动作无效果。</summary>
public sealed class SingleFrameHost : IFlowHost
{
    private Frame _frame;
    public SingleFrameHost(Frame frame) => _frame = frame;

    public string Name => "单帧";
    public bool IsReplay => true;
    public Frame Capture() => _frame;
    public void Tap(string button) { }
    public void Gesture(string dir) { }
    public void SleepMs(int ms) { }
    public bool Firewall(bool enable) => true;
    public bool WaitCloudCue(int timeoutSec) => true;
}
