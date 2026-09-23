using AutoPickup.Config;
using AutoPickup.Core.Audio;
using AutoPickup.Core.Capture;
using AutoPickup.Core.Input;
using AutoPickup.Core.Jobs;
using AutoPickup.Core.Net;
using AutoPickup.Core.Vision;
using AutoPickup.Core.Vision.Ocr;
using AutoPickup.Logging;
using System.Security.Cryptography;

namespace AutoPickup;

/// <summary>组合根：装配日志/配置/能力服务，并载入流程（班次 / 进线上 / 回线下）。</summary>
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
    public ShiftOrchestrator Jobs { get; }
    /// <summary>原子引擎（新流程）。</summary>
    public AutoPickup.Core.Flow.FlowEngine Atoms { get; }
    /// <summary>班次主流程（flows/shift_single.json）。</summary>
    public AutoPickup.Core.Flow.FlowProgram? AtomsFlow { get; }
    /// <summary>进线上流程（flows/go_online.json）：流程页模式测试/诊断用。</summary>
    public AutoPickup.Core.Flow.FlowProgram? GoOnlineFlow { get; }
    /// <summary>回线下流程（flows/return_story.json）：模式测试 + 失败轮/停止时的安全收尾用。</summary>
    public AutoPickup.Core.Flow.FlowProgram? ReturnStoryFlow { get; }

    /// <summary>载入流程：优先用户数据目录（可自行修改），其次开发期工作区。</summary>
    private static AutoPickup.Core.Flow.FlowProgram? LoadFlow(string fileName, LogBus log)
    {
        try
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoPickup");
            foreach (var p in new[]
            {
                Path.Combine(AutoPickup.Core.AssetBootstrap.FlowsDir(dataDir), fileName),
                Path.Combine(AppContext.BaseDirectory, "flows", fileName),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "flows", fileName),
            })
            {
                string full = Path.GetFullPath(p);
                if (File.Exists(full))
                {
                    var f = AutoPickup.Core.Flow.FlowJson.Load(full);
                    log.Info("流程已载入: " + full + "（" + f.Steps.Count + " 步，sha256 "
                        + FlowFileHash(full) + "）", "Flow");
                    return f;
                }
            }
            log.Warn("未找到 flows/" + fileName, "Flow");
        }
        catch (Exception e) { log.Error("载入流程失败 " + fileName + ": " + e.Message, "Flow"); }
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
        // 引导期（资源释放/更新）的提示：日志系统起来后补记一条，方便定位"流程版本不对"
        foreach (var m in AutoPickup.Core.AssetBootstrap.DrainNotices()) log.Info(m, "Assets");

        // 原子引擎：班次、模式切换、安全收尾都跑同一套流程
        AtomsFlow = LoadFlow("shift_single.json", log);
        GoOnlineFlow = LoadFlow("go_online.json", log);
        ReturnStoryFlow = LoadFlow("return_story.json", log);
        var atomsHost = new AutoPickup.Core.Flow.LiveFlowHost(Window, Input, Firewall, settings, log,
            passive: false, audio: Audio);
        Atoms = new AutoPickup.Core.Flow.FlowEngine(atomsHost, log, settings, Tab, Reader, Ocr, RowReader);
        Jobs = new ShiftOrchestrator(Firewall, settings, log, Atoms, AtomsFlow, ReturnStoryFlow);
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