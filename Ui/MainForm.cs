using System.Text.Json;
using AutoPickup;
using AutoPickup.Config;
using AutoPickup.Core.Native;
using AutoPickup.Core.Vision;
using AutoPickup.Logging;

namespace AutoPickup.Ui;

/// <summary>主控窗口 v0.5.1：可拖拽布局 + 内容自适应高度（不再固定/截断）+ 记住窗口尺寸；
/// 自检=实时灯+只读探测；参数=扁平可编辑；流程=封存档F11+模式切换+班次。</summary>
public sealed class MainForm : Form
{
    private static readonly Color C_Bg = Color.FromArgb(243, 246, 250);
    private static readonly Color C_Panel = Color.FromArgb(255, 255, 255);
    private static readonly Color C_Text = Color.FromArgb(38, 42, 50);
    private static readonly Color C_Muted = Color.FromArgb(112, 120, 132);
    private static readonly Color C_Accent = Color.FromArgb(31, 96, 176);
    private static readonly Color LOk = Color.FromArgb(46, 160, 67);
    private static readonly Color LWarn = Color.FromArgb(230, 160, 20);
    private static readonly Color LBad = Color.FromArgb(220, 60, 60);
    private static readonly Color LIdle = Color.FromArgb(170, 176, 186);
    private const int HotkeyId = 0x4150;        // 封存档（F7）
    private const int HotkeyIdTune = 0x4151;    // OCR 覆盖层（F6）
    private const int HotkeyIdBlockAll = 0x4152;// 完全断网（F8）

    private readonly AppRuntime _rt;
    private readonly RichTextBox _logBox;
    private readonly CheckBox _autoScroll;
    private readonly Button _btnLogFold;
    private readonly SplitContainer _split;
    private readonly Dictionary<string, (Label Dot, Label Detail)> _lights = new();
    private readonly List<Label> _wrapLabels = new();
    private readonly System.Windows.Forms.Timer _lightTimer = new() { Interval = 1000 };
    private readonly string _uiPath;
    private DateTime _fwCheckedAt = DateTime.MinValue;
    private bool _fwExists;
    private bool? _fwEnabled;
    private DateTime _fwAllCheckedAt = DateTime.MinValue;
    private bool _fwAllExists;
    private bool? _fwAllEnabled;
    private int _logHeight = 190;
    private bool _logExpanded = true;
    private readonly List<Control> _actionControls = new();
    private PropertyGrid? _grid;
    private SettingsView? _paramsView;
    private TabControl _tabs = null!;
    private volatile bool _busy;
    private CancellationTokenSource? _shiftCts;
    private Button _btnStartShift = null!;
    private Button _btnStopShift = null!;
    private NumericUpDown _rounds = null!;
    private NumericUpDown _waitMin = null!;
    private CheckBox _stayOnline = null!;
    private CheckBox _useFw = null!;
    private CheckBox _quickEntry = null!;
    private CheckBox _quickReturn = null!;
    private CheckBox _bgMode = null!;
    private Label _shiftStatus = null!;
    private Label _blockLabel = null!;
    private OverlayForm? _overlay;
    private Button? _btnOverlay;
    private CheckBox? _chkSaveSample;
    private CheckBox? _chkVerboseLog;
    private Label? _dataInfoLabel;
    private DateTime _dataInfoAt = DateTime.MinValue;

