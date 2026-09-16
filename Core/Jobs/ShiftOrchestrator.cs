using AutoPickup.Config;
using AutoPickup.Core.Fsm;
using AutoPickup.Core.Audio;
using AutoPickup.Core.Net;
using AutoPickup.Logging;

namespace AutoPickup.Core.Jobs;

/// <summary>
/// 卡大仓班次编排（P4 骨架）。
/// 玩家准备阶段：手动指派员工取货后下线（>48 分钟后任意时刻启动皆可）。
/// 每轮：关防火墙→进在线(仅限邀请)→自由操作临界(下云≈可开菜单)后立即开防火墙封云存档→
/// 验证“保存失败”提示→回故事(线下)→恢复联网→停留→下一轮。
/// 说明：只封存档服IP，故仍能进线上/收“已完成取货”，但收货不被云端记录 → 下轮可再领。
/// </summary>
public sealed class ShiftOrchestrator
{
    private readonly NetmodeMachine _machine;
    private readonly FirewallController _fw;
    private readonly IAudioCueSource _audio;
    private readonly AppSettings _settings;
    private readonly LogBus _log;
    private readonly AutoPickup.Core.Flow.FlowEngine? _atoms;
    private readonly AutoPickup.Core.Flow.FlowProgram? _atomsFlow;

    public ShiftOrchestrator(NetmodeMachine machine, FirewallController fw, IAudioCueSource audio,
        AppSettings settings, LogBus log,
        AutoPickup.Core.Flow.FlowEngine? atoms = null, AutoPickup.Core.Flow.FlowProgram? atomsFlow = null)
    {
        _machine = machine;
        _fw = fw;
        _audio = audio;
        _settings = settings;
        _log = log;
        _atoms = atoms;
        _atomsFlow = atomsFlow;
    }

    /// <summary>当前是否走原子引擎（新流程）。缺流程或设置成 legacy 时回退旧流程。</summary>
    public bool UsingAtoms =>
        _settings.Shift.Engine.Equals("atoms", StringComparison.OrdinalIgnoreCase)
        && _atoms is not null && _atomsFlow is not null;

    /// <summary>执行 shift 轮次。中断条件：轮数用完或某一关键腿失败。</summary>
    public bool RunShifts(int count, bool stayOnlineAfterFinished = false, Func<bool>? stopRequested = null)
    {
        var s = _settings.Shift;
        if (stopRequested?.Invoke() == true)
        {
            _log.Warn("已请求停止，班次未启动", "Job");
            return false;
        }
        if (s.WaitStartMins > 0)
        {
            _log.Hint("启动前等待 " + s.WaitStartMins + " 分钟（玩家设定）…", "Job");
            if (!WaitMinutes(s.WaitStartMins, stopRequested))
            {
                RecoverSafe();
                return false;
            }
        }

        var failedRounds = new List<int>();
        for (int i = 0; i < count; i++)
        {
            if (stopRequested?.Invoke() == true)
            {
                _log.Warn("用户请求停止（轮次间安全退出），先回线下并恢复联网", "Job");
                RecoverSafe();
                return false;
            }
            int round = i + 1;
            _log.Hint("==== 班次 [" + round + "/" + count + "] ====", "Job");

            // 旧流程：保持原行为（一次失败即中止），只作为回退路径
            if (!UsingAtoms)
            {
                if (!RunOneShift())
                {
                    _log.Warn("第 " + round + " 轮失败，中止（旧流程策略）", "Job");
                    RecoverSafe();
                    return false;
                }
            }
            else
            {
                // 新流程：只有“没读到保存失败”才是整批致命；其余算单轮失败——重试后继续下一轮
                int tries = 1 + Math.Max(0, s.RoundRetries);
                bool ok = false, fatal = false;
                for (int t = 1; t <= tries; t++)
                {
                    if (t > 1)
                    {
                        _log.Hint("第 " + round + " 轮重试（第 " + t + "/" + tries + " 次），先恢复同步…", "Job");
                        RecoverSafe();
                    }
                    (ok, fatal) = RunOneShiftAtoms();
                    if (ok || fatal) break;
                }
                if (fatal)
                {
                    _log.Error("第 " + round + " 轮未读到“保存失败”提示 → 到货可能已上传云存档，继续跑没有意义，停止本次班次", "Job");
                    RecoverSafe();
                    return false;
                }
                if (!ok)
                {
                    failedRounds.Add(round);
                    _log.Warn("第 " + round + " 轮未完成（不中止，继续下一轮）", "Job");
                }
            }

            if (stayOnlineAfterFinished && i == count - 1)
            {
                _log.Hint("最后一轮留在线（-o），跳过回线下", "Job");
                return failedRounds.Count == 0;
            }
            if (stopRequested is not null)
            {
                for (int k = 0; k < s.BetweenHoldSec && stopRequested() != true; k++) Thread.Sleep(1000);
            }
            else Thread.Sleep(s.BetweenHoldSec * 1000);
        }

        if (failedRounds.Count == 0)
        {
            _log.Okay("全部 " + count + " 轮完成", "Job");
            return true;
        }
        _log.Warn("班次结束：完成 " + (count - failedRounds.Count) + "/" + count + " 轮，失败 "
            + failedRounds.Count + " 轮（第 " + string.Join("、", failedRounds) + " 轮）", "Job");
        return false;
    }

