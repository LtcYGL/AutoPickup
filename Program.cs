using AutoPickup;
using AutoPickup.Core.Capture;
using AutoPickup.Core.Native;
using AutoPickup.Core.Menus;
using AutoPickup.Core.Vision;
using AutoPickup.Ui;

namespace AutoPickup;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private const long DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    private static void SetDpiAware()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)); }
        catch { }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        SetDpiAware();

        // 单 exe 引导：把内嵌资源（模板/原生 dll/默认流程）释放到 %LOCALAPPDATA%\AutoPickup\，
        // 并注册原生库解析器。放在最前面，保证**所有模式**（GUI/实机/回放/诊断）都能拿到资源。
        // 幂等：已有文件不覆盖（用户的模板替换/新增不会被冲掉）。
        try
        {
            string bootDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoPickup");
            AutoPickup.Core.AssetBootstrap.RegisterNativeResolver(bootDataDir);
            AutoPickup.Core.AssetBootstrap.ExtractAll(bootDataDir);
        }
        catch { /* 释放失败不阻断启动；后续按缺失降级 */ }

        if (args.Contains("--banner-probe"))
        {
            try { RunBannerProbe(args); }
            catch (Exception ex) { Console.WriteLine("banner-probe error: " + ex); }
            return;
        }
        if (args.Contains("--profile-read"))
        {
            try { RunProfileRead(args); }
            catch (Exception ex) { Console.WriteLine("profile-read error: " + ex); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--classify")
        {
            try { RunClassify(args[1]); }
            catch (Exception ex) { Console.WriteLine("classify error: " + ex.Message); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--batch")
        {
            try { RunBatch(args[1]); }
            catch (Exception ex)
            {
                try { using var rtx = AppRuntime.CreateDefault(); rtx.Log.Error("batch FATAL: " + ex); }
                catch { }
            }
            return;
        }
        if (args.Contains("--audioscan"))
        {
            try
            {
                int secs = 5;
                for (int i = 1; i < args.Length; i++) if (args[i] == "--audioscan" && i + 1 < args.Length) int.TryParse(args[i + 1], out secs);
                secs = Math.Clamp(secs, 2, 30);
                var devs = AutoPickup.Core.Audio.NAudioCueSource.ListDevices();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("逐设备回环采样 " + secs + "s（观察哪台设备真的有声音；请让游戏/音乐持续出声）");
                sb.AppendLine("设备\t最大峰值%\t平均峰值%\t样本数");
                foreach (var (idx, name, isDef) in devs)
                {
                    double mx = 0, sum = 0; int cnt = 0;
                    try
                    {
                        using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                        var all = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active);
                        if (idx >= all.Count) continue;
                        using var cap = new NAudio.Wave.WasapiLoopbackCapture(all[idx]);
                        cap.DataAvailable += (s, e) =>
                        {
                            int bps = Math.Max(2, (cap.WaveFormat?.BitsPerSample ?? 32) / 8);
                            if (bps != 4 && bps != 2) bps = 4;
                            int n = e.BytesRecorded / bps;
                            if (n <= 0) return;
                            double acc = 0;
                            if (bps == 4)
                                for (int i = 0; i + 3 < e.BytesRecorded; i += 4) { float v = BitConverter.ToSingle(e.Buffer, i); acc += (double)v * v; }
                            else
                                for (int i = 0; i + 1 < e.BytesRecorded; i += 2) { float v = BitConverter.ToInt16(e.Buffer, i) / 32768f; acc += (double)v * v; }
                            double rms = Math.Sqrt(acc / n) * 8.0;
                            if (rms > mx) mx = rms;
                            sum += rms; cnt++;
                        };
                        cap.StartRecording();
                        Thread.Sleep(secs * 1000);
                        cap.StopRecording();
                    }
                    catch (Exception ex) { sb.AppendLine("#" + idx + " " + name + "\t(失败: " + ex.Message + ")"); continue; }
                    sb.AppendLine("#" + idx + " " + name + (isDef ? " [默认]" : "") + "\t"
                        + (mx * 100).ToString("F1") + "\t" + (cnt > 0 ? (sum / cnt * 100).ToString("F1") : "0") + "\t" + cnt);
                }
                string outPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoPickup", "audio_scan.txt");
                try { File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false)); } catch { }
                Console.WriteLine(sb.ToString());
                Console.WriteLine("=> " + outPath);
            }
            catch (Exception ex) { Console.WriteLine("audioscan error: " + ex); }
            return;
        }
        if (args.Contains("--audio-devices"))
        {
            try
            {
                var list = AutoPickup.Core.Audio.NAudioCueSource.ListDevices();
                Console.WriteLine("活动的输出设备（回环可捕获的目标）：");
                foreach (var (idx, name, isDef) in list)
                    Console.WriteLine("  #" + idx + "  " + name + (isDef ? "   <- 系统默认" : ""));
                Console.WriteLine();
                Console.WriteLine("在参数页「3 音频 · 捕获设备」填 设备名片段 或 #序号 即可锁定（留空=系统默认）。");
                string outPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoPickup", "audio_devices.txt");
                try { File.WriteAllText(outPath, string.Join(Environment.NewLine, list.Select(x => "#" + x.Index + " " + x.Name + (x.IsDefault ? "  [默认]" : "")))); } catch { }
                NativeConsole.Ln("=> " + outPath);
            }
            catch (Exception ex) { Console.WriteLine("audio-devices error: " + ex); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--audiotest")
        {
            try
            {
                int secs = args.Length >= 3 && int.TryParse(args[2], out var sv) ? sv : 60;
                using var rta = AppRuntime.CreateDefault();
                RunAudioTest(rta, secs);
            }
            catch (Exception ex) { Console.WriteLine("audiotest error: " + ex.Message); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--shift")
        {
            try
            {
                int n = int.TryParse(args[1], out var v) ? v : 1;
                bool stay = Array.IndexOf(args, "--stay-online") >= 0;
                using var rtj = AppRuntime.CreateDefault();
                NativeConsole.Ln("shift start: " + n + " 轮");
                using var mirror = MirrorLog(rtj);
                bool ok = rtj.Jobs.RunShifts(n, stay);
                NativeConsole.Ln("shift => " + (ok ? "OK" : "FAIL"));
            }
            catch (Exception ex) { Console.WriteLine("shift error: " + ex.Message); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--fw")
        {
            try { RunFw(args[1]); }
            catch (Exception ex) { Console.WriteLine("fw error: " + ex.Message); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--words")
        {
            try { RunWords(args[1]); }
            catch (Exception ex) { Console.WriteLine("words error: " + ex.Message); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--tab")
        {
            try { RunTab(args[1]); }
            catch (Exception ex) { Console.WriteLine("tab error: " + ex.Message); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--say")
        {
            // 把一条提示打到游戏窗口左上角（覆盖层），不弹窗、不抢焦点。用于在用户玩游戏时递话。
            try
            {
                using var rts = AppRuntime.CreateDefault();
                var ov = new AutoPickup.Ui.OverlayForm(rts.Window, rts.Settings, rts.Log);
                ov.Show();
                string msg = string.Join(" ", args.Skip(1));
                ov.ShowToast(msg);
                NativeConsole.Ln("say => " + msg);
                var swSay = System.Diagnostics.Stopwatch.StartNew();
                while (swSay.Elapsed.TotalSeconds < 30) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(60); }
                ov.Close();
            }
            catch (Exception ex) { Console.WriteLine("say error: " + ex.Message); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--record")
        {
            // 实机采帧：--record <标签> [间隔秒] [时长秒]，期间在游戏里操作即可，按 Esc 提前结束
            try
            {
                string label = args[1];
                double gapSec = 1.5, durSec = 90;
                if (args.Length >= 3) double.TryParse(args[2], out gapSec);
                if (args.Length >= 4) double.TryParse(args[3], out durSec);
                using var rt = AppRuntime.CreateDefault();
                var dir = Path.Combine(rt.DataDir, "frames", "record_" + label);
                Directory.CreateDirectory(dir);
                NativeConsole.Ln("采帧开始 → " + dir);
                NativeConsole.Ln("间隔 " + gapSec + "s，最长 " + durSec + "s，随时按 Esc 结束（游戏内操作不受影响）");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int n = 0;
                while (sw.Elapsed.TotalSeconds < durSec)
                {
                    var f = rt.Window.CaptureClient();
                    if (f.IsValid)
                    {
                        string p = Path.Combine(dir, n.ToString("D3") + "_" + DateTime.Now.ToString("HHmmss") + ".png");
                        SaveFramePng(f, p);
                        n++;
                        NativeConsole.Ln("  [" + n + "] " + Path.GetFileName(p));
                    }
                    if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) break;
                    Thread.Sleep((int)(gapSec * 1000));
                }
                NativeConsole.Ln("采帧结束，共 " + n + " 张");
            }
            catch (Exception ex) { Console.WriteLine("record error: " + ex.Message); }
            return;
        }
        if (args.Contains("--draft-selftest"))
        {
            // 往返自检：读流程 JSON → 草稿 → 另存 → 再读回 → 比对结构与步数
            try
            {
                string src = args.Length >= 2 ? args[1] : "flows/online2story.json";
                using var rtx = AppRuntime.CreateDefault();
                var d = AutoPickup.Core.Flow.FlowDraft.Load(src);
                string outPath = Path.Combine(rtx.DataDir, "diag", "draft_roundtrip.json");
                d.Save(outPath);
                var d2 = AutoPickup.Core.Flow.FlowDraft.Load(outPath);
                var prog1 = d.Compile();
                var prog2 = d2.Compile();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("草稿往返自检: " + src);
                sb.AppendLine("  步数 " + d.Steps.Count + " → 另存 → " + d2.Steps.Count);
                sb.AppendLine("  名称 " + d.Name + " → " + d2.Name);
                bool same = d.Steps.Count == d2.Steps.Count
                    && d.Steps.Select((s, i) => s.AtomId == d2.Steps[i].AtomId).All(x => x);

                // 编辑器逻辑自检（无界面）：建流程 → 加原子 → 移动 → 存 → 读回 → 编译
                var ed = new AutoPickup.Core.Flow.FlowDraft { Name = "editor-selftest" };
                var a1 = ed.Add(AutoPickup.Core.Flow.AtomCatalog.Get("obs.mode")!);
                var a2 = ed.Add(AutoPickup.Core.Flow.AtomCatalog.Get("act.gesture")!);
                a2.Args["dir"] = "down";
                var a3 = ed.Add(AutoPickup.Core.Flow.AtomCatalog.Get("gate.ctx")!);
                int before = ed.Steps.Count;
                ed.Move(ed.Steps[2], -1);
                string order = string.Join(",", ed.Steps.Select(x => x.AtomId));
                string edPath = Path.Combine(rtx.DataDir, "diag", "editor_selftest.json");
                ed.Save(edPath);
                var ed2 = AutoPickup.Core.Flow.FlowDraft.Load(edPath);
                var edProg = ed2.Compile();
                sb.AppendLine("编辑器自检: 加 3 个原子=" + (before == 3) + "  移动后顺序=" + order
                    + "  读回步数=" + ed2.Steps.Count + "  编译步数=" + edProg.Steps.Count
                    + "  手势方向=" + (ed2.Steps.FirstOrDefault(x => x.AtomId == "act.gesture")?.Args.GetValueOrDefault("dir") ?? "?"));
                sb.AppendLine("编辑器自检文件: " + edPath);
                sb.AppendLine("  原子序列一致: " + same);
                sb.AppendLine("  编译后步骤数 " + prog1.Steps.Count + " vs " + prog2.Steps.Count);
                foreach (var (s, i) in d2.Steps.Select((s, i) => (s, i)))
                    sb.AppendLine($"    [{i}] {s.AtomId} id={s.Id} expect=[{string.Join(", ", s.Expect)}] retry={s.RetryMax}x{s.RetryIntervalMs} timeout={s.TimeoutSec} onFail={s.OnFail}");
                string outp = Path.Combine(rtx.DataDir, "diag", "draft_selftest.txt");
                try { File.WriteAllText(outp, sb.ToString(), new System.Text.UTF8Encoding(false)); } catch { }
                Console.WriteLine(sb.ToString());
                Console.WriteLine("=> " + outp + " / " + outPath);
            }
            catch (Exception ex) { Console.WriteLine("draft-selftest error: " + ex); }
            return;
        }
        if (args.Contains("--obs-check"))
        {
            // 用法：--obs-check [图片]  （不传图片则抓当前游戏窗口）
            // 用**运行时 settings.json** 构造引擎的观察，打印 mode/menuopen/dialog/screen 的判定与证据
            try
            {
                using var rt = AppRuntime.CreateDefault();
                string? imgPath = null;
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--obs-check" && !args[i + 1].StartsWith("--")) imgPath = args[i + 1];
                var frame = imgPath is null ? rt.Window.CaptureClient() : AutoPickup.Core.Vision.ImagingIo.LoadImage(imgPath);
                if (frame is null || !frame.IsValid) { Console.WriteLine("无可用帧"); return; }

                // 极简宿主：只提供这一帧（观察是纯读，不需要真输入）
                var host = new AutoPickup.Core.Flow.SingleFrameHost(frame);
                var eng = new AutoPickup.Core.Flow.FlowEngine(host, rt.Log, rt.Settings, rt.Tab, rt.Reader, rt.Ocr, rt.RowReader);
                // 焦点行 + 附近词框（诊断列表导航用）
                try
                {
                    var fr = new AutoPickup.Core.Vision.FocusRowReader(rt.Ocr, rt.Log, rt.Settings);
                    var row = fr.FindFocusRow(frame, null);
                    Console.WriteLine("focusrow(无 listTop) = " + (row?.Label ?? "(null)") + "  y=" + (row?.Y0.ToString() ?? "?"));
                    var trr = rt.Tab.Read(frame);
                    if (trr is not null)
                    {
                        Console.WriteLine("tab.BandY0/1 = " + trr.BandY0 + ".." + trr.BandY1);
                        var row2 = fr.FindFocusRow(frame, trr.BandY1 + 6);
                        Console.WriteLine("focusrow(listTop=" + (trr.BandY1 + 6) + ") = " + (row2?.Label ?? "(null)") + "  y=" + (row2?.Y0.ToString() ?? "?"));
                    }
                }
                catch (Exception fx) { Console.WriteLine("focusrow 诊断失败: " + fx.Message); }
                foreach (var name in new[] { "mode", "menuopen", "dialog", "toast", "selectedtab", "focusrow", "text" })
                {
                    var o = eng.Observe(name);
                    Console.WriteLine($"{name,-9} = {o?.Value,-8} 证据: {o?.Detail}");
                }
                Console.WriteLine("上下文快照: " + eng.Ctx.Snapshot());
            }
            catch (Exception ex) { Console.WriteLine("obs-check error: " + ex); }
            return;
        }
        if (args.Contains("--tab-live"))
        {
            // 实机对齐用：抓当前游戏窗口一帧，用**运行时 settings.json** 读 tab 条与列表区域
            try
            {
                using var rt = AppRuntime.CreateDefault();
                var f = rt.Window.CaptureClient();
                if (!f.IsValid) { Console.WriteLine("抓帧失败（游戏未运行/独占全屏）"); return; }
                var v = rt.Settings.Vision;
                Console.WriteLine($"帧 {f.Width}x{f.Height}  tab条带 y {v.TabStripTopPercent}%~{v.TabStripBottomPercent}%"
                    + $" = {(int)(f.Height * v.TabStripTopPercent / 100)}..{(int)(f.Height * v.TabStripBottomPercent / 100)}px"
                    + $"   列表 x {v.ListLeftPercent}%~{v.ListRightPercent}% y {v.ListTopPercent}%~{v.ListBottomPercent}%");
                var tr = rt.Tab.Read(f);
                Console.WriteLine("tab条词集: " + (tr?.StripWords ?? "(null)"));
                Console.WriteLine("  选中tab=" + (tr?.Selected ?? "?") + "  白块x " + (tr?.WhiteX0 ?? -1) + ".." + (tr?.WhiteX1 ?? -1)
                    + "  bandY " + (tr?.BandY0 ?? -1) + ".." + (tr?.BandY1 ?? -1));
                var words = rt.Ocr.RecognizeWords(f.Bgra, f.Width, f.Height);
                Console.WriteLine("整帧词数=" + words.Count + "（用来判断条带是否切掉了文字）");
                string outPath = Path.Combine(rt.DataDir, "diag", "tab_live.txt");
                try { File.WriteAllText(outPath, "帧 " + f.Width + "x" + f.Height + "\ntab条=" + (tr?.StripWords ?? "(null)")); } catch { }
            }
            catch (Exception ex) { Console.WriteLine("tab-live error: " + ex.Message); }
            return;
        }
        if (args.Contains("--obs-selftest"))
        {
            try { RunObsSelfTest(args); }
            catch (Exception ex) { Console.WriteLine("obs-selftest error: " + ex); }
            return;
        }
        if (args.Contains("--run-shift"))
        {
            // 实机整程入口（新引擎）：--run-shift [流程json] [班次数]
            // 用途：a) 影子对照（与旧 FSM 并存，只跑不接管）；b) 验证后接管
            try
            {
                string flowPath = "flows/shift_single.json";
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--run-shift" && !args[i + 1].StartsWith("--")) flowPath = args[i + 1];
                int rounds = 1;
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--rounds") int.TryParse(args[i + 1], out rounds);
                if (!Path.IsPathRooted(flowPath)) flowPath = Path.Combine(AppContext.BaseDirectory, flowPath);
                using var rts = AppRuntime.CreateDefault();
                var flow = AutoPickup.Core.Flow.FlowJson.Load(flowPath);
                // 默认**影子模式**（只观察不按键，零风险）；确认要动真机再加 --live
                bool liveMode = args.Contains("--live");
                if (!liveMode)
                    rts.Log.Hint("影子模式：只观察与判定，不按键、不动防火墙（加 --live 才真操作）", "Flow");
                var live = new AutoPickup.Core.Flow.LiveFlowHost(rts.Window, rts.Input, rts.Firewall, rts.Settings, rts.Log, passive: !liveMode);
                var eng = new AutoPickup.Core.Flow.FlowEngine(live, rts.Log, rts.Settings, rts.Tab, rts.Reader, rts.Ocr, rts.RowReader);
                int ok = 0;
                for (int r = 1; r <= Math.Max(1, rounds); r++)
                {
                    rts.Log.Hint("==== 新引擎班次 [" + r + "/" + Math.Max(1, rounds) + "] ====", "Flow");
                    bool pass = eng.Run(flow);
                    if (pass) ok++;
                    rts.Log.Info("班次 " + r + " 结果: " + (pass ? "OK" : "FAIL") + "  证据: " + eng.Ctx.Snapshot(), "Flow");
                    if (!pass) break;
                }
                rts.Log.Okay("新引擎完成: " + ok + "/" + Math.Max(1, rounds), "Flow");
                WriteFlowTrace(flow, live.Name, ok > 0, 0, eng,
                    Path.Combine(rts.DataDir, "diag", "flow_trace.txt"));
            }
            catch (Exception ex) { Console.WriteLine("run-shift error: " + ex); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--run-flow")
        {
            try { RunFlowV2(args); }
            catch (Exception ex) { Console.WriteLine("run-flow error: " + ex); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--flow")
        {
            try { RunFlow(args[1]); }
            catch (Exception ex) { Console.WriteLine("flow error: " + ex.Message); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--navlist")
        {
            try { RunNavList(args[1]); }
            catch (Exception ex) { Console.WriteLine("navlist error: " + ex.Message); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--row")
        {
            try { RunRow(args[1]); }
            catch (Exception ex) { Console.WriteLine("row error: " + ex.Message); }
            return;
        }
        if (args.Contains("--ui-layout-dump"))
        {
            try
            {
                using var rtu = AppRuntime.CreateDefault();
                using var frm = new AutoPickup.Ui.MainForm(rtu);
                frm.StartPosition = FormStartPosition.Manual;
                frm.Location = new System.Drawing.Point(-4000, -4000);
                frm.Show();
                for (int i = 0; i < 40; i++) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                string dump = frm.DumpLayout();
                string lp = System.IO.Path.Combine(rtu.DataDir, "diag", "ui_layout_dump.txt");
                System.IO.File.WriteAllText(lp, dump);
                frm.Close();
                NativeConsole.Ln("ui-layout-dump => " + lp);
            }
            catch (Exception ex) { Console.WriteLine("ui-layout-dump error: " + ex.Message); }
            return;
        }
        if (args.Contains("--ocrregress"))
        {
            // OCR 离线回归：仓库 1024_768/ 校准图 × 各识别站点，走真实管线（含工作像素/目标字高/双三次），
            // 打印“站点 | 输入尺寸 | 缩放 | 耗时 | 命中 | 文本”，结果写数据目录 ocr_regress.txt
            try { RunOcrRegress(args.Contains("--gui")); }
            catch (Exception ex)
            {
                if (args.Contains("--gui")) MessageBox.Show("ocrregress error: " + ex, "AutoPickup");
                else Console.WriteLine("ocrregress error: " + ex);
            }
            return;
        }
        if (args.Length >= 2 && args[0] == "--analyze")
        {
            // 离线解读一张图片：把 OCR 结果（词框/文本/尺寸耗时）+ 识别区域画在游戏窗口上供肉眼核对
            try { RunAnalyze(args[1]); }
            catch (Exception ex) { MessageBox.Show("analyze error: " + ex, "AutoPickup"); }
            return;
        }
        if (args.Contains("--dump-frame"))
        {
            // 抓一帧当前游戏窗口存盘到数据目录 frames\，用于离线分析 OCR/标定（唯一需要看真实画面的诊断入口）
            try
            {
                using var rts = AppRuntime.CreateDefault();
                var f = rts.Window.CaptureClient();
                string dir = Path.Combine(rts.DataDir, "frames");
                Directory.CreateDirectory(dir);
                string p = Path.Combine(dir, "live_" + DateTime.Now.ToString("HHmmss") + "_" + f.Width + "x" + f.Height + ".png");
                if (f.IsValid)
                {
                    SaveFramePng(f, p);
                    NativeConsole.Ln("dump-frame => " + p);
                    // 顺手把当前帧的 tab 条读数也打出来（对齐用）
                    try
                    {
                        var tr = rts.Tab.Read(f);
                        NativeConsole.Ln("  tab条=" + (tr?.StripWords ?? "(null)") + " 选中=" + (tr?.Selected ?? "?")
                            + " bandY=" + (tr?.BandY0 ?? -1) + ".." + (tr?.BandY1 ?? -1));
                    }
                    catch { }
                }
                else NativeConsole.Ln("dump-frame => 抓帧失败（游戏未运行/独占全屏）");
            }
            catch (Exception ex) { Console.WriteLine("dump-frame error: " + ex); }
            return;
        }
        if (args.Contains("--ocrbench"))
        {
            // WinExe 没有控制台，统一渲染成窗口（可用 [复制到剪贴板] 取表格）
            try { RunOcrBenchGui(); }
            catch (Exception ex)
            {
                MessageBox.Show("ocrbench error: " + ex, "AutoPickup");
            }
            return;
        }
        if (args.Contains("--settings-dump"))
        {
            try
            {
                using var rts = AppRuntime.CreateDefault();
                var sv = new AutoPickup.Config.SettingsView(rts.Settings);
                var sb = new System.Text.StringBuilder();
                int n = 0;
                foreach (System.ComponentModel.PropertyDescriptor pd in sv.GetProperties())
                {
                    sb.AppendLine(pd.Category + " | " + pd.DisplayName + " | " + (pd.GetValue(null)?.ToString() ?? "null"));
                    n++;
                }
                var probe = sv.GetProperties()["ListLeftPercent"];
                double before = rts.Settings.Vision.ListLeftPercent;
                probe?.SetValue(null, 7.7);
                int noDesc = 0;
                foreach (System.ComponentModel.PropertyDescriptor pd in sv.GetProperties())
                {
                    if (string.IsNullOrWhiteSpace(pd.Description)) { sb.AppendLine("缺说明: " + pd.Category + " / " + pd.DisplayName); noDesc++; }
                }
                sb.AppendLine("total=" + n + "  缺说明=" + noDesc + "  ListLeftPercent " + before.ToString("F1") + " -> " + rts.Settings.Vision.ListLeftPercent.ToString("F1") + " (写回验证)");
                probe?.SetValue(null, before);
                string dumpPath = System.IO.Path.Combine(rts.DataDir, "diag", "settings_dump.txt");
                System.IO.File.WriteAllText(dumpPath, sb.ToString());
                NativeConsole.Ln("settings-dump => " + dumpPath);
            }
            catch (Exception ex) { Console.WriteLine("settings-dump error: " + ex.Message); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--gesture")
        {
            try
            {
                using var rtg = AppRuntime.CreateDefault();
                using var mirror = MirrorLog(rtg);
                int waitSec = 0;
                for (int i = 2; i < args.Length; i++)
                    if (args[i] == "--wait" && i + 1 < args.Length) int.TryParse(args[i + 1], out waitSec);
                bool okg = rtg.Machine.TryQuickGestureStandalone(args[1], waitSec);
                NativeConsole.Ln("gesture => " + (okg ? "OK(出现确认弹窗)" : "FAIL(未见确认弹窗)"));
            }
            catch (Exception ex) { Console.WriteLine("gesture error: " + ex.Message); }
            return;
        }
        if (args.Contains("--probe"))
        {
            try
            {
                using var rtp = AppRuntime.CreateDefault();
                bool okp = rtp.Machine.Probe(cycles: 2);
                Console.WriteLine("probe result: " + (okp ? "OK" : "FAIL"));
            }
            catch (Exception ex)
            {
                try { using var rtx = AppRuntime.CreateDefault(); rtx.Log.Error("probe FATAL: " + ex); }
                catch { }
            }
            return;
        }

        bool createdNew;
        using var mutex = new Mutex(true, "AutoPickup_Single_Local", out createdNew);
        if (!createdNew)
        {
            System.Windows.Forms.MessageBox.Show("AutoPickup 已在运行。", "AutoPickup");
            return;
        }

        using var runtime = AppRuntime.CreateDefault();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            runtime.Log.Error("未处理异常: " + e.ExceptionObject, "App");
            runtime.Firewall.SafeCleanup();
        };
        Application.ThreadException += (_, e) =>
        {
            runtime.Log.Error("UI 线程异常: " + e.Exception, "App");
        };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.Run(new MainForm(runtime));
    }


    /// <summary>把 LogBus 输出实时镜像到控制台（cmd 直跑也能看）。</summary>
    private static IDisposable MirrorLog(AutoPickup.AppRuntime rt)
    {
        void Handler(Logging.LogEntry e) => NativeConsole.Ln(e.ToString());
        rt.Log.EntryAdded += Handler;
        return new _Mirror(() => rt.Log.EntryAdded -= Handler);
    }

    private sealed class _Mirror : IDisposable
    {
        private readonly Action _onDispose;
        public _Mirror(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }

    /// <summary>把 OCR 分辨率基准跑成一个独立窗口（沙箱/无控制台环境也能看结果；不依赖文件输出）。</summary>
    private static void RunOcrBenchGui()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var (report, _, _) = RunOcrBenchCore();
        var frm = new Form
        {
            Text = "AutoPickup — OCR 分辨率基准（原生 vs 缩到工作分辨率）",
            Width = 1200,
            Height = 1040,
            Location = new System.Drawing.Point(10, 10),
            StartPosition = FormStartPosition.Manual,
            BackColor = Color.FromArgb(24, 27, 33),
        };
        var box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            Font = new Font("Consolas", 10.5f),
            BackColor = Color.FromArgb(24, 27, 33),
            ForeColor = Color.FromArgb(226, 230, 236),
            BorderStyle = BorderStyle.None,
            Text = report,
        };
        var bar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.FromArgb(31, 35, 42) };
        var btnCopy = new Button { Text = "复制到剪贴板", AutoSize = true, Margin = new Padding(10, 8, 0, 0), FlatStyle = FlatStyle.System };
        btnCopy.Click += (_, _) => { try { Clipboard.SetText(report); } catch { } };
        var tip = new Label
        {
            Text = "  说明：同一张 1024x768 真实帧 + 合成的 1440p/4K（按比例放大，模拟高分辨率下的同区域像素量）。",
            ForeColor = Color.FromArgb(150, 200, 255),
            AutoSize = true,
            Margin = new Padding(16, 14, 0, 0),
        };
        bar.Controls.Add(btnCopy);
        bar.Controls.Add(tip);
        frm.Controls.Add(box);
        frm.Controls.Add(bar);
        Application.Run(frm);
    }

    /// <summary>基准主体：返回（文本报告, JSON 行, 报告落盘路径）。</summary>
    private static (string Report, string JsonRows, string OutPath) RunOcrBenchCore()
    {
        string? path = null;
        var all = Environment.GetCommandLineArgs();
        for (int i = 0; i < all.Length - 1; i++) if (all[i] == "--ocrbench" && !all[i + 1].StartsWith("--")) { path = all[i + 1]; break; }
        using var rt = AppRuntime.CreateDefault();
        if (!rt.Ocr.Available) return ("OCR 不可用（缺语言包？）", "", "");

        var frames = new List<(string Label, Frame F)>();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var f = ImagingIo.LoadImage(path);
            if (f is not null) frames.Add((Path.GetFileName(path), f));
        }
        else
        {
            // 优先抓当前正在运行的 GTA 窗口一帧（真实分辨率最准）；抓不到再退回仓库校准图
            var live = rt.Window.CaptureClient();
            if (live.IsValid) frames.Add(("实时窗口 " + live.Width + "x" + live.Height, live));
            var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "1024_768"));
            if (Directory.Exists(dir))
            {
                foreach (var name in new[] { "save_failed.jpg", "online_pausemenu.jpg" })
                {
                    var p = Path.Combine(dir, name);
                    if (!File.Exists(p)) continue;
                    var f = ImagingIo.LoadImage(p);
                    if (f is not null) frames.Add((name, f));
                }
            }
        }
        if (frames.Count == 0) return ("没有可用基准图（可传 --ocrbench <图片>）", "", "");

        const int workH = 768, workW = 1280;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# OCR 分辨率基准（同一真实帧：原生 vs 缩到工作分辨率 " + workW + "x" + workH + " 长边上限）");
        sb.AppendLine("# 站点=实际调用点；面积=送进 OCR 的像素数");
        var jsonRows = new List<string>();
        foreach (var (label, f) in frames)
        {
            double k = Math.Min((double)workH / f.Height, (double)workW / f.Width);
            if (k > 1.0) k = 1.0;   // 只缩不放
            var scaled = ScaleFrame(f, k);

            var sites = new (string Name, Func<Frame, (byte[] Bgra, int W, int H)> Prep)[]
            {
                ("整帧(全量文本)", fr => (fr.Bgra, fr.Width, fr.Height)),
                ("整帧(词+框/菜单用)", fr => (fr.Bgra, fr.Width, fr.Height)),
                ("左下toast(0..35%W,45..90%H)+2x", fr => Crop(fr, 0, 0.45, 0.35, 0.90, 2)),
                ("tab条(0..100%W,11.98..21.88%H)+2x", fr => Crop(fr, 0, 0.1198, 1.0, 0.2188, 2)),
                ("中央弹窗(18..82%W,26..74%H)+2x", fr => Crop(fr, 0.18, 0.26, 0.82, 0.74, 2)),
            };

            sb.AppendLine();
            sb.AppendLine("## " + label + "  原始 " + f.Width + "x" + f.Height + "  缩放系数 k=" + k.ToString("F3")
                + (k >= 0.999 ? "（不缩）" : "") + "  →  工作 " + scaled.Width + "x" + scaled.Height);
            sb.AppendLine("站点\t冷启动(ms)\t原生稳态(ms)\t原生面积\t缩放稳态(ms)\t缩放面积\t缩放/原生");
            bool firstCall = true;
            foreach (var (name, prep) in sites)
            {
                var (b1, w1, h1) = prep(f);
                var (b2, w2, h2) = prep(scaled);
                // 冷启动：全局第一次 OCR 调用不做预热，单独计时
                long cold = -1;
                if (firstCall)
                {
                    firstCall = false;
                    var tc = System.Diagnostics.Stopwatch.StartNew();
                    rt.Ocr.Recognize(b1, w1, h1);
                    cold = tc.ElapsedMilliseconds;
                }
                else rt.Ocr.Recognize(b1, w1, h1);   // 预热
                rt.Ocr.Recognize(b2, w2, h2);        // 预热
                var t1 = System.Diagnostics.Stopwatch.StartNew();
                rt.Ocr.Recognize(b1, w1, h1);
                long ms1 = t1.ElapsedMilliseconds;
                var t2 = System.Diagnostics.Stopwatch.StartNew();
                rt.Ocr.Recognize(b2, w2, h2);
                long ms2 = t2.ElapsedMilliseconds;
                string ratio = ms1 > 0 ? (ms2 / (double)ms1).ToString("F2") + "x" : "-";
                sb.AppendLine(name + "\t" + (cold >= 0 ? cold.ToString() : "-") + "\t" + ms1 + "\t" + (w1 * h1)
                    + "\t" + ms2 + "\t" + (w2 * h2) + "\t" + ratio);
                jsonRows.Add("{\"frame\":\"" + label + "\",\"site\":\"" + name + "\",\"cold_ms\":" + cold
                    + ",\"native_ms\":" + ms1 + ",\"native_px\":" + (w1 * h1) + ",\"scaled_ms\":" + ms2 + ",\"scaled_px\":" + (w2 * h2) + "}");
            }

        }
        string outPath = Path.Combine(rt.DataDir, "diag", "ocr_bench.txt");
        try { File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false)); } catch { }
        return (sb.ToString(), string.Join(",", jsonRows), outPath);
    }

    /// <summary>把整帧按比例缩放（双线性；只缩不放）。</summary>
    private static Frame ScaleFrame(Frame f, double k)
    {
        if (k >= 0.999 || !f.IsValid) return f;
        int dw = Math.Max(1, (int)Math.Round(f.Width * k)), dh = Math.Max(1, (int)Math.Round(f.Height * k));
        using var src = FrameToBitmap(f);
        using var dst = new System.Drawing.Bitmap(dw, dh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(dst))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(src, new System.Drawing.Rectangle(0, 0, dw, dh));
        }
        return BitmapToFrame(dst);
    }

    /// <summary>按百分比裁剪并做整数倍最近邻放大，模拟各 OCR 站点的预处理。</summary>
    private static (byte[] Bgra, int W, int H) Crop(Frame f, double l, double t, double r, double b, int scale)
    {
        int x0 = (int)Math.Round(f.Width * l), x1 = (int)Math.Round(f.Width * r);
        int y0 = (int)Math.Round(f.Height * t), y1 = (int)Math.Round(f.Height * b);
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(f.Width, Math.Max(x0 + 1, x1)); y1 = Math.Min(f.Height, Math.Max(y0 + 1, y1));
        int w = x1 - x0, h = y1 - y0;
        int uw = w * scale, uh = h * scale;
        var gray = Imaging.BgraToGray(f.Bgra, f.Width, f.Height);
        var bgra = new byte[uw * uh * 4];
        for (int yy = 0; yy < uh; yy++)
        {
            int sy = Math.Min(h - 1, yy / scale) + y0;
            for (int xx = 0; xx < uw; xx++)
            {
                int sx = Math.Min(w - 1, xx / scale) + x0;
                byte v = gray[sy * f.Width + sx];
                int i = (yy * uw + xx) * 4;
                bgra[i] = v; bgra[i + 1] = v; bgra[i + 2] = v; bgra[i + 3] = 255;
            }
        }
        return (bgra, uw, uh);
    }

    private static System.Drawing.Bitmap FrameToBitmap(Frame f)
    {
        var bmp = new System.Drawing.Bitmap(f.Width, f.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, f.Width, f.Height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(f.Bgra, 0, data.Scan0, f.Bgra.Length);
        bmp.UnlockBits(data);
        return bmp;
    }

    private static Frame BitmapToFrame(System.Drawing.Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var bytes = new byte[w * h * 4];
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(data);
        return new Frame(w, h, bytes);
    }

    /// <summary>横幅判定对比：对一张图同时给出 ①归一化后各 scale 的 NCC 最佳分数 ②上部搜索带的 OCR 文本。
    /// 用法：--banner-probe [图片]（不传则抓当前游戏窗口一帧）。</summary>
    private static void RunBannerProbe(string[] args)
    {
        using var rt = AppRuntime.CreateDefault();
        string? path = null;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--banner-probe" && !args[i + 1].StartsWith("--")) path = args[i + 1];
        Frame? frame = null;
        if (!string.IsNullOrWhiteSpace(path)) frame = ImagingIo.LoadImage(path);
        if (frame is null) frame = rt.Window.CaptureClient();
        if (!frame.IsValid) { Console.WriteLine("无可用帧"); return; }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("帧 " + frame.Width + "x" + frame.Height);
        // ① NCC 全尺度扫描（归一化 768）
        var v = rt.Settings.Vision;
        var gray = Imaging.BgraToGray(frame.Bgra, frame.Width, frame.Height);
        int workH = Math.Max(240, v.BannerWorkHeight);
        double k = frame.Height > workH + 8 ? workH / (double)frame.Height : 1.0;
        int dw = Math.Max(64, (int)Math.Round(frame.Width * k));
        var small = k < 1.0 ? Imaging.GrayDownsample(gray, frame.Width, frame.Height, dw, workH) : gray;
        int sh = k < 1.0 ? workH : frame.Height;
        sb.AppendLine("归一化 " + dw + "x" + sh + "  k=" + k.ToString("F3"));
        sb.AppendLine("模板\tscale\tNCC\t位置");
        foreach (var tname in new[] { "title_GrandTheftAutoV.jpg", "title_GrandTheftAuto在线模式.jpg", "title_GrandTheftAuto在线模式_2.jpg" })
        {
            var tpath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "assets", "templates", tname);
            if (!File.Exists(tpath)) tpath = Path.Combine(AppContext.BaseDirectory, "assets", "templates", tname);
            if (!File.Exists(tpath)) { sb.AppendLine(tname + "\t(缺模板)"); continue; }
            var tf = ImagingIo.LoadImage(tpath);
            if (tf is null) { sb.AppendLine(tname + "\t(读失败)"); continue; }
            var tpl = new AutoPickup.Core.Vision.TemplateDef
            {
                Name = tname, Group = "probe", Width = tf.Width, Height = tf.Height,
                Gray = Imaging.BgraToGray(tf.Bgra, tf.Width, tf.Height),
            };
            foreach (double s in new[] { 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0 })
            {
                var vv = new AutoPickup.Config.AppSettings.VisionSection
                {
                    BannerWorkHeight = 4000, ScaleMin = s, ScaleMax = s, ScaleStep = 0.5,
                    BannerSearchPercent = v.BannerSearchPercent,
                };
                var mm = new AutoPickup.Core.Vision.NccMatcher(vv);
                var hit = mm.LocateBestCore(small, dw, sh, tpl, AutoPickup.Core.Vision.MatchFeature.Text, Math.Clamp(v.BannerSearchPercent / 100.0, 0.05, 1.0));
                sb.AppendLine(tname + "\t" + s.ToString("F2") + "\t" + (hit is null ? "-" : hit.Score.ToString("F3"))
                    + "\t" + (hit is null ? "-" : hit.X1 + "," + hit.Y1));
            }
        }
        // ② 横幅带 OCR
        int bandH = Math.Max(24, (int)Math.Round(frame.Height * Math.Clamp(v.BannerSearchPercent / 100.0, 0.05, 1.0)));
        var strip = new byte[frame.Width * bandH];
        for (int y = 0; y < bandH; y++) Array.Copy(gray, y * frame.Width, strip, y * frame.Width, frame.Width);
        var prep = AutoPickup.Core.Vision.OcrPrep.ForFullFrame(strip, frame.Width, bandH, v);
        var txt = rt.Ocr.RecognizeGray(prep.Gray, prep.Width, prep.Height);
        sb.AppendLine();
        sb.AppendLine("横幅带 OCR（上部 " + v.BannerSearchPercent + "% = " + bandH + "px，送OCR " + prep.Width + "x" + prep.Height + "）：");
        sb.AppendLine("  " + (txt ?? "(无文本)"));
        // 词框（原帧坐标）：用来精确确定横幅模板该裁哪里
        var words = rt.Ocr.RecognizeWords(BgraFromGrayLocal(prep.Gray), prep.Width, prep.Height);
        if (words.Count > 0)
        {
            double inv = prep.Resized ? 1.0 / prep.Scale : 1.0;
            sb.AppendLine("  词框(原帧坐标, 只列高度≥8 或宽度≥40):");
            foreach (var wd in words.OrderByDescending(x => x.X2 - x.X1))
            {
                int x1 = (int)(wd.X1 * inv), y1 = (int)(wd.Y1 * inv), x2 = (int)(wd.X2 * inv), y2 = (int)(wd.Y2 * inv);
                if ((y2 - y1) >= 8 || (x2 - x1) >= 40)
                    sb.AppendLine("    [" + x1 + "," + y1 + " - " + x2 + "," + y2 + "] " + wd.Text);
            }
        }
        var norm = txt is null ? "" : new string(txt.Where(c => !char.IsWhiteSpace(c)).ToArray());
        sb.AppendLine("  含“在线模式”=" + norm.Contains("在线模式") + "  含“职业”=" + norm.Contains("职业")
            + "  含“简讯”=" + norm.Contains("简讯") + "  含“Grand”=" + norm.Contains("Grand"));
        string outPath = Path.Combine(rt.DataDir, "diag", "banner_probe.txt");
        try { File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false)); } catch { }
        Console.WriteLine(sb.ToString());
        Console.WriteLine("=> " + outPath);
    }

    /// <summary>灰度→BGRA（本地小工具，避免依赖测试命名空间）。</summary>
    private static byte[] BgraFromGrayLocal(byte[] gray)
    {
        var b = new byte[gray.Length * 4];
        for (int i = 0, j = 0; i < gray.Length; i++, j += 4)
        { byte v = gray[i]; b[j] = v; b[j + 1] = v; b[j + 2] = v; b[j + 3] = 255; }
        return b;
    }

    /// <summary>性能剖析：对一张图跑 Read（含横幅模板+NCC+OCR），打印每阶段与每条模板的耗时。
    /// 用法：--profile-read [图片]（不传则抓当前游戏窗口一帧）。</summary>
    private static void RunProfileRead(string[] args)
    {
        using var rt = AppRuntime.CreateDefault();
        string? path = null;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--profile-read" && !args[i + 1].StartsWith("--")) path = args[i + 1];
        Frame? frame = null;
        if (!string.IsNullOrWhiteSpace(path)) frame = ImagingIo.LoadImage(path);
        if (frame is null) frame = rt.Window.CaptureClient();
        if (!frame.IsValid) { Console.WriteLine("无可用帧（传图片路径或先启动游戏）"); return; }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("帧 " + frame.Width + "x" + frame.Height + (path ?? "（实时窗口）"));
        NccMatcher.TimingReport = (name, featMs, totalMs, sc, w, h, band) =>
            sb.AppendLine(string.Format("  模板 {0,-34} 特征图 {1,8:F0}ms 总 {2,8:F0}ms 尺度 {3} 搜索带 {4:F0}%",
                name, featMs, totalMs, sc, band * 100));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = rt.Reader.Read(frame, withOcr: true);
        long total = sw.ElapsedMilliseconds;
        NccMatcher.TimingReport = null;
        sb.AppendLine(string.Format("Read 合计 {0}ms   判定={1} 故事={2:F3} 在线={3:F3}", total, res.Kind, res.StoryScore, res.OnlineScore));
        string outPath = Path.Combine(rt.DataDir, "diag", "profile_read.txt");
        try { File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false)); } catch { }
        Console.WriteLine(sb.ToString());
        Console.WriteLine("=> " + outPath);
    }

    /// <summary>OCR 离线回归（不碰游戏）：用仓库校准图跑真实识别管线，核对区域关键词是否仍命中、耗时是否可控。</summary>
    private static void RunOcrRegress(bool showGui)
    {
        using var rt = AppRuntime.CreateDefault();
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "1024_768"));
        var cases = new (string File, string Site, string[] Keys)[]
        {
            ("save_failed.jpg", "左下toast(保存失败)", new[] { "保存", "失败", "灾", "失" }),
            ("online_pausemenu.jpg", "tab条(在线菜单)", new[] { "地图", "在线", "职业", "好友", "商店", "信息", "简讯", "游戏" }),
            ("online_pausemenu.jpg", "整帧(菜单页)", new[] { "在线", "职业", "好友", "商店", "简讯", "游戏" }),
            ("story_pausemenu1.jpg", "整帧(故事菜单)", new[] { "故事", "简讯", "在线", "好友", "商店" }),
            ("online2story_exitdialog.jpg", "中央弹窗(退出确认)", new[] { "退出", "确认", "在线", "故事", "保存" }),
            ("online2storyitem.jpg", "焦点行(列表)", new[] { "在线", "好友", "商店", "地图", "设置", "信息", "简讯", "游戏", "职业" }),
        };
        var sb = new System.Text.StringBuilder();
        int pass = 0, total = 0;
        sb.AppendLine("OCR 离线回归（" + root + "）");
        sb.AppendLine("图片\t站点\t送OCR尺寸\t缩放\t耗时ms\t命中\t文本");
        foreach (var (file, site, keys) in cases)
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path)) { sb.AppendLine(file + "\t" + site + "\t(缺文件)"); continue; }
            var frame = ImagingIo.LoadImage(path);
            if (frame is null) { sb.AppendLine(file + "\t" + site + "\t(读图失败)"); continue; }
            string? text;
            string note = "";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (site.StartsWith("左下toast"))
            {
                var prep = rt.Reader.PrepareRegion(frame, "左下toast");
                text = prep.Text;
                note = prep.Note;
            }
            else if (site.StartsWith("中央弹窗"))
            {
                var prep = rt.Reader.PrepareRegion(frame, "中央弹窗");
                text = prep.Text;
                note = prep.Note;
            }
            else if (site.StartsWith("tab条"))
            {
                var t = rt.Tab.Read(frame);
                text = t?.StripWords;
                note = "tab条";
            }
            else if (site.StartsWith("焦点行"))
            {
                var row = rt.RowReader.FindFocusRow(frame);
                text = row?.Label;
                note = "焦点行";
            }
            else
            {
                var res = rt.Reader.Read(frame, withOcr: true);
                text = res.OcrText;
                note = "整帧(判定=" + res.Kind + ")";
            }
            long ms = sw.ElapsedMilliseconds;
            string norm = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
            var hit = keys.Where(k => norm.Contains(k)).ToArray();
            total++;
            if (hit.Length > 0) pass++;
            sb.AppendLine(file + "\t" + site + "\t" + note + "\t-\t" + ms + "\t" + hit.Length + "/" + keys.Length
                + "\t" + (text ?? "(null)").Replace("\t", " ").Replace("\n", " "));
        }
        sb.AppendLine();
        sb.AppendLine("命中站点 " + pass + "/" + total);
        string outPath = Path.Combine(rt.DataDir, "diag", "ocr_regress.txt");
        try { File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false)); } catch { }
        if (showGui)
        {
            var frm = new Form
            {
                Text = "AutoPickup — OCR 离线回归（校准图）",
                Width = 1200,
                Height = 780,
                Location = new System.Drawing.Point(20, 20),
                StartPosition = FormStartPosition.Manual,
                BackColor = Color.FromArgb(24, 27, 33),
            };
            var box = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                WordWrap = false,
                Font = new Font("Consolas", 10f),
                BackColor = Color.FromArgb(24, 27, 33),
                ForeColor = Color.FromArgb(226, 230, 236),
                BorderStyle = BorderStyle.None,
                Text = sb.ToString(),
            };
            frm.Controls.Add(box);
            Application.Run(frm);
        }
        else
        {
            NativeConsole.Ln(sb.ToString() + "=> " + outPath);
        }
    }

    /// <summary>离线解读一张图片：游戏窗口上实时显示“送进 OCR 的灰度图 + 词框 + 文本 + 尺寸/耗时”，并画出识别区域与横幅命中。
    /// 用法：AutoPickup.exe --analyze &lt;png/jpg&gt;；需要 GTA 窗口存在（覆盖层贴在游戏窗口上），Esc 或关闭窗口退出。</summary>
    private static void RunAnalyze(string imagePath)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var rt = AppRuntime.CreateDefault();
        var frame = ImagingIo.LoadImage(imagePath);
        if (frame is null) { MessageBox.Show("无法读取图片: " + imagePath, "AutoPickup"); return; }
        if (rt.Window.Handle == IntPtr.Zero)
        {
            MessageBox.Show("未找到 GTA 窗口（覆盖层要贴在游戏窗口上，请先启动游戏）", "AutoPickup");
            return;
        }
        using var ov = new AutoPickup.Ui.OverlayForm(rt.Window, rt.Settings, rt.Log, rt.Matcher, rt.Bank);
        // 固定放在桌面左上（离线预览不依赖游戏窗口状态，便于截图/核对）
        ov.UseFixedBounds(new System.Drawing.Rectangle(300, 60, 900, 640));
        ov.Show();
        Application.DoEvents();
        ov.SetTuner(true);            // 区域可视化（只画框，不贴截图）
        Application.DoEvents();

        rt.Log.Info(string.Format("离线解读 {0}：帧 {1}x{2}，OCR {3}…", Path.GetFileName(imagePath), frame.Width, frame.Height,
            rt.Ocr.Available ? "可用" : "不可用"), "Analyze");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        rt.Reader.PreviewWords = true;
        var res = rt.Reader.Read(frame, withOcr: true);
        long ms = sw.ElapsedMilliseconds;
        ov.PushEvidence(frame.Width, frame.Height, res.OcrWords, res.OcrText ?? "(无OCR文本)",
            Path.GetFileName(imagePath) + " 判定=" + res.Kind, ms, res.OcrSizeNote);
        rt.Log.Info(string.Format("判定={0} 置信={1:F3} 横幅 故事={2:F3}/在线={3:F3}/主菜单={4:F3} 词框={5} 送OCR={6} 耗时={7}ms",
            res.Kind, res.Confidence, res.StoryScore, res.OnlineScore, res.HomeScore, res.OcrWords.Count, res.OcrSizeNote, ms), "Analyze");

        // 10 秒内持续重绘（给横幅探测留出时间），然后自动退出
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(30);
        }
    }

    /// <summary>单张识别。用法: AutoPickup.exe --classify &lt;png/jpg&gt; [--ocr]</summary>
    private static void RunClassify(string imagePath)
    {
        using var rt = AppRuntime.CreateDefault();
        var frame = ImagingIo.LoadImage(imagePath);
        if (frame is null) { Console.WriteLine("无法读取图片: " + imagePath); return; }
        bool withOcr = Array.IndexOf(Environment.GetCommandLineArgs(), "--ocr") >= 0;
        bool quick = Array.IndexOf(Environment.GetCommandLineArgs(), "--quick") >= 0;
        var res = rt.Reader.Read(frame, withOcr, includeHome: !quick);
        Console.WriteLine("Kind: " + res.Kind + "  Confidence: " + res.Confidence.ToString("F3"));
        Console.WriteLine("故事=" + res.StoryScore.ToString("F3") + " 在线=" + res.OnlineScore.ToString("F3") + " 主菜单=" + res.HomeScore.ToString("F3"));
        foreach (var d in res.DebugLines) Console.WriteLine("  " + d);
        if (res.OcrText is not null)
        {
            Console.WriteLine("OCR: " + (res.OcrText.Length > 400 ? res.OcrText.Substring(0, 400) : res.OcrText));
        }
    }

    /// <summary>焦点行识别：--row &lt;png/jpg&gt; 打印白带坐标与 OCR 文本（校准用）。</summary>
    private static void RunRow(string imagePath)
    {
        using var rt = AppRuntime.CreateDefault();
        var frame = ImagingIo.LoadImage(imagePath);
        if (frame is null) { Console.WriteLine("无法读取图片: " + imagePath); return; }
        var row = rt.RowReader.FindFocusRow(frame);
        if (row is null) { Console.WriteLine("未找到焦点行白带"); return; }
        Console.WriteLine("focus: x " + row.X0 + ".." + row.X1 + "  y " + row.Y0 + ".." + row.Y1 +
                          "  h " + row.Height + "  label: " + (row.Label ?? "(null)"));
    }

    /// <summary>音频峰值表自测：--audiotest [秒]（游戏出声时观察音量与 cue 阈值）。</summary>
    private static void RunAudioTest(AppRuntime rt, int secs)
    {
        using var mirror = MirrorLog(rt);
        if (!rt.Audio.IsAvailable) { NativeConsole.Ln("音频 cue 源不可用（未找到 GTA 音频会话）"); return; }
        NativeConsole.Ln("音频测试开始，请让游戏出声（如切线上时的下云声）…");
        var det = new AutoPickup.Core.Audio.CloudCueDetector(rt.Log, rt.Settings.Audio);
        det.Begin();
        var stw = System.Diagnostics.Stopwatch.StartNew();
        var window = new List<float>();
        int printed = 0;
        while (stw.Elapsed.TotalSeconds < secs)
        {
            float p = rt.Audio.CurrentPeak;
            window.Add(p);
            if (window.Count > 60) window.RemoveAt(0);
            bool hit = det.Feed(p);
            int el = (int)stw.Elapsed.TotalSeconds;
            if (el > printed)
            {
                printed = el;
                double avg = window.Average(), mx = window.Max();
                NativeConsole.Ln(string.Format("[{0,3}s] peak={1,5:P1} avg={2:P1} max={3:P1}{4}",
                    el, p, avg, mx, hit ? "  << cue!" : ""));
            }
            Thread.Sleep(100);
        }
        NativeConsole.Ln("音频测试结束");
    }

    /// <summary>防火墙：--fw add|enable|disable|delete|status（管理员）。</summary>
    private static void RunFw(string op)
    {
        using var rt = AppRuntime.CreateDefault();
        using var mirror = MirrorLog(rt);
        switch (op.ToLowerInvariant())
        {
            case "add":
                var path = rt.Window.IsProcessRunning() ? Core.Native.Win32.FindWindowW("sgaWindow", "Grand Theft Auto V") : IntPtr.Zero;
                // 规则需程序路径：由 Firewall.AddRule 内部解析进程路径失败时提示
                _ = path;
                bool added = rt.Firewall.AddRule(FindGtaPath());
                NativeConsole.Ln("fw add => " + (added ? "OK" : "FAIL"));
                break;
            case "enable":
                NativeConsole.Ln("fw enable => " + (rt.Firewall.Enable() ? "OK" : "FAIL"));
                break;
            case "disable":
                NativeConsole.Ln("fw disable => " + (rt.Firewall.Disable() ? "OK" : "FAIL"));
                break;
            case "delete":
                NativeConsole.Ln("fw delete => " + (rt.Firewall.DeleteRule() ? "OK" : "FAIL"));
                break;
            case "status":
                NativeConsole.Ln("rule exists=" + rt.Firewall.Exists() +
                                 (rt.Firewall.IsEnabled() is bool e ? " enabled=" + e : " enabled=?" ));
                break;
            default:
                NativeConsole.Ln("用法: --fw add|enable|disable|delete|status");
                break;
        }
    }

    private static string FindGtaPath()
    {
        try
        {
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("GTA5_Enhanced"))
            {
                if (!string.IsNullOrEmpty(proc.MainModule?.FileName)) return proc.MainModule.FileName;
            }
        }
        catch { }
        return "";
    }

    /// <summary>词级 OCR 诊断：--words &lt;png/jpg&gt; 打印词数与样本。</summary>
    private static void RunWords(string imagePath)
    {
        using var rt = AppRuntime.CreateDefault();
        var frame = ImagingIo.LoadImage(imagePath);
        if (frame is null) { Console.WriteLine("无法读取图片: " + imagePath); return; }
        var words = rt.Ocr.RecognizeWords(frame.Bgra, frame.Width, frame.Height);
        Console.WriteLine("words count: " + words.Count);
        int shown = 0;
        foreach (var w in words)
        {
            if (shown++ >= 40) break;
            Console.WriteLine("  [" + w.Y1 + ".." + w.Y2 + " x" + w.X1 + ".." + w.X2 + "] " + w.Text);
        }
    }

    /// <summary>标签条识别：--tab &lt;png/jpg&gt;。</summary>
    private static void RunTab(string imagePath)
    {
        using var rt = AppRuntime.CreateDefault();
        var frame = ImagingIo.LoadImage(imagePath);
        if (frame is null) { Console.WriteLine("无法读取图片: " + imagePath); return; }
        var t = rt.Tab.Read(frame);
        Console.WriteLine("选中tab: " + (t?.Selected ?? "(null)") + "  白块x " +
                          (t is { WhiteX0: >= 0 } ? t.WhiteX0 + ".." + t.WhiteX1 : "-") +
                          "  strip: " + (t?.StripWords ?? ""));
    }

    /// <summary>
    /// 新流程引擎入口（与旧死流程并存）：
    ///   --run-flow &lt;流程.json&gt; --replay &lt;回放脚本.json&gt;   ← 离线回放，不碰游戏
    ///   --run-flow &lt;流程.json&gt;                             ← 实机执行
    /// 轨迹输出到数据目录 flow_trace.txt（供断言/查看）。
    /// </summary>
    private static void RunFlowV2(string[] args)
    {
        string flowPath = args[1];
        string? replayPath = null;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--replay") replayPath = args[i + 1];
        var flow = AutoPickup.Core.Flow.FlowJson.Load(flowPath);

        string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoPickup");
        Directory.CreateDirectory(dataDir);
        string outPath = Path.Combine(dataDir, "flow_trace.txt");

        if (replayPath is not null)
        {
            // 回放：不建 AppRuntime（不碰音频/手柄/防火墙），但**必须用同一份 settings.json**，
            // 否则区域百分比/阈值与实机不一致，回放结论就没意义（曾因此误判为“OCR 退化”）。
            var store = new AutoPickup.Config.SettingsStore(dataDir);
            var settings = store.Load();
            var log = new Logging.LogBus(Path.Combine(dataDir, "logs", "flow_replay.log"));
            var host = new AutoPickup.Core.Flow.ReplayFlowHost(replayPath);
            var ocr = AutoPickup.Core.Vision.Ocr.OcrFactory.Create(log);          // 若语言包可用则做真实 OCR
            var tab = new AutoPickup.Core.Vision.TabReader(ocr, log, settings);
            var bank = new AutoPickup.Core.Vision.TemplateBank(log);
            var tplDir = AppRuntime.ResolveTemplateDir();
            if (tplDir is not null) bank.LoadFromDirectory(tplDir);
            var reader = new AutoPickup.Core.Vision.ScreenReader(bank,
                new AutoPickup.Core.Vision.NccMatcher(settings.Vision), ocr, log, settings.Vision);
            var engine = new AutoPickup.Core.Flow.FlowEngine(host, log, settings, tab, reader, ocr,
                new AutoPickup.Core.Vision.FocusRowReader(ocr, log, settings));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = engine.Run(flow);
            sw.Stop();
            WriteFlowTrace(flow, host.Name, ok, sw.ElapsedMilliseconds, engine, outPath);
            return;
        }

        using var rt = AppRuntime.CreateDefault();
        var live = new AutoPickup.Core.Flow.LiveFlowHost(rt.Window, rt.Input, rt.Firewall, rt.Settings, rt.Log);
        var eng = new AutoPickup.Core.Flow.FlowEngine(live, rt.Log, rt.Settings, rt.Tab, rt.Reader, rt.Ocr);
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        bool ok2 = eng.Run(flow);
        sw2.Stop();
        WriteFlowTrace(flow, live.Name, ok2, sw2.ElapsedMilliseconds, eng, outPath);
    }

    /// <summary>
    /// 诊断：同一张图连续观察 N 次，看 OCR 词数是否随次数退化（复现“同进程连续 1080p OCR 退化”）。
    /// 用法：--obs-selftest &lt;图片&gt; [次数]
    /// </summary>
    private static void RunObsSelfTest(string[] args)
    {
        string img = args[1];
        int times = 6;
        for (int i = 1; i < args.Length - 1; i++) if (args[i] == "--obs-selftest") int.TryParse(args[i + 1], out times);
        string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoPickup");
        var log = new Logging.LogBus(Path.Combine(dataDir, "logs", "obs_selftest.log"));
        var settings = new AutoPickup.Config.AppSettings();
        var ocr = AutoPickup.Core.Vision.Ocr.OcrFactory.Create(log);
        var tab = new AutoPickup.Core.Vision.TabReader(ocr, log, settings);
        var frame = AutoPickup.Core.Vision.ImagingIo.LoadImage(img);
        if (frame is null) { Console.WriteLine("读图失败: " + img); return; }
        // 对照：同一张图分别走 TabReader 与 ScreenReader（后者=reader.Read 的路径）
        try
        {
            var bank = new AutoPickup.Core.Vision.TemplateBank(log);
            var tplDir = AppRuntime.ResolveTemplateDir();
            if (tplDir is not null) bank.LoadFromDirectory(tplDir);
            var reader = new AutoPickup.Core.Vision.ScreenReader(bank,
                new AutoPickup.Core.Vision.NccMatcher(settings.Vision), ocr, log, settings.Vision);
            var rr = reader.Read(frame, withOcr: true);
            Console.WriteLine("  [对照] ScreenReader.Read → 判定=" + rr.Kind + " OcrWords=" + rr.OcrWords.Count
                + " OcrText前40=" + (rr.OcrText is null ? "(null)" : rr.OcrText.Substring(0, Math.Min(40, rr.OcrText.Length))));
            var tr2 = tab.Read(frame);
            Console.WriteLine("  [对照] TabReader.Read → tab条=" + (tr2?.StripWords ?? "(null)") + " 选中=" + (tr2?.Selected ?? "?"));
        }
        catch (Exception ex) { Console.WriteLine("  [对照] 失败: " + ex.Message); }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("图: " + Path.GetFileName(img) + "  " + frame.Width + "x" + frame.Height + "  连续观察 " + times + " 次:");
        for (int i = 1; i <= times; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int words = ocr.RecognizeWords(frame.Bgra, frame.Width, frame.Height).Count;
            long wms = sw.ElapsedMilliseconds;
            sw.Restart();
            var tr = tab.Read(frame);
            long tms = sw.ElapsedMilliseconds;
            sb.AppendLine($"  第 {i} 次: OCR词数={words} ({wms}ms)  tab条={(tr?.StripWords ?? "(null)")} 选中={tr?.Selected ?? "?"} ({tms}ms)");
        }
        string outPath = Path.Combine(dataDir, "obs_selftest.txt");
        try { File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false)); } catch { }
        Console.WriteLine(sb.ToString());
        Console.WriteLine("=> " + outPath);
    }

    /// <summary>把一帧存成 PNG（--dump-frame / --record 共用）。</summary>
    private static void SaveFramePng(AutoPickup.Core.Capture.Frame f, string path)
    {
        using var bmp = new System.Drawing.Bitmap(f.Width, f.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, f.Width, f.Height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(f.Bgra, 0, d.Scan0, f.Bgra.Length);
        bmp.UnlockBits(d);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void WriteFlowTrace(AutoPickup.Core.Flow.FlowProgram flow, string hostName, bool ok,
        long ms, AutoPickup.Core.Flow.FlowEngine engine, string outPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("流程: " + flow.Name + (string.IsNullOrEmpty(flow.Description) ? "" : "  (" + flow.Description + ")"));
        sb.AppendLine("宿主: " + hostName + "   结果: " + (ok ? "OK" : "FAIL") + "   用时: " + ms + "ms");
        sb.AppendLine("轨迹:");
        foreach (var t in engine.Trace) sb.AppendLine("  " + t);
        sb.AppendLine("最终证据: " + engine.Ctx.Snapshot());
        try { File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false)); } catch { }
        Console.WriteLine(sb.ToString());
        Console.WriteLine("=> " + outPath);
    }

    /// <summary>完整流程：--flow story（在线→故事）或 --flow online（故事→在线邀请战局）。</summary>
    private static void RunFlow(string which)
    {
        using var rt = AppRuntime.CreateDefault();
        using var mirror = MirrorLog(rt);
        NativeConsole.Ln("flow start: " + which);
        bool ok = which switch
        {
            "story" => rt.Machine.EnsureStory(),
            "online" => rt.Machine.EnsureOnlineInvite(),
            _ => false,
        };
        NativeConsole.Ln("flow " + which + " => " + (ok ? "OK" : "FAIL"));
    }

    /// <summary>列表内逐键导航自测：--navlist &lt;目标条目&gt;（游戏需已暂停且在 在线 tab 列表）。</summary>
    private static void RunNavList(string target)
    {
        using var rt = AppRuntime.CreateDefault();
        using var mirror = MirrorLog(rt);
        NativeConsole.Ln("导航目标: " + target + "（在 在线 列表内逐键闭环）");
        var last = rt.Machine.NavigateToListTarget(MenuRegistry.OnlineMainItems, target);
        NativeConsole.Ln("结果: " + (last ?? "null"));
    }

    /// <summary>批量识别：结果写数据目录 batch_result.tsv（逐行落盘，单文件失败不影响继续）。</summary>
    private static void RunBatch(string root)
    {
        using var rt = AppRuntime.CreateDefault();
        bool withOcr = Array.IndexOf(Environment.GetCommandLineArgs(), "--ocr") >= 0;
        if (!Directory.Exists(root))
        {
            Console.WriteLine("目录不存在: " + root);
            return;
        }
        string outPath = Path.Combine(rt.DataDir, "diag", "batch_result.tsv");
        using var sw = new StreamWriter(outPath, false, new System.Text.UTF8Encoding(false));
        void W(string s)
        {
            sw.WriteLine(s);
            sw.Flush();
        }
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f);
        rt.Log.Info("batch start: " + root, "Batch");
        foreach (var f in files)
        {
            string label = new DirectoryInfo(Path.GetDirectoryName(f)!).Name;
            string name = Path.GetFileName(f);
            try
            {
                var frame = ImagingIo.LoadImage(f);
                if (frame is null) { W(label + "|" + name + "|loadfail"); continue; }
                var res = rt.Reader.Read(frame, withOcr);
                string ocr = "";
                if (res.OcrText is not null)
                {
                    string tx = res.OcrText.Replace((char)9, ' ').Trim();
                    ocr = tx.Length > 120 ? tx.Substring(0, 120) : tx;
                }
                W(label + "|" + name + "|" + res.Kind + "|" +
                  res.Confidence.ToString("F3") + "|" +
                  res.StoryScore.ToString("F3") + "|" + res.OnlineScore.ToString("F3") + "|" +
                  res.HomeScore.ToString("F3") + "|" + ocr);
            }
            catch (Exception ex)
            {
                W(label + "|" + name + "|ERR|" + ex.Message.Replace((char)9, ' '));
                rt.Log.Error("B fail " + name + " : " + ex, "Batch");
            }
        }
        rt.Log.Info("batch done: " + outPath, "Batch");
        Console.WriteLine("完成: " + outPath);
    }

    /// <summary>ASCII 亮度图：把截图降采样成字符画打印，用于离线标定菜单几何。</summary>
    private static void RunView(string imagePath)
    {
        using var rt = AppRuntime.CreateDefault();
        var frame = ImagingIo.LoadImage(imagePath);
        if (frame is null) { Console.WriteLine("无法读取: " + imagePath); return; }
        int cell = 12;
        int cols = frame.Width / cell, rows = frame.Height / cell;
        string ramp = " .:-=+*#%@";
        var sb = new System.Text.StringBuilder();
        for (int cy = 0; cy < rows; cy++)
        {
            for (int cx = 0; cx < cols; cx++)
            {
                long lum = 0; int n = 0;
                int y0 = cy * cell, x0 = cx * cell;
                for (int y = y0; y < y0 + cell && y < frame.Height; y++)
                {
                    int o = (y * frame.Width + x0) * 4;
                    for (int x = x0; x < x0 + cell && x < frame.Width; x++, o += 4)
                    {
                        int bb = frame.Bgra[o], gg = frame.Bgra[o + 1], rr = frame.Bgra[o + 2];
                        lum += (rr * 77 + gg * 150 + bb * 29) >> 8;
                        n++;
                    }
                }
                int idx = n == 0 ? 0 : (int)((lum / n) * (ramp.Length - 1) / 255.0);
                sb.Append(ramp[idx]);
            }
            sb.AppendLine("|" + (cy * cell).ToString().PadLeft(4));
        }
        string outPath = Path.Combine(Path.GetTempPath(), "apview_" + Path.GetFileNameWithoutExtension(imagePath) + ".txt");
        System.IO.File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false));
        Console.WriteLine("ok: " + outPath);
    }

}