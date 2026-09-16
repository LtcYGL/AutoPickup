using AutoPickup.Config;
using AutoPickup.Core.Capture;
using AutoPickup.Core.Fsm;
using AutoPickup.Core.Input;
using AutoPickup.Core.Menus;
using AutoPickup.Core.Vision;
using AutoPickup.Core.Vision.Ocr;
using AutoPickup.Logging;

namespace AutoPickup.Core.Flow;

/// <summary>流程运行时的证据账本：任何一步的观察结果都进这里，后续任何 Gate 都能引用“之前任意一步的证据”。</summary>
public sealed class FlowContext
{
    private readonly List<Observed> _history = new();
    private readonly Dictionary<string, Observed> _latest = new(StringComparer.OrdinalIgnoreCase);

    public string LastRule = "";
    public int StepIndex = -1;
    public int Attempt;
    public Dictionary<string, string> Facts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Observed> History => _history;

    /// <summary>历史条数上限：长驻影子每小时采样上千次，历史不能无限涨（每步还挂着证据文本）。</summary>
    private const int HistoryCap = 240;

    public void Record(Observed o)
    {
        _history.Add(o);
        if (_history.Count > HistoryCap) _history.RemoveRange(0, _history.Count - HistoryCap);
        _latest[o.Kind] = o;
        // 同时提供 <kind>.value / <kind>.score 两个便捷键
        _latest[o.Kind + ".value"] = o;
        _latest[o.Kind] = o;
    }

    public Observed? Get(string name) => _latest.TryGetValue(name, out var o) ? o : null;

    /// <summary>清空证据账本（每轮开始时调用）。</summary>
    public void Reset()
    {
        _history.Clear();
        _latest.Clear();
        Facts.Clear();
    }

    /// <summary>当前证据快照（给日志/覆盖层/断言的“为什么”。）</summary>
    public string Snapshot()
    {
        var parts = new List<string>();
        foreach (var kv in _latest.Where(k => !k.Key.Contains('.')))
            parts.Add(kv.Key + "=" + kv.Value.Value + (kv.Value.Score is double sc ? "(" + sc.ToString("F2") + ")" : ""));
        foreach (var kv in Facts) parts.Add(kv.Key + "#" + kv.Value);
        return string.Join(" ", parts);
    }
}

/// <summary>一步流程：可选的准入/断言条件 + 一个动作 + 重试与失败策略。</summary>
public sealed class FlowStep
{
    public string Id { get; init; } = "";
    /// <summary>给用户看的短标签（日志用）；缺省回落到 Id。</summary>
    public string Title { get; init; } = "";
    public string Note { get; init; } = "";
    /// <summary>准入：不满足则跳过这一步（用于分支）。</summary>
    public List<Condition> Require { get; init; } = new();
    /// <summary>动作（op + 参数）。</summary>
    public ActionSpec Action { get; init; } = new();
    /// <summary>执行动作前重新观察哪些量（这些观察会进 ctx）。</summary>
    public List<string> Observe { get; init; } = new();
    /// <summary>前置断言：不满足则重试（不执行动作）。</summary>
    public List<Condition> Pre { get; init; } = new();
    /// <summary>动作后断言：不满足则重试，重试用尽按 onFail 处理。</summary>
    public List<Condition> Expect { get; init; } = new();
    public int TimeoutSec { get; init; } = 0;
    public RetrySpec Retry { get; init; } = new();
    public string OnFail { get; init; } = "abort";     // abort | skip | continue | rollback
}

public sealed class ActionSpec
{
    public string Op { get; init; } = "noop";           // tap/gesture/sleep/firewall/menu/tab/navigate/check/noop
    public string Button { get; init; } = "A";
    public string Dir { get; init; } = "up";
    public int Ms { get; init; } = 150;
    public int Times { get; init; } = 1;
    public bool Enable { get; init; } = true;
    /// <summary>menu 原子：open(已开就沿用) / close / toggle</summary>
    public string Mode { get; init; } = "open";
    /// <summary>tab 原子：目标 tab 名（简讯/职业/…）</summary>
    public string Target { get; init; } = "";
    /// <summary>navigate 原子：目标列表项（模糊匹配）</summary>
    public string Name { get; init; } = "";
}

public sealed class RetrySpec
{
    public int Max { get; init; } = 1;                   // 最多执行几次（1=不重试；2=一次重试）
    public int IntervalMs { get; init; } = 1500;
}

public sealed class FlowProgram
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<FlowStep> Steps { get; init; } = new();
    /// <summary>失败/中止时的收尾步骤（回卷）。</summary>
    public List<FlowStep> Cleanup { get; init; } = new();
}

/// <summary>
/// 流程解释器：只做“顺序执行 + 条件求值 + 重试/失败策略 + 证据记录”，
/// **不含任何游戏业务判断**（业务都在原语与 Gate 里）。这样步骤可任意拼接，也能离线回放。
/// </summary>
public sealed class FlowEngine
{
    private readonly IFlowHost _host;
    private readonly LogBus _log;
    private readonly TabReader? _tab;
    private readonly ScreenReader? _reader;
    private readonly AutoPickup.Core.Vision.Ocr.IOcrEngine? _ocr;
    private readonly FocusRowReader? _rows;
    private readonly AppSettings _settings;

