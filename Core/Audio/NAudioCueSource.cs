using AutoPickup.Config;
using AutoPickup.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AutoPickup.Core.Audio;

/// <summary>
/// “下云”音频源：WASAPI 回环捕获 → RMS 音量（免驱动，等价原项目录 CABLE 思路）。
///
/// 关键坑（2026-09-12 用户实测 avg=0.0% 定位）：
/// 1) 默认构造 <c>new WasapiLoopbackCapture()</c> 只抓「本进程启动那一刻的系统默认输出设备」，
///    之后用户换默认设备 / 给游戏单独指定输出 / 用 Steam 串流或虚拟声卡，它都不会跟随 →
///    抓到的是那台“已经不出声”的设备，全程 0，静音基线永远是 0.0%。
/// 2) 因此这里支持用 <see cref="AppSettings.AudioSection.CaptureDevice"/> 按名字锁定设备，
///    并在启动时打印候选清单与最终选择，便于核对。
/// 3) WASAPI 回环在“完全无声”时**不给回调**，所以 OnData 不触发 ≠ 采集坏了；
///    但也正因如此，长时间完全没回调就是异常信号，这里会告警。
/// </summary>
public sealed class NAudioCueSource : IAudioCueSource
{
    private readonly LogBus _log;
    private readonly AppSettings.AudioSection _a;
    private WasapiLoopbackCapture? _cap;
    private volatile float _peak;
    private long _lastDataTs;
    private long _startTs;
    private System.Threading.Timer? _watch;

    public string Name { get; private set; } = "回环RMS(默认输出)";
    public bool IsAvailable { get; private set; }
    public float CurrentPeak => _peak;

    /// <summary>当前捕获设备名（诊断/状态灯用）。</summary>
    public string DeviceName { get; private set; } = "";

    public NAudioCueSource(LogBus log, AppSettings.AudioSection audio)
    {
        _log = log;
        _a = audio;
        try
        {
            var dev = PickDevice(out string candidates);
            if (!string.IsNullOrEmpty(candidates)) _log.Info("可用的回环输出设备：" + candidates, "Audio");
            _cap = dev is null ? new WasapiLoopbackCapture() : new WasapiLoopbackCapture(dev);
            DeviceName = dev?.FriendlyName ?? "(系统默认)";
            _cap.DataAvailable += OnData;
            IsAvailable = true;
            _log.Okay("NAudio 回环捕获就绪 → " + DeviceName + "（格式 " + _cap.WaveFormat + "）", "Audio");
        }
        catch (Exception e)
        {
            _log.Warn("回环捕获不可用: " + e.Message, "Audio");
        }
    }

    /// <summary>按配置名（模糊匹配）挑设备；没配就用系统默认（dev=null 时由默认构造处理）。</summary>
    private MMDevice? PickDevice(out string candidates)
    {
        candidates = "";
        try
        {
            using var en = new MMDeviceEnumerator();
            var all = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            candidates = string.Join(" / ", all.Select((d, i) => "#" + i + " " + d.FriendlyName));
            string want = (_a.CaptureDevice ?? "").Trim();
            if (want.Length == 0)
            {
                // 没显式配置：仍用系统默认，但把清单打出来供选择
                return null;
            }
            // 支持 "#3" 或 "#3 名字..."（取 # 后到第一个空格之间的数字当序号）
            if (want.StartsWith("#"))
            {
                int sp = want.IndexOf(' ');
                string numPart = sp > 0 ? want.Substring(1, sp - 1) : want.Substring(1);
                if (int.TryParse(numPart, out int idx) && idx >= 0 && idx < all.Count)
                    return all[idx];
            }
            // 名字匹配：配置里常写成 "#3 CABLE In" 这种“序号+名字”，而设备名是 "CABLE In 16 Ch (…)";
            // 直接整串 Contains 会失败，所以按词数从多到少逐步缩短，取第一个命中的片段。
            string baseNeedle = want.StartsWith("#") ? want.Substring(1).Trim() : want;
            var wordsN = baseNeedle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int take = wordsN.Length; take >= 1; take--)
            {
                string needle = string.Join(" ", wordsN.Take(take));
                if (needle.Length < 3) break;
                var hit = all.FirstOrDefault(d => d.FriendlyName.Contains(needle, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) return hit;
            }
            _log.Warn("配置的音频设备未找到：'" + want + "'，回退系统默认（用 --audio-devices 看清单）", "Audio");
            return null;
        }
        catch (Exception e)
        {
            _log.Warn("枚举音频设备失败: " + e.Message, "Audio");
            return null;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        _lastDataTs = Environment.TickCount64;
        int bps = Math.Max(2, (_cap?.WaveFormat?.BitsPerSample ?? 32) / 8);   // 每样本字节
        if (bps != 4 && bps != 2) bps = 4;
        int n = e.BytesRecorded / bps;
        if (n <= 0) return;
        double sum = 0;
        if (bps == 4)
        {
            for (int i = 0; i + 3 < e.BytesRecorded; i += 4)
            {
                float v = BitConverter.ToSingle(e.Buffer, i);
                sum += (double)v * v;
            }
        }
        else
        {
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                float v = BitConverter.ToInt16(e.Buffer, i) / 32768f;
                sum += (double)v * v;
            }
        }
        float rms = (float)Math.Sqrt(sum / n);
        // 标度：RMS 远小于峰值，放大到与阈值(12%/40%)可比
        _peak = Math.Clamp(rms * 8f, 0f, 1f);
    }

    public bool Start()
    {
        if (_cap is null) return false;
        try
        {
            _cap.StartRecording();
            _startTs = Environment.TickCount64;
            _lastDataTs = _startTs;
            _log.Okay("音频回环采集已开始 → " + DeviceName, "Audio");
            // 看门狗：启动后 6s 仍无任何回调，通常说明选错了设备（完全不发声的设备不给回调）
            _watch = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (Environment.TickCount64 - _lastDataTs > 6000)
                        _log.Warn("音频回环 6 秒无任何数据 → 可能选错了输出设备（当前：" + DeviceName
                            + "）；请在参数页“音频·捕获设备”里换一个，或用 --audio-devices 查看清单", "Audio");
                }
                catch { }
            }, null, 7000, 10000);
            return true;
        }
        catch (Exception e)
        {
            _log.Warn("回环启动失败: " + e.Message, "Audio");
            return false;
        }
    }

    public void Stop()
    {
        try { _watch?.Dispose(); _watch = null; } catch { }
        try { _cap?.StopRecording(); } catch { }
    }

    public void Dispose() => Stop();

    /// <summary>列出所有“活动的输出设备”，供 --audio-devices / 参数页选择。</summary>
    public static List<(int Index, string Name, bool IsDefault)> ListDevices()
    {
        var list = new List<(int, string, bool)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string defId = "";
            try { defId = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
            var all = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            int i = 0;
            foreach (var d in all)
            {
                list.Add((i, d.FriendlyName, d.ID == defId));
                i++;
            }
        }
        catch { }
        return list;
    }
}
