using System.Text.Json;
using AutoPickup.Config;
using AutoPickup.Core.Capture;
using AutoPickup.Core.Input;
using AutoPickup.Core.Net;
using AutoPickup.Core.Vision;
using AutoPickup.Logging;

namespace AutoPickup.Core.Flow;

/// <summary>实机宿主：动作真的发给游戏；观察读真实窗口。</summary>
public sealed class LiveFlowHost : IFlowHost
{
    private readonly GtaWindowSource _window;
    private readonly IInputLayer _input;
    private readonly FirewallController _fw;
    private readonly AutoPickup.Core.Audio.IAudioCueSource? _audio;
    private readonly AppSettings _settings;
    private readonly LogBus _log;

    public string Name { get; }
    public bool IsReplay => false;
    /// <summary>影子模式：只观察、不按键、不动防火墙。用于与旧 FSM 并存时对照证据，零风险。</summary>
    public bool Passive { get; set; }

    public LiveFlowHost(GtaWindowSource window, IInputLayer input, FirewallController fw,
        AppSettings settings, LogBus log, bool passive = false,
        AutoPickup.Core.Audio.IAudioCueSource? audio = null)
    {
        _window = window; _input = input; _fw = fw; _settings = settings; _log = log; _audio = audio;
        Passive = passive;
        Name = passive ? "实机(影子)" : "实机";
    }

    public Frame Capture() => _window.CaptureClient();

    public void Tap(string button)
    {
        if (Passive) { _log.Info("[影子] 跳过按键 " + button, "Flow"); return; }
        if (Enum.TryParse<PadButton>(button, true, out var b)) _input.Tap(b, _settings.Automation.PressMs);
        else _log.Warn("未知按键原语: " + button, "Flow");
    }

    public void Gesture(string dir)
    {
        if (Passive) { _log.Info("[影子] 跳过手势 " + dir, "Flow"); return; }
        var d = dir.Equals("down", StringComparison.OrdinalIgnoreCase) ? QuickLookDir.Down : QuickLookDir.Up;
        bool ok = _input.TryQuickLook(d, _settings.QuickSwitch.WheelOpenMs, _settings.QuickSwitch.LookHoldMs, _settings.QuickSwitch.StickMagnitude);
        if (!ok) _log.Warn("输入层不支持轮盘手势", "Flow");
    }

    public void SleepMs(int ms) { if (ms > 0) Thread.Sleep(ms); }

    public bool Firewall(bool enable)
    {
        if (Passive) { _log.Info("[影子] 跳过防火墙 " + (enable ? "封网" : "恢复"), "Flow"); return true; }
        return enable ? _fw.Enable() : _fw.Disable();
    }

    public bool WaitCloudCue(int timeoutSec)
    {
        if (Passive) { _log.Info("[影子] 跳过等待下云 cue", "Flow"); return true; }
        if (_audio is null || !_audio.IsAvailable)
        {
            _log.Warn("音频 cue 不可用（无 GTA 音频会话）", "Flow");
            return false;
        }
        _log.Info("等待“下云”声音 cue（安静→响亮，最长 " + timeoutSec + "s）…", "Flow");
        var det = new AutoPickup.Core.Audio.CloudCueDetector(_log, _settings.Audio);
        det.Begin();
        int step = Math.Max(20, _settings.Audio.SampleIntervalMs);   // 采样间隔（参数页可调）
        int waited = 0, total = timeoutSec * 1000;
        while (waited < total)
        {
            if (det.Feed(_audio.CurrentPeak)) return true;
            Thread.Sleep(step);
            waited += step;
        }
        _log.Warn("等待下云声音超时(" + timeoutSec + "s)", "Flow");
        return false;
    }
}