    public MainForm(AppRuntime rt)
    {
        _rt = rt;
        _uiPath = Path.Combine(_rt.DataDir, "ui.json");
        Text = "AutoPickup — GTAV 挂机取货助手";
        Width = 1180;
        Height = 880;
        MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = C_Bg;
        Font = new Font("Microsoft YaHei UI", 9f);

        // ---------- 日志区（SplitContainer 下半，可拖拽/收起） ----------
        var logPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Color.FromArgb(24, 27, 33) };
        logPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var logHead = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 3, 6, 0),
            BackColor = Color.FromArgb(31, 35, 42),
            WrapContents = true,
            AutoScroll = false,
        };
        logHead.Controls.Add(new Label { Text = "运行日志", ForeColor = Color.FromArgb(215, 222, 232), AutoSize = true, Margin = new Padding(0, 4, 14, 0), Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold) });
        _autoScroll = new CheckBox { Text = "自动滚动", ForeColor = Color.FromArgb(150, 200, 255), Checked = true, AutoSize = true, Margin = new Padding(0, 5, 14, 0) };
        logHead.Controls.Add(_autoScroll);
        // 底层细节默认不看（OCR 逐次输入、观察词集、跳过的分支…），但**日志文件始终全量记录**，
        // 出问题时可勾上还原现场。
        _chkVerboseLog = new CheckBox
        {
            Text = "显示底层细节（OCR/观察）",
            ForeColor = Color.FromArgb(150, 200, 255),
            Checked = false,
            AutoSize = true,
            Margin = new Padding(0, 5, 14, 0),
        };
        _chkVerboseLog.CheckedChanged += (_, _) => _logBox?.Clear();
        logHead.Controls.Add(_chkVerboseLog);
        _btnLogFold = new Button { Text = "收起日志", AutoSize = true, FlatStyle = FlatStyle.System, Margin = new Padding(0, 1, 0, 0) };
        _btnLogFold.Click += (_, _) => ToggleLog();
        logHead.Controls.Add(_btnLogFold);
        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Font = new Font("Consolas", 9.5f),
            BackColor = Color.FromArgb(24, 27, 33),
            ForeColor = Color.FromArgb(226, 230, 236),
            BorderStyle = BorderStyle.None,
        };
        logPanel.Controls.Add(logHead, 0, 0);
        logPanel.Controls.Add(_logBox, 0, 1);

        _tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9.5f) };
        _tabs.TabPages.Add(MkPage("自检", BuildSelfCheck()));
        _tabs.TabPages.Add(MkPage("参数", BuildParams()));
        _tabs.TabPages.Add(MkPage("流程", BuildFlow()));
        // 「编排」页已按用户拍板封存（群友反馈自由拼流程需求不大）：代码留在 archive/FlowEditorPage.cs.archived，
        // 引擎侧的原子能力（menu/tab/navigate/firewall/observe/gate）全部保留，用于优化卡大仓完整流程。
        _tabs.SelectedIndexChanged += (_, _) => BeginInvoke(() => SyncStackWidths());

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            Panel1MinSize = 200,
            Panel2MinSize = 60,
            BackColor = C_Bg,
        };
        _split.Panel1.Controls.Add(_tabs);
        _split.Panel2.Controls.Add(logPanel);
        Controls.Add(_split);

        _rt.Log.EntryAdded += OnLogEntry;
        _lightTimer.Tick += (_, _) => RefreshLights();
        _lightTimer.Start();
        LoadUiState();
        _rt.Log.Hint("热键：F6 显示识别框 · F7 封云存档 · F8 完全断网（故意掉线）。首次使用建议点[使用向导]。");
        RefreshLights();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyLogHeight(_logHeight);
        ApplyLogFold(_logExpanded);
        EnsureOverlay();
        BeginInvoke(() => SyncStackWidths());
    }

    /// <summary>创建游戏窗口覆盖层（懒加载；纯外部窗口，不注入/不抢焦点）。</summary>
    private OverlayForm? EnsureOverlay()
    {
        if (_overlay is null && _rt.Settings.Overlay.Enabled)
        {
            try
            {
                _overlay = new OverlayForm(_rt.Window, _rt.Settings, _rt.Log, _rt.Matcher, _rt.Bank);
            }
            catch (Exception e) { _rt.Log.Warn("覆盖层创建失败: " + e.Message, "覆盖层"); }
        }
        return _overlay;
    }

    private static TabPage MkPage(string title, Control body)
    {
        var p = new TabPage(title) { BackColor = C_Bg, Padding = new Padding(0) };
        p.Controls.Add(body);
        return p;
    }

    private static GroupBox Group(string text)
        => new()
        {
            Text = text,
            BackColor = C_Panel,
            ForeColor = C_Accent,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            Padding = new Padding(10, 4, 10, 10),
            Margin = new Padding(0, 0, 0, 8),
        };

    private static Label Tip(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(700, 0),
        ForeColor = C_Muted,
        Margin = new Padding(2, 4, 2, 2),
    };

    /// <summary>页面容器：自上而下堆叠、宽度随窗口自适应、内容超高时自动滚动。</summary>
    private Control Stack(params Control[] items)
    {
        var flp = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = C_Bg,
            Padding = new Padding(10, 8, 10, 8),
            Tag = "stack",
        };
        foreach (var it in items)
        {
            it.Margin = new Padding(0, 0, 0, 8);
            flp.Controls.Add(it);
        }
        flp.ClientSizeChanged += (_, _) => SyncStackWidths();
        flp.HandleCreated += (_, _) => SyncStackWidths();
        return flp;
    }

    /// <summary>分组框：高度由内容决定（AutoSize），内部各行自动排布，不再固定高度导致裁切。</summary>
    private GroupBox StackGroup(string title, params Control[] inner)
    {
        var g = Group(title);
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = C_Panel,
            Padding = new Padding(2),
        };
        for (int i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (c is FlowLayoutPanel flp)
            {
                flp.Dock = DockStyle.Top;
                flp.AutoSize = true;
                flp.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                flp.AutoScroll = false;
                flp.WrapContents = true;
            }
            else if (c is Label lb)
            {
                lb.AutoSize = true;
                lb.Dock = DockStyle.Top;
                _wrapLabels.Add(lb);
                int captured = i;
                host.Resize += (_, _) =>
                {
                    int w = Math.Max(240, host.ClientSize.Width - 8);
                    if (captured < inner.Length && inner[captured] is Label l2) l2.MaximumSize = new Size(w, 0);
                };
            }
            else
            {
                c.Dock = DockStyle.Top;
            }
            host.Controls.Add(c, 0, i);
        }
        g.Controls.Add(host);
        g.Tag = host;   // 供 SyncStackWidths 依据内容测量高度
        return g;
    }

    /// <summary>把每个页面里的分组框宽度对齐到页面可用宽度，并按宽度重算自动换行标签。</summary>
    /// <summary>宽度对齐到页面可用宽度，并按内容重新测量每个分组框的高度（两遍以处理换行引起的连锁变化）。</summary>
    private void SyncStackWidths()
    {
        if (!IsHandleCreated) return;
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (TabPage page in _tabs.TabPages)
            {
                var stack = page.Controls.OfType<FlowLayoutPanel>().FirstOrDefault(x => (x.Tag as string) == "stack");
                if (stack is null) continue;
                int w = stack.ClientSize.Width - stack.Padding.Horizontal
                        - (stack.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0);
                w = Math.Max(420, w);
                foreach (Control g in stack.Controls)
                {
                    if (g.Width != w) g.Width = w;
                    if (g.Tag is TableLayoutPanel host)
                    {
                        host.PerformLayout();
                        foreach (var lb in _wrapLabels)
                        {
                            if (lb.Parent == host)
                                lb.MaximumSize = new Size(Math.Max(240, w - g.Padding.Horizontal - 24), 0);
                        }
                        host.PerformLayout();
                        int h = host.PreferredSize.Height;
                        int want = Math.Max(54, h + g.Padding.Vertical + 24);
                        if (g.Height != want) g.Height = want;
                    }
                }
            }
        }
    }

    /// <summary>诊断用：导出三页各分组框的实际宽高（验证布局未被压扁/裁切）。</summary>
    public string DumpLayout()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("form=" + Width + "x" + Height + "  client=" + ClientSize.Width + "x" + ClientSize.Height);
        foreach (TabPage page in _tabs.TabPages)
        {
            sb.AppendLine("== 页签[" + page.Text + "] page=" + page.Width + "x" + page.Height);
            var stack = page.Controls.OfType<FlowLayoutPanel>().FirstOrDefault(x => (x.Tag as string) == "stack");
            if (stack is null) { sb.AppendLine("   (无堆叠容器)"); continue; }
            sb.AppendLine("   stack=" + stack.ClientSize.Width + "x" + stack.ClientSize.Height + " 滚动条=" + stack.VerticalScroll.Visible);
            foreach (Control g in stack.Controls)
            {
                string host = g.Tag is TableLayoutPanel t ? "  host=" + t.ClientSize.Width + "x" + t.ClientSize.Height + " pref高=" + t.PreferredSize.Height : "";
                sb.AppendLine("   - " + g.GetType().Name + " [" + g.Text + "] " + g.Width + "x" + g.Height + host);
            }
        }
        return sb.ToString();
    }

    private void ToggleLog() => ApplyLogFold(!_logExpanded);

    private void ApplyLogFold(bool expanded)
    {
        _logExpanded = expanded;
        _split.Panel2Collapsed = !expanded;
        _btnLogFold.Text = expanded ? "收起日志" : "展开日志";
        if (expanded) ApplyLogHeight(_logHeight);
    }

    private void ApplyLogHeight(int h)
    {
        _logHeight = Math.Max(80, h);
        try
        {
            int visible = _split.Height - _split.SplitterWidth;
            if (visible > _logHeight + 200)
                _split.SplitterDistance = visible - _logHeight;
        }
        catch { }
    }

    // ================= 自检页 =================

    private Control BuildSelfCheck()
    {
        string[] names = { "游戏进程", "游戏窗口", "抓帧", "输入层", "音频cue", "防火墙", "封存档", "OCR引擎", "模板库", "快捷切换" };
        // 两排状态灯：每排 5 个（6 列 = 点/名/详情 × 2）
        const int perRow = 5;
        var lt = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 6,
            BackColor = C_Panel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(2),
        };
        for (int c = 0; c < 6; c++)
            lt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 0,3=灯点 1,4=名称 2,5=详情
        for (int i = 0; i < names.Length; i++)
        {
            int row = i / perRow, col = (i % perRow) * 3;
            if (col == 0) lt.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var dot = new Label { Text = "●", ForeColor = LIdle, AutoSize = true, Font = new Font("Segoe UI", 12f), Margin = new Padding(2, 0, 0, 0) };
            var nm = new Label { Text = names[i], AutoSize = true, ForeColor = C_Text, Margin = new Padding(0, 5, 6, 0) };
            var dt = new Label { Text = "-", AutoSize = true, ForeColor = C_Muted, Margin = new Padding(0, 5, 14, 0) };
            lt.Controls.Add(dot, col + 0, row);
            lt.Controls.Add(nm, col + 1, row);
            lt.Controls.Add(dt, col + 2, row);
            _lights[names[i]] = (dot, dt);
        }
        var gLight = StackGroup("环境状态（两排实时灯；每秒刷新，防火墙每 5 秒）", lt);

        var fa = new FlowLayoutPanel { BackColor = C_Panel, Margin = new Padding(2, 2, 2, 2) };
        fa.Controls.AddRange(new Control[]
        {
            ActionButton("状态机探测", () => RunOp("状态机探测", RunProbe)),
            // 单独的手势测试按钮已移除：与“状态机探测”的手势段落冗余（探测里已含手势自检）
            ActionButton("使用向导", () => { using var w = new WizardForm(_rt); w.ShowDialog(this); }),
            _btnOverlay = ActionButton("显示OCR区域(F6)", ToggleOverlayTuner),
            // 默认不存样本：避免数据目录随使用无限增长（需要排查时再勾上）
            _chkSaveSample = new CheckBox { Text = "识别时存样本（排查用）", AutoSize = true, Checked = false, Margin = new Padding(10, 8, 4, 0) },
        });
        _actionControls.Add(_chkSaveSample);
        var gAct = StackGroup("自检与诊断（只读；不会改变游戏状态）", fa);

        // 数据目录占用与清理：把“会增长的东西”摆出来，并给一个手动清理入口
        var fd = new FlowLayoutPanel { BackColor = C_Panel, Margin = new Padding(2, 2, 2, 2) };
        fd.Controls.Add(ActionButton("打开数据目录", () => OpenFolder(_rt.DataDir)));
        fd.Controls.Add(ActionButton("清理临时文件", CleanTempData));
        _dataInfoLabel = new Label
        {
            Text = "统计中…", AutoSize = true, ForeColor = C_Muted,
            Margin = new Padding(14, 10, 0, 0),
        };
        fd.Controls.Add(_dataInfoLabel);
        var gData = StackGroup("数据与占用（帧/样本/日志会随使用增长；清理只删这些，不动设置与模板）", fd);

        var ff = new FlowLayoutPanel { BackColor = C_Panel, Margin = new Padding(2, 2, 2, 2) };
        ff.Controls.AddRange(new Control[]
        {
            ActionButton("添加全部规则", () => RunOp("防火墙", () => _rt.Firewall.AddAllRules())),
            ActionButton("删除全部规则", () => RunOp("防火墙", () => _rt.Firewall.DeleteAllRules())),
            ActionButton("封存档 开/关 (F7)", () => RunOp("封存档(F7)", ToggleBlockSave)),
            ActionButton("完全断网 开/关 (F8)", () => RunOp("完全断网", OnHotkeyBlockAll)),
        });
        var gFw = StackGroup("联网控制（两条规则独立：封存档=只断云存档，游戏不掉线；完全断网=故意掉线）", ff);

        var gTip = StackGroup("说明", Tip(
            "灯色：绿=就绪；黄=需注意（如已封网）；灰=未启用；红=异常。\r\n" +
            "[自检与诊断] 里的按钮都是只读的，不会动游戏；[打开数据目录]/[清理临时文件] 用来查看和清理会增长的帧、样本、日志。"));

        return Stack(gLight, gAct, gData, gFw, gTip);
    }

    /// <summary>数据目录各子项的占用（MB）。只统计会增长的那几类。</summary>
    private static (string Name, double Mb)[] DataUsage(string dataDir)
    {
        var items = new[] { "frames", "samples", "logs", "diag" };
        var list = new List<(string, double)>();
        foreach (var d in items)
        {
            string p = Path.Combine(dataDir, d);
            double mb = 0;
            try
            {
                if (Directory.Exists(p))
                    mb = Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
                        .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }) / 1048576.0;
            }
            catch { }
            list.Add((d, mb));
        }
        return list.ToArray();
    }

    private void RefreshDataInfo()
    {
        if (_dataInfoLabel is null) return;
        if ((DateTime.UtcNow - _dataInfoAt).TotalSeconds < 15) return;   // 节流：目录遍历不必每秒做
        _dataInfoAt = DateTime.UtcNow;
        try
        {
            var u = DataUsage(_rt.DataDir);
            double total = u.Sum(x => x.Mb);
            string parts = string.Join(" · ", u.Where(x => x.Mb >= 0.05).Select(x => x.Name + " " + x.Mb.ToString("F1") + "MB"));
            _dataInfoLabel.Text = (parts.Length == 0 ? "临时文件已清空" : parts) + "　合计 " + total.ToString("F1") + "MB";
        }
        catch { _dataInfoLabel.Text = "统计失败"; }
    }

    /// <summary>清理会增长的临时数据（帧/样本/日志/诊断）；不动 settings.json、ui.json、templates、flows。</summary>
    private void CleanTempData()
    {
        var u = DataUsage(_rt.DataDir);
        double total = u.Sum(x => x.Mb);
        var ans = MessageBox.Show(
            "将删除以下临时文件（设置与模板不受影响）：\r\n\r\n" +
            string.Join("\r\n", u.Select(x => "· " + x.Name + "　" + x.Mb.ToString("F1") + " MB")) +
            "\r\n\r\n合计 " + total.ToString("F1") + " MB，确定清理？",
            "清理临时文件", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (ans != DialogResult.OK) return;
        int removed = 0;
        foreach (var d in new[] { "frames", "samples", "diag" })
        {
            try
            {
                string p = Path.Combine(_rt.DataDir, d);
                if (!Directory.Exists(p)) continue;
                var files = Directory.GetFiles(p, "*", SearchOption.AllDirectories);
                foreach (var f in files) { try { File.Delete(f); removed++; } catch { } }
                foreach (var sub in Directory.GetDirectories(p)) { try { Directory.Delete(sub, true); } catch { } }
            }
            catch { }
        }
        try
        {
            // 日志只删历史卷（正在写的当前日志由轮转管理）
            string logs = Path.Combine(_rt.DataDir, "logs");
            if (Directory.Exists(logs))
                foreach (var f in Directory.GetFiles(logs, "*.1"))
                { try { File.Delete(f); removed++; } catch { } }
        }
        catch { }
        _rt.Log.Okay("已清理临时文件 " + removed + " 个（释放约 " + total.ToString("F1") + " MB）", "UI");
        _dataInfoAt = DateTime.MinValue;
        RefreshDataInfo();
    }

    private void OpenFolder(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { _rt.Log.Warn("打开文件夹失败: " + ex.Message, "UI"); }
    }

    // ================= 参数页 =================

    private Control BuildParams()
    {
        var lay = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10, 8, 10, 8), BackColor = C_Bg };
        lay.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        lay.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        lay.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, BackColor = C_Bg };
        btnRow.Controls.AddRange(new Control[] { Btn("保存参数", SaveParams), Btn("恢复默认", ResetParams), Btn("打开数据目录", () => OpenFolder(_rt.DataDir)) });
        lay.Controls.Add(btnRow, 0, 0);
        var note = new Label { Dock = DockStyle.Fill, ForeColor = C_Muted, AutoSize = false, Text = "说明：全部参数按中文分类列出，直接点值列修改；改完点[保存参数]写入 settings.json（即时作用于后续动作），右下角为所选参数的说明。" };
        lay.Controls.Add(note, 0, 1);

        // 参数视图保持同一个实例：SettingsView 的 GetPropertyOwner 需要按行解析宿主 Section，
        // 重建实例会让 PropertyGrid 在重设对象时走入 WinForms 的空 owner 分支（值回退/崩溃）。
        _paramsView = new SettingsView(_rt.Settings);
        _grid = new PropertyGrid
        {
            Dock = DockStyle.Fill,
            BackColor = C_Panel,
            SelectedObject = _paramsView,
            PropertySort = PropertySort.Categorized,
            HelpVisible = true,
            ToolbarVisible = false,
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        _grid.PropertyValueChanged += OnParamValueChanged;
        var chkHelp = new CheckBox { Text = "显示参数说明", AutoSize = true, Checked = true, Margin = new Padding(10, 6, 0, 0) };
        chkHelp.CheckedChanged += (_, _) => { if (_grid is not null) _grid.HelpVisible = chkHelp.Checked; };
        btnRow.Controls.Add(chkHelp);
        lay.Controls.Add(_grid, 0, 2);
        return lay;
    }

    /// <summary>参数值变更：确认写回真实配置对象（不是只改了界面），并把结果告诉用户。</summary>
    private void OnParamValueChanged(object? sender, PropertyValueChangedEventArgs e)
    {
        try
        {
            var pd = e.ChangedItem?.PropertyDescriptor;
            if (pd is null || _paramsView is null) return;
            var shown = e.ChangedItem!.Value;
            bool found = _paramsView.TryReadBack(pd.DisplayName, out var applied);
            bool ok = found && Equals(applied, shown);
            _rt.Log.Info(string.Format("{0} = {1}（{2}）", pd.DisplayName, applied ?? shown ?? "(null)",
                ok ? "已写回配置，点[保存参数]写盘" : "写回不一致，请重试"), "UI");
            if (!ok && _grid is not null) _grid.Refresh();
        }
        catch (Exception ex)
        {
            _rt.Log.Warn("参数写回校验失败: " + ex.Message, "UI");
        }
    }

    /// <summary>诊断用：进入参数页并返回 PropertyGrid 实例（供 CLI 自检使用）。</summary>
    public PropertyGrid? ParamsGrid => _grid;

    private void SaveParams()
    {
        if (_busy) { _rt.Log.Warn("任务运行中，稍后再保存", "UI"); return; }
        try
        {
            _rt.Store.Save(_rt.Settings);
            RegisterHotkey();
            _rt.Log.Okay("参数已保存到 settings.json", "UI");
        }
        catch (Exception ex) { _rt.Log.Error("保存参数失败: " + ex.Message, "UI"); }
    }

    private void ResetParams()
    {
        if (_busy) { _rt.Log.Warn("任务运行中，稍后再重置", "UI"); return; }
        try
        {
            var fresh = new AppSettings();
            foreach (var pi in typeof(AppSettings).GetProperties())
            {
                if (!pi.CanWrite || pi.SetMethod is null || !pi.SetMethod.IsPublic) continue;
                var cur = pi.GetValue(_rt.Settings);
                var def = pi.GetValue(fresh);
                if (cur is null || def is null) { pi.SetValue(_rt.Settings, def); continue; }
                foreach (var lp in cur.GetType().GetProperties())
                {
                    if (!lp.CanWrite || lp.SetMethod is null || !lp.SetMethod.IsPublic) continue;
                    lp.SetValue(cur, lp.GetValue(def));
                }
            }
            _rt.Store.Save(_rt.Settings);
            // 就地写回所有叶子值，视图对象保持不变（不重建），只有同类的默认值需要重绘
            if (_grid is not null) _grid.Refresh();
            RegisterHotkey();
            RefreshLights();
            _rt.Log.Okay("参数已恢复默认并保存", "UI");
        }
        catch (Exception ex) { _rt.Log.Error("恢复默认失败: " + ex.Message, "UI"); }
    }

    // ================= 流程页 =================
    // ================= 流程页 =================

    private Control BuildFlow()
    {
        var fb = new FlowLayoutPanel { BackColor = C_Panel, Margin = new Padding(2, 2, 2, 2) };
        fb.Controls.Add(ActionButton("封存档 开/关 (F7)", () => RunOp("封存档", ToggleBlockSave)));
        _blockLabel = new Label { Text = "封存档：读取中…", AutoSize = true, ForeColor = C_Muted, Margin = new Padding(14, 10, 0, 0) };
        fb.Controls.Add(_blockLabel);
        var gBlock = StackGroup("封存档（阻断云存档；F7 在任意焦点即时切换，班次运行中会先弹警告）", fb);

        var fm = new FlowLayoutPanel { BackColor = C_Panel, Margin = new Padding(2, 2, 2, 2) };
        fm.Controls.AddRange(new Control[]
        {
            ActionButton("进入线上（摇杆↓优先）", () => RunOp("进入线上", GoOnline)),
            ActionButton("回到故事（摇杆↑优先）", () => RunOp("回到故事", GoStory)),
        });
        var f2 = new FlowLayoutPanel { BackColor = C_Panel, Margin = new Padding(2, 2, 2, 2) };
        _quickEntry = new CheckBox { Text = "进线上·先摇杆", AutoSize = true, Checked = _rt.Settings.QuickSwitch.EnableOnlineEntry, Margin = new Padding(4, 8, 10, 0) };
        _quickReturn = new CheckBox { Text = "回故事·先摇杆", AutoSize = true, Checked = _rt.Settings.QuickSwitch.EnableStoryReturn, Margin = new Padding(0, 8, 0, 0) };
        _quickEntry.CheckedChanged += (_, _) => _rt.Settings.QuickSwitch.EnableOnlineEntry = _quickEntry.Checked;
        _quickReturn.CheckedChanged += (_, _) => _rt.Settings.QuickSwitch.EnableStoryReturn = _quickReturn.Checked;
        _bgMode = new CheckBox { Text = "后台模式（假激活，不抢前台）", AutoSize = true, Checked = _rt.Settings.Automation.BackgroundFakeActivate, Margin = new Padding(20, 8, 0, 0) };
        _bgMode.CheckedChanged += (_, _) => _rt.Settings.Automation.BackgroundFakeActivate = _bgMode.Checked;
        f2.Controls.Add(new Label { Text = "快捷切换开关（即时生效，存盘在[参数]页）", AutoSize = true, Margin = new Padding(4, 12, 8, 0), ForeColor = C_Muted });
        f2.Controls.Add(_quickEntry);
        f2.Controls.Add(_quickReturn);
        f2.Controls.Add(_bgMode);
        var gMode = StackGroup("模式切换 — 摇杆优先（失败自动回退暂停菜单）", fm, f2,
            Tip("说明：摇杆上=回故事（线下）、摇杆下=进线上；弹确认框后自动按 A；手势失败会自动改走暂停菜单流程（与旧流程一致）。\r\n后台模式：勾选后每次动作前不抢前台，改为假激活（窗口需可见，最小化无效）；其余流程完全不变。"));
        _actionControls.Add(_quickEntry);
        _actionControls.Add(_quickReturn);
        _actionControls.Add(_bgMode);

        // 班次引擎选择：默认原子引擎；取消勾选=一键回退旧流程（出问题时的退路）
        var chkAtoms = new CheckBox
        {
            Text = "用原子引擎（取消=回退旧流程）",
            AutoSize = true,
            Checked = _rt.Settings.Shift.Engine.Equals("atoms", StringComparison.OrdinalIgnoreCase),
            Margin = new Padding(0, 6, 4, 0),
        };
        chkAtoms.CheckedChanged += (_, _) =>
        {
            _rt.Settings.Shift.Engine = chkAtoms.Checked ? "atoms" : "legacy";
            _rt.Log.Okay("班次引擎 = " + _rt.Settings.Shift.Engine
                + (_rt.Jobs.UsingAtoms ? "（下一轮起用原子引擎）" : "（下一轮起走旧流程）"), "UI");
            try { _rt.Store.Save(_rt.Settings); } catch { }
        };
        _actionControls.Add(chkAtoms);

        var r1 = new FlowLayoutPanel { BackColor = C_Panel, Margin = new Padding(2, 2, 2, 2) };
        _rounds = MkNum(1, 500, _rt.Settings.Shift.Count);
        _waitMin = MkNum(0, 480, _rt.Settings.Shift.WaitStartMins);
        _stayOnline = new CheckBox { Text = "末轮留在线", AutoSize = true, Checked = false, Margin = new Padding(0, 6, 4, 0) };
        _useFw = new CheckBox { Text = "封网(阻断云存档)", AutoSize = true, Checked = _rt.Settings.Shift.UseFirewall, Margin = new Padding(0, 6, 0, 0) };
        r1.Controls.Add(new Label { Text = "轮数:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        r1.Controls.Add(_rounds);
        r1.Controls.Add(new Label { Text = "启动前等待(分钟):", AutoSize = true, Margin = new Padding(14, 8, 4, 0) });
        r1.Controls.Add(_waitMin);
        r1.Controls.Add(_stayOnline);
        r1.Controls.Add(_useFw);
        r1.Controls.Add(chkAtoms);
        var r2 = new FlowLayoutPanel { BackColor = C_Panel, Margin = new Padding(2, 2, 2, 2) };
        _btnStartShift = ActionButton("开始班次", StartShift);
        _btnStopShift = Btn("停止（轮次间生效）", StopShift);
        _btnStopShift.Enabled = false;
        _shiftStatus = new Label { Text = "状态：空闲", AutoSize = true, Margin = new Padding(14, 8, 6, 0), ForeColor = C_Accent };
        r2.Controls.Add(_btnStartShift);
        r2.Controls.Add(_btnStopShift);
        r2.Controls.Add(_shiftStatus);
        var gShift = StackGroup("班次 — 卡大仓取货循环（每轮：进线上 → 下云cue临界封网 → 保存失败验证 → 回故事 → 恢复联网 → 停留）",
            r1, r2,
            Tip("说明：轮数与启动前等待在此设置；[停止]在轮次间生效，当前轮会先安全收尾（回线下 + 恢复联网）。\r\n不管哪一步失败，都会先恢复联网再停——不会把你留在断网状态。"));

        return Stack(gBlock, gMode, gShift);
    }

    private void GoOnline()
    {
        if (_overlay?.PreviewActive == true) CaptureEvidence("进入线上前·识别", withOcr: false);
        bool ok = _rt.Machine.EnsureOnlineInvite(timeoutSec: 300);
        _rt.Log.Hint("进入线上 => " + (ok ? "OK" : "FAIL（已含暂停菜单备份尝试）"), "UI");
    }

    private void GoStory()
    {
        if (_overlay?.PreviewActive == true) CaptureEvidence("回到故事前·识别", withOcr: false);
        bool ok = _rt.Machine.EnsureStory(timeoutSec: 180);
        _rt.Log.Hint("回到故事 => " + (ok ? "OK" : "FAIL"), "UI");
    }

    /// <summary>抓一帧 → 识别（可选OCR）→ 覆盖层实时预览“送进 OCR 的是什么/识别到什么” → 可选落盘样本。</summary>
    private ReadResult? CaptureEvidence(string label, bool withOcr)
    {
        var frame = _rt.Window.CaptureClient();
        if (!frame.IsValid) { _rt.Log.Warn(label + "：抓帧失败", "UI"); return null; }
        _rt.Reader.PreviewWords = _overlay?.WantWords == true;   // F6 区域可视化开启时才多取一次词框
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 预览型读取不需要横幅模板（最贵的一段）；判定仍会由后续真实流程调用带横幅的 Read
        var res = _rt.Reader.Read(frame, withOcr, includeHome: true, withBanner: false);
        long ms = sw.ElapsedMilliseconds;
        _overlay?.PushEvidence(frame.Width, frame.Height, res.OcrWords,
            res.OcrText ?? "(无OCR文本)", label, ms, res.OcrSizeNote);
        _rt.Log.Info(string.Format("{0}：帧 {1}x{2} 识别={3} 置信 {4:F3} 横幅 故事={5:F3} 在线={6:F3} 主菜单={7:F3} 耗时 {8}ms",
            label, frame.Width, frame.Height, res.Kind, res.Confidence, res.StoryScore, res.OnlineScore, res.HomeScore, ms), "UI");
        if (res.OcrText is not null) _rt.Log.Info("   OCR: " + (res.OcrText.Length > 160 ? res.OcrText.Substring(0, 160) : res.OcrText), "UI");
        if (_chkSaveSample?.Checked == true)
        {
            string p = Path.Combine(_rt.DataDir, "samples", "vision_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".jpg");
            _rt.Log.Info(ImagingIo.SaveFrame(frame, p) ? "   样本: " + p : "   样本保存失败", "UI");
        }
        return res;
    }

    private void RunProbe()
    {
        var sb = new List<string>();
        _rt.Log.Hint("======== 状态机探测（只读，不真切换）========", "探测");
        var snap = _rt.Window.CaptureClient();
        if (!snap.IsValid) { _rt.Log.Warn("抓帧失败，探测中止", "探测"); return; }
        _rt.Reader.PreviewWords = _overlay?.WantWords == true;
        var probeSw = System.Diagnostics.Stopwatch.StartNew();
        var res = _rt.Reader.Read(snap, withOcr: true);
        long probeMs = probeSw.ElapsedMilliseconds;
        _overlay?.PushEvidence(snap.Width, snap.Height, res.OcrWords,
            res.OcrText ?? "(无OCR文本)", "探测·识别", probeMs, res.OcrSizeNote);
        if (_chkSaveSample?.Checked == true)
        {
            ImagingIo.SaveFrame(snap, Path.Combine(_rt.DataDir, "samples", "probe_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".jpg"));
        }
        _rt.Log.Info(string.Format("① 识别：{0} 置信 {1:F3}｜横幅 故事={2:F3} 在线={3:F3} 主菜单={4:F3}", res.Kind, res.Confidence, res.StoryScore, res.OnlineScore, res.HomeScore), "探测");
        foreach (var d in res.DebugLines) _rt.Log.Info("   " + d, "探测");
        _rt.Log.Info("   （探测只读，不再自动存样本/写盘）", "探测");
        sb.Add("识别=" + res.Kind);

        if (_rt.Machine.OpenPauseMenu(15))
        {
            var s1 = _rt.Machine.ReadMenuSnapshot();
            _rt.Log.Info(string.Format("② 菜单已开：横幅 {0}（故事={1:F3}/在线={2:F3}）词证：{3}", s1.Kind, s1.StoryScore, s1.OnlineScore, Shorten(s1.StripWords, 64)), "探测");
            _rt.Log.Info("   tab：选中=" + (s1.SelectedTab ?? "?") + "　白块x " + s1.WhiteX0 + ".." + s1.WhiteX1 + "　焦点行=" + (s1.FocusLabel ?? "(空)"), "探测");
            sb.Add("菜单=开 tab=" + (s1.SelectedTab ?? "?"));

            if (_rt.Machine.ProbeEnterListAndBack("地图", out var row))
            {
                _rt.Log.Info("③ 子菜单读数：焦点行=[" + row + "]（已按 B 退回）", "探测");
                sb.Add("子菜单=" + row);
            }
            else { _rt.Log.Warn("③ 子菜单未读到焦点行", "探测"); sb.Add("子菜单=未读到"); }

            _rt.Machine.ClosePauseMenu(8);
        }
        else { _rt.Log.Warn("② 打不开暂停菜单", "探测"); sb.Add("菜单=打不开"); }

        foreach (var dir in new[] { "down", "up" })
        {
            bool seen = _rt.Machine.TryQuickGestureStandalone(dir, 20, cancelAfter: true);
            sb.Add((dir == "down" ? "手势↓（进线上）" : "手势↑（回故事）") + (seen ? "=弹框已取消" : "=无弹框"));
        }
        _rt.Log.Hint("======== 探测完成：" + string.Join("　", sb) + " ========", "探测");
    }

    private static string Shorten(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

    private void ToggleBlockSave()
    {
        bool ok = _rt.Firewall.Toggle();
        _rt.Log.Hint("封存档切换 => " + (ok ? "OK" : "FAIL（看日志）"), "UI");
        if (ok)
        {
            // 覆盖层左上角提示：让玩家在游戏画面上直接看到“已封/已解除”
            bool blocked = _rt.Firewall.IsEnabled() == true;
            EnsureOverlay()?.ShowToast(blocked ? "封存档：已封（云存档已阻断）" : "封存档：已解除（联网正常）");
        }
        _fwCheckedAt = DateTime.MinValue;
    }

    private void StartShift()
    {
        if (!TryAcquire()) return;
        var s = _rt.Settings.Shift;
        s.Count = (int)_rounds.Value;
        s.WaitStartMins = (int)_waitMin.Value;
        s.UseFirewall = _useFw.Checked;
        bool stay = _stayOnline.Checked;
        _rt.Store.Save(_rt.Settings);
        _shiftCts = new CancellationTokenSource();
        var ct = _shiftCts;
        if (_rt.Settings.Overlay.HideDuringShift) _overlay?.SuspendForTask(true);
        BeginInvoke(() => { _btnStopShift.Enabled = true; SetShiftStatus("班次运行中…"); });
        Task.Run(() =>
        {
            bool stopped = false;
            try
            {
                bool ok = _rt.Jobs.RunShifts(s.Count, stay, () => ct.IsCancellationRequested);
                stopped = ct.IsCancellationRequested;
                _rt.Log.Hint("班次结束 => " + (ok ? "OK 全部完成" : stopped ? "已按请求停止（安全收尾）" : "FAIL（看上面日志）"), "UI");
            }
            catch (Exception ex) { _rt.Log.Error("班次异常: " + ex.Message, "UI"); }
            finally
            {
                BeginInvoke(() =>
                {
                    _shiftCts = null;
                    _btnStopShift.Enabled = false;
                    SetShiftStatus("空闲");
                    if (_rt.Settings.Overlay.HideDuringShift) _overlay?.SuspendForTask(false);
                    Release();
                });
            }
        });
    }

    private void StopShift()
    {
        if (_shiftCts is null) { _rt.Log.Warn("当前没有运行中的班次", "UI"); return; }
        _shiftCts.Cancel();
        _rt.Log.Hint("已请求停止班次（轮次间生效，当前轮先安全收尾）", "UI");
    }

    private void SetShiftStatus(string text)
    {
        if (IsHandleCreated) BeginInvoke(() => { if (_shiftStatus is not null) _shiftStatus.Text = "状态：" + text; });
    }

    // ================= 窗口/日志尺寸记忆 =================

    private sealed class UiState
    {
        public int W { get; set; }
        public int H { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public bool Max { get; set; }
        public int LogHeight { get; set; }
        public bool LogExpanded { get; set; } = true;
    }

    private void LoadUiState()
    {
        try
        {
            if (!File.Exists(_uiPath)) return;
            var st = JsonSerializer.Deserialize<UiState>(File.ReadAllText(_uiPath));
            if (st is null) return;
            if (st.W >= MinimumSize.Width && st.H >= MinimumSize.Height)
            {
                var target = new Rectangle(st.X, st.Y, st.W, st.H);
                bool onScreen = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(target));
                if (onScreen) { StartPosition = FormStartPosition.Manual; Bounds = target; }
                else Size = new Size(st.W, st.H);
            }
            _logHeight = st.LogHeight > 0 ? st.LogHeight : _logHeight;
            _logExpanded = st.LogExpanded;
            if (st.Max) WindowState = FormWindowState.Maximized;
        }
        catch { }
    }

    private void SaveUiState()
    {
        try
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var st = new UiState
            {
                W = Math.Max(MinimumSize.Width, b.Width),
                H = Math.Max(MinimumSize.Height, b.Height),
                X = b.X,
                Y = b.Y,
                Max = WindowState == FormWindowState.Maximized,
                LogHeight = _logHeight,
                LogExpanded = _logExpanded,
            };
            File.WriteAllText(_uiPath, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ================= 热键 =================

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkey();
    }

    private void RegisterHotkey()
    {
        try
        {
            Win32.UnregisterHotKey(Handle, HotkeyId);
            Win32.UnregisterHotKey(Handle, HotkeyIdTune);
            Win32.UnregisterHotKey(Handle, HotkeyIdBlockAll);
            if (!_rt.Settings.Hotkeys.Enabled) return;
            string? used = null;
            RegisterOne(_rt.Settings.Hotkeys.TuneOverlayKey, HotkeyIdTune, "可视OCR条件", ref used);
            RegisterOne(_rt.Settings.Hotkeys.BlockSaveKey, HotkeyId, "封云存档", ref used);
            RegisterOne(_rt.Settings.Hotkeys.BlockAllKey, HotkeyIdBlockAll, "完全断网(故意掉线)", ref used);
        }
        catch (Exception ex) { _rt.Log.Warn("热键注册异常: " + ex.Message, "UI"); }
    }

    private void RegisterOne(string keyName, int id, string purpose, ref string? used)
    {
        if (!Enum.TryParse<Keys>(keyName, true, out var k) || k == Keys.None) return;
        if (used is not null && string.Equals(used, k.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            _rt.Log.Warn("热键冲突：" + k + " 已用于其它功能，本次跳过（" + purpose + "）", "UI");
            return;
        }
        bool ok = Win32.RegisterHotKey(Handle, id, Win32.MOD_NONE, (uint)k);
        if (ok) used = k.ToString();
        _rt.Log.Info("全局热键 " + k + (ok ? " 注册成功（" + purpose + "）" : " 注册失败（可能被其它程序占用）"), "UI");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HotkeyId) { OnHotkeyBlockSave(); return; }
            if (id == HotkeyIdTune) { ToggleOverlayTuner(); return; }
            if (id == HotkeyIdBlockAll) { OnHotkeyBlockAll(); return; }
        }
        base.WndProc(ref m);
    }

    /// <summary>F8：完全断网（故意掉线）开/关。与封存档独立。</summary>
    private void OnHotkeyBlockAll()
    {
        try
        {
            bool on = _rt.Firewall.BlockAllEnabled() == true;
            if (_busy)
                _rt.Log.Warn("班次运行中：完全断网会中断游戏会话，请确认后再用（本次仍按你的热键执行）", "UI");
            _rt.Log.Hint((on ? "关闭" : "打开") + "完全断网（故意掉线）…", "UI");
            _rt.Firewall.ToggleBlockAll();
            if (_overlay is not null)
                _overlay.ShowToast(_rt.Firewall.BlockAllEnabled() == true ? "已完全断网（故意掉线）" : "完全断网已解除");
            RefreshLights();
        }
        catch (Exception ex) { _rt.Log.Error("完全断网切换失败: " + ex.Message, "UI"); }
    }

    /// <summary>开/关游戏窗口上的 OCR 区域覆盖层（热键或自检页按钮；点击穿透，不抢焦点）。</summary>
    private void ToggleOverlayTuner()
    {
        var ov = EnsureOverlay();
        if (ov is null)
        {
            _rt.Log.Warn("覆盖层未启用（参数页“10 覆盖层 · 启用覆盖层”被关掉）", "UI");
            return;
        }
        if (_busy && _rt.Settings.Overlay.HideDuringShift)
        {
            _rt.Log.Warn("任务运行中：覆盖层按设置在自动化期间保持隐藏", "UI");
            return;
        }
        ov.ToggleTuner();
        SyncOverlayButton();
    }

    private void SyncOverlayButton()
    {
        if (_btnOverlay is null) return;
        bool on = _overlay?.TunerOn == true;
        _btnOverlay.Text = on ? "隐藏OCR区域(F6)" : "显示OCR区域(F6)";
    }

    private void OnHotkeyBlockSave()
    {
        if (_busy)
        {
            _rt.Log.Warn("班次运行中：手动切换封存档（自动封网优先，可能互相影响）", "UI");
            MessageBox.Show(this, "班次运行中：自动封网/恢复联网流程仍在工作，仍将按你的要求手动切换“封存档”。",
                "AutoPickup 警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RunOp("封存档(F7)", ToggleBlockSave);
    }

    // ================= 状态灯 =================

    private void SetLight(string name, Color c, string detail)
    {
        if (_lights.TryGetValue(name, out var l))
        {
            l.Dot.ForeColor = c;
            l.Detail.Text = detail;
        }
    }

    private void RefreshLights()
    {
        if (!IsHandleCreated || _lights.Count == 0) return;
        try
        {
            bool proc = _rt.Window.IsProcessRunning();
            bool win = _rt.Window.IsWindowValid;
            SetLight("游戏进程", proc ? LOk : LBad, proc ? "运行中" : "未运行 GTA5_Enhanced.exe");
            SetLight("游戏窗口", win ? LOk : LBad, win ? "已找到 sgaWindow" : "未找到窗口");
            if (_busy) SetLight("抓帧", LIdle, "任务运行中（暂停抓帧检测）");
            else if (!win) SetLight("抓帧", LBad, "无窗口");
            else
            {
                var f = _rt.Window.CaptureClient();
                SetLight("抓帧", f.IsValid ? LOk : LBad, f.IsValid ? f.Width + "x" + f.Height + "（" + _rt.Window.LastMethod + "）" : "失败/黑屏（遮挡或全屏独占）");
            }
            SetLight("输入层", _rt.Input.IsAvailable ? LOk : LBad, _rt.Input.Name + (_rt.Input.IsAvailable ? "" : "（不可用，需 ViGEmBus）"));
            SetLight("音频cue", _rt.Audio.IsAvailable ? LOk : LIdle,
                _rt.Audio.Name + (_rt.Audio.IsAvailable ? string.Format("　当前音量 {0:P0}", _rt.Audio.CurrentPeak) : "（未启用）"));

            if ((DateTime.UtcNow - _fwCheckedAt).TotalSeconds > 5)   // 两条规则各自节流，避免 netsh 刷屏
            {
                _fwExists = _rt.Firewall.ExistsQuiet();
                _fwEnabled = _rt.Firewall.IsEnabled();
                _fwCheckedAt = DateTime.UtcNow;
            }
            bool blocked = _fwExists && _fwEnabled == true;
            SetLight("防火墙", !_fwExists ? LIdle : blocked ? LWarn : LOk,
                !_fwExists ? "未创建（点[添加规则]或按 F7 创建）" : blocked ? "启用（云存档已阻断）" : "存在 · 禁用（联网正常）");
            if ((DateTime.UtcNow - _fwAllCheckedAt).TotalSeconds > 5)
            {
                _fwAllExists = _rt.Firewall.BlockAllExists();
                _fwAllEnabled = _rt.Firewall.BlockAllEnabled();
                _fwAllCheckedAt = DateTime.UtcNow;
            }
            bool allOff = _fwAllEnabled == true;
            SetLight("封存档", blocked ? LWarn : LIdle,
                (blocked ? "已封（F7 解除）" : "未封（F7 启用）") + "　热键 " + _rt.Settings.Hotkeys.BlockSaveKey + (_rt.Settings.Hotkeys.Enabled ? "" : "（热键已禁用）"));
            SetLight("完全断网", allOff ? LBad : (_fwAllExists ? LIdle : LIdle),
                (allOff ? "已完全断网（F8 恢复）" : _fwAllExists ? "未断开（F8 断网）" : "未创建（F8 会创建）")
                + "　热键 " + _rt.Settings.Hotkeys.BlockAllKey);
            SetLight("OCR引擎", _rt.Ocr.Available ? LOk : LBad, _rt.Ocr.Name + (_rt.Ocr.Available ? "" : " 不可用"));
            SetLight("模板库", _rt.Bank.Templates.Count > 0 ? LOk : LBad, _rt.Bank.Templates.Count + " 张");
            var q = _rt.Settings.QuickSwitch;
            SetLight("快捷切换", (q.EnableOnlineEntry || q.EnableStoryReturn) ? LOk : LIdle,
                "进线上=" + q.EnableOnlineEntry + "　回故事=" + q.EnableStoryReturn + "（摇杆上=回故事 / 下=进线上）");
            if (_blockLabel is not null) _blockLabel.Text = blocked ? "封存档：已封（F7 解除）" : "封存档：未封（F7 启用）";
            RefreshDataInfo();
        }
        catch { }
    }

    // ================= 通用 =================

    private Button ActionButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(4, 3, 4, 3), FlatStyle = FlatStyle.System, MinimumSize = new Size(64, 0) };
        b.Click += (_, _) => onClick();
        _actionControls.Add(b);
        return b;
    }

    private static Button Btn(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(4, 3, 4, 3), FlatStyle = FlatStyle.System };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static NumericUpDown MkNum(int min, int max, int value)
    {
        var n = new NumericUpDown { Minimum = min, Maximum = max, Width = 70, Margin = new Padding(0, 4, 0, 0) };
        n.Value = Math.Clamp(value, min, max);
        return n;
    }

    private void RunOp(string name, Action work)
    {
        if (!TryAcquire()) return;
        Task.Run(() =>
        {
            try { work(); }
            catch (Exception ex) { _rt.Log.Error(name + " 异常: " + ex.Message, "UI"); }
            finally { Release(); }
        });
    }

    private bool TryAcquire()
    {
        if (_busy) { _rt.Log.Warn("已有任务在运行，请等它结束再操作", "UI"); return false; }
        _busy = true;
        if (IsHandleCreated) BeginInvoke(() => SetActionsEnabled(false));
        else SetActionsEnabled(false);
        return true;
    }

    private void Release()
    {
        _busy = false;
        if (IsHandleCreated) BeginInvoke(() => { SetActionsEnabled(true); RefreshLights(); });
        else { SetActionsEnabled(true); RefreshLights(); }
    }

    private void SetActionsEnabled(bool enabled)
    {
        foreach (var c in _actionControls) if (c is not null) c.Enabled = enabled;
    }

    /// <summary>界面日志过滤：只挡掉“底层细节”，文件日志不受影响（排查时勾选“显示底层细节”即可还原）。</summary>
    private bool ShowInLogView(LogEntry entry)
    {
        if (_chkVerboseLog?.Checked == true) return true;
        return entry.Source is not ("Ocr" or "Detail");
    }

    private void OnLogEntry(LogEntry entry)
    {
        if (InvokeRequired) { BeginInvoke(() => OnLogEntry(entry)); return; }
        if (!ShowInLogView(entry)) return;
        Color lv = entry.Level switch
        {
            LogLevel.Okay => Color.FromArgb(122, 224, 132),
            LogLevel.Warn => Color.FromArgb(255, 205, 90),
            LogLevel.Error => Color.FromArgb(255, 122, 122),
            LogLevel.Hint => Color.FromArgb(120, 200, 255),
            _ => Color.FromArgb(205, 214, 224),
        };
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionColor = Color.FromArgb(128, 138, 152);
        _logBox.AppendText("[" + entry.Timestamp.ToString("HH:mm:ss.fff") + "] ");
        _logBox.SelectionColor = lv;
        _logBox.AppendText("[" + entry.Level + "] ");
        _logBox.SelectionColor = Color.FromArgb(226, 230, 236);
        _logBox.AppendText(entry.Source + ": " + entry.Message + Environment.NewLine);
        if (_autoScroll.Checked) { _logBox.SelectionStart = _logBox.TextLength; _logBox.ScrollToCaret(); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveUiState();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { _lightTimer.Stop(); } catch { }
        try { _shiftCts?.Cancel(); } catch { }
        try { _overlay?.Dispose(); } catch { }
        try { Win32.UnregisterHotKey(Handle, HotkeyId); } catch { }
        try { Win32.UnregisterHotKey(Handle, HotkeyIdTune); } catch { }
        _rt.Log.EntryAdded -= OnLogEntry;
        base.OnFormClosed(e);
    }
}
