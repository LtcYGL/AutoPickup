using AutoPickup.Config;
using AutoPickup.Core.Audio;
using AutoPickup.Core.Capture;
using AutoPickup.Core.Fsm;
using AutoPickup.Core.Input;
using AutoPickup.Core.Jobs;
using AutoPickup.Core.Net;
using AutoPickup.Core.Vision;
using AutoPickup.Core.Vision.Ocr;
using AutoPickup.Logging;
using System.Security.Cryptography;

namespace AutoPickup;

/// <summary>组合根：装配日志/配置/能力服务。供 UI 与后续状态机/编排器共用。</summary>
public sealed class AppRuntime : IDisposable
{
    public AppSettings Settings { get; }
    public SettingsStore Store { get; }
    public LogBus Log { get; }
    public GtaWindowSource Window { get; }
    public FirewallController Firewall { get; }
    public IInputLayer Input { get; }
    public IAudioCueSource Audio { get; }
    public TemplateBank Bank { get; }
    public NccMatcher Matcher { get; }
    public IOcrEngine Ocr { get; }
    public ScreenReader Reader { get; }
    public FocusRowReader RowReader { get; }
    public TabReader Tab { get; }
    public NetmodeMachine Machine { get; }
    public ShiftOrchestrator Jobs { get; }
    /// <summary>原子引擎（新流程）。</summary>
    public AutoPickup.Core.Flow.FlowEngine Atoms { get; }
    /// <summary>班次用的原子流程（flows/shift_single.json）；缺失则为 null，班次自动回退 legacy。</summary>
    public AutoPickup.Core.Flow.FlowProgram? AtomsFlow { get; }

    /// <summary>找并载入班次原子流程（exe 旁 flows/shift_single.json，其次工作区）。</summary>
    private static AutoPickup.Core.Flow.FlowProgram? LoadShiftFlow(LogBus log)
    {
        try
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoPickup");
            foreach (var p in new[]
            {
                Path.Combine(AutoPickup.Core.AssetBootstrap.FlowsDir(dataDir), "shift_single.json"),
                Path.Combine(AppContext.BaseDirectory, "flows", "shift_single.json"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "flows", "shift_single.json"),
            })
            {
                string full = Path.GetFullPath(p);
                if (File.Exists(full))
                {
                    var f = AutoPickup.Core.Flow.FlowJson.Load(full);
                    log.Info("原子流程已载入: " + full + "（" + f.Steps.Count + " 步，sha256 "
                        + FlowFileHash(full) + "）", "Flow");
                    return f;
                }
            }
            log.Warn("未找到 flows/shift_single.json，班次将走 legacy 流程", "Flow");
        }
        catch (Exception e) { log.Error("载入原子流程失败（将走 legacy）: " + e.Message, "Flow"); }
        return null;
    }

    /// <summary>流程文件 sha256 前 8 位。日志里带上它，就能一眼确认"跑的是不是这一版流程"。</summary>
    private static string FlowFileHash(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant()[..8];
        }
        catch { return "?"; }
    }

    public string DataDir => Store.DataDir;

    private AppRuntime(AppSettings settings, SettingsStore store, LogBus log)
    {
        Settings = settings;
        Store = store;
        Log = log;
        Window = new GtaWindowSource(log,
            settings.Game.ProcessName, settings.Game.WindowClass, settings.Game.WindowTitle);
        Firewall = new FirewallController(log, settings);
        if (settings.Automation.InputMode.Equals("Keyboard", StringComparison.OrdinalIgnoreCase))
        {
            Input = new KeyboardPadInput(log);
        }
        else
        {
            var pad = new ViGEmPadInput(log);
            Input = pad.IsAvailable ? pad : new NullPadInput(log);
        }
        if (settings.Audio.Enable)
        {
            var src = new NAudioCueSource(log, settings.Audio);
            Audio = src;
            if (!src.Start()) Audio = new NullAudioCue(log);
        }
        else
        {
            Audio = new NullAudioCue(log);
        }

        Matcher = new NccMatcher(settings.Vision);
        Ocr = OcrFactory.Create(log);
        Bank = new TemplateBank(log);
        var tplDir = ResolveTemplateDir();
        if (tplDir is not null) Bank.LoadFromDirectory(tplDir);
        Reader = new ScreenReader(Bank, Matcher, Ocr, log, settings.Vision);
        RowReader = new FocusRowReader(Ocr, log, settings);
        Tab = new TabReader(Ocr, log, settings);
        Machine = new NetmodeMachine(Window, Reader, RowReader, Tab, Input, settings, log);

        // 引导期（资源释放/更新）的提示：日志系统起来后补记一条，方便定位"流程版本不对"
        foreach (var m in AutoPickup.Core.AssetBootstrap.DrainNotices()) log.Info(m, "Assets");

        // 原子引擎（新流程）：与旧 FSM 并存，参数页/流程页可一键切回 legacy
        AtomsFlow = LoadShiftFlow(log);
        var atomsHost = new AutoPickup.Core.Flow.LiveFlowHost(Window, Input, Firewall, settings, log,
            passive: false, audio: Audio);
        Atoms = new AutoPickup.Core.Flow.FlowEngine(atomsHost, log, settings, Tab, Reader, Ocr, RowReader);
        Jobs = new ShiftOrchestrator(Machine, Firewall, Audio, settings, log, Atoms, AtomsFlow);
    }

    /// <summary>模板目录：优先用户数据目录（内嵌资源释放处，用户可替换/新增），其次开发期工作区。</summary>
    public static string? ResolveTemplateDir()
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoPickup");
        var appData = AutoPickup.Core.AssetBootstrap.TemplatesDir(dataDir);
        if (Directory.Exists(appData) && Directory.EnumerateFiles(appData).Any()) return appData;

        var cur = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && cur is not null; i++)
        {
            var p = Path.Combine(cur.FullName, "assets", "templates");
            if (Directory.Exists(p)) return p;
            cur = cur.Parent;
        }
        return null;
    }

    public static AppRuntime CreateDefault()
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoPickup");
        var store = new SettingsStore(dataDir);
        var settings = store.Load();
        var log = new LogBus(Path.Combine(dataDir, "logs", "autopickup.log"));
        log.Info("AutoPickup 启动，数据目录: " + dataDir);
        return new AppRuntime(settings, store, log);
    }

    public void Dispose()
    {
        Firewall.SafeCleanup();
        Input.Dispose();
        Audio.Stop();
        Log.Dispose();
    }
}