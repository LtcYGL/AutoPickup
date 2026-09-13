using AutoPickup.Core.Capture;
using AutoPickup.Core.Vision;
using AutoPickup.Logging;

namespace AutoPickup.Core.Fsm;

/// <summary>
/// 屏幕观察器：捕获→识别循环，带“连续 N 帧同结果”迟滞与超时。
/// 这是状态机“到达验证/等弹窗/等加载”的基础原语（替代原版固定 sleep）。
/// </summary>
public sealed class ScreenWatcher
{
    private readonly GtaWindowSource _window;
    private readonly ScreenReader _reader;
    private readonly LogBus _log;
    private readonly int _intervalMs;

    public ScreenWatcher(GtaWindowSource window, ScreenReader reader, LogBus log, int intervalMs = 1200)
    {
        _window = window;
        _reader = reader;
        _log = log;
        _intervalMs = Math.Max(300, intervalMs);
    }

    /// <summary>抓一帧并识别（不迟滞）。</summary>
    public ReadResult? ReadOnce(bool withOcr = false, bool includeHome = true)
    {
        var sw0 = System.Diagnostics.Stopwatch.StartNew();
        var frame = _window.CaptureClient();
        long capMs = sw0.ElapsedMilliseconds;
        if (!frame.IsValid)
        {
            _log.Warn("抓帧失败（游戏未运行/被遮挡/全屏独占）", "Watcher");
            return null;
        }
        var rd = _reader.Read(frame, withOcr, includeHome);
        long totalMs = sw0.ElapsedMilliseconds;
        if (totalMs > 1800)
        {
            _log.Info(string.Format("慢帧: 抓屏{0}ms(方式 {1}) 总{2}ms", capMs, _window.LastMethod, totalMs), "Watcher");
        }
        return rd;
    }

    /// <summary>
    /// 横幅定点快速复查：true=在 / false=不在 / null=无法判定(需全图)。
    /// 优先用记忆框做廉价复查；无缓存时才退化为全图读取（会更新缓存）。
    /// </summary>
    public bool? BannerPresent()
    {
        var frame = _window.CaptureClient();
        if (!frame.IsValid) return null;
        if (_reader.HasBannerCache(frame)) return _reader.FastBannerPresent(frame);
        var r = _reader.Read(frame, withOcr: false, includeHome: false);
        if (r is null) return null;
        return r.Kind is UiKind.StoryTitle or UiKind.OnlineTitle;
    }

    /// <summary>
    /// 等待某状态出现：连续 stableFrames 帧满足 predicate 才算命中。
    /// 默认开启 OCR 以覆盖弹窗/加载/主菜单等横幅不可判的状态（代价略高）。
    /// </summary>
    public ReadResult? WaitUntil(Func<UiKind, bool> predicate, TimeSpan timeout,
        bool withOcr = true, int stableFrames = 2, bool includeHome = true)
    {
        var deadline = DateTime.UtcNow + timeout;
        int streak = 0;
        ReadResult? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = ReadOnce(withOcr, includeHome);
            if (last is null)
            {
                streak = 0;
                Thread.Sleep(_intervalMs);
                continue;
            }
            if (predicate(last.Kind))
            {
                streak++;
                if (streak >= stableFrames)
                {
                    _log.Okay("状态命中: " + last.Kind + "（连续 " + streak + " 帧）", "Watcher");
                    return last;
                }
            }
            else
            {
                streak = 0;
            }
            Thread.Sleep(_intervalMs);
        }
        _log.Warn("等待超时(" + timeout.TotalSeconds + "s)，最后状态: " + (last?.Kind.ToString() ?? "无"), "Watcher");
        return null;
    }

    public ReadResult? WaitNotIn(UiKind a, UiKind b, TimeSpan timeout, bool withOcr = false)
        => WaitUntil(k => k != a && k != b, timeout, withOcr, 2);
}