namespace AutoPickup.Core.Audio;

/// <summary>
/// “下云”cue 的音频源：实时给出 0..1 的音量峰值。
/// 现实现 = NAudio WASAPI **回环捕获指定输出设备**（免驱动，等价原项目录 CABLE 思路）；
/// 设备在程序启动那刻绑定（见参数页「3 音频 · 捕获设备」），不可用时退化为 NullAudioCue。
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