    /// <summary>旧死流程跑一轮（仅在参数页把“流程引擎”改为 legacy 时使用）。</summary>
    private bool RunOneShift()
    {
        var s = _settings.Shift;
        // 0) 联网（规则禁用）
        if (s.UseFirewall) _fw.Disable();
        // 1) 进在线·仅限邀请；期间在“下云”声音临界点立即封云存档（onCloudCue 回调）
        bool cueHit = false;
        if (!_machine.EnsureOnlineInvite(timeoutSec: 300, audio: _audio,
                onCloudCue: () => { if (s.UseFirewall) { _fw.Enable(); cueHit = true; } }, waitCue: s.UseFirewall))
        {
            _log.Warn("进入在线失败", "Job");
            return false;
        }
        // 2) 兜底：若 cue 未命中则到达后补封（此时云存档可能已提交，警告）
        if (s.UseFirewall)
        {
            if (!cueHit)
            {
                _log.Warn("下云 cue 未命中，已在线上（fallback 补封云存档）", "Job");
                _fw.Enable();
            }
            // 3) 验证“保存失败”提示
            _machine.WaitForSaveFailToast(s.SaveFailWaitSec);
        }
        // 4) 回线下（故事），成功即代表本轮到货已结算/下次可重领
        if (!_machine.EnsureStory())
        {
            _log.Warn("回到故事失败", "Job");
            return false;
        }
        // 5) 回线下后恢复联网
        if (s.UseFirewall) _fw.Disable();
        return true;
    }

    /// <summary>
    /// 原子引擎跑一轮：动作只做、判据另挂、每步都有证据。失败即返回 false，由外层 RecoverSafe 收尾。
    /// 想退回旧死流程：参数页「8 班次 · 流程引擎」改 legacy（或流程页取消勾选）。
    /// </summary>
    private (bool Ok, bool Fatal) RunOneShiftAtoms()
    {
        try
        {
            var eng = _atoms!;
            var flow = _atomsFlow!;
            eng.ResetContext();                       // 清掉上一轮的证据，避免残留误读
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = eng.Run(flow);
            sw.Stop();

            // 整批致命：本轮没读到“保存失败”提示（= 到货已上传云存档）
            bool fatal = !ok && eng.Ctx.Facts.TryGetValue("wait_savefail.ok", out var sf) && sf == "false";

            string summary = GoalSummary(eng, sw.Elapsed);
            if (ok) _log.Okay("本轮完成：" + summary, "Job");
            else _log.Warn("本轮未完成：" + summary, "Job");

            if (!ok)
            {
                // 只在失败时输出完整证据与轨迹（成功时保持日志干净，失败时保留可追溯性）
                _log.Info("  证据: " + eng.Ctx.Snapshot(), "Job");
                foreach (var t in eng.Trace.TakeLast(8)) _log.Info("  轨迹: " + t, "Job");
            }
            eng.ResetFrameCache();
            return (ok, fatal);
        }
        catch (Exception e)
        {
            _log.Error("原子引擎异常: " + e.Message, "Job");
            return (false, false);
        }
    }

    /// <summary>把本轮的四个目标翻译成人话：进线上 / 封网 / 保存失败 / 回线下。</summary>
    private static string GoalSummary(AutoPickup.Core.Flow.FlowEngine eng, TimeSpan elapsed)
    {
        string Mark(string step)
            => eng.Ctx.Facts.TryGetValue(step + ".ok", out var v) && v == "true" ? "✓" : "✗";
        string cue = eng.Ctx.Facts.TryGetValue("cue", out var c) && c == "true" ? "✓" : "✗";
        return "进线上" + Mark("wait_online")
             + " · 封网" + cue
             + " · 保存失败" + Mark("wait_savefail")
             + " · 回线下" + Mark("wait_story")
             + "（用时 " + (int)elapsed.TotalMinutes + "分" + elapsed.Seconds.ToString("D2") + "秒）";
    }

    private void RecoverSafe()
    {
        try { _fw.Disable(); } catch { }
        try { _machine.EnsureStory(timeoutSec: 120, allowQuick: false); } catch { }
    }

    private bool WaitMinutes(int minutes, Func<bool>? stopRequested = null)
    {
        int total = minutes * 60;
        for (int i = total; i > 0; i--)
        {
            if (stopRequested?.Invoke() == true)
            {
                _log.Warn("等待被用户停止", "Job");
                return false;
            }
            if (i % 60 == 0) _log.Info("倒计时剩余 " + (i / 60) + " 分钟", "Job");
            Thread.Sleep(1000);
        }
        return true;
    }
}