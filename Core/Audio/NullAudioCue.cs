namespace AutoPickup.Core.Audio;

/// <summary>音频 cue 不可用时的占位实现：把“为什么不可用”带进状态灯与日志。
/// （以前一律写“尚未接入（占位）”，让人误以为这功能没做。）</summary>
public sealed class NullAudioCue : IAudioCueSource
{
    private readonly string _reason;

    public NullAudioCue(string reason) => _reason = reason;

    public string Name => "不可用：" + _reason;
    public bool IsAvailable => false;
    public float CurrentPeak => 0f;

    public bool Start() => false;
    public void Stop() { }
}
