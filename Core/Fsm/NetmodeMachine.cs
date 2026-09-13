using AutoPickup.Config;
using AutoPickup.Core.Audio;
using AutoPickup.Core.Capture;
using AutoPickup.Core.Input;
using AutoPickup.Core.Menus;
using AutoPickup.Core.Vision;
using AutoPickup.Logging;

namespace AutoPickup.Core.Fsm;

/// <summary>
/// 模式切换状态机：主输入 = ViGEm 虚拟手柄（PC 键位仅语义参考：Q/E=LB/RB、Enter=A、Esc=Start/B 类取消）。
/// 关键语义：Start=顶层开/关暂停（模态弹窗下无效）；A=确认/进入；B=取消/返回（清弹窗/退层级优先用 B）。
/// 识别：菜单开否=tab 条词集；焦点行=整行白带+行OCR；导航=光标计数+每步校准+最短方向。
/// </summary>
public sealed class NetmodeMachine
{
    private readonly ScreenWatcher _watcher;
    private readonly ScreenReader _reader;
    private readonly GtaWindowSource _window;
    private readonly FocusRowReader _rows;
    private readonly TabReader _tab;
    private readonly IInputLayer _input;
    private readonly AppSettings _settings;
    private readonly LogBus _log;

    public UiKind? LastBelief { get; private set; }
    private int _listTopY = -1;

    public NetmodeMachine(GtaWindowSource window, ScreenReader reader, FocusRowReader rows,
        TabReader tab, IInputLayer input, AppSettings settings, LogBus log)
    {
        _window = window;
        _reader = reader;
        _watcher = new ScreenWatcher(window, reader, log);
        _rows = rows;
        _tab = tab;
        _input = input;
        _settings = settings;
        _log = log;
    }

    // ============ 基础 ============

    private bool EnsureForeground()
    {
        // 后台模式：不抢前台，改用“假激活”（实测：窗口可见、非最小化时有效；最小化无效）
        if (_settings.Automation.BackgroundFakeActivate)
        {
            if (!_window.IsWindowValid)
            {
                _log.Warn("后台模式：游戏窗口不存在，放弃本次动作", "Fsm");
                return false;
            }
            _window.FakeActivate();
            Thread.Sleep(Math.Max(20, _settings.Automation.FakeActivateSettleMs));
            return true;
        }
        if (!_settings.Automation.BringToFrontBeforeAction) return true;
        if (_window.IsForeground) return true; // 已在前台：不等待
        if (!_window.BringToFront())
        {
            _log.Warn("未能把游戏窗口带到前台（游戏未运行？），放弃本次动作", "Fsm");
            return false;
        }
        Thread.Sleep(Math.Max(50, _settings.Automation.ForegroundSettleMs));
        return true;
    }

    /// <summary>每次按键前重夺前台（Steam 提示/DS4Windows/弹窗可能抢走焦点吞掉输入）。</summary>
    private bool FrontForPress()
        => EnsureForeground();

    private static bool IsPauseBanner(UiKind k)
        => k is UiKind.StoryTitle or UiKind.OnlineTitle or UiKind.ExitDialog;

