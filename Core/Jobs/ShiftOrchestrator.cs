using AutoPickup.Config;
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
    private readonly FirewallController _fw;
    private readonly AppSettings _settings;
    private readonly LogBus _log;
    private readonly AutoPickup.Core.Flow.FlowEngine? _atoms;
    private readonly AutoPickup.Core.Flow.FlowProgram? _atomsFlow;
    /// <summary>“回到线下”流程：失败轮/停止/致命停批时的安全收尾用（先回线下、再解封）。</summary>
    private readonly AutoPickup.Core.Flow.FlowProgram? _returnFlow;

    public ShiftOrchestrator(FirewallController fw, AppSettings settings, LogBus log,
        AutoPickup.Core.Flow.FlowEngine? atoms = null, AutoPickup.Core.Flow.FlowProgram? atomsFlow = null,
        AutoPickup.Core.Flow.FlowProgram? returnFlow = null)
    {
        _fw = fw;
        _settings = settings;
        _log = log;
        _atoms = atoms;
        _atomsFlow = atomsFlow;
        _returnFlow = returnFlow;
    }

    /// <summary>执行 shift 轮次。中断条件：轮数用完、用户停止、或出现整批致命（没读到“保存失败”）。</summary>
    public bool RunShifts(int count, Func<bool>? stopRequested = null)
    {
        var s = _settings.Shift;
        if (stopRequested?.Invoke() == true)
        {
            _log.Warn("已请求停止，班次未启动", "Job");
            return false;
        }
        if (_atoms is null || _atomsFlow is null)
        {
            _log.Error("未载入原子流程（flows/shift_single.json），无法开始班次", "Job");
            return false;
        }
        if (s.WaitStartMins > 0)
        {
            _log.Hint("启动前等待 " + s.WaitStartMins + " 分钟（玩家设定）…", "Job");
            if (!WaitMinutes(s.WaitStartMins, stopRequested))
            {
                ReleaseNetwork();
                return false;
            }
        }

        var failedRounds = new List<int>();
        for (int i = 0; i < count; i++)
        {
            if (stopRequested?.Invoke() == true)
            {
                _log.Warn("用户请求停止（轮次间安全退出），先回线下并恢复联网", "Job");
                RecoverToStory();
                return false;
            }
            int round = i + 1;
            _log.Hint("==== 班次 [" + round + "/" + count + "] ====", "Job");

            // 只有“没读到保存失败”才是整批致命；其余算单轮失败——重试后继续下一轮
            int tries = 1 + Math.Max(0, s.RoundRetries);
            bool ok = false, fatal = false;
            for (int t = 1; t <= tries; t++)
            {
                if (t > 1)
                {
                    _log.Hint("第 " + round + " 轮重试（第 " + t + "/" + tries + " 次），先恢复同步…", "Job");
                    RecoverToStory();
                }
                (ok, fatal) = RunOneShiftAtoms();
                if (ok || fatal) break;
            }
            if (fatal)
            {
                _log.Error("第 " + round + " 轮既没读到“已获取”（到货）也没读到“保存失败” → 可能已上传云存档或本轮没到货，停止本次班次", "Job");
                RecoverToStory();
                return false;
            }
            if (!ok)
            {
                failedRounds.Add(round);
                _log.Warn("第 " + round + " 轮未完成（不中止，继续下一轮）", "Job");
                // 失败轮可能停在线上任意位置：先回线下、再解封，别让下一轮从半路开始，
                // 也别在还在线时解封把没存上的存档补传上去。
                _log.Hint("失败轮收尾：先回线下，再恢复联网…", "Job");
                RecoverToStory();
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

    /// <summary>
    /// 原子引擎跑一轮：动作只做、判据另挂、每步都有证据。失败即返回 false，由外层 RecoverToStory 收尾。
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
            bool fatal = !ok && eng.Ctx.Facts.TryGetValue("wait_hint.ok", out var sf) && sf == "false";

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
    private string GoalSummary(AutoPickup.Core.Flow.FlowEngine eng, TimeSpan elapsed)
    {
        string Mark(string step)
            => eng.Ctx.Facts.TryGetValue(step + ".ok", out var v) && v == "true" ? "✓" : "✗";
        string cue = eng.Ctx.Facts.TryGetValue("cue", out var c) && c == "true" ? "✓" : "✗";
        // 封网：启用封网时看 block_cloud 的**真实结果**（fact:blocked，末尾解封不会覆盖它）；
        // 未启用封网时沿用 cue 的“跳过”语义，不显示成失败。原来这里一律用 cue，netsh 真失败也会显示 ✓。
        string block = _settings.Shift.UseFirewall
            ? (eng.Ctx.Facts.TryGetValue("blocked", out var b) && b == "true" ? "✓" : "✗")
            : cue;
        return "进线上" + Mark("wait_online")
             + " · 封网" + block
             + " · 到货/保存失败" + Mark("wait_hint")
             + " · 回线下" + Mark("wait_story")
             + "（用时 " + (int)elapsed.TotalMinutes + "分" + elapsed.Seconds.ToString("D2") + "秒）";
    }

    /// <summary>安全收尾：**先回线下、再解封**（顺序不能反）。在线状态下解封会让游戏立刻把没上传成功的
    /// 存档补传上去，把本轮的货记到云上；gtaz 参考实现的铁律也是“解封只发生在回线下之后”。
    /// 回线下走 flows/return_story.json（与班次主流程同一套原子）；无论成功与否最后一定解封，
    /// 绝不把用户留在封网状态。</summary>
    private void RecoverToStory()
    {
        try
        {
            if (_returnFlow is not null && _atoms is not null)
            {
                _atoms.ResetContext();
                _atoms.Run(_returnFlow);
            }
            else _log.Warn("未载入 flows/return_story.json，跳过回线下（只做解封）", "Job");
        }
        catch (Exception e) { _log.Warn("回线下收尾异常: " + e.Message, "Job"); }
        ReleaseNetwork();
    }

    /// <summary>只恢复联网（已经在理想位置时用，不去动游戏）。</summary>
    private void ReleaseNetwork()
    {
        try { _fw.Disable(); } catch { }
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