    public FlowContext Ctx { get; } = new();
    /// <summary>每一步的轨迹（GUI/回放断言用）：步骤 id、动作、结果、证据。</summary>
    public List<string> Trace { get; } = new();

    // 同一帧的观察结果缓存：①避免同一帧反复付出 OCR/匹配代价 ②让“观察必须纯”的契约可被验证
    // ③缓存命中后可安全并行（只读同一份结果）。键用帧数组引用 + 尺寸。
    private readonly Dictionary<(byte[] Bgra, int W, int H, bool Ocr), FrameObs> _frameCache = new();
    private readonly ReaderWriterLockSlim _cacheLock = new();
    /// <summary>单飞锁：同一帧的观察只由一条线程构建（OCR 引擎非线程安全）。</summary>
    private readonly object FrameObsLock = new();
    /// <summary>列表行 OCR 兜底开关（默认关，见 FocusRowObserved 注释）。</summary>
    public bool AllowRowFallback { get; set; }

    private sealed record FrameObs(string Mode, string ModeDetail, bool MenuOpen, bool MenuFast,
        bool Dialog, string DialogDetail)
    {
        public string ModeValue => Mode;
    }

    public FlowEngine(IFlowHost host, LogBus log, AppSettings settings,
        TabReader? tab = null, ScreenReader? reader = null, AutoPickup.Core.Vision.Ocr.IOcrEngine? ocr = null,
        FocusRowReader? rows = null)
    {
        _host = host;
        _log = log;
        _settings = settings;
        _tab = tab;
        _reader = reader;
        _ocr = ocr;
        _rows = rows;
    }

    public bool Run(FlowProgram program)
    {
        _log.Hint("流程开始: " + program.Name + "（" + (_host.IsReplay ? "回放" : "实机") + "）", "Flow");
        Ctx.Facts["host"] = _host.Name;
        bool ok = true;
        for (int i = 0; i < program.Steps.Count; i++)
        {
            var step = program.Steps[i];
            Ctx.StepIndex = i;
            if (!EvalAll(step.Require, "require", out string reqEv))
            {
                _log.Info($"[{i}] {Label(step)} 跳过（条件不满足：{reqEv}）", "Detail");
                Trace.Add(i + " " + step.Id + " SKIP " + reqEv);
                continue;
            }
            var (stepOk, why) = RunStep(i, step);   // 日志里用中文短标签，方便用户读
            // 每步结果记为事实：后续步骤可用 "fact:<stepId>.ok eq false" 做分支/回退
            Ctx.Facts[step.Id + ".ok"] = stepOk ? "true" : "false";
            if (stepOk) continue;
            _log.Warn($"[{i}] {Label(step)} 失败：{why}", "Flow");
            // skip/continue = **可容忍失败**：记轨迹但不算整轮失败（否则可选步骤会把班次拖中止）
            if (step.OnFail is "skip" or "continue")
            {
                Trace.Add($"[{i}] {step.Id} FAIL({step.OnFail}) {why}");
                continue;
            }
            ok = false;
            switch (step.OnFail)
            {
                case "rollback":
                    Trace.Add(i + " " + step.Id + " FAIL(rollback) " + why);
                    RunSteps(program.Cleanup, "cleanup");
                    return false;
                default:
                    Trace.Add(i + " " + step.Id + " FAIL(abort) " + why);
                    RunSteps(program.Cleanup, "cleanup");
                    return false;
            }
        }
        _log.Okay("流程完成: " + program.Name, "Flow");
        return ok;
    }

    /// <summary>按键名翻译成用户看得懂的中文（日志用）。</summary>
    private static string Btn(string b) => b switch
    {
        "Start" => "Start(菜单)",
        "A" => "A",
        "B" => "B",
        "DPadUp" => "方向键上",
        "DPadDown" => "方向键下",
        "DPadLeft" => "方向键左",
        "DPadRight" => "方向键右",
        "LeftShoulder" => "LB",
        "RightShoulder" => "RB",
        _ => b,
    };

    /// <summary>日志用的步骤短标签：优先 title，其次 id。</summary>
    private static string Label(FlowStep step)
        => string.IsNullOrWhiteSpace(step.Title) ? step.Id : step.Title;

    private (bool Ok, string Why) RunStep(int idx, FlowStep step)
    {
        int max = Math.Max(1, step.Retry.Max);
        string last = "";
        for (int attempt = 1; attempt <= max; attempt++)
        {
            Ctx.Attempt = attempt;
            // 1) 先观察（把证据写进 ctx，供本步 Pre/Expect 与后续任何步引用）
            foreach (var name in step.Observe) Observe(name);

            if (!EvalAll(step.Pre, "pre", out string preEv))
            {
                last = "pre 未满足: " + preEv;
                _log.Info($"[{idx}] {Label(step)}（第 {attempt}/{max} 次）{last}", "Detail");
                if (attempt < max) { _host.SleepMs(step.Retry.IntervalMs); continue; }
                return (false, last);
            }

            // 2) 执行动作（动作自身不判断成败）
            var acted = Do(step.Action);
            _log.Info($"[{idx}] {Label(step)}" + (attempt > 1 ? "（第 " + attempt + "/" + max + " 次）" : "") + "：" + acted.Detail, "Flow");

            // 3) 断言（必要时在超时窗口内反复重新观察）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok;
            string ev;
            do
            {
                if (step.TimeoutSec > 0) _host.SleepMs(Math.Min(500, step.TimeoutSec * 1000));
                foreach (var name in step.Observe) Observe(name);
                ok = EvalAll(step.Expect, "expect", out ev);
                if (ok) break;
            } while (step.TimeoutSec > 0 && sw.ElapsedMilliseconds < step.TimeoutSec * 1000);

            if (ok)
            {
                Trace.Add($"[{idx}] {step.Id} OK {ev} :: {acted.Detail}");
                return (true, ev);
            }
            last = "expect 未满足: " + ev;
            if (attempt < max) _host.SleepMs(step.Retry.IntervalMs);
        }
        return (false, last);
    }

