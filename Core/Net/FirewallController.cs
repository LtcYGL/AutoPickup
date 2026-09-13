using System.Diagnostics;
using System.Net;
using AutoPickup.Config;
using AutoPickup.Logging;

namespace AutoPickup.Core.Net;

/// <summary>
/// 防火墙规则管理（netsh advfirewall）。与原版一致：只封“存档服 IP”的出站，而不是整条游戏流量，
/// 保证断网后游戏会话仍在本地运行、能收到到货事件，但云存档写不上去。
/// 退出/崩溃看门狗会强制把规则置为禁用，避免把用户留在断网态。
/// </summary>
public sealed class FirewallController
{
    private readonly LogBus _log;
    private readonly AppSettings _settings;
    private readonly object _lock = new();

    public FirewallController(LogBus log, AppSettings settings)
    {
        _log = log;
        _settings = settings;
    }

    public string RuleName => _settings.Firewall.RuleName;
    /// <summary>“完全断网（故意掉线）”的独立规则名。</summary>
    public string BlockAllRuleName => _settings.Firewall.BlockAllRuleName;

    private static string Q(string s)
    {
        // 输出双引号包围，避免任何反斜杠转义依赖
        return '"' + s + '"';
    }

    /// <summary>封网对象：解析存档域名的全部 IPv4 + 附加固定 IP（云存档会换 CDN IP）。</summary>
    private List<string> ResolveTargets()
    {
        var set = new HashSet<string>();
        foreach (var ip in _settings.Firewall.ExtraIps)
            if (!string.IsNullOrWhiteSpace(ip)) set.Add(ip.Trim());
        foreach (var d in _settings.Firewall.BlockDomains)
        {
            try
            {
                foreach (var addr in Dns.GetHostAddresses(d.Trim()))
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        set.Add(addr.ToString());
            }
            catch (Exception e)
            {
                _log.Warn("解析存档域名失败 " + d + " : " + e.Message, "Firewall");
            }
        }
        return set.ToList();
    }

