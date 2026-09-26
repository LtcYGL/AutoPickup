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
    /// <summary>退出收尾开始后置位：拒绝任何新的封网操作，防止仍在跑的班次在清理之后又把规则打开。</summary>
    private volatile bool _shuttingDown;
    /// <summary>最近一次已知的启用状态（null=未知，第一次仍走真实查询）。</summary>
    private bool? _lastEnabled;
    /// <summary>上次解析存档 IP 的时间；超过 IpRefreshHours 才重新解析（域名换 CDN IP 时用一次慢路径刷新）。</summary>
    private DateTime _ipResolvedUtc = DateTime.MinValue;
    /// <summary>快路径“沿用上次解析的 IP”的有效期（参数页「IP刷新周期(小时)」，默认 6）。</summary>
    private double IpRefreshHours => _settings.Firewall.IpRefreshHours;

    public FirewallController(LogBus log, AppSettings settings)
    {
        _log = log;
        _settings = settings;
    }

    public string RuleName => _settings.Firewall.RuleName;
    /// <summary>已废弃的旧版“完全断网”规则名：启动自愈时清掉，避免残留规则一直拦着用户的机器。</summary>
    private const string LegacyBlockAllRuleName = "AutoPickupBlockAll";

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

    public bool AddRule()
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

    /// <summary>
    /// 关键窗口预置：在“等下云 cue”那几十秒~几分钟里，先把规则建好并保持**禁用**，
    /// 这样 cue 命中时只剩一次开关（实测 ~0.26s）。
    /// 不预置的话，慢路径的 delete+add+域名解析（实测 0.5~1.0s）、以及快路径探测失败后的
    /// 回退（实测有 8.8s / 10.7s 两次），全部压在“cue→封网”这段最要命的时间里 ——
    /// 表现就是“封网不实时”，甚至等它封上时游戏已经把云存档写完了。
    /// </summary>
    public bool Arm()
    {
        lock (_lock)
        {
            if (_shuttingDown) { _log.Warn("正在退出，已拒绝封网预置", "Firewall"); return false; }
            if (_lastExists && (DateTime.UtcNow - _ipResolvedUtc).TotalHours < IpRefreshHours)
            {
                // 规则在、IP 新鲜。但若它当前是**启用**态（上一轮清理失败、或手工 F7 打开），
                // 这里必须关掉：封网时机由流程的 cue 决定，提前封会把“下云”下载本身掐断。
                if ((_lastEnabled ?? EnabledCore()) == true) Disable();
                return true;
            }
            bool ok = AddRule();
            if (ok) _log.Info("封网预置完成：规则就绪且禁用（cue 命中后只需 1 次开关）", "Firewall");
            return ok;
        }
    }

    public bool Enable()
    {
        lock (_lock)
        {
            if (_shuttingDown) { _log.Warn("正在退出，已拒绝封网操作", "Firewall"); return false; }
            // 快路径（1 次 netsh ≈0.1s）：规则本进程建过、IP 还新鲜 → 只翻开关。
            // 原来每次都 delete+add+DNS 解析，单次实测 0.52~7.3s，全落在“下云 cue 命中后立刻封网”的关键窗口里。
            if (_lastExists && (DateTime.UtcNow - _ipResolvedUtc).TotalHours < IpRefreshHours)
            {
                var swFast = Stopwatch.StartNew();
                var (fok, _, fse) = Netsh("set rule name=" + RuleName + " new enable=yes");
                // **必须回读验证**：规则若已被外部删除（例如用户执行过 netsh advfirewall reset），
                // `set` 匹配不到任何规则却仍以“成功”退出 —— 于是我们记了“已启用”，系统里其实没有规则，
                // 而 UI 另查一次状态就会永远显示“已解除”。慢路径（delete+add）才是可靠结果。
                if (fok && EnabledCoreOf(RuleName) == true)
                {
                    _lastEnabled = true;
                    _log.Okay("防火墙规则已启用（沿用上次解析的 IP）", "Firewall");
                    return true;
                }
                _log.Warn((fok ? "快路径 set 后规则并未启用（可能已被外部删除），改为重建规则"
                               : ("快路径启用失败，回退重建规则: " + fse))
                              + "（快路径耗时 " + swFast.ElapsedMilliseconds + "ms）", "Firewall");
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
            // 注意：这里**不能**因为“缓存说已禁用”就跳过。状态若被另一个实例或外部工具改过，
            // 跳过就等于把它留在启用态 —— 症状是游戏一直“无法从云服务器下载存档”。一次 set 只要 ~0.1~1s。
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

    /// <summary>“封存档”开关（F7 即时切换）：不存在→创建并启用；已启用→禁用；已禁用→启用。
    /// <paramref name="enabled"/> 回传**切换后实际是否处于封网态**，供 UI 直接显示提示 ——
    /// 以前调用方自己再查一次状态，与这里的判断口径不一致时会显示反（实测“永远显示已解除”）。</summary>
    public bool Toggle(out bool enabled)
    {
        lock (_lock)
        {
            if (_shuttingDown) { _log.Warn("正在退出，已拒绝封网操作", "Firewall"); enabled = false; return false; }
            if (!_lastExists && !ExistsCore())
            {
                _log.Info("封存档：规则不存在 → 创建并启用", "Firewall");
                bool created = AddRule() && Enable();
                enabled = created;
                return created;
            }
            bool on = _lastEnabled ?? EnabledCore() ?? false;
            if (on)
            {
                _log.Info("封存档：当前启用 → 禁用（恢复联网）", "Firewall");
                bool ok = Disable();
                enabled = ok && !on;      // 禁用成功 ⇒ 现在是“未封”
                return ok;
            }
            _log.Info("封存档：当前禁用 → 启用（阻断云存档）", "Firewall");
            bool en = Enable();
            enabled = en && !on;
            return en;
        }
    }

    /// <summary>
    /// 安全清理：确保退出时封网规则处于禁用态，绝不把用户留在断网状态。
    /// 返回“是否**确认**规则已关闭”。
    /// 以前这里只在“规则存在且状态查询恰好成功”时才动手，查询失败（netsh 返回非 0）就**静默跳过**，
    /// 而调用方的退出日志照样写“（防火墙已恢复）”—— 实测遗留过启用态规则，
    /// 症状正是游戏一直“无法从 Rockstar 云服务器下载您保存的数据”。
    /// </summary>
    public bool SafeCleanup()
    {
        _shuttingDown = true;   // 之后任何 Enable/Toggle/Arm 都被拒绝：否则仍在跑的班次会把规则又打开
        try
        {
            lock (_lock)
            {
                // 无条件发一次关闭命令（没有匹配规则时 netsh 也只是退 0，无副作用），再回读确认
                var (ok, _, se) = Netsh("set rule name=" + RuleName + " new enable=no");
                var state = EnabledCoreOf(RuleName);
                if (state == false)
                {
                    _lastEnabled = false;
                    _log.Okay("退出清理：已确认封网规则关闭（" + RuleName + "）", "Firewall");
                    return true;
                }
                if (state == true)
                {
                    _log.Error("退出清理失败：规则仍处于封网状态，请手动执行 netsh advfirewall firewall set rule name="
                        + RuleName + " new enable=no ：" + se, "Firewall");
                    return false;
                }
                _log.Warn(ok
                    ? "退出清理：已发出关闭命令，但状态查询失败、无法确认（" + RuleName + "）"
                    : "退出清理失败：关闭命令未成功（" + RuleName + "）：" + se, "Firewall");
                return false;
            }
        }
        catch (Exception e)
        {
            _log.Error("退出清理防火墙失败: " + e.Message, "Firewall");
            return false;
        }
    }

    /// <summary>
    /// 启动自愈：进程被任务管理器强杀/崩溃时 SafeCleanup 跑不到，规则会**留在启用态**，
    /// 症状就是游戏反复“无法从 Rockstar 云服务器下载您保存的数据”。
    /// 每次启动先把它恢复掉，并清掉旧版“完全断网”功能留下的规则。
    /// </summary>
    public void HealLeftovers()
    {
        try
        {
            lock (_lock)
            {
                if (RuleExistsCore(RuleName) && EnabledCoreOf(RuleName) == true)
                {
                    // 必须看返回值：非管理员时 netsh 会失败（“需要提升”），以前这里不看结果，
                    // 于是规则照样开着、游戏照样“无法下载云存档”，日志却写着“已恢复联网”。
                    var (ok, _, se) = Netsh("set rule name=" + RuleName + " new enable=no");
                    if (ok)
                    {
                        _lastExists = true; _lastEnabled = false;
                        _log.Warn("启动自愈：上次退出时封网规则仍在启用态，已恢复联网（" + RuleName + "）", "Firewall");
                    }
                    else _log.Error("启动自愈失败：封网规则仍启用（游戏将无法下载云存档），请以管理员运行本程序或手动执行 "
                        + "netsh advfirewall firewall set rule name=" + RuleName + " new enable=no ：" + se, "Firewall");
                }
                if (RuleExistsCore(LegacyBlockAllRuleName))
                {
                    var (ok, _, se) = Netsh("delete rule name=" + LegacyBlockAllRuleName);
                    if (ok) _log.Okay("启动清理：已删除旧版“完全断网”残留规则", "Firewall");
                    else _log.Error("启动清理：删除旧版规则失败 " + se, "Firewall");
                }
            }
        }
        catch (Exception e) { _log.Error("启动自愈失败: " + e.Message, "Firewall"); }
    }

    /// <summary>一键删除规则（若处于封网状态会先恢复，避免残留断网），并清掉旧版功能残留。</summary>
    public bool DeleteRules()
    {
        bool a = DeleteRule();
        bool b = true;
        lock (_lock)
        {
            if (RuleExistsCore(LegacyBlockAllRuleName))
            {
                var (ok, _, se) = Netsh("delete rule name=" + LegacyBlockAllRuleName);
                b = ok;
                if (!ok) _log.Error("删除旧版“完全断网”规则失败: " + se, "Firewall");
            }
        }
        _log.Info("删除规则: 封存档=" + (a ? "OK" : "失败") + " 旧版残留=" + (b ? "OK" : "失败"), "Firewall");
        return a && b;
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

    /// <summary>
    /// 启用状态查询（中英双语）：同名规则可能有多条 —— **全部为“是”才算启用**，任一条为“否”即视为禁用。
    /// （旧版只读第一行，多条同名规则状态不一致时会读错，曾导致 F8 开关变成永久空操作。）
    /// </summary>
    private bool? EnabledCoreOf(string name)
    {
        var (ok, stdout, _) = Netsh("show rule name=" + name + " verbose");
        if (!ok) return null;
        bool? all = null;
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("Enabled:", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("已启用:")) continue;
            bool yes = t.Contains("Yes", StringComparison.OrdinalIgnoreCase) || t.Contains("是");
            bool no = t.Contains("No", StringComparison.OrdinalIgnoreCase) || t.Contains("否");
            if (!yes && !no) continue;
            all = all is null ? yes : (all.Value && yes);
        }
        return all;
    }
}