/// <summary>回放脚本里的一帧：图片路径（相对 manifest） + 触发它前进的动作键。</summary>
public sealed class ReplayFrame
{
    public string Path { get; set; } = "";
    /// <summary>该帧停留多久（毫秒）后**按时间轴自动**推进到下一帧；0/不填=不自动推进。</summary>
    public int DwellMs { get; set; }
    /// <summary>动作键 → 下一帧下标。键形如 "tap:A"、"gesture:down"、"tap:Start"。</summary>
    public Dictionary<string, int> On { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ReplayScript
{
    public string Name { get; set; } = "";
    public List<ReplayFrame> Frames { get; set; } = new();
}

/// <summary>
/// 回放宿主：从磁盘图片按脚本“演”出游戏状态。动作（tap/gesture）按脚本推进到下一帧，
/// 观察不推进（因为观察必须纯）。因此整条流程可以在不碰游戏的情况下被完整执行与断言。
/// </summary>
public sealed class ReplayFlowHost : IFlowHost
{
    private readonly List<(ReplayFrame Frame, Frame Image)> _frames = new();
    private readonly bool _quiet;
    private int _cur;
    private long _virtualMs;

    public string Name { get; }
    public bool IsReplay => true;
    public int CurrentIndex => _cur;
    public string CurrentPath => _cur >= 0 && _cur < _frames.Count ? _frames[_cur].Frame.Path : "(none)";

    public ReplayFlowHost(string scriptPath, bool quiet = false)
    {
        _quiet = quiet;
        var json = File.ReadAllText(scriptPath);
        var script = JsonSerializer.Deserialize<ReplayScript>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("脚本解析失败");
        string dir = Path.GetDirectoryName(Path.GetFullPath(scriptPath)) ?? ".";
        Name = "回放:" + (string.IsNullOrEmpty(script.Name) ? Path.GetFileNameWithoutExtension(scriptPath) : script.Name);
        foreach (var f in script.Frames)
        {
            var img = ImagingIo.LoadImage(Path.Combine(dir, f.Path));
            if (img is null) throw new FileNotFoundException("回放帧缺失: " + f.Path + "（" + dir + "）");
            _frames.Add((f, img));
        }
        if (_frames.Count == 0) throw new InvalidOperationException("回放脚本没有帧");
    }

    public Frame Capture() => _frames[Math.Clamp(_cur, 0, _frames.Count - 1)].Image;

    private void Advance(string key)
    {
        var f = _frames[Math.Clamp(_cur, 0, _frames.Count - 1)].Frame;
        if (f.On.TryGetValue(key, out int next) && next >= 0 && next < _frames.Count)
        {
            _cur = next;
            if (!_quiet) Console.WriteLine("  [回放] " + key + " → 帧#" + _cur + " " + _frames[_cur].Frame.Path);
        }
        else if (!_quiet) Console.WriteLine("  [回放] " + key + "（脚本未定义转移，停留在帧#" + _cur + "）");
    }

    public void Tap(string button) => Advance("tap:" + button);
    public void Gesture(string dir) => Advance("gesture:" + dir.ToLowerInvariant());

    /// <summary>
    /// 回放的时间轴：sleep 推进**虚拟时间**（不真等）。若当前帧设了 DwellMs，虚拟时间越过它
    /// 就自动前进到下一帧——这样“加载中”这种画面可以自己走完，流程里的等待也能如实表达。
    /// </summary>
    public void SleepMs(int ms)
    {
        if (ms > 0) _virtualMs += ms;
        StepTimeline();
        if (ms > 0) Thread.Sleep(Math.Min(ms, 30));   // 只留极小间隔，避免空转
    }

    /// <summary>回放没有音频：脚本可用 "cue" 定义转移到下一帧；未定义则视为命中（便于跑流程结构）。</summary>
    public bool WaitCloudCue(int timeoutSec) { Advance("cue"); return true; }

    /// <summary>按虚拟时间把帧往前推进（可跨多帧）。</summary>
    private void StepTimeline()
    {
        while (_cur >= 0 && _cur < _frames.Count)
        {
            var f = _frames[_cur].Frame;
            if (f.DwellMs <= 0 || _virtualMs < f.DwellMs) break;
            if (_cur + 1 >= _frames.Count) break;
            _virtualMs -= f.DwellMs;
            _cur++;
            if (!_quiet) Console.WriteLine("  [回放] 时间轴 → 帧#" + _cur + " " + _frames[_cur].Frame.Path);
        }
    }

    /// <summary>当前虚拟时间（毫秒），供日志/断言。</summary>
    public long VirtualMs => _virtualMs;
    public bool Firewall(bool enable) => true;   // 回放视作成功（防火墙与画面无关）
}
