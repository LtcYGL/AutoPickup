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

        for (int i = 0; i < count; i++)
        {
            if (stopRequested?.Invoke() == true)
            {
                _log.Warn("用户请求停止（轮次间安全退出），先回线下并恢复联网", "Job");
                RecoverSafe();
                return false;
            }
            _log.Hint("==== 班次 [" + (i + 1) + "/" + count + "] ====", "Job");
            bool ok = RunOneShift();
            if (!ok)
            {
                _log.Warn("第 " + (i + 1) + " 轮失败，中止（先确保已回线下且联网恢复）", "Job");
                RecoverSafe();
                return false;
            }
            if (stayOnlineAfterFinished && i == count - 1)
            {
                _log.Hint("最后一轮留在线（-o），跳过回线下", "Job");
                return true;
            }
            if (stopRequested is not null)
            {
                for (int k = 0; k < s.BetweenHoldSec && stopRequested() != true; k++) Thread.Sleep(1000);
            }
            else Thread.Sleep(s.BetweenHoldSec * 1000);
        }
        _log.Okay("全部 " + count + " 轮完成", "Job");
        return true;
    }

    private bool RunOneShift()
    {
        if (UsingAtoms) return RunOneShiftAtoms();

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
    private bool RunOneShiftAtoms()
    {
        try
        {
            var eng = _atoms!;
            var flow = _atomsFlow!;
            _log.Info("本轮走原子引擎（" + flow.Name + "）", "Job");
            bool ok = eng.Run(flow);
            _log.Info("原子引擎本轮结果: " + (ok ? "OK" : "FAIL") + "  证据: " + eng.Ctx.Snapshot(), "Job");
            if (!ok)
            {
                foreach (var t in eng.Trace.TakeLast(6)) _log.Info("  轨迹: " + t, "Job");
            }
            eng.Trace.Clear();
            eng.ResetFrameCache();
            return ok;
        }
        catch (Exception e)
        {
            _log.Error("原子引擎异常: " + e.Message, "Job");
            return false;
        }
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