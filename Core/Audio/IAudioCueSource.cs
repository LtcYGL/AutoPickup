namespace AutoPickup.Core.Audio;

/// <summary>
/// “进场音频/下云声”cue 源：读取 GTA5_Enhanced.exe 音频会话的实时峰值（0..1）。
/// 实现（P1.5）：WASAPI IAudioMeterInformation（进程会话级，无需 VBCABLE）。
/// </summary>
public interface IAudioCueSource
{
    string Name { get; }
    bool IsAvailable { get; }

    /// <summary>启动采样线程（幂等）。</summary>
    bool Start();

    void Stop();

    /// <summary>最近一次峰值（0..1），无效返回 0。</summary>
    float CurrentPeak { get; }
}
