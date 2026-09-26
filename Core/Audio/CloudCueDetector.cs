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
    private readonly Queue<float> _short = new();   // ~1s 短窗
    private readonly Queue<float> _longQ = new();   // ~2.5s，用于安静判定
    private long _startTs;
    private bool _quietOk;
    /// <summary>窗口按“秒”换算成样本数，跟着参数页「采样间隔(ms)」走 —— 以前写死 10/25 个样本，
    /// 改采样间隔后“1 秒短窗”“2.5 秒静音前提”就失真了（调快调慢都会误判）。</summary>
    private readonly int _shortN;
    private readonly int _longN;

    public CloudCueDetector(LogBus log, AppSettings.AudioSection audio)
    {
        _log = log;
        _a = audio;
        int iv = Math.Max(20, audio.SampleIntervalMs);
        _shortN = Math.Max(2, (int)Math.Round(1000.0 / iv));
        _longN = Math.Max(_shortN, (int)Math.Round(2500.0 / iv));
    }

    public void Begin()
    {
        lock (_lock)
        {
            _short.Clear();
            _longQ.Clear();
            _startTs = Environment.TickCount64;
            _quietOk = false;
        }
    }

    private readonly object _lock = new();

    public bool Feed(float peak)
    {
        lock (_lock)
        {
            _short.Enqueue(peak);
            while (_short.Count > _shortN) _short.Dequeue();
            _longQ.Enqueue(peak);
            while (_longQ.Count > _longN) _longQ.Dequeue();
            if (Environment.TickCount64 - _startTs < 1000) return false; // 起步缓冲

            // 安静判定：最近 ~2s 平均 < 8%
            var la = _longQ.ToArray();
            double longAvg = la.Average();
            if (!_quietOk)
            {
                if (longAvg < 0.08 && la.Length >= _longN)
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