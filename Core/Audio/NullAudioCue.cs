using AutoPickup.Logging;

namespace AutoPickup.Core.Audio;

/// <summary>占位实现：音频 cue 模块将在后续迭代接入 WASAPI。</summary>
public sealed class NullAudioCue : IAudioCueSource
{
    private readonly LogBus _log;
    public string Name => "NullAudio(未接入 WASAPI)";
    public bool IsAvailable => false;
    public float CurrentPeak => 0f;

    public NullAudioCue(LogBus log) => _log = log;

    public bool Start()
    {
        _log.Warn("音频 cue 尚未接入（占位）", "Audio");
        return false;
    }

    public void Stop() { }
}