    private void RunSteps(List<FlowStep> steps, string label)
    {
        foreach (var s in steps)
        {
            if (!EvalAll(s.Require, label + ".require", out _)) continue;
            foreach (var name in s.Observe) Observe(name);
            var acted = Do(s.Action);
            _log.Info($"[{label}] {s.Id}: {acted.Detail}", "Flow");
            Trace.Add(label + " " + s.Id + " " + acted.Detail);
        }
    }

    // ---------------- 条件求值 ----------------

    private bool EvalAll(List<Condition> conds, string phase, out string evidence)
    {
        if (conds.Count == 0) { evidence = "(无条件)"; return true; }
        var parts = new List<string>();
        bool all = true;
        foreach (var c in conds)
        {
            var r = Eval(c);
            parts.Add((c.Note ?? c.ToString()) + "=" + (r.Ok ? "✓" : "✗") + "(" + r.Evidence + ")");
            if (!r.Ok) all = false;
        }
        evidence = string.Join(" ", parts);
        return all;
    }

    private CheckResult Eval(Condition c)
    {
        string? actual = null;
        double? num = null;
        if (c.Kind.Equals("ctx", StringComparison.OrdinalIgnoreCase))
        {
            var o = Ctx.Get(c.Name);
            if (o is null) return CheckResult.Fail("无此证据" + c.Name);
            actual = o.Value; num = o.Score;
            // 数值型证据（如横幅分数）也可以直接比较
            if (double.TryParse(o.Value, out double dv)) num = dv;
        }
        else if (c.Kind.Equals("fact", StringComparison.OrdinalIgnoreCase))
        {
            if (!Ctx.Facts.TryGetValue(c.Name, out actual)) return CheckResult.Fail("无此事实" + c.Name);
            if (double.TryParse(actual, out double dv2)) num = dv2;
        }
        else if (c.Kind.Equals("obs", StringComparison.OrdinalIgnoreCase))
        {
            var o = Observe(c.Name);
            if (o is null) return CheckResult.Fail("观察失败" + c.Name);
            actual = o.Value; num = o.Score;
        }
        else return CheckResult.Fail("未知条件类型" + c.Kind);

        string want = c.Value ?? "";
        bool ok = c.Op.ToLowerInvariant() switch
        {
            "eq" => string.Equals(actual, want, StringComparison.OrdinalIgnoreCase),
            "ne" => !string.Equals(actual, want, StringComparison.OrdinalIgnoreCase),
            "in" => want.Split('|', StringSplitOptions.RemoveEmptyEntries).Any(x => string.Equals(x.Trim(), actual, StringComparison.OrdinalIgnoreCase)),
            "nin" => !want.Split('|', StringSplitOptions.RemoveEmptyEntries).Any(x => string.Equals(x.Trim(), actual, StringComparison.OrdinalIgnoreCase)),
            "contains" => actual is not null && want.Length > 0
                          && (actual.Contains(want, StringComparison.OrdinalIgnoreCase)
                              || NoSpace(actual).Contains(NoSpace(want), StringComparison.OrdinalIgnoreCase)),
            // 字符重合度：OCR 会丢字（实测“退至故事模式”→Clean 后只剩“退故事模式”，少了“至”），
            // 精确/包含都会误判；这里按“目标字里有多少在实测文本中出现”判，默认阈值 0.6。
            "like" => actual is not null && want.Length > 0 && Overlap(NoSpace(actual), NoSpace(want)) >= LikeThreshold,
            "truthy" => actual is not null && !actual.Equals("false", StringComparison.OrdinalIgnoreCase) && actual != "0",
            "falsy" => actual is null || actual.Equals("false", StringComparison.OrdinalIgnoreCase) || actual == "0",
            "gt" => num.HasValue && num.Value > double.Parse(want),
            "ge" => num.HasValue && num.Value >= double.Parse(want),
            "lt" => num.HasValue && num.Value < double.Parse(want),
            "le" => num.HasValue && num.Value <= double.Parse(want),
            _ => false,
        };
        return ok ? CheckResult.Pass(actual ?? "null") : CheckResult.Fail(actual ?? "null");
    }

    // ---------------- 原语 ----------------

    /// <summary>清空本轮证据（事实/历史/轨迹）。**每轮开始必须调用**：否则上一轮的 fact 会残留，
    /// 既影响日志可读性，也会让 "fact:&lt;step&gt;.ok" 分支读到过期值。</summary>
    public void ResetContext()
    {
        Ctx.Reset();
        Trace.Clear();
    }