    private bool IsMenuOpenFrame(Frame frame)
    {
        if (!frame.IsValid) return false;
        try
        {
            var tr = _tab.Read(frame);
            if (tr is { StripWords.Length: > 1 })
            {
                NoteListTop(tr);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void NoteListTop(TabRead tr)
    {
        int v = tr.BandY1 + 6;
        if (v > _listTopY) _listTopY = v;
    }

    private FocusRow? CurrentRow(Frame frame)
        => _rows.FindFocusRow(frame, _listTopY >= 0 ? _listTopY : null);

    private UiKind? GuessModeFromMenu()
    {
        var f = _window.CaptureClient();
        if (!f.IsValid) return null;
        try
        {
            var tr = _tab.Read(f);
            if (tr is { StripWords.Length: > 2 })
            {
                NoteListTop(tr);
                string s = TextMatcher.Clean(tr.StripWords);
                if (s.Contains("简") && s.Contains("讯")) return UiKind.StoryTitle;
                if (s.Contains("职") && s.Contains("业")) return UiKind.OnlineTitle;
            }
        }
        catch { }
        // 流程里的模式判定靠 tab 词证（简讯/职业），横幅 NCC 已不参与定案且很贵 → 跳过
        var r = _reader.Read(f, false, false, withBanner: false);
        return r is null ? null : r.Kind;
    }

    private bool PollMenuOpen(bool wantOpen, int need, int windowMs)
    {
        var stw = System.Diagnostics.Stopwatch.StartNew();
        int consec = 0;
        while (stw.ElapsedMilliseconds < windowMs)
        {
            var f = _window.CaptureClient();
            bool open = IsMenuOpenFrame(f);
            if (open == wantOpen)
            {
                consec++;
                if (consec >= need) return true;
            }
            else consec = 0;
            Thread.Sleep(160);
        }
        return false;
    }

    /// <summary>失败现场的轻量诊断：只打日志与当前识别证据，不再把帧写盘（用户要求流程/动作不存 sample）。
    /// 需要落盘图片时用命令行 --dump-frame / --record，或 --classify 离线分析。</summary>
    private void LogDiag(string tag)
    {
        try
        {
            var f = _window.CaptureClient();
            if (!f.IsValid)
            {
                _log.Info("现场[" + tag + "]: 抓帧失败", "Fsm");
                return;
            }
            var r = _reader.Read(f, withOcr: false, includeHome: false);
            _log.Info(string.Format("现场[{0}]: {1}x{2} 抓帧方式={3} 横幅 故事={4:F3} 在线={5:F3} 判定={6}",
                tag, f.Width, f.Height, _window.LastMethod,
                r?.StoryScore ?? 0, r?.OnlineScore ?? 0, r?.Kind.ToString() ?? "无"), "Fsm");
        }
        catch (Exception e)
        {
            _log.Warn("现场诊断失败: " + e.Message, "Fsm");
        }
    }

    private bool? FullBannerVerdict()
    {
        var r = _watcher.ReadOnce(withOcr: false, includeHome: false);
        if (r is null) return null;
        return IsPauseBanner(r.Kind);
    }

    // ============ 菜单开/关 ============

    public bool OpenPauseMenu(int timeoutSec = 15)
    {
        if (!EnsureForeground()) return false;
        _log.Hint("打开暂停菜单…", "Fsm");
        // 失焦自动暂停：游戏可能已自己开着暂停菜单，此时再按 Start 会把它关掉。
        // 先探测“已开”再决定是否按键（tab 条词集为主，横幅作旁证；各需连续命中防单帧误判）。
        if (MenuOpenEvidence(3000))
        {
            _log.Info("菜单已处于打开状态（含失焦自动暂停，直接沿用）", "Fsm");
            return true;
        }
        int openTries = Math.Max(1, _settings.Automation.MaxActionRetries);
        for (int attempt = 0; attempt < openTries; attempt++)
        {
            // 每次按键前重夺前台（首次 Start 可能被启动时的焦点争夺吞掉——日志曾见 Start 后 8s 无菜单）
            if (!EnsureForeground()) { Thread.Sleep(800); continue; }
            _log.Info("按 Start 开菜单（第 " + (attempt + 1) + " 次）", "Fsm");
            _input.Tap(PadButton.Start, _settings.Automation.PressMs);
            Thread.Sleep(400);
            if (MenuOpenEvidence(4000))
            {
                _log.Info("菜单已打开", "Fsm");
                return true;
            }
            // Start 是开关：若先前自动暂停已开但探测漏了，这一下会把它关掉；
            // 特征是“现在确认关闭”→ 立刻再按一次把它开回来。
            if (PollMenuOpen(false, 1, 1200))
            {
                _log.Info("Start 疑似把已开的自动暂停关了，补按一次开回…", "Fsm");
                if (!EnsureForeground()) { Thread.Sleep(800); continue; }
                _input.Tap(PadButton.Start, _settings.Automation.PressMs);
                Thread.Sleep(400);
                if (MenuOpenEvidence(4000))
                {
                    _log.Info("菜单已打开（补按生效）", "Fsm");
                    return true;
                }
            }
            LogDiag("开菜单未生效（第 " + (attempt + 1) + " 次尝试后仍未见到菜单）");
        }
        // 慢确认兜底（加载/过渡帧较长时最后再等一轮）
        if (MenuOpenEvidence(Math.Max(3000, Math.Min(8000, timeoutSec * 1000 / 2))))
        {
            _log.Info("菜单确认（慢确认）", "Fsm");
            return true;
        }
        _log.Warn("打开暂停菜单失败（多次尝试）", "Fsm");
        LogDiag("开菜单失败现场");
        return false;
    }

    /// <summary>暂停“已开”双证据探测：tab 条词集 或 横幅(故事/在线/退出弹窗)。各需连续命中防单帧误判。</summary>
    private bool MenuOpenEvidence(int windowMs)
    {
        var stw = System.Diagnostics.Stopwatch.StartNew();
        int stripStreak = 0, bannerStreak = 0;
        while (stw.ElapsedMilliseconds < windowMs)
        {
            var f = _window.CaptureClient();
            bool stripOpen = IsMenuOpenFrame(f);
            if (stripOpen)
            {
                if (++stripStreak >= 2) return true;
            }
            else stripStreak = 0;
            if (f.IsValid)
            {
                var r = _reader.Read(f, withOcr: false, includeHome: false);
                if (r is not null && IsPauseBanner(r.Kind))
                {
                    if (++bannerStreak >= 2) return true;
                }
                else bannerStreak = 0;
            }
            Thread.Sleep(220);
        }
        return false;
    }

    public bool ClosePauseMenu(int timeoutSec = 14)
    {
        if (!EnsureForeground()) return false;
        _log.Hint("关闭暂停菜单…", "Fsm");
        if (PollMenuOpen(false, 2, 2000))
        {
            _log.Info("菜单已关闭", "Fsm");
            return true;
        }
        bool closed = false;
        int closeTries = Math.Max(1, _settings.Automation.MaxActionRetries);
        for (int attempt = 0; attempt < closeTries; attempt++)
        {
            var key = attempt % 2 == 0 ? PadButton.B : PadButton.Start;
            _log.Info("按 " + (key == PadButton.B ? "B(取消)" : "Start") + " 关菜单（第 " + (attempt + 1) + " 次）", "Fsm");
            _input.Tap(key, _settings.Automation.PressMs + attempt * 60);
            if (PollMenuOpen(false, 2, 4500)) { closed = true; break; }
            if (_watcher.WaitUntil(k => !IsPauseBanner(k),
                    TimeSpan.FromSeconds(Math.Max(3, timeoutSec / 4)), withOcr: false,
                    stableFrames: 1, includeHome: false) is not null
                && PollMenuOpen(false, 1, 1200))
            {
                closed = true;
                break;
            }
        }
        if (!closed)
        {
            _log.Warn("关闭暂停菜单失败（多次尝试后仍在）", "Fsm");
            return false;
        }
        Thread.Sleep(800);
        if (PollMenuOpen(true, 2, 3000))
        {
            _log.Warn("菜单复现（疑似失焦自动暂停），补关一次", "Fsm");
            _input.Tap(PadButton.B, _settings.Automation.PressMs);
            bool fixed2 = PollMenuOpen(false, 2, 4000);
            _log.Info(fixed2 ? "补关成功" : "补关后仍在（尽力）", "Fsm");
        }
        return true;
    }

    public ReadResult? WaitFor(UiKind kind, int timeoutSec, bool withOcr = true)
    {
        var r = _watcher.WaitUntil(k => k == kind, TimeSpan.FromSeconds(timeoutSec), withOcr);
        if (r is not null) LastBelief = r.Kind;
        return r;
    }

    public bool ConfirmExitDialogIfPresent(int timeoutSec = 15)
    {
        var r = _watcher.WaitUntil(k => k == UiKind.ExitDialog,
            TimeSpan.FromSeconds(Math.Max(3, timeoutSec)), withOcr: true, stableFrames: 1);
        if (r is null)
        {
            _log.Info("未出现退出弹窗", "Fsm");
            return false;
        }
        _log.Okay("看到退出弹窗，确认（A）…", "Fsm");
        _input.Tap(PadButton.A, 250);
        var gone = _watcher.WaitUntil(
            k => k is UiKind.Loading or UiKind.StoryTitle or UiKind.OnlineTitle or UiKind.Unknown or UiKind.TitleHome,
            TimeSpan.FromSeconds(timeoutSec), withOcr: false);
        return gone is not null;
    }

    // ============ Tab ============

    public bool EnsureTab(string targetTab, int maxPolls = 60)
    {
        if (!EnsureForeground()) return false;
        string tc = TextMatcher.Clean(targetTab);
        bool moved = false;
        string? prev = null;
        int stall = 0;
        int movedTotal = 0;
        int resetBudget = 2;
        for (int i = 0; i < maxPolls; i++)
        {
                string? cur = null;
            for (int a = 0; a < 3 && string.IsNullOrEmpty(cur); a++)
            {
                var frame = _window.CaptureClient();
                var read = frame.IsValid ? _tab.Read(frame) : null;
                if (read is not null)
                {
                    NoteListTop(read);
                    cur = read.Selected;
                }
                if (string.IsNullOrEmpty(cur)) Thread.Sleep(350);
            }
            if (cur is not null && TextMatcher.FuzzyEqual(cur, tc))
            {
                if (!moved)
                {
                    _log.Okay("打开即已在 tab [" + targetTab + "]（未按过肩键，视为已进入）", "Fsm");
                    return true;
                }
                _log.Info("tab [" + targetTab + "] 已高亮，按 A 进入列表…", "Fsm");
                _input.Tap(PadButton.A, _settings.Automation.PressMs + 80);
                Thread.Sleep(900);
                // 按 A 后验证：目标 tab 仍高亮 或 列表焦点行已出现（说明已进入该 tab 的列表）。
                // 单独一次条带读错（地图小字整字漏读）不应判为“进错”。
                bool entered = false;
                string? lastSel = null;
                for (int v = 0; v < 3 && !entered; v++)
                {
                    var fv2 = _window.CaptureClient();
                    var rv2 = fv2.IsValid ? _tab.Read(fv2) : null;
                    if (rv2 is not null)
                    {
                        NoteListTop(rv2);
                        lastSel = rv2.Selected;
                        if (!string.IsNullOrEmpty(lastSel) && TextMatcher.FuzzyEqual(lastSel, tc)) entered = true;
                    }
                    if (!entered && fv2.IsValid && CurrentRow(fv2) is not null) entered = true;
                    if (!entered) Thread.Sleep(450);
                }
                if (!entered)
                {
                    _log.Warn("按 A 后既未见目标 tab 也无列表焦点行（条带: " + (lastSel ?? "?") + "），返回失败走回卷", "Fsm");
                    return false;
                }
                _log.Okay("已按 A 进入 tab [" + targetTab + "]", "Fsm");
                return true;
            }

            if (cur is not null && cur == prev)
            {
                stall++;
                if (stall >= 3)
                {
                    // 识别连续同 tab：不再按 B 退层（子菜单里 RB/LB 同样能切 tab，B 反而可能关掉整个暂停）。
                    _log.Warn("连续 " + stall + " 次同 tab [" + cur + "]，不按 B：稍候后继续 RB 切换", "Fsm");
                    stall = 0;
                    prev = null;
                    Thread.Sleep(700);
                    continue; // 下一轮按正常分支 RB 前进
                }
                Thread.Sleep(400);
                continue;
            }
            stall = 0;
            prev = cur ?? prev;

            _log.Info("当前高亮 tab: " + (cur ?? "?") + "，按 RB 切下一个", "Fsm");
            _input.Tap(PadButton.RightShoulder, _settings.Automation.PressMs);
            moved = true;
            movedTotal++;
            Thread.Sleep(750);

            if (movedTotal >= 16 && resetBudget-- > 0)
            {
                _log.Warn("移动 " + movedTotal + " 次仍未命中，整菜单重置（先关后开）…", "Fsm");
                ClosePauseMenu(8);
                Thread.Sleep(700);
                OpenPauseMenu(10);
                movedTotal = 0;
                prev = null;
                moved = false;
            }
        }
        _log.Warn("未能切换到 tab [" + targetTab + "]", "Fsm");
        return false;
    }

    private bool EnterFocusedItem(int waitMs = 1200)
    {
        _log.Info("按 A 进入当前项…", "Fsm");
        _input.Tap(PadButton.A, _settings.Automation.PressMs);
        var stw = System.Diagnostics.Stopwatch.StartNew();
        while (stw.ElapsedMilliseconds < waitMs)
        {
            var f = _window.CaptureClient();
            if (f.IsValid)
            {
                var trb = _tab.Read(f);
                if (trb is not null) NoteListTop(trb);
                if (CurrentRow(f) is not null) return true;
            }
            Thread.Sleep(200);
        }
        return false;
    }

    // ============ 列表导航（光标计数） ============

    public string? NavigateToListTarget(string[] items, string target, int maxSteps = 0)
    {
        if (!EnsureForeground()) return null;
        string tc = TextMatcher.Clean(target);
        int len = items.Length;
        int targetIdx = -1;
        for (int i = 0; i < len; i++)
            if (TextMatcher.FuzzyEqual(items[i], tc)) { targetIdx = i; break; }
        if (targetIdx < 0) { _log.Warn("目标不在列表中: " + target, "Fsm"); return null; }

        int cursor = -1;
        string? firstLabel = null;
        int anchorTries = Math.Max(1, _settings.Automation.MaxActionRetries);
        for (int attempt = 0; attempt < anchorTries && cursor < 0; attempt++)
        {
            var f0 = _window.CaptureClient();
            var r0 = f0.IsValid ? CurrentRow(f0) : null;
            if (r0?.Label is not null)
            {
                firstLabel = TextMatcher.Clean(r0.Label);
                cursor = MenuRegistry.IndexOf(items, firstLabel);
            }
            if (cursor < 0) Thread.Sleep(300);
        }
        if (cursor < 0)
        {
            _log.Info("初始焦点读不到，向上扫描找锚点…", "Fsm");
            for (int i = 0; i < len && cursor < 0; i++)
            {
                _input.Tap(PadButton.DPadUp, _settings.Automation.PressMs);
                Thread.Sleep(420);
                var fh = _window.CaptureClient();
                var rh = fh.IsValid ? CurrentRow(fh) : null;
                if (rh?.Label is not null)
                {
                    string lb = TextMatcher.Clean(rh.Label);
                    firstLabel = lb;
                    cursor = MenuRegistry.IndexOf(items, lb);
                }
            }
        }
        if (cursor < 0)
        {
            if (len <= 6)
            {
                _log.Info("小列表(≤" + len + ")锚定失败，假定焦点在首项（进入 tab 后通常置顶）", "Fsm");
                cursor = 0;
            }
            else
            {
                _log.Warn("无法锚定当前列表行（OCR 全部读空），中止导航", "Fsm");
                LogDiag("列表锚定失败现场");
                return null;
            }
        }
        _log.Info("导航起始: 焦点=[" + (firstLabel ?? "?") + "] idx=" + cursor + " 目标[" + target + "] idx=" + targetIdx, "Fsm");

        int safety = maxSteps > 0 ? maxSteps : len * 2 + 4;
        int steps = 0;
        string? lastLabel = firstLabel;
        while (cursor != targetIdx && steps < safety)
        {
            steps++;
            int down = (targetIdx - cursor + len) % len;
            int up = (cursor - targetIdx + len) % len;
            bool goDown = down <= up;
            _input.Tap(goDown ? PadButton.DPadDown : PadButton.DPadUp, _settings.Automation.PressMs);
            Thread.Sleep(420);
            cursor = (cursor + (goDown ? 1 : -1) + len) % len;

            var f = _window.CaptureClient();
            var row = f.IsValid ? CurrentRow(f) : null;
            if (row?.Label is not null)
            {
                string lab = TextMatcher.Clean(row.Label);
                lastLabel = lab;
                int idx = MenuRegistry.IndexOf(items, lab);
                if (idx >= 0 && idx != cursor)
                {
                    _log.Info("校准: 读到 [" + lab + "] idx=" + idx + "（光标 " + cursor + "→" + idx + "）", "Fsm");
                    cursor = idx;
                }
                else if (idx >= 0)
                {
                    _log.Info("步 " + steps + ": [" + lab + "] (idx " + idx + ")", "Fsm");
                }
            }
        }
        if (cursor == targetIdx)
        {
            _log.Okay("已到目标 [" + target + "]（idx " + cursor + "）", "Fsm");
            return target;
        }
        _log.Warn("列表导航未达目标 [" + target + "]，最后标签: " + (lastLabel ?? "无"), "Fsm");
        LogDiag("列表导航失败现场: 最后标签=" + (lastLabel ?? "无"));
        return null;
    }

    // ============ 流程（含回卷重试） ============

    /// <summary>重读暂停 tab 条数次直到拿到 故事/在线 判定（容忍单帧 OCR 读空/残字）。</summary>
    private UiKind? ReadMenuModeStable(int tries = 3)
    {
        UiKind? md = null;
        for (int i = 0; i < tries; i++)
        {
            md = GuessModeFromMenu();
            if (md is UiKind.StoryTitle or UiKind.OnlineTitle) break;
            Thread.Sleep(650);
        }
        return md;
    }

    private bool EnsureStoryOnce(int timeoutSec = 120)
    {
        _log.Hint("流程：确保故事模式（在线→故事）", "Fsm");
        if (!OpenPauseMenu()) return false;
        // 模式判定优先用 tab 条词集：故事含“简讯”、在线含“职业”。
        // 若已在故事模式（可能上一轮已退出成功但到达确认超时误判），直接成功，不再找“退至故事模式”。
        var m0 = ReadMenuModeStable(3);
        if (m0 == UiKind.StoryTitle)
        {
            _log.Okay("暂停菜单即故事模式（tab词证），确认已在线下", "Fsm");
            ClosePauseMenu(8);
            return true;
        }
        if (m0 == UiKind.OnlineTitle) { /* 需要走退出流程 */ }
        else
        {
            // 条带一直读不出（过渡/动画帧）：关掉重开一次再试；仍不行才按在线处理
            _log.Warn("暂停条带模式读不到（" + (m0?.ToString() ?? "null") + "），关开一次重读", "Fsm");
            ClosePauseMenu(6);
            Thread.Sleep(800);
            if (!OpenPauseMenu()) return false;
            m0 = ReadMenuModeStable(3);
            if (m0 == UiKind.StoryTitle)
            {
                _log.Okay("重开后即故事模式（tab词证），确认已在线下", "Fsm");
                ClosePauseMenu(8);
                return true;
            }
        }
        if (!EnsureTab("在线")) { ClosePauseMenu(8); return false; }
        var last = NavigateToListTarget(MenuRegistry.OnlineMainItems, "退至故事模式");
        if (last is null || !TextMatcher.FuzzyEqual(last, "退至故事模式"))
        {
            ClosePauseMenu(8);
            return false;
        }
        _log.Info("已聚焦 退至故事模式，按 A 触发退出", "Fsm");
        _input.Tap(PadButton.A, _settings.Automation.PressMs);
        if (!ConfirmExitDialogIfPresent()) { ClosePauseMenu(8); return false; }
        bool arrived = WaitStoryReachable(timeoutSec);
        if (arrived) ClosePauseMenu(8);
        _log.Okay("到达故事模式: " + arrived, "Fsm");
        return arrived;
    }

    private bool WaitCloudCue(IAudioCueSource audio, int timeoutSec = 150)
    {
        if (audio is null || !audio.IsAvailable)
        {
            _log.Warn("音频 cue 不可用（无 GTA 音频会话）", "Fsm");
            return false;
        }
        _log.Info("等待“下云”声音 cue（安静→响亮）…", "Fsm");
        var det = new CloudCueDetector(_log, _settings.Audio);
        det.Begin();
        int step = Math.Max(20, _settings.Audio.SampleIntervalMs);
        int total = timeoutSec * 1000;
        int waited = 0;
        while (waited < total)
        {
            if (det.Feed(audio.CurrentPeak)) return true;
            Thread.Sleep(step);
            waited += step;
        }
        _log.Warn("等待下云声音超时(" + timeoutSec + "s)", "Fsm");
        return false;
    }

    public bool EnsureOnlineInvite(int timeoutSec = 240, IAudioCueSource? audio = null,
        Action? onCloudCue = null, bool waitCue = false)
    {
        if (_settings.QuickSwitch.EnableOnlineEntry
            && TryQuickSwitch(QuickGoal.Online, timeoutSec, audio, onCloudCue, waitCue))
            return true;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            if (EnsureOnlineInviteOnce(timeoutSec, audio, onCloudCue, waitCue)) return true;
            _log.Warn("进在线第 " + attempt + " 次失败，回卷菜单后重试…", "Fsm");
            ClosePauseMenu(8);
            Thread.Sleep(1500);
        }
        return false;
    }

    private bool EnsureOnlineInviteOnce(int timeoutSec = 240, IAudioCueSource? audio = null,
        Action? onCloudCue = null, bool waitCue = false)
    {
        _log.Hint("流程：进入在线·仅限邀请战局", "Fsm");
        if (!OpenPauseMenu()) return false;
        if (!EnsureTab("在线")) { ClosePauseMenu(8); return false; }
        // 模式判定优先用 tab 条词集（故事含“简讯”、在线含“职业”），锚点行 OCR 不可靠
        var mode = GuessModeFromMenu();
        bool fromStory = mode == UiKind.StoryTitle;
        bool fromOnline = mode == UiKind.OnlineTitle;
        if (!fromStory && !fromOnline)
        {
            string? first = null;
            var f0 = _window.CaptureClient();
            var row0 = f0.IsValid ? CurrentRow(f0) : null;
            if (row0?.Label is not null) first = TextMatcher.Clean(row0.Label);
            fromStory = MenuRegistry.IndexOf(MenuRegistry.StoryOnlineTabItems, first ?? "") >= 0
                        || MenuRegistry.IndexOf(MenuRegistry.StoryJoinOnlineItems, first ?? "") >= 0;
            fromOnline = !fromStory;
        }
        _log.Info("判定当前为 " + (fromStory ? "故事(在线 tab)" : fromOnline ? "在线" : "未知(按在线处理)"), "Fsm");
        if (!fromStory) fromOnline = true;

        if (fromStory)
        {
            var last = NavigateToListTarget(MenuRegistry.StoryOnlineTabItems, "进入GTA在线模式");
            if (last is null || !TextMatcher.FuzzyEqual(last, "进入GTA在线模式")) { ClosePauseMenu(8); return false; }
            if (!EnterFocusedItem()) { ClosePauseMenu(8); return false; }
            var last2 = NavigateToListTarget(MenuRegistry.StoryJoinOnlineItems, "凭邀请加入的战局");
            if (last2 is null || !TextMatcher.FuzzyEqual(last2, "凭邀请加入的战局")) { ClosePauseMenu(8); return false; }
            _input.Tap(PadButton.A, _settings.Automation.PressMs);
            // 故事→在线：选完会话类型后会弹“退出 GTAV？未保存进度将丢失”确认框
            _log.Info("等待退出 GTAV 确认弹窗…", "Fsm");
            if (!ConfirmExitDialogIfPresent()) { ClosePauseMenu(8); return false; }
            // 确认后开始切入在线：此时以“下云”声音为掐点封网（尚未可操作）
            if (waitCue && audio is not null)
            {
                if (WaitCloudCue(audio)) onCloudCue?.Invoke();
                else _log.Warn("下云 cue 未捕获（fallback：到达后补封网）", "Fsm");
            }
            bool arrived = WaitOnlineReachable(timeoutSec);
            if (arrived) ClosePauseMenu(8);
            _log.Okay("到达在线: " + arrived, "Fsm");
            return arrived;
        }
        else
        {
            var last = NavigateToListTarget(MenuRegistry.OnlineMainItems, "寻找新战局");
            if (last is null || !TextMatcher.FuzzyEqual(last, "寻找新战局")) { ClosePauseMenu(8); return false; }
            if (!EnterFocusedItem()) { ClosePauseMenu(8); return false; }
            var last2 = NavigateToListTarget(MenuRegistry.OnlineFindNewSessionItems, "仅限邀请的战局");
            if (last2 is null || !TextMatcher.FuzzyEqual(last2, "仅限邀请的战局")) { ClosePauseMenu(8); return false; }
            _input.Tap(PadButton.A, _settings.Automation.PressMs);
            if (waitCue && audio is not null)
            {
                if (WaitCloudCue(audio)) onCloudCue?.Invoke();
                else _log.Warn("下云 cue 未捕获（fallback：到达后补封网）", "Fsm");
            }
            bool arrived = WaitOnlineReachable(timeoutSec);
            if (arrived) ClosePauseMenu(8);
            _log.Okay("已切新邀请战局: " + arrived, "Fsm");
            return arrived;
        }
    }

    public bool EnsureStory(int timeoutSec = 120, bool allowQuick = true)
    {
        if (allowQuick && _settings.QuickSwitch.EnableStoryReturn
            && TryQuickSwitch(QuickGoal.Story, timeoutSec, null, null, false))
            return true;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            if (EnsureStoryOnce(timeoutSec)) return true;
            _log.Warn("确保故事第 " + attempt + " 次失败，回卷菜单后重试…", "Fsm");
            ClosePauseMenu(8);
            Thread.Sleep(1500);
        }
        return false;
    }

    /// <summary>
    /// 从容探测到达目标模式：直接轮询暂停 tab 条词证（故事=简讯 / 在线=职业），
    /// 菜单确认关闭后才按 Start（避免 toggle 已开菜单），只有连续两次见“另一模式”才判败。
    /// 不依赖横幅模板（故事/在线横幅分数常接近歧义），也不依赖 OpenPauseMenu 的整段开-关。
    /// </summary>
    private bool WaitModeReachable(UiKind want, UiKind other, string label, int timeoutSec)
    {
        _log.Info("等待到达" + label + "（网络/加载时长不定，采用从容探测）…", "Fsm");
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        int wrong = 0;
        while (DateTime.UtcNow < deadline)
        {
            var f = _window.CaptureClient();
            UiKind? md = f.IsValid ? StripModeOf(f) : null;
            if (md == want)
            {
                _log.Okay("检测到" + label + "（tab词证）", "Fsm");
                return true;
            }
            if (md == other)
            {
                wrong++;
                _log.Warn("此时仍为另一侧（第 " + wrong + " 次）", "Fsm");
                if (wrong >= 2)
                {
                    _log.Warn("多次仍见另一侧，判定切换未生效", "Fsm");
                    return false;
                }
                // 仍是另一侧：关掉菜单等下一轮，避免一直开着
                if (IsMenuOpenFrame(f)) _input.Tap(PadButton.B, _settings.Automation.PressMs);
                Thread.Sleep(3000);
                continue;
            }
            // md==null：条带读不到。可能菜单已开但 OCR 未出（短等重读），或确实关闭（才按 Start）。
            bool open = f.IsValid && IsMenuOpenFrame(f);
            if (open)
            {
                Thread.Sleep(700);   // 已开但条带未出：重读，不按 Start（防 toggle）
                continue;
            }
            // 确认关闭（连续两帧无条带）才按 Start
            if (!ConfirmMenuClosed(1200))
            {
                Thread.Sleep(400);
                continue;
            }
            if (!EnsureForeground()) { Thread.Sleep(3000); continue; }
            _log.Info("菜单未开，按 Start 探测…", "Fsm");
            _input.Tap(PadButton.Start, _settings.Automation.PressMs);
            Thread.Sleep(2500);
        }
        _log.Warn("等待到达" + label + "超时(" + timeoutSec + "s)", "Fsm");
        return false;
    }

    /// <summary>确认暂停菜单当前是关闭的（连续 2 帧读不到 tab 条）。</summary>
    private bool ConfirmMenuClosed(int windowMs)
    {
        var stw = System.Diagnostics.Stopwatch.StartNew();
        int consec = 0;
        while (stw.ElapsedMilliseconds < windowMs)
        {
            var f = _window.CaptureClient();
            bool open = IsMenuOpenFrame(f);
            if (!open)
            {
                consec++;
                if (consec >= 2) return true;
            }
            else consec = 0;
            Thread.Sleep(180);
        }
        return false;
    }

    /// <summary>只凭 tab 条词集判模式（故事含“简讯”、在线含“职业”）；菜单没开/读不到返回 null。</summary>
    private UiKind? StripModeOf(Frame frame)
    {
        if (!frame.IsValid) return null;
        try
        {
            var tr = _tab.Read(frame);
            if (tr is { StripWords.Length: > 2 })
            {
                NoteListTop(tr);
                string s = TextMatcher.Clean(tr.StripWords);
                if (s.Contains("简") && s.Contains("讯")) return UiKind.StoryTitle;
                if (s.Contains("职") && s.Contains("业")) return UiKind.OnlineTitle;
            }
        }
        catch { }
        return null;
    }

    private bool WaitStoryReachable(int timeoutSec)
        => WaitModeReachable(UiKind.StoryTitle, UiKind.OnlineTitle, "故事模式", timeoutSec);

    private bool WaitOnlineReachable(int timeoutSec)
        => WaitModeReachable(UiKind.OnlineTitle, UiKind.StoryTitle, "在线", timeoutSec);

    /// <summary>Flow 用：条带驱动的到达等待（故事=简讯 / 在线=职业，不依赖横幅）。</summary>
    public bool WaitArriveMode(string mode, int timeoutSec)
        => mode == "story" ? WaitStoryReachable(timeoutSec)
           : mode == "online" ? WaitOnlineReachable(timeoutSec)
           : false;

    /// <summary>封云存档后等待左下“保存失败”类提示出现（failsafe 可在后续接 信息→通知）。
    /// 阶段1：左下条带快速轮询（toast 小字整帧 OCR 会读碎成“保存灾”且每帧 5-9s，条带 2x 放大约 0.2-0.5s/帧）；
    /// 阶段2：剩余时间退回整帧观察，兜底中央断连大弹窗（不在左下条带内）。</summary>
    public bool WaitForSaveFailToast(int timeoutSec = 30)
    {
        _log.Info("等待 保存失败 提示…", "Fsm");
        var start = DateTime.UtcNow;
        double stripBudgetSec = Math.Max(6.0, timeoutSec * 0.7);
        int tries = 0;
        string? lastText = null;
        while (DateTime.UtcNow - start < TimeSpan.FromSeconds(stripBudgetSec))
        {
            tries++;
            var frame = _window.CaptureClient();
            if (!frame.IsValid)
            {
                Thread.Sleep(500);
                continue;
            }
            string? txt = _reader.ScanToastStrip(frame);
            if (txt is not null && !string.IsNullOrWhiteSpace(txt))
            {
                lastText = txt;
                if (SaveFailWords(txt))
                {
                    _log.Okay("检测到 保存失败（左下提示）: " + ShortText(txt), "Fsm");
                    return true;
                }
            }
            Thread.Sleep(450);
        }
        _log.Info("左下条带 " + tries + " 次未命中，退回整帧观察断连警报…", "Fsm");
        var remain = start.AddSeconds(timeoutSec) - DateTime.UtcNow;
        if (remain > TimeSpan.Zero)
        {
            var r = _watcher.WaitUntil(k => k is UiKind.SaveFail or UiKind.DisconnectAlert,
                remain, withOcr: true, stableFrames: 1);
            if (r is not null)
            {
                _log.Okay("检测到 " + r.Kind + "（整帧，云存档被阻断生效）", "Fsm");
                return true;
            }
        }
        _log.Warn("未检测到 保存失败/断连 提示（超时 " + timeoutSec + "s"
            + (lastText is null ? "，条带无文字" : "，条带文字: " + ShortText(lastText)) + "）", "Fsm");
        return false;
    }

    /// <summary>条带 OCR 文本规整后按关键词判定（保存失败 / 断连警报类）。
    /// OCR 不稳定（败→灾/丢字），故 保存失败 用容错组合：保存+失败/灾/矢/无法保存/云服务器+被保存。</summary>
    private static bool SaveFailWords(string text)
    {
        var norm = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        if (norm.Contains("保存失败") || norm.Contains("保存灾") || norm.Contains("保存矢")
            || norm.Contains("无法保存") || norm.Contains("保存不了"))
            return true;
        // 容错：条带里同时出现“保存”与失败类残字，或 云服务器+将被保存 的主干
        if ((norm.Contains("保存") && (norm.Contains("失败") || norm.Contains("灾")
                || norm.Contains("矢") || norm.Contains("丢失")))
            || (norm.Contains("云服务器") && norm.Contains("被保存")))
            return true;
        return norm.Contains("无法保持与游戏服务器的连接")
            || norm.Contains("无法从游戏服务下载") || norm.Contains("无法连接服务进行验证")
            || norm.Contains("连接丢失");
    }

    private static string ShortText(string text)
    {
        var s = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 90 ? s : s.Substring(0, 90) + "…";
    }

// ============ 快捷切换（先有限试手势，失败回退传统暂停菜单） ============
    private enum QuickGoal { Online, Story }

    private QuickLookDir DirFor(QuickGoal goal)
    {
        bool up = (goal == QuickGoal.Online) == _settings.QuickSwitch.OnlineIsUp;
        return up ? QuickLookDir.Up : QuickLookDir.Down;
    }

    /// <summary>手势前置（与暂停菜单路径同规则）：先确保游戏窗口在前台；若失焦自动暂停开着则先关菜单
    /// （轮盘快捷手势只在自由漫游有效），关不掉或处于主菜单则放弃（由调用方回退传统流程）。</summary>
    private bool PrepareForGesture()
    {
        if (!EnsureForeground())
        {
            _log.Warn("手势前无法把游戏窗口带到前台（游戏未运行/被遮挡？），放弃快捷手势", "Fsm");
            return false;
        }
        if (FastMenuOpenNow())
        {
            _log.Warn("暂停菜单开着（失焦自动暂停），快速关闭后再执行快捷手势…", "Fsm");
            if (!QuickDismissPause(8))
            {
                _log.Warn("快速关闭暂停失败，放弃快捷手势，回退传统流程", "Fsm");
                return false;
            }
        }
        // 只用“顶部 tab 条是否在读”做廉价判定：
        // 原来这里调用 _reader.Read(includeHome:true) 会跑整帧横幅 NCC（1080p 下 10~40s），
        // 但手势前摇根本不需要模式判定，只需要知道“是不是主菜单/暂停菜单”。
        var f3 = _window.CaptureClient();
        if (f3.IsValid && _tab.IsTabStripPresent(f3))
        {
            _log.Info("手势前：顶部 tab 条在读，仍在菜单/主菜单上下文（交给上层回退）", "Fsm");
        }
        if (f3.IsValid && _reader.HasBannerCache(f3) && _reader.FastBannerPresent(f3))
        {
            _log.Info("手势前：记忆框里仍有横幅（菜单上下文）", "Fsm");
        }
        return true;
    }

    /// <summary>当前帧暂停菜单是否开着：条带 2x 快检（约 0.2~1s），不走全帧词OCR。</summary>
    private bool FastMenuOpenNow()
    {
        var f = _window.CaptureClient();
        return f.IsValid && _tab.IsTabStripPresent(f);
    }

    /// <summary>低成本关暂停菜单：条带快检轮询 + B/Start 交替（避免 ClosePauseMenu 的多轮全帧词OCR，前摇从 ~15s 压到 2~4s）。</summary>
    private bool QuickDismissPause(int timeoutSec)
    {
        _log.Info("快速关闭暂停菜单（tab条带快检）…", "Fsm");
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(4, timeoutSec));
        int attempts = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (!FastMenuOpenNow())
            {
                _log.Info("暂停菜单已关闭（条带快检确认）", "Fsm");
                return true;
            }
            if (!EnsureForeground()) break;
            var key = attempts % 2 == 0 ? PadButton.B : PadButton.Start;
            _log.Info("关菜单按 " + (key == PadButton.B ? "B" : "Start") + "（第 " + (attempts + 1) + " 次）", "Fsm");
            _input.Tap(key, _settings.Automation.PressMs);
            int closed = 0;
            var inner = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < inner && closed < 2)
            {
                if (!FastMenuOpenNow()) closed++; else closed = 0;
                Thread.Sleep(350);
            }
            if (closed >= 2) return true;
            attempts++;
            Thread.Sleep(250);
        }
        return !FastMenuOpenNow();
    }

    /// <summary>流程层入口：按配置先试快捷手势（按住 下方向键/Alt + 右摇杆/鼠标推向目标 + 同帧松）。
    /// 手势产生确认弹窗并按 A 完成到达验证才算成功；否则回退调用方的传统暂停菜单流程。</summary>
    private bool TryQuickSwitch(QuickGoal goal, int timeoutSec, IAudioCueSource? audio,
        Action? onCloudCue, bool waitCue)
    {
        string label = goal == QuickGoal.Online ? "进线上" : "回故事(线下)";
        int attempts = Math.Max(1, _settings.QuickSwitch.Attempts);
        for (int a = 0; a < attempts; a++)
        {
            _log.Hint("先试快捷切换[" + (a + 1) + "/" + attempts + "]（" + label + "）：按住 下方向键 + 右摇杆"
                + (DirFor(goal) == QuickLookDir.Up ? "上" : "下") + "，同帧松开…", "Fsm");
            if (!QuickGesturePerform(DirFor(goal)))
            {
                _log.Warn("快捷手势不可用/上下文不适合，回退传统暂停菜单流程", "Fsm");
                return false;
            }
            if (!QuickWaitDialogThenArrive(goal, timeoutSec, audio, onCloudCue, waitCue)) continue;
            return true;
        }
        _log.Warn("快捷切换" + attempts + " 次未成功，回退传统暂停菜单流程", "Fsm");
        return false;
    }

    /// <summary>执行一次轮盘手势。前置：窗口前台、非暂停菜单、非主菜单（轮盘仅在自由漫游可用）。</summary>
    private bool QuickGesturePerform(QuickLookDir dir)
    {
        if (!PrepareForGesture()) return false;
        _input.Reset();
        if (!_input.TryQuickLook(dir, _settings.QuickSwitch.WheelOpenMs,
                _settings.QuickSwitch.LookHoldMs, _settings.QuickSwitch.StickMagnitude))
        {
            _log.Warn("当前输入层不支持快捷手势（需 ViGEm 手柄或键盘Alt+鼠标），回退传统流程", "Fsm");
            return false;
        }
        return true;
    }

    /// <summary>手势松手后等确认弹窗；出现→按 A 确认并走到达验证；始终未见→false（回退）。</summary>
    private bool QuickWaitDialogThenArrive(QuickGoal goal, int timeoutSec,
        IAudioCueSource? audio, Action? onCloudCue, bool waitCue)
    {
        int waitSec = Math.Max(5, _settings.QuickSwitch.DialogWaitSec);
        var stw = System.Diagnostics.Stopwatch.StartNew();
        UiKind last = UiKind.Unknown;
        while (stw.Elapsed.TotalSeconds < waitSec)
        {
            var f = _window.CaptureClient();
            if (!f.IsValid) { Thread.Sleep(400); continue; }
            var k = _reader.Read(f, withOcr: true, includeHome: false);
            if (k is null) { Thread.Sleep(400); continue; }
            last = k.Kind;
            if (k.Kind == UiKind.ExitDialog)
            {
                _log.Okay("快捷切换确认弹窗出现（" + stw.Elapsed.TotalSeconds.ToString("F1") + "s），按 A 确认…", "Fsm");
                LogDiag("快捷切换确认弹窗出现");
                _input.Tap(PadButton.A, _settings.Automation.PressMs + 60);
                return QuickArriveAfterConfirm(goal, timeoutSec, audio, onCloudCue, waitCue);
            }
            Thread.Sleep(450);
        }
        _log.Warn("快捷切换后 " + waitSec + "s 未见确认弹窗（末状态 " + last + "），回退传统流程", "Fsm");
        LogDiag("快捷切换等待弹窗失败现场: 末状态=" + last);
        return false;
    }

    /// <summary>确认后收尾：与暂停菜单路径共用 cue / 到达验证 / 关菜单。</summary>
    private bool QuickArriveAfterConfirm(QuickGoal goal, int timeoutSec,
        IAudioCueSource? audio, Action? onCloudCue, bool waitCue)
    {
        if (goal == QuickGoal.Online)
        {
            if (waitCue && audio is not null)
            {
                if (WaitCloudCue(audio)) onCloudCue?.Invoke();
                else _log.Warn("下云 cue 未捕获（fallback：到达后补封网）", "Fsm");
            }
            bool arrived = WaitOnlineReachable(timeoutSec);
            if (arrived) ClosePauseMenu(8);
            _log.Okay("快捷切换到达在线: " + arrived, "Fsm");
            return arrived;
        }
        bool st = WaitStoryReachable(timeoutSec);
        if (st) ClosePauseMenu(8);
        _log.Okay("快捷切换到达故事: " + st, "Fsm");
        return st;
    }

    /// <summary>仅“手势+确认弹窗”的独立测试（CLI --gesture up|down；自检页按钮），用于实机调参/诊断，不做任何流程。
    /// cancelAfter=true 时若出现确认弹窗会按 B 取消（自检页“不真切换”模式）。</summary>
    public bool TryQuickGestureStandalone(string upOrDown, int dialogWaitSec = 0, bool cancelAfter = false)
    {
        QuickLookDir dir = (upOrDown ?? "").ToLowerInvariant() switch
        {
            "up" or "online" => QuickLookDir.Up,
            "down" or "story" => QuickLookDir.Down,
            _ => QuickLookDir.Up,
        };
        int wait = dialogWaitSec > 0 ? dialogWaitSec : Math.Max(8, _settings.QuickSwitch.DialogWaitSec);
        _log.Hint("快捷手势独立测试：按住 下方向键 + 右摇杆" + (dir == QuickLookDir.Up ? "上" : "下")
            + "，同帧松开，等确认弹窗 ≤" + wait + "s（不进入流程）", "Fsm");
        if (!PrepareForGesture()) { _log.Warn("手势前置条件不满足，放弃本次测试", "Fsm"); return false; }
        var f0 = _window.CaptureClient();
        if (f0.IsValid)
        {
            var k0 = _reader.Read(f0, withOcr: false, includeHome: false);
            _log.Info("手势前画面: " + (k0?.Kind.ToString() ?? "无") + "（抓帧 " + _window.LastMethod + "）", "Fsm");
        }
        _input.Reset();
        if (!_input.TryQuickLook(dir, _settings.QuickSwitch.WheelOpenMs,
                _settings.QuickSwitch.LookHoldMs, _settings.QuickSwitch.StickMagnitude))
        {
            _log.Warn("输入层不支持快捷手势", "Fsm");
            return false;
        }
        var sw0 = System.Diagnostics.Stopwatch.StartNew();
        bool seen = false;
        while (sw0.Elapsed.TotalSeconds < wait)
        {
            var f = _window.CaptureClient();
            var k = f.IsValid ? _reader.Read(f, withOcr: true, includeHome: false) : null;
            if (k is not null && k.Kind == UiKind.ExitDialog)
            {
                seen = true;
                _log.Okay("确认弹窗出现 @ " + sw0.Elapsed.TotalSeconds.ToString("F1") + "s", "Fsm");
                LogDiag("手势确认弹窗出现");
                if (cancelAfter)
                {
                    _log.Info("按 B 取消该弹窗（不真切换）…", "Fsm");
                    DismissDialog();
                }
                break;
            }
            Thread.Sleep(400);
        }
        if (!seen)
        {
            var tail = _watcher.ReadOnce(withOcr: true, includeHome: false);
            _log.Warn("未见确认弹窗（等 " + wait + "s，末状态 " + (tail?.Kind.ToString() ?? "无") + "）", "Fsm");
        }
        return seen;
    }

    public sealed record MenuSnapshot(UiKind Kind, double StoryScore, double OnlineScore,
        string? SelectedTab, int WhiteX0, int WhiteX1, string StripWords, string? FocusLabel, bool MenuOpen);

    /// <summary>只读菜单快照（自检/诊断用）：横幅判定 + tab 条读数 + 焦点行标签，不按任何键。</summary>
    public MenuSnapshot ReadMenuSnapshot()
    {
        var f = _window.CaptureClient();
        if (!f.IsValid) return new MenuSnapshot(UiKind.Unknown, 0, 0, null, -1, -1, "", null, false);
        var r = _reader.Read(f, withOcr: false, includeHome: true);
        var tr = _tab.Read(f);
        string? row = null;
        try { row = CurrentRow(f)?.Label; } catch { }
        bool open = tr is { StripWords.Length: > 1 };
        return new MenuSnapshot(r?.Kind ?? UiKind.Unknown, r?.StoryScore ?? 0, r?.OnlineScore ?? 0,
            tr?.Selected, tr?.WhiteX0 ?? -1, tr?.WhiteX1 ?? -1, tr?.StripWords ?? "", row, open);
    }

    /// <summary>诊断用：EnsureTab → 按 A 进列表 → 读焦点行 → 按 B 退回。只在列表内读数，不选任何会改状态的项。</summary>
    public bool ProbeEnterListAndBack(string tabName, out string? rowLabel)
    {
        rowLabel = null;
        if (!EnsureTab(tabName)) return false;
        _input.Tap(PadButton.A, _settings.Automation.PressMs + 60);
        Thread.Sleep(900);
        int probeTries = Math.Max(1, _settings.Automation.MaxActionRetries);
        for (int i = 0; i < probeTries && rowLabel is null; i++)
        {
            var f = _window.CaptureClient();
            if (f.IsValid) rowLabel = CurrentRow(f)?.Label;
            if (rowLabel is null) Thread.Sleep(400);
        }
        _input.Tap(PadButton.B, _settings.Automation.PressMs);
        Thread.Sleep(700);
        return rowLabel is not null;
    }

    /// <summary>按 B 取消当前确认弹窗（诊断用：测手势但不真切换）。</summary>
    public bool DismissDialog(int timeoutSec = 12)
    {
        _input.Tap(PadButton.B, _settings.Automation.PressMs);
        var r = _watcher.WaitUntil(k => k != UiKind.ExitDialog, TimeSpan.FromSeconds(Math.Max(3, timeoutSec)),
            withOcr: true, stableFrames: 2, includeHome: false);
        bool ok = r is not null;
        _log.Info(ok ? "弹窗已取消" : "弹窗取消确认超时（继续）", "Fsm");
        return ok;
    }

    // ============ 探测 ============

    public bool Probe(int cycles = 2)
    {
        for (int i = 0; i < cycles; i++)
        {
            _log.Hint("探测循环 [" + (i + 1) + "/" + cycles + "]", "Fsm");
            if (!OpenPauseMenu()) { _log.Warn("探测失败：打不开暂停菜单", "Fsm"); return false; }
            var kind = _watcher.WaitUntil(_ => true, TimeSpan.FromSeconds(8),
                withOcr: false, stableFrames: 1, includeHome: false);
            if (kind is not null) { LastBelief = kind.Kind; _log.Info("当前状态: " + kind.Kind, "Fsm"); }
            if (!ClosePauseMenu()) { _log.Warn("探测失败：关不掉暂停菜单", "Fsm"); return false; }
            Thread.Sleep(1200);
        }
        _log.Okay("状态机探测完成", "Fsm");
        return true;
    }
}