    private (bool ok, string stdout, string stderr) Netsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = "advfirewall firewall " + args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode == 0, so, se);
        }
        catch (Exception e)
        {
            return (false, "", e.Message);
        }
    }

    private bool ExistsCore()
    {
        var (ok, so, _) = Netsh("show rule name=" + RuleName);
        return ok
            && !so.Contains("No rules match", StringComparison.OrdinalIgnoreCase)
            && !so.Contains("没有匹配的规则", StringComparison.OrdinalIgnoreCase);
    }

    private bool? EnabledCore()
    {
        var (ok, so, _) = Netsh("show rule name=" + RuleName + " verbose");
        if (!ok) return null;
        foreach (var line in so.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith("Enabled:", StringComparison.OrdinalIgnoreCase))
                return t.Contains("Yes", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("是", StringComparison.OrdinalIgnoreCase);
            if (t.StartsWith("已启用:"))
                return t.Contains("是");
        }
        return null;
    }

    /// <summary>静默存在性查询（状态灯/周期刷新用，不写日志，避免刷屏）。</summary>
    public bool ExistsQuiet()
    {
        lock (_lock) return ExistsCore();
    }

    public bool Exists()
    {
        lock (_lock)
        {
            bool e = ExistsCore();
            _log.Info("防火墙规则 [" + RuleName + "] " + (e ? "存在" : "不存在"), "Firewall");
            return e;
        }
    }

    public bool? IsEnabled()
    {
        lock (_lock) return EnabledCore();
    }

    public bool AddRule(string processPath = "")
    {
        lock (_lock)
        {
            if (ExistsCore()) Netsh("delete rule name=" + RuleName);
            var targets = ResolveTargets();
            if (targets.Count == 0)
            {
                _log.Error("无可封 IP（域名解析失败且无附加 IP）", "Firewall");
                return false;
            }
            string args = "add rule name=" + RuleName
                + " dir=out action=block protocol=TCP"
                + " remoteip=" + Q(string.Join(",", targets))
                + " enable=no";
            var (ok, _, se) = Netsh(args);
            if (ok) _log.Okay("规则已添加（封 TCP: " + string.Join(",", targets) + "）", "Firewall");
            else _log.Error("添加规则失败: " + se, "Firewall");
            return ok;
        }
    }

    public bool DeleteRule()
    {
        lock (_lock)
        {
            if (!ExistsCore())
            {
                _log.Info("规则不存在，无需删除", "Firewall");
                return true;
            }
            var (ok, _, se) = Netsh("delete rule name=" + RuleName);
            if (ok) _log.Okay("规则已删除: " + RuleName, "Firewall");
            else _log.Error("删除失败: " + se, "Firewall");
            return ok;
        }
    }

    public bool Enable()
    {
        lock (_lock)
        {
            if (ExistsCore()) Netsh("delete rule name=" + RuleName);
            var targets = ResolveTargets();
            if (targets.Count == 0)
            {
                _log.Error("无可封 IP（域名解析失败且无附加 IP）", "Firewall");
                return false;
            }
            string args = "add rule name=" + RuleName
                + " dir=out action=block protocol=TCP"
                + " remoteip=" + Q(string.Join(",", targets))
                + " enable=yes";
            var (ok, _, se) = Netsh(args);
            if (ok) _log.Okay("防火墙规则已启用（封 TCP: " + string.Join(",", targets) + "）", "Firewall");
            else _log.Error("启用失败: " + se, "Firewall");
            return ok;
        }
    }

    public bool Disable()
    {
        lock (_lock)
        {
            if (!ExistsCore()) { _log.Warn("规则不存在，无法禁用", "Firewall"); return false; }
            if (EnabledCore() == false) { _log.Info("规则已禁用，跳过", "Firewall"); return true; }
            var (ok, _, se) = Netsh("set rule name=" + RuleName + " new enable=no");
            if (ok) _log.Okay("防火墙规则已禁用（网络恢复）", "Firewall");
            else _log.Error("禁用失败: " + se, "Firewall");
            return ok;
        }
    }

    /// <summary>“封存档”开关（F11 即时切换）：不存在→创建并启用；已启用→禁用；已禁用→启用。</summary>
    public bool Toggle()
    {
        lock (_lock)
        {
            if (!ExistsCore())
            {
                _log.Info("封存档：规则不存在 → 创建并启用", "Firewall");
                if (!AddRule("")) return false;
                return Enable();
            }
            if (EnabledCore() == true)
            {
                _log.Info("封存档：当前启用 → 禁用（恢复联网）", "Firewall");
                return Disable();
            }
            _log.Info("封存档：当前禁用 → 启用（阻断云存档）", "Firewall");
            return Enable();
        }
    }

    /// <summary>安全清理：确保退出时两条规则都处于禁用态，绝不把用户留在断网状态。</summary>
    public void SafeCleanup()
    {
        try
        {
            lock (_lock)
            {
                foreach (var name in new[] { RuleName, BlockAllRuleName })
                {
                    if (RuleExistsCore(name) && EnabledCoreOf(name) == true)
                    {
                        Netsh("set rule name=" + name + " new enable=no");
                        _log.Okay("退出清理：已禁用防火墙规则 " + name, "Firewall");
                    }
                }
            }
        }
        catch (Exception e)
        {
            _log.Error("退出清理防火墙失败: " + e.Message, "Firewall");
        }
    }

    // ================= 完全断网（故意掉线）=================

    /// <summary>完全断网是否已启用。</summary>
    public bool? BlockAllEnabled() { lock (_lock) return EnabledCoreOf(BlockAllRuleName); }

    public bool BlockAllExists() { lock (_lock) return RuleExistsCore(BlockAllRuleName); }

    /// <summary>
    /// 完全断网（故意掉线）：另建一条规则封**全部出站**（TCP+UDP，或 protocol=any），
    /// 与“封存档”完全独立——两条规则可以同时开、分别关。
    /// </summary>
    public bool EnableBlockAll()
    {
        lock (_lock)
        {
            if (RuleExistsCore(BlockAllRuleName)) Netsh("delete rule name=" + BlockAllRuleName);
            bool ok;
            string what;
            if (_settings.Firewall.BlockAllUseProtocols)
            {
                var (ok1, _, se1) = Netsh("add rule name=" + BlockAllRuleName + " dir=out action=block protocol=TCP enable=yes");
                var (ok2, _, se2) = Netsh("add rule name=" + BlockAllRuleName + " dir=out action=block protocol=UDP enable=yes");
                ok = ok1 && ok2;
                what = "TCP+UDP 全部出站";
                if (!ok) { _log.Error("完全断网启用失败: " + (ok1 ? se2 : se1), "Firewall"); return false; }
            }
            else
            {
                var (okA, _, seA) = Netsh("add rule name=" + BlockAllRuleName + " dir=out action=block protocol=any enable=yes");
                ok = okA;
                what = "全部出站(any)";
                if (!ok) { _log.Error("完全断网启用失败: " + seA, "Firewall"); return false; }
            }
            if (ok) _log.Okay("完全断网已启用（故意掉线，" + what + "）", "Firewall");
            return ok;
        }
    }

    public bool DisableBlockAll()
    {
        lock (_lock)
        {
            if (!RuleExistsCore(BlockAllRuleName)) { _log.Warn("完全断网规则不存在", "Firewall"); return false; }
            if (EnabledCoreOf(BlockAllRuleName) == false) { _log.Info("完全断网已禁用，跳过", "Firewall"); return true; }
            var (ok, _, se) = Netsh("set rule name=" + BlockAllRuleName + " new enable=no");
            if (ok) _log.Okay("完全断网已解除（网络恢复）", "Firewall");
            else _log.Error("完全断网解除失败: " + se, "Firewall");
            return ok;
        }
    }

    /// <summary>完全断网开关（F8 即时切换）。</summary>
    public bool ToggleBlockAll()
        => BlockAllEnabled() == true ? DisableBlockAll() : EnableBlockAll();

    /// <summary>创建“完全断网”规则（默认禁用，等 F8 或按钮启用）。</summary>
    public bool AddBlockAllRule()
    {
        lock (_lock)
        {
            if (RuleExistsCore(BlockAllRuleName)) Netsh("delete rule name=" + BlockAllRuleName);
            bool ok;
            if (_settings.Firewall.BlockAllUseProtocols)
            {
                var (a, _, ea) = Netsh("add rule name=" + BlockAllRuleName + " dir=out action=block protocol=TCP enable=no");
                var (b, _, eb) = Netsh("add rule name=" + BlockAllRuleName + " dir=out action=block protocol=UDP enable=no");
                ok = a && b;
                if (!ok) { _log.Error("添加完全断网规则失败: " + (a ? eb : ea), "Firewall"); return false; }
            }
            else
            {
                var (a, _, ea) = Netsh("add rule name=" + BlockAllRuleName + " dir=out action=block protocol=any enable=no");
                ok = a;
                if (!ok) { _log.Error("添加完全断网规则失败: " + ea, "Firewall"); return false; }
            }
            _log.Okay("完全断网规则已添加（默认禁用）", "Firewall");
            return true;
        }
    }

    /// <summary>一键添加两条规则（都是禁用态，之后用 F7/F8 开）。</summary>
    public bool AddAllRules()
    {
        bool a = AddRule("");
        bool b = AddBlockAllRule();
        _log.Info("添加全部规则: 封存档=" + (a ? "OK" : "失败") + " 完全断网=" + (b ? "OK" : "失败"), "Firewall");
        return a && b;
    }

    /// <summary>一键删除两条规则（若处于封网状态会先恢复，避免残留断网）。</summary>
    public bool DeleteAllRules()
    {
        bool a = DeleteRule();
        bool b = DeleteBlockAllRule();
        _log.Info("删除全部规则: 封存档=" + (a ? "OK" : "失败") + " 完全断网=" + (b ? "OK" : "失败"), "Firewall");
        return a && b;
    }

    public bool DeleteBlockAllRule()
    {
        lock (_lock)
        {
            if (!RuleExistsCore(BlockAllRuleName)) return true;
            var (ok, _, se) = Netsh("delete rule name=" + BlockAllRuleName);
            if (ok) _log.Okay("完全断网规则已删除", "Firewall");
            else _log.Error("完全断网规则删除失败: " + se, "Firewall");
            return ok;
        }
    }

    /// <summary>对任意规则名做存在性检查（原 ExistsCore 只认封存档规则）。</summary>
    private bool RuleExistsCore(string name)
    {
        var (ok, stdout, _) = Netsh("show rule name=" + name);
        return ok && (stdout.Contains("Yes") || stdout.Contains("No") || stdout.Contains(name));
    }

    private bool? EnabledCoreOf(string name)
    {
        var (ok, stdout, _) = Netsh("show rule name=" + name);
        if (!ok) return null;
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Enabled:", StringComparison.OrdinalIgnoreCase))
            {
                if (t.EndsWith("Yes", StringComparison.OrdinalIgnoreCase)) return true;
                if (t.EndsWith("No", StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        return null;
    }
}