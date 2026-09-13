using AutoPickup.Config;
using AutoPickup.Logging;

namespace AutoPickup.Core.Audio;

/// <summary>
/// “下云”cue：安静(≥2s)后 1s 短窗音量突增（avg/max 同时超阈值）。
/// 实测：下云≈安静 1-4% 后 ~2s 内 avg→16%、max→60%；平时电台持续大声不会误判。
/// </summary>
public sealed class CloudCueDetector
{
    private readonly LogBus _log;
    private readonly AppSettings.AudioSection _a;
    private readonly Queue<float> _short = new(); // 1s @100ms
    private readonly Queue<float> _longQ = new(); // 用于安静判定(最近 2.5s)
    private long _startTs;
    private bool _quietOk;
    private int _quietSamplesNeeded;

    public CloudCueDetector(LogBus log, AppSettings.AudioSection audio)
    {
        _log = log;
        _a = audio;
        _quietSamplesNeeded = 25; // ~2.5s 静音前提（避开确认键提示音）
    }

    public void Begin()
    {
        lock (_lock2())
        {
            _short.Clear();
            _longQ.Clear();
            _startTs = Environment.TickCount64;
            _quietOk = false;
        }
    }

    private object _lock2() => _lock;

    private readonly object _lock = new();

    public bool Feed(float peak)
    {
        lock (_lock)
        {
            _short.Enqueue(peak);
            while (_short.Count > 10) _short.Dequeue();
            _longQ.Enqueue(peak);
            while (_longQ.Count > 25) _longQ.Dequeue();
            if (Environment.TickCount64 - _startTs < 1000) return false; // 起步缓冲

            // 安静判定：最近 ~2s 平均 < 8%
            var la = _longQ.ToArray();
            double longAvg = la.Average();
            if (!_quietOk)
            {
                if (longAvg < 0.08 && la.Length >= _quietSamplesNeeded)
                {
                    _quietOk = true;
                    _log.Info("静音基线确认（avg=" + (longAvg * 100).ToString("F1") + "%）", "Audio");
                }
                return false;
            }

            var sa = _short.ToArray();
            double avg = sa.Average();
            double mx = sa.Max();
            double thA = _a.CueAvgPercent / 100.0;
            double thM = _a.CueMaxPercent / 100.0;
            if (avg >= thA && mx >= thM)
            {
                _log.Info(string.Format("下云 cue: 1s短窗 avg={0:F0}% max={1:F0}%", avg * 100, mx * 100), "Audio");
                return true;
            }
            return false;
        }
    }
}