    /// <summary>清空帧级观察缓存。**长驻采样（影子）每轮必须调用**：缓存键持有整帧字节数组（1080p≈8MB）。</summary>
    public void ResetFrameCache()
    {
        _cacheLock.EnterWriteLock();
        try { _frameCache.Clear(); }
        finally { _cacheLock.ExitWriteLock(); }
    }

    /// <summary>观察（纯读）：结果进 ctx 并返回。未知名称返回 null（不抛）。</summary>
    public Observed? Observe(string name)
    {
        Observed? o = name.ToLowerInvariant() switch
        {
            // menuopen 采双证据：条带快检 或 “tab 词集里读到了模式词”（后者更耐区域参数漂移）
            "menuopen" => MenuOpenObserved(),
            "mode" => ModeObserved(),
            "dialog" => DialogObserved(),
            "selectedtab" => new Observed("selectedtab", SelectedTabObserved()),
            "focusrow" => new Observed("focusrow", FocusRowObserved() ?? ""),
            "toast" => ToastObserved(),
            "text" => TextObserved(),
            "bannerstory" => BannerObserved("story"),
            "banneronline" => BannerObserved("online"),
            _ => null,
        };
        if (o is not null) Ctx.Record(o);
        return o;
    }

    /// <summary>
    /// 同一帧的三种独立观察（tab 条 / 中央弹窗 / 画面分类）一次并行算完并缓存：
    /// ①省掉同帧重复 OCR 与匹配；②并行利用多核（Windows OCR + 模板匹配 + 区域裁剪互不依赖）。
    /// 纯读语义不变：缓存只存结果，不改变游戏状态。
    /// </summary>
    private FrameObs GetFrameObs()
    {
        var f = _host.Capture();
        var key = (f.Bgra, f.Width, f.Height, true);
        _cacheLock.EnterReadLock();
        try { if (_frameCache.TryGetValue(key, out var hit)) return hit; }
        finally { _cacheLock.ExitReadLock(); }

        string mode = "unknown", modeDetail = "tab 条读不到";
        bool menuFast = false, dialog = false;
        string dialogDetail = "未见确认弹窗";

        // 注意：WindowsOcrEngine 是**单实例非线程安全**（并行跑 tab 条与中央弹窗 OCR 会互相干扰，
        // 实测立刻退化成 unknown）。所以观察串行；省时间靠“同一帧只算一次”的缓存，而不是并行 OCR。
        // 需要并行时只能并行“不含 OCR”的部分，或给每个观察独立 OCR 引擎实例。
        var obsLock = FrameObsLock;
        lock (obsLock)
        {
            if (_tab is not null)
            {
                menuFast = _tab.IsTabStripPresent(f);
                var tr = _tab.Read(f);
                if (tr is { StripWords.Length: > 2 })
                {
                    string s = TextMatcher.Clean(tr.StripWords);
                    _log.Info("[observe] tab条词集: " + tr.StripWords + " | 选中=" + (tr.Selected ?? "?")
                        + " | 白块x " + tr.WhiteX0 + ".." + tr.WhiteX1, "Detail");
                    var m = DecideMode(tr);
                    mode = m.Value;
                    modeDetail = m.Detail ?? mode;
                }
            }
            if (_reader is not null)
            {
                string? t = _reader.ScanCenterDialog(f);
                dialog = t is not null && DialogWords(t);
                dialogDetail = dialog ? "中央弹窗命中退出/切换确认" : "未见确认弹窗";

            }
        }

        var obs = new FrameObs(mode, modeDetail, menuFast || mode is "story" or "online", menuFast, dialog, dialogDetail);
        _cacheLock.EnterWriteLock();
        try
        {
            // 实时采样时每帧都是新数组，缓存必须封顶，否则会一直涨（1920x1080 一帧≈8MB）。
            // 只需覆盖“同一帧被多次观察”，回放时脚本帧也少，3 条足够；超了整体清空。
            if (_frameCache.Count >= 3) _frameCache.Clear();
            _frameCache[key] = obs;
        }
        finally { _cacheLock.ExitWriteLock(); }
        return obs;
    }

    /// <summary>菜单是否已开：条带快检为主，另用“tab 词集是否含模式词”作旁证（区域参数漂移时更稳）。</summary>
    private Observed MenuOpenObserved()
    {
        if (_tab is null) return new Observed("menuopen", "false", Detail: "无 TabReader");
        var o = GetFrameObs();
        return new Observed("menuopen", o.MenuOpen ? "true" : "false",
            Detail: "条带快检=" + o.MenuFast + " 模式=" + o.Mode + " (" + o.ModeDetail + ")");
    }

    /// <summary>模式观察：只有 tab 条读到词证才给 story/online，否则 unknown（三值，不再强行二分）。</summary>
    private Observed ModeObserved()
    {
        if (_tab is null) return new Observed("mode", "unknown", Detail: "无 TabReader");
        var o = GetFrameObs();
        return new Observed("mode", o.Mode, Detail: o.ModeDetail);
    }

