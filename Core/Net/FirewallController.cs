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

    // ---- 进程内状态记忆：每次 netsh 调用 100~800ms，能不问就不问 ----
    /// <summary>本进程内我们建过的“封存档”规则一定存在（真被外部删了 netsh 会失败，再回退查询）。</summary>
    private bool _lastExists;
    /// <summary>最近一次已知的启用状态（null=未知，第一次仍走真实查询）。</summary>
    private bool? _lastEnabled;
    /// <summary>上次解析存档 IP 的时间；超过 IpRefreshHours 才重新解析（域名换 CDN IP 时用一次慢路径刷新）。</summary>
    private DateTime _ipResolvedUtc = DateTime.MinValue;
    private bool _lastBlockAllExists;
    private bool? _lastBlockAllEnabled;
    /// <summary>快路径“沿用上次解析的 IP”的有效期（参数页「IP刷新周期(小时)」，默认 6）。</summary>
    private double IpRefreshHours => _settings.Firewall.IpRefreshHours;

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

    /// <summary>封网对象：IP 库网段（整段封锁 Rockstar 存档/交易服）+ 附加固定 IP + 解析存档域名的全部 IPv4。
    /// 已被网段覆盖的单个 IP 不再重复写进规则，remoteip 保持最小集。</summary>
    private List<string> ResolveTargets()
    {
        var set = new HashSet<string>();
        var pool = new List<string>();
        foreach (var c in _settings.Firewall.BlockCidrs)
            if (!string.IsNullOrWhiteSpace(c)) { pool.Add(c.Trim()); set.Add(c.Trim()); }
        foreach (var ip in _settings.Firewall.ExtraIps)
            if (!string.IsNullOrWhiteSpace(ip) && !Covered(pool, ip)) set.Add(ip.Trim());
        foreach (var d in _settings.Firewall.BlockDomains)
        {
            try
            {
                foreach (var addr in Dns.GetHostAddresses(d.Trim()))
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !Covered(pool, addr.ToString()))
                        set.Add(addr.ToString());
            }
            catch (Exception e)
            {
                _log.Warn("解析存档域名失败 " + d + " : " + e.Message, "Firewall");
            }
        }
        return set.ToList();
    }

    private static bool Covered(List<string> cidrs, string ip)
    {
        foreach (var c in cidrs) if (CidrCovers(c, ip)) return true;
        return false;
    }

    /// <summary>ip（IPv4）是否落在 cidr（如 192.81.241.0/24）内。</summary>
    private static bool CidrCovers(string cidr, string ip)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var net) || !IPAddress.TryParse(ip, out var addr)) return false;
        var nb = net.GetAddressBytes();
        var ab = addr.GetAddressBytes();
        if (nb.Length != 4 || ab.Length != 4) return false;
        if (!int.TryParse(parts[1], out int bits) || bits < 0 || bits > 32) return false;
        for (int i = 0; i < 4; i++)
        {
            int take = Math.Clamp(bits - i * 8, 0, 8);
            if (take == 0) break;
            byte mask = (byte)(0xFF << (8 - take));
            if ((nb[i] & mask) != (ab[i] & mask)) return false;
        }
        return true;
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

    private bool ExistsCore() => RuleExistsCore(RuleName);

    private bool? EnabledCore() => EnabledCoreOf(RuleName);

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
            if (ok)
            {
                _lastExists = true; _lastEnabled = false;
                _ipResolvedUtc = DateTime.UtcNow;
                _log.Okay("规则已添加（封 TCP: " + string.Join(",", targets) + "）", "Firewall");
            }
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
            if (ok)
            {
                _lastExists = false; _lastEnabled = false; _ipResolvedUtc = DateTime.MinValue;
                _log.Okay("规则已删除: " + RuleName, "Firewall");
            }
            else _log.Error("删除失败: " + se, "Firewall");
            return ok;
        }
    }

    public bool Enable()
    {
        lock (_lock)
        {
            // 快路径（1 次 netsh ≈0.1s）：规则本进程建过、IP 还新鲜 → 只翻开关。
            // 原来每次都 delete+add+DNS 解析，单次实测 0.52~7.3s，全落在“下云 cue 命中后立刻封网”的关键窗口里。
            if (_lastExists && (DateTime.UtcNow - _ipResolvedUtc).TotalHours < IpRefreshHours)
            {
                var (fok, _, fse) = Netsh("set rule name=" + RuleName + " new enable=yes");
                if (fok)
                {
                    _lastEnabled = true;
                    _log.Okay("防火墙规则已启用（沿用上次解析的 IP）", "Firewall");
                    return true;
                }
                _log.Warn("快路径启用失败，回退重建规则: " + fse, "Firewall");
            }
            // 慢路径（首次启动 / 超 6 小时 / 快路径失败）要重建规则：必须先删掉同名旧规则，
            // 否则每次启动都只加不删，系统里会越堆越多同名 AutoPickupBlock。
            if (ExistsCore()) Netsh("delete rule name=" + RuleName);
            _lastExists = false;
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
            if (ok)
            {
                _lastExists = true; _lastEnabled = true;
                _ipResolvedUtc = DateTime.UtcNow;
                _log.Okay("防火墙规则已启用（封 TCP: " + string.Join(",", targets) + "）", "Firewall");
            }
            else _log.Error("启用失败: " + se, "Firewall");
            return ok;
        }
    }

    public bool Disable()
    {
        lock (_lock)
        {
            // 已知是关的：直接返回。收尾路径会禁用两次（流程末 + cleanup），第二次不该再问 netsh。
            if (_lastExists && _lastEnabled == false) return true;
            if (_lastExists || ExistsCore())
            {
                var (ok, _, se) = Netsh("set rule name=" + RuleName + " new enable=no");
                if (ok)
                {
                    _lastExists = true; _lastEnabled = false;
                    _log.Okay("防火墙规则已禁用（网络恢复）", "Firewall");
                }
                else _log.Error("禁用失败: " + se, "Firewall");
                return ok;
            }
            _log.Warn("规则不存在，无法禁用", "Firewall");
            return false;
        }
    }

    /// <summary>“封存档”开关（F11 即时切换）：不存在→创建并启用；已启用→禁用；已禁用→启用。</summary>
    public bool Toggle()
    {
        lock (_lock)
        {
            if (!_lastExists && !ExistsCore())
            {
                _log.Info("封存档：规则不存在 → 创建并启用", "Firewall");
                if (!AddRule("")) return false;
                return Enable();
            }
            bool on = _lastEnabled ?? EnabledCore() ?? false;
            if (on)
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
                        if (name == RuleName) _lastEnabled = false; else _lastBlockAllEnabled = false;
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
            string what = _settings.Firewall.BlockAllUseProtocols ? "TCP+UDP 全部出站" : "全部出站(any)";
            if (_lastBlockAllEnabled == true) return true;
            // 快路径：规则已在 → 只翻开关（全封要能在“检测到加速器”时几十毫秒内落下）
            if (_lastBlockAllExists || RuleExistsCore(BlockAllRuleName))
            {
                var (sok, _, sse) = Netsh("set rule name=" + BlockAllRuleName + " new enable=yes");
                if (sok)
                {
                    _lastBlockAllExists = true; _lastBlockAllEnabled = true;
                    _log.Okay("完全断网已启用（故意掉线，" + what + "）", "Firewall");
                    return true;
                }
                _log.Warn("快路径启用失败，回退重建完全断网规则: " + sse, "Firewall");
            }
            if (RuleExistsCore(BlockAllRuleName)) Netsh("delete rule name=" + BlockAllRuleName);
            bool ok;
            if (_settings.Firewall.BlockAllUseProtocols)
            {
                var (ok1, _, se1) = Netsh("add rule name=" + BlockAllRuleName + " dir=out action=block protocol=TCP enable=yes");
                var (ok2, _, se2) = Netsh("add rule name=" + BlockAllRuleName + " dir=out action=block protocol=UDP enable=yes");
                ok = ok1 && ok2;
                if (!ok) { _log.Error("完全断网启用失败: " + (ok1 ? se2 : se1), "Firewall"); return false; }
            }
            else
            {
                var (okA, _, seA) = Netsh("add rule name=" + BlockAllRuleName + " dir=out action=block protocol=any enable=yes");
                ok = okA;
                if (!ok) { _log.Error("完全断网启用失败: " + seA, "Firewall"); return false; }
            }
            if (ok) { _lastBlockAllExists = true; _lastBlockAllEnabled = true; _log.Okay("完全断网已启用（故意掉线，" + what + "）", "Firewall"); }
            return ok;
        }
    }

    public bool DisableBlockAll()
    {
        lock (_lock)
        {
            if (_lastBlockAllEnabled == false) { _log.Info("完全断网已禁用，跳过", "Firewall"); return true; }
            if (!_lastBlockAllExists && !RuleExistsCore(BlockAllRuleName)) { _log.Warn("完全断网规则不存在", "Firewall"); return false; }
            var (ok, _, se) = Netsh("set rule name=" + BlockAllRuleName + " new enable=no");
            if (ok) { _lastBlockAllExists = true; _lastBlockAllEnabled = false; _log.Okay("完全断网已解除（网络恢复）", "Firewall"); }
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
            _lastBlockAllExists = true; _lastBlockAllEnabled = false;
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
            if (ok)
            {
                _lastBlockAllExists = false; _lastBlockAllEnabled = false;
                _log.Okay("完全断网规则已删除", "Firewall");
            }
            else _log.Error("完全断网规则删除失败: " + se, "Firewall");
            return ok;
        }
    }

    /// <summary>规则是否存在：netsh 找不到时退出码非 0，输出里也会写“没有匹配的规则 / No rules match”。</summary>
    private bool RuleExistsCore(string name)
    {
        var (ok, stdout, _) = Netsh("show rule name=" + name);
        if (!ok) return false;
        if (stdout.Contains("No rules match", StringComparison.OrdinalIgnoreCase)) return false;
        if (stdout.Contains("没有匹配的规则")) return false;
        return true;
    }

    /// <summary>启用状态查询（中英双语输出都认）：Enabled: Yes/No 或 已启用: 是/否。</summary>
    private bool? EnabledCoreOf(string name)
    {
        var (ok, stdout, _) = Netsh("show rule name=" + name + " verbose");
        if (!ok) return null;
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("Enabled:", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("已启用:")) continue;
            if (t.Contains("Yes", StringComparison.OrdinalIgnoreCase) || t.Contains("是")) return true;
            if (t.Contains("No", StringComparison.OrdinalIgnoreCase) || t.Contains("否")) return false;
        }
        return null;
    }
}