    /// <summary>
    /// 模式判定（比“词序先撞上谁”稳）：
    /// 1) 若“选中 tab”本身就是模式词（简讯/职业/在线）→ 直接采信；
    /// 2) 否则看白块中心与哪个模式词的水平位置最近（选中的 tab 就在白块下方）；
    /// 3) 都没有才退回“词集里出现即算”的旧逻辑。
    /// 之所以不能只按词序：实机实测 tab 条里**同时**会出现“简讯”和“职业”（列表/子菜单文字被卷进条带）。
    /// </summary>
    private Observed DecideMode(TabRead tr)
    {
        string sel = TextMatcher.Clean(tr.Selected ?? "");
        if (sel.Contains("简") || sel.Contains("讯")) return new Observed("mode", "story", Detail: "选中tab=" + tr.Selected);
        if (sel.Contains("职") || sel.Contains("业") || sel.Contains("线")) return new Observed("mode", "online", Detail: "选中tab=" + tr.Selected);

        if (tr.WhiteX0 >= 0 && tr.WhiteX1 > tr.WhiteX0 && _ocr is not null)
        {
            var f = _host.Capture();
            // 统一走“工作像素预算”再 OCR（与 TabReader/Reader 同一条路），词框按同一系数还原
            var g = Imaging.BgraToGray(f.Bgra, f.Width, f.Height);
            var prep = OcrPrep.ForFullFrame(g, f.Width, f.Height, _settings.Vision);
            double kk = prep.Resized ? prep.Scale : 1.0;
            var rawW = _ocr.RecognizeWords(ToBgra(prep.Gray), prep.Width, prep.Height);
            var words = rawW.Select(w => new OcrWord(w.Text,
                (int)Math.Round(w.X1 / kk), (int)Math.Round(w.Y1 / kk),
                (int)Math.Round(w.X2 / kk), (int)Math.Round(w.Y2 / kk))).ToList();
            int center = (tr.WhiteX0 + tr.WhiteX1) / 2;
            int bestD = int.MaxValue; string bestKind = "";
            foreach (var wd in words)
            {
                string c = TextMatcher.Clean(wd.Text);
                string? kind = null;
                if (c.Contains("简") || c.Contains("讯")) kind = "story";
                else if (c.Contains("职") || c.Contains("业") || c == "在线") kind = "online";
                if (kind is null) continue;
                int d = Math.Abs((wd.X1 + wd.X2) / 2 - center);
                if (d < bestD) { bestD = d; bestKind = kind; }
            }
            if (bestKind.Length > 0)
                return new Observed("mode", bestKind, Detail: "白块中心 " + center + " 最近的模式词=" + bestKind + " (d=" + bestD + ")");
        }

        string s = TextMatcher.Clean(tr.StripWords);
        if (s.Contains("简") && s.Contains("讯")) return new Observed("mode", "story", Detail: "词集兜底: " + tr.StripWords);
        if (s.Contains("职") && s.Contains("业")) return new Observed("mode", "online", Detail: "词集兜底: " + tr.StripWords);

        // 4) 横幅文字兜底（“打开暂停菜单看横幅”）：复用现成的横幅带 OCR，不依赖模板。
        //    仅在“条带里确实出现了 tab 名”时启用——说明眼前确实是暂停菜单，
        //    避免把加载画面上的标题误判成故事模式（实测加载页也会读到 Grand Theft Auto V）。
        if (_reader is not null && HasTabName(s))
        {
            var rr = _reader.Read(_host.Capture(), withOcr: true, withBanner: true, includeHome: false);
            if (rr.BannerOcrOnline && !rr.BannerOcrStory)
                return new Observed("mode", "online", Detail: "横幅文字: " + rr.BannerOcrText);
            if (rr.BannerOcrStory && !rr.BannerOcrOnline)
                return new Observed("mode", "story", Detail: "横幅文字: " + rr.BannerOcrText);
            if (rr.BannerOcrText is not null)
                return new Observed("mode", "unknown", Detail: "横幅未定案: " + rr.BannerOcrText);
        }
        return new Observed("mode", "unknown", Detail: "词集未含模式词: " + tr.StripWords);
    }

    /// <summary>条带里是否出现了已知 tab 名——用来确认“眼前是暂停菜单”而不是加载画面。</summary>
    private static bool HasTabName(string cleanedStrip)
    {
        if (string.IsNullOrEmpty(cleanedStrip)) return false;
        foreach (var t in new[] { "地图", "简讯", "统计", "设置", "游戏", "在线", "职业", "好友", "信息", "商店" })
            if (cleanedStrip.Contains(t[0])) return true;
        return false;
    }

    /// <summary>确认弹窗观察：命中退出/切换确认框返回 true。</summary>
    private Observed DialogObserved()
    {
        if (_reader is null) return new Observed("dialog", "false", Detail: "无 Reader");
        var o = GetFrameObs();
        return new Observed("dialog", o.Dialog ? "true" : "false", Detail: o.DialogDetail);
    }

    /// <summary>
    /// 左下角提示（toast）观察：裁左下条带 OCR，返回命中的语义标签，便于把“保存失败”这类提示写成 Gate。
    /// 取值：savefail / cargo（员工已获取）/ sell（已出售）/ levelup / other / none
    /// </summary>
    private Observed ToastObserved()
    {
        if (_reader is null) return new Observed("toast", "none", Detail: "无 Reader");
        var t = _reader.ScanToastStrip(_host.Capture());
        if (string.IsNullOrWhiteSpace(t)) return new Observed("toast", "none");
        string q = TextMatcher.Clean(t);
        // 抗 OCR 错字：不用“整词相等”，改用**关键字符命中数**（实测“保存失败”会被读成“保 荏 矢 败”）
        int Hit(params char[] cs) => cs.Count(c => q.Contains(c));
        string kind;
        if (Hit('保', '存', '荏') >= 1 && Hit('失', '矢', '败') >= 1) kind = "savefail";
        else if (Hit('员', '工') >= 1 && Hit('获', '取', '得') >= 1) kind = "cargo";
        else if (Hit('出', '售') >= 1 && Hit('已', '出') >= 1) kind = "sell";
        else if (Hit('等', '级', '升') >= 1 && Hit('升', '级') >= 1) kind = "levelup";
        else if (Hit('云', '服', '务', '器') >= 2) kind = "cloud";
        else kind = "other";
        return new Observed("toast", kind, Detail: t.Length > 60 ? t.Substring(0, 60) : t);
    }

    /// <summary>
    /// 通用文本观察：整帧 OCR 后去空白，供 "ctx:text contains XXX" 之类的判据用。
    /// 主要用于**未知画面诊断**——比如死亡/传送画面读不到 tab 条时，靠它拿到画面里到底写了什么。
    /// </summary>
    private Observed TextObserved()
    {
        if (_ocr is null) return new Observed("text", "");
        try
        {
            var f = _host.Capture();
            // **故意不缩到工作分辨率**：这是“什么都读不到”时的诊断手段，准确性优先。
            // 缩到 0.98M 后小字会消失（实测自由漫游帧全帧 0 词、原分辨率能读到 toast 与 HUD）。
            var g = Imaging.BgraToGray(f.Bgra, f.Width, f.Height);
            var words = _ocr.RecognizeWords(ToBgra(g), f.Width, f.Height);
            string s = TextMatcher.Clean(string.Join(" ", words.Select(w => w.Text)));
            return new Observed("text", s, Detail: "原分辨率词数 " + words.Count + " (" + f.Width + "x" + f.Height + ")");
        }
        catch (Exception ex) { return new Observed("text", "", Detail: "异常: " + ex.Message); }
    }

    /// <summary>当前选中的 tab 名（空=读不到）。</summary>
    private string SelectedTabObserved()
    {
        if (_tab is null) return "";
        try { return _tab.Read(_host.Capture())?.Selected ?? ""; } catch { return ""; }
    }

    /// <summary>
    /// 当前焦点行文字。优先用 FocusRowReader（认白块）；它读不到时退回“列表左栏区域直接 OCR”，
    /// 取最靠近上方的短行作为焦点候选——实测故事侧某些高亮样式 FocusRowReader 认不出，
    /// 但文字本身 OCR 得到，这样 navigate 不至于直接瘫掉。
    /// </summary>
    private string? FocusRowObserved()
    {
        var f = _host.Capture();
        if (_rows is not null)
        {
            try
            {
                var r = _rows.FindFocusRow(f, null);
                if (!string.IsNullOrWhiteSpace(r?.Label)) return r.Label;
            }
            catch { }
        }
        // 列表行兜底：默认**关闭**。实测在故事侧那份几何下会把 tab 行当列表项（"地统图简讯计"），
        // 假证据比没证据更坏，会让 navigate 越走越偏。要用就显式打开（等区域参数针对该几何校准后再启用）。
        if (!AllowRowFallback) return null;
        try
        {
            var v = _settings.Vision;
            int x0 = (int)(f.Width * v.ListLeftPercent / 100.0);
            int x1 = (int)(f.Width * Math.Min(100.0, v.ListRightPercent + 12) / 100.0);
            var (crop, cw, ch) = CropRegion(f, x0, 0, x1, f.Height);
            if (cw <= 8 || ch <= 8) return null;
            var g = Imaging.BgraToGray(crop, cw, ch);
            var prep = OcrPrep.ForRegionSite(g, cw, ch, "focusrow", v);
            var words = _ocr?.RecognizeWords(ToBgra(prep.Gray), prep.Width, prep.Height);
            if (words is null || words.Count == 0) return null;
            double k = prep.Resized ? prep.Scale : 1.0;
            // 列表项都在**左栏**，按行聚类后取“最上面那一行列表项”当焦点候选。
            // 必须排除横幅标题行（含 Grand/Theft/Auto 的那行），否则会把标题当焦点行（实测踩过）。
            int leftLimit = (int)(f.Width * Math.Min(40.0, v.ListRightPercent + 4) / 100.0);
            var lines = words
                .Where(w => w.X1 / k < leftLimit)
                .Select(w => new { Text = w.Text, Y = (w.Y1 + w.Y2) / 2.0 / k })
                .OrderBy(x => x.Y).ToList();
            if (lines.Count == 0) return null;
            var rowsText = new List<string>();
            double curY = lines[0].Y;
            var cur = new List<string>();
            foreach (var w in lines)
            {
                if (w.Y - curY > 22) { rowsText.Add(string.Join("", cur)); cur.Clear(); curY = w.Y; }
                cur.Add(w.Text);
            }
            if (cur.Count > 0) rowsText.Add(string.Join("", cur));
            foreach (var t in rowsText)
            {
                string lab = TextMatcher.Clean(t);
                string low = lab.ToLowerInvariant();
                if (low.Contains("grand") || low.Contains("theft") || low.Contains("auto")) continue;
                if (lab.Length >= 2) return lab;
            }
            return null;
        }
        catch { return null; }
    }

    private static (byte[] Bgra, int W, int H) CropRegion(Frame f, int x0, int y0, int x1, int y1)
    {
        x0 = Math.Clamp(x0, 0, f.Width - 1); x1 = Math.Clamp(x1, x0 + 1, f.Width);
        y0 = Math.Clamp(y0, 0, f.Height - 1); y1 = Math.Clamp(y1, y0 + 1, f.Height);
        int w = x1 - x0, h = y1 - y0;
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int src = ((y0 + y) * f.Width + x0) * 4;
            Buffer.BlockCopy(f.Bgra, src, buf, y * w * 4, w * 4);
        }
        return (buf, w, h);
    }

    /// <summary>
    /// tab 名 → 序号。**必须按模式分列**：故事侧是 地图/简讯/统计/设置/游戏/在线，在线侧是 地图/在线/职业/好友/信息/商店；
    /// 同一个“在线”在两侧序号不同（故事侧 index 5、在线侧 index 1），不分模式就会按错次数。
    /// </summary>
    private static int TabIndex(string? name, string mode)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        string s = name.Trim();
        string[] order = mode == "story"
            ? new[] { "地图", "简讯", "统计", "设置", "游戏", "在线" }
            : new[] { "地图", "在线", "职业", "好友", "信息", "商店" };
        for (int i = 0; i < order.Length; i++)
        {
            // 用首字匹配即可（OCR 会把字拆开/丢字）
            if (s.Contains(order[i][0])) return i;
        }
        return -1;
    }

    /// <summary>去掉空白，供 contains 做“忽略空格”的比较（OCR 会把字拆开/丢空格）。</summary>
    private static string NoSpace(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>like 判据的重合度阈值（可用参数调；默认 0.6）。</summary>
    public double LikeThreshold { get; set; } = 0.6;

    /// <summary>目标字里落在实测文本中的比例。</summary>
    private static double Overlap(string actual, string want)
    {
        if (want.Length == 0) return 0;
        int hit = 0;
        var pool = new List<char>(actual);
        foreach (char c in want)
        {
            int i = pool.IndexOf(c);
            if (i >= 0) { hit++; pool.RemoveAt(i); }
        }
        return hit / (double)want.Length;
    }

    private static byte[] ToBgra(byte[] gray)
    {
        var b = new byte[gray.Length * 4];
        for (int i = 0, j = 0; i < gray.Length; i++, j += 4)
        { byte v = gray[i]; b[j] = v; b[j + 1] = v; b[j + 2] = v; b[j + 3] = 255; }
        return b;
    }

    private static bool DialogWords(string text)
    {
        var q = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        return q.Contains("退出") && (q.Contains("确认") || q.Contains("确定") || q.Contains("取消")
            || q.Contains("故事") || q.Contains("在线") || q.Contains("保存") || q.Contains("进度"));
    }

    private Observed BannerObserved(string which)
    {
        if (_reader is null) return new Observed("banner" + which, "0");
        var r = _reader.Read(_host.Capture(), withOcr: false, includeHome: false, withBanner: true);
        double sc = which == "story" ? r.StoryScore : r.OnlineScore;
        return new Observed("banner" + which, sc.ToString("F3"), sc);
    }

    private Acted Do(ActionSpec a)
    {
        switch (a.Op.ToLowerInvariant())
        {
            case "tap":
                for (int i = 0; i < Math.Max(1, a.Times); i++)
                {
                    _host.Tap(a.Button);
                    if (i + 1 < a.Times) _host.SleepMs(a.Ms);
                }
                return new Acted(true, "按 " + Btn(a.Button) + (a.Times > 1 ? " ×" + a.Times : ""));
            case "gesture":
                _host.Gesture(a.Dir);
                return new Acted(true, a.Dir.Equals("down", StringComparison.OrdinalIgnoreCase)
                    ? "轮盘手势↓（进线上）" : "轮盘手势↑（回故事）");
            case "menu":
            {
                // 打开/关闭暂停菜单原子。**先看再动**：已开就别按 Start（按了等于关，这正是旧 FSM 的坑）。
                bool open = MenuOpenObserved().Value == "true";
                string how = a.Mode.ToLowerInvariant();
                if (how == "close" || (how == "toggle" && open))
                {
                    if (!open) return new Acted(true, "菜单已关，无需按键");
                    _host.Tap("B");
                    return new Acted(true, "关闭暂停菜单（按 B）");
                }
                if (open && how == "open")
                    return new Acted(true, "菜单已开，沿用（不按键）");
                _host.Tap("Start");
                return new Acted(true, "打开暂停菜单（按 Start）");
            }
            case "tab":
            {
                // 切 tab：按当前选中位置决定按几次 RB/LB（动作不判断成败，交给 expect 的 selectedtab 证据）。
                // tab 顺序**按模式不同**（故事：地图/简讯/统计/设置/游戏/在线；在线：地图/在线/职业/好友/信息/商店）。
                string md = ModeObserved().Value;
                var cur = SelectedTabObserved();
                int want = TabIndex(a.Target, md);
                int now = TabIndex(cur, md);
                if (want < 0 || now < 0) return new Acted(false, "tab 目标/当前不可解析（" + a.Target + " vs " + cur + "）");
                int delta = want - now;
                if (delta == 0) return new Acted(true, "tab 已在 " + a.Target + "（不按键）");
                string btn = delta > 0 ? "RightShoulder" : "LeftShoulder";
                for (int i = 0; i < Math.Abs(delta); i++)
                {
                    _host.Tap(btn);
                    _host.SleepMs(180);
                }
                return new Acted(true, "tab " + cur + " → " + a.Target + "（按 " + btn + " x" + Math.Abs(delta) + "）");
            }
            case "navigate":
            {
                // 列表导航：**边走边看**，最多走 Times 步（默认 15），每步先读焦点行再决定要不要继续。
                // 动作仍不自验：读不到焦点行就如实返回“走不动”，由 Gate/retry 决定怎么办。
                string back = "DPadDown";
                string fwd = string.IsNullOrEmpty(a.Target) ? "DPadDown" : a.Target;
                if (fwd.Equals("up", StringComparison.OrdinalIgnoreCase) || fwd.Equals("DPadUp", StringComparison.OrdinalIgnoreCase))
                    back = "DPadUp";
                int maxSteps = Math.Max(1, a.Times > 1 ? a.Times : 15);
                int steps = 0;
                string lastSeen = "";
                for (int i = 0; i < maxSteps; i++)
                {
                    string? row = FocusRowObserved();
                    if (row is null) return new Acted(false, "读不到焦点行（走不动）");
                    lastSeen = TextMatcher.Clean(row);
                    if (TextMatcher.FuzzyEqual(lastSeen, a.Name))
                        return new Acted(true, "焦点已到「" + a.Name + "」（走 " + steps + " 步）");
                    _host.Tap(back);
                    steps++;
                    _host.SleepMs(_settings.Automation.PressMs + 120);
                }
                return new Acted(false, "没走到「" + a.Name + "」（走 " + steps + " 步，当前「" + lastSeen + "」）");
            }
            case "sleep":
                _host.SleepMs(a.Ms);
                return new Acted(true, "等待 " + (a.Ms / 1000.0).ToString("0.#") + " 秒");
            case "waitmode":
            {
                // 到达某模式的“探针”原子（对应旧流程的 WaitModeReachable）：
                // mode 判据来自**暂停菜单的 tab 条**，菜单不开就永远读不到 —— 所以必须主动按 Start 去开。
                // 关键：**先看再动**，菜单已经开着就绝不按 Start（否则会把它关掉，就是那个 toggle 坑）。
                string want = string.IsNullOrEmpty(a.Target) ? "story" : a.Target.ToLowerInvariant();
                int timeoutSec = a.Ms > 0 ? a.Ms : 240;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int presses = 0;
                while (sw.Elapsed.TotalSeconds < timeoutSec)
                {
                    var o = ModeObserved();
                    if (o.Value == want)
                    {
                        Ctx.Facts["mode_reached"] = "true";
                        return new Acted(true, "确认到达" + (want == "story" ? "故事" : "在线")
                            + "（用时 " + (int)sw.Elapsed.TotalSeconds + "s，按 Start " + presses + " 次）");
                    }
                    bool open = MenuOpenObserved().Value == "true";
                    if (!open)
                    {
                        _host.Tap("Start");
                        presses++;
                        _host.SleepMs(_settings.Automation.PressMs + 2000);
                    }
                    else _host.SleepMs(1200);   // 已开但条带没出字：短等重读，不按键
                }
                Ctx.Facts["mode_reached"] = "false";
                return new Acted(false, "未确认到达" + (want == "story" ? "故事" : "在线")
                    + "（超时 " + timeoutSec + "s，按 Start " + presses + " 次）");
            }
            case "waitcue":
            {
                // 未启用封网时无需等 cue（纯“进线上/回故事”场景）
                if (!_settings.Shift.UseFirewall)
                {
                    Ctx.Facts["cue"] = "true";
                    return new Acted(true, "跳过等待下云 cue（未启用封网）");
                }
                // 卡大仓的关键时机：下云声音一响就该封网。结果记为 fact:cue，由后续步骤/Gate 决定要不要封。
                int to = a.Times > 1 ? a.Times : Math.Max(10, _settings.Shift.CueTimeoutSec);
                bool hit = _host.WaitCloudCue(to);
                Ctx.Facts["cue"] = hit ? "true" : "false";
                return new Acted(hit, hit ? "下云 cue 命中" : "下云 cue 超时");
            }
            case "firewall":
                if (!_settings.Shift.UseFirewall)
                {
                    Ctx.Facts["firewall"] = "true";
                    return new Acted(true, "跳过防火墙（未启用封网）");
                }
                bool ok = _host.Firewall(a.Enable);
                // 结果记成事实，方便 Gate 用 "fact:firewall eq true" 引用
                Ctx.Facts["firewall"] = (ok && a.Enable) ? "true" : "false";
                return new Acted(ok, (a.Enable ? "已封网" : "已恢复联网") + (ok ? "" : " —— 失败/忽略"));
            case "check":
                return new Acted(true, "check");
            default:
                return new Acted(true, "仅观察，不动作");
        }
    }
}
