using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AutoPickup.Config;
using AutoPickup.Core.Capture;
using AutoPickup.Core.Native;
using AutoPickup.Core.Vision;
using AutoPickup.Core.Vision.Ocr;
using AutoPickup.Logging;

namespace AutoPickup.Ui;

/// <summary>
/// 游戏窗口覆盖层：一个**完全外部**的分层窗口，贴在 GTA 客户区上方。
/// 不注入游戏、不读游戏内存、不抓取输入、不抢前台焦点（WS_EX_NOACTIVATE|WS_EX_TRANSPARENT|WS_EX_TOOLWINDOW），
/// 因此对输入输出零影响，也不改变游戏画面（PrintWindow 抓帧不受影响）。
/// 用途：①OCR 区域可视化调参（F10 / 自检页按钮）②F11 封存档/恢复提示（左上角短暂浮现）。
/// 注：真·独占全屏时任何第三方窗口都无法显示，需用无边框窗口模式（否则覆盖层自动隐藏、不报错）。
/// </summary>
public sealed class OverlayForm : Form
{
    private readonly GtaWindowSource _window;
    private readonly AppSettings.VisionSection _vision;
    private readonly AppSettings.OverlaySection _overlay;
    private readonly LogBus _log;

    private readonly System.Windows.Forms.Timer _track = new() { Interval = 400 };
    private readonly System.Windows.Forms.Timer _repaint = new() { Interval = 250 };

    private bool _tunerOn;
    private string? _toastText;
    private DateTime _toastUntil = DateTime.MinValue;

    /// <summary>实时识别预览：最近若干次 OCR 的“词框（原帧坐标）+ 文本 + 输入尺寸/耗时”。
    /// 只画框，不贴截图——框就是 OCR 实际框定范围的可视化。</summary>
    public sealed record Evidence(int W, int H, IReadOnlyList<OcrWord> Words,
        string Text, string Label, long Ms, string SizeNote, DateTime At);
    private readonly ConcurrentQueue<Evidence> _evidence = new();
    private int _evidenceLimit = 4;
    private Rectangle? _fixedBounds;
    // 横幅模板探测（后台线程做，不阻塞界面）；只用模板匹配、不跑 OCR
    private readonly NccMatcher? _bannerMatcher;
    private readonly TemplateBank? _bannerBank;
    private volatile ReadResult? _probe;
    private volatile bool _probeRunning;
    private DateTime _probeAt = DateTime.MinValue;

    public bool TunerOn => _tunerOn;

    public OverlayForm(GtaWindowSource window, AppSettings settings, LogBus log, NccMatcher? matcher = null, TemplateBank? bank = null)
    {
        _window = window;
        _vision = settings.Vision;
        _overlay = settings.Overlay;
        _log = log;
        _bannerMatcher = matcher;
        _bannerBank = bank;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.95;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        // 点击穿透/不抢焦点/不占任务栏：CreateParams 与 OnHandleCreated 双重强制
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var cur = GetWindowLong(Handle, GWL_EXSTYLE);
        SetWindowLong(Handle, GWL_EXSTYLE,
            cur | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        _track.Tick += (_, _) => Tick();
        _repaint.Tick += (_, _) => Tick();
        _track.Start();
        _repaint.Start();
    }

    /// <summary>开关 OCR 区域可视化。</summary>
    public void SetTuner(bool on)
    {
        if (!_overlay.Enabled) on = false;
        if (_tunerOn == on) return;
        _tunerOn = on;
        _log.Info(on ? "覆盖层：OCR 区域可视化 开" : "覆盖层：OCR 区域可视化 关", "覆盖层");
        Tick();
    }

    public void ToggleTuner() => SetTuner(!_tunerOn);

    /// <summary>固定位置显示（离线预览/自检用）：不再跟随游戏窗口。</summary>
    public void UseFixedBounds(Rectangle r)
    {
        _fixedBounds = r;
        Bounds = r;
    }

    /// <summary>推送一次识别结果（测试/探测/流程里调用）：只记词框+文本+尺寸，不存图。</summary>
    public void PushEvidence(int frameW, int frameH, IReadOnlyList<OcrWord> words, string text, string label, long ms, string sizeNote)
    {
        if (!_overlay.Enabled || !PreviewActive || frameW <= 0 || frameH <= 0) return;
        _evidence.Enqueue(new Evidence(frameW, frameH, words ?? Array.Empty<OcrWord>(),
            text ?? "", label ?? "", ms, sizeNote ?? "", DateTime.UtcNow));
        while (_evidence.Count > _evidenceLimit && _evidence.TryDequeue(out _)) { }
    }

    /// <summary>预览是否生效：F6 区域可视化打开时为真。</summary>
    public bool PreviewActive => _tunerOn;

    /// <summary>是否需要“词框”数据：F6 打开时一并显示 OCR 词框（用户要的“实时框”）。</summary>
    public bool WantWords => _tunerOn;

    /// <summary>左上角短暂提示（F11 封存档/恢复）。</summary>
    public void ShowToast(string text)
    {
        if (!_overlay.Enabled || !_overlay.ToastOnBlockSave) return;
        _toastText = text;
        _toastUntil = DateTime.UtcNow.AddMilliseconds(Math.Max(600, _overlay.ToastMs));
        Tick();
    }

    /// <summary>班次等自动化任务期间强制隐藏（避免任何视觉/抓帧干扰）。</summary>
    public void SuspendForTask(bool hide)
    {
        if (hide) { Visible = false; }
        else Tick();
    }

    private void Tick()
    {
        try
        {
            if (_fixedBounds is Rectangle fb)
            {
                if (!_overlay.Enabled || (!_tunerOn && _toastText is null)) { if (Visible) Visible = false; return; }
                if (Bounds != fb) Bounds = fb;
                if (!Visible) Show();
                MaybeProbe();
                Invalidate();
                return;
            }
            var h = _window.Handle;
            bool ok = _overlay.Enabled && h != IntPtr.Zero && !Win32.IsIconic(h);
            bool toastActive = ok && _toastText is not null && DateTime.UtcNow < _toastUntil;
            if (!ok || (!_tunerOn && !toastActive))
            {
                if (Visible) Visible = false;
                if (!toastActive) _toastText = null;
                return;
            }
            if (!Win32.GetClientRect(h, out var c)) { if (Visible) Visible = false; return; }
            var origin = new Win32.POINT { X = 0, Y = 0 };
            Win32.ClientToScreen(h, ref origin);
            int w = c.Right - c.Left, ht = c.Bottom - c.Top;
            if (w < 40 || ht < 40) { if (Visible) Visible = false; return; }
            var want = new Rectangle(origin.X, origin.Y, w, ht);
            if (Bounds != want) Bounds = want;
            if (!Visible) Show();
            MaybeProbe();
            Invalidate();
        }
        catch (Exception e)
        {
            _log.Warn("覆盖层刷新异常: " + e.Message, "覆盖层");
        }
    }

    /// <summary>调参时周期性做一次“横幅模板探测”（不含 OCR，约 0.2~0.8s），把命中框画出来。</summary>
    private void MaybeProbe()
    {
        if (_tunerOn) Invalidate();   // 识别预览需要持续重绘（40ms 级刷新的证据队列）
        if (!_tunerOn || !_vision.DrawBannerProbe) return;
        if (_bannerMatcher is null || _bannerBank is null) return;
        if (_probeRunning || (DateTime.UtcNow - _probeAt).TotalMilliseconds < 900) return;
        var frame = _window.CaptureClient();
        if (!frame.IsValid) return;
        _probeRunning = true;
        _probeAt = DateTime.UtcNow;
        Task.Run(() =>
        {
            try
            {
                double band = Math.Clamp(_vision.BannerSearchPercent / 100.0, 0.05, 1.0);
                var r = new ReadResult
                {
                    FrameWidth = frame.Width,
                    FrameHeight = frame.Height,
                    BannerSearchRatio = band,
                };
                var gray = Imaging.BgraToGray(frame.Bgra, frame.Width, frame.Height);
                r.StoryBest = BestBanner(gray, frame.Width, frame.Height, "mode_story", band);
                r.OnlineBest = BestBanner(gray, frame.Width, frame.Height, "mode_online", band);
                r.HomeBest = BestBanner(gray, frame.Width, frame.Height, "mode_home", 1.0);
                _probe = r;
            }
            catch { }
            finally { _probeRunning = false; }
        });
    }

    private TemplateHit? BestBanner(byte[] gray, int w, int h, string group, double topRatio)
    {
        TemplateHit? best = null;
        foreach (var t in _bannerBank!.ByGroup(group))
        {
            var hit = _bannerMatcher!.LocateBest(gray, w, h, t, MatchFeature.Text, topRatio);
            if (hit is not null && (best is null || hit.Score > best.Score)) best = hit;
        }
        return best;
    }

    /// <summary>实时画 OCR 词框（原帧坐标等比映射到客户端），并给出一条尺寸/耗时/文本说明。
    /// 不贴截图——框本身就是“OCR 实际框定了什么”。</summary>
    private void PaintEvidence(Graphics g, int W, int H)
    {
        var list = _evidence.ToArray();
        if (list.Length == 0) return;
        float f = Math.Max(7f, Math.Min(18f, _overlay.FontSize));
        using var lbl = new Font("Microsoft YaHei UI", f, FontStyle.Bold);
        using var bg = new SolidBrush(Color.FromArgb(240, 10, 12, 16));
        using var penBox = new Pen(Color.FromArgb(255, 90, 255, 140), 2);
        using var penInput = new Pen(Color.FromArgb(200, 255, 210, 80), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };

        int lineY = 34;
        for (int i = 0; i < list.Length; i++)
        {
            var ev = list[i];
            if (ev.W <= 0 || ev.H <= 0) continue;
            // 原帧 → 客户端 等比映射（只用一个系数，绝不拉伸）
            double sx = W / (double)ev.W, sy = H / (double)ev.H;
            double s = Math.Min(sx, sy);
            int offX = (int)((W - ev.W * s) / 2.0), offY = (int)((H - ev.H * s) / 2.0);
            foreach (var wd in ev.Words)
            {
                int x = offX + (int)Math.Round(wd.X1 * s), y = offY + (int)Math.Round(wd.Y1 * s);
                int w = Math.Max(3, (int)Math.Round((wd.X2 - wd.X1) * s));
                int h = Math.Max(3, (int)Math.Round((wd.Y2 - wd.Y1) * s));
                g.DrawRectangle(penBox, x, y, w, h);
            }
            // 顶部一行说明（只在最新一条上显示，避免刷屏）
            if (i != list.Length - 1) continue;
            string head = ev.Label + "   " + ev.W + "x" + ev.H + "   送OCR " + (string.IsNullOrEmpty(ev.SizeNote) ? "-" : ev.SizeNote)
                + "   " + ev.Ms + "ms   词框 " + ev.Words.Count;
            string body = ev.Text.Replace('\n', ' ').Replace('\t', ' ');
            if (body.Length > 110) body = body.Substring(0, 110) + "…";
            var sz1 = TextRenderer.MeasureText(g, head, lbl, new Size(W - 24, int.MaxValue), TextFormatFlags.WordBreak);
            var sz2 = TextRenderer.MeasureText(g, body, lbl, new Size(W - 24, int.MaxValue), TextFormatFlags.WordBreak);
            var box = new Rectangle(10, lineY - 4, Math.Max(sz1.Width, sz2.Width) + 14, sz1.Height + sz2.Height + 12);
            g.FillRectangle(bg, box);
            g.DrawRectangle(penInput, box);
            TextRenderer.DrawText(g, head, lbl, new Rectangle(16, lineY, W - 24, sz1.Height), Color.FromArgb(255, 255, 226, 120), TextFormatFlags.WordBreak);
            TextRenderer.DrawText(g, body, lbl, new Rectangle(16, lineY + sz1.Height + 2, W - 24, sz2.Height), Color.FromArgb(255, 245, 250, 255), TextFormatFlags.WordBreak);
            lineY += sz1.Height + sz2.Height + 16;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try { PaintCore(e.Graphics); }
        catch (Exception ex) { _log.Warn("覆盖层绘制异常: " + ex.Message, "覆盖层"); }
    }

    private void PaintCore(Graphics g)
    {
        int W = ClientSize.Width, H = ClientSize.Height;
        if (W < 40 || H < 40) return;

        if (_tunerOn && _overlay.DrawRegions)
        {
            float f = Math.Max(7f, Math.Min(20f, _overlay.FontSize));
            using var lbl = new Font("Microsoft YaHei UI", f, FontStyle.Bold);
            using var lblBg = new SolidBrush(Color.FromArgb(255, 20, 22, 26));
            using var penList = new Pen(Color.FromArgb(255, 80, 220, 120), 2);
            using var penTab = new Pen(Color.FromArgb(255, 255, 200, 60), 2);
            using var penDialog = new Pen(Color.FromArgb(255, 90, 180, 255), 2);
            using var penToast = new Pen(Color.FromArgb(255, 255, 110, 200), 2);

            void Region(Pen pen, double l, double t, double r, double b, string name)
            {
                int x0 = (int)Math.Round(W * l), y0 = (int)Math.Round(H * t);
                int x1 = (int)Math.Round(W * r), y1 = (int)Math.Round(H * b);
                if (x1 <= x0 || y1 <= y0) return;
                g.DrawRectangle(pen, x0, y0, x1 - x0, y1 - y0);
                if (!_overlay.ShowLabels || string.IsNullOrEmpty(name)) return;
                var sz = TextRenderer.MeasureText(g, name, lbl, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                int lx = Math.Min(Math.Max(0, x0 + 3), Math.Max(0, W - sz.Width - 4));
                int ly = y0 - sz.Height - 3;
                if (ly < 0) ly = y0 + 3;
                g.FillRectangle(lblBg, new Rectangle(lx - 2, ly - 1, sz.Width + 5, sz.Height + 3));
                TextRenderer.DrawText(g, name, lbl, new Point(lx, ly), pen.Color, TextFormatFlags.NoPadding);
            }

            // 与 Core/Vision 的读取逻辑保持同一套百分比
            Region(penList, _vision.ListLeftPercent / 100.0, _vision.ListTopPercent / 100.0,
                _vision.ListRightPercent / 100.0, _vision.ListBottomPercent / 100.0, "列表(焦点行)");
            Region(penTab, 0, _vision.TabStripTopPercent / 100.0, 1,
                _vision.TabStripBottomPercent / 100.0, "tab条");
            Region(penToast, 0, _vision.ToastTopPercent / 100.0, _vision.ToastRightPercent / 100.0,
                _vision.ToastBottomPercent / 100.0, "左下提示(保存失败)");
            Region(penDialog, _vision.DialogCropLeft, _vision.DialogCropTop,
                _vision.DialogCropRight, _vision.DialogCropBottom, "中央弹窗裁剪");

            // ---- 模式横幅：模板匹配（不是固定 OCR 区域）----
            if (_vision.DrawBannerProbe && _bannerMatcher is not null && _bannerBank is not null)
            {
                var pr = _probe;
                using var penBand = new Pen(Color.FromArgb(220, 200, 140, 255), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                using var penHit = new Pen(Color.FromArgb(255, 255, 120, 120), 3);
                if (pr is not null && pr.BannerSearchRatio < 0.999)
                {
                    int bandY = (int)Math.Round(H * pr.BannerSearchRatio);
                    g.DrawLine(penBand, 0, bandY, W, bandY);
                    if (_overlay.ShowLabels)
                    {
                        string bt = "横幅搜索带 上部 " + (pr.BannerSearchRatio * 100).ToString("F0") + "%";
                        var bsz = TextRenderer.MeasureText(g, bt, lbl, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                        g.FillRectangle(lblBg, new Rectangle(4, Math.Max(0, bandY - bsz.Height - 3), bsz.Width + 5, bsz.Height + 3));
                        TextRenderer.DrawText(g, bt, lbl, new Point(6, Math.Max(0, bandY - bsz.Height - 3) + 1), penBand.Color, TextFormatFlags.NoPadding);
                    }
                }
                void Hit(TemplateHit? hh, string name)
                {
                    if (hh is null) return;
                    bool strong = hh.Score >= 0.75;
                    bool mid = hh.Score >= 0.5;
                    if (!strong && !mid) return;
                    using var p = new Pen(Color.FromArgb(strong ? 255 : 160, 255, 120, 120), strong ? 3 : 2);
                    g.DrawRectangle(p, hh.X1, hh.Y1, Math.Max(1, hh.Width), Math.Max(1, hh.Height));
                    if (!_overlay.ShowLabels) return;
                    string txt = name + " " + hh.Score.ToString("F2")
                        + (strong ? " 命中" : " 弱(阈值0.5)");
                    var sz2 = TextRenderer.MeasureText(g, txt, lbl, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                    int tx = Math.Min(Math.Max(0, hh.X1), Math.Max(0, W - sz2.Width - 4));
                    int ty = Math.Max(0, hh.Y1 - sz2.Height - 3);
                    g.FillRectangle(lblBg, new Rectangle(tx - 2, ty - 1, sz2.Width + 5, sz2.Height + 3));
                    TextRenderer.DrawText(g, txt, lbl, new Point(tx, ty), p.Color, TextFormatFlags.NoPadding);
                }
                if (pr is not null)
                {
                    Hit(pr.StoryBest, "故事横幅");
                    Hit(pr.OnlineBest, "在线横幅");
                }
            }

            using var cross = new Pen(Color.FromArgb(140, 255, 255, 255), 1);
            g.DrawLine(cross, W / 2 - 14, H / 2, W / 2 + 14, H / 2);
            g.DrawLine(cross, W / 2, H / 2 - 14, W / 2, H / 2 + 14);
        }

        // ---- 实时识别预览（测试/探测动作时）----
        if (PreviewActive && !_evidence.IsEmpty) PaintEvidence(g, W, H);

        if (_toastText is not null && DateTime.UtcNow < _toastUntil)
        {
            using var font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
            var sz = TextRenderer.MeasureText(g, _toastText, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            int pad = 10;
            var box = new Rectangle(16, 16, sz.Width + pad * 2, sz.Height + pad * 2);
            using var bg = new SolidBrush(Color.FromArgb(255, 18, 20, 24));
            using var border = new Pen(Color.FromArgb(255, 255, 200, 60), 2);
            g.FillRectangle(bg, box);
            g.DrawRectangle(border, box);
            TextRenderer.DrawText(g, _toastText, font, new Rectangle(box.X + pad, box.Y + pad, sz.Width, sz.Height),
                Color.FromArgb(255, 255, 226, 120), TextFormatFlags.NoPadding);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _track.Dispose(); _repaint.Dispose(); } catch { } }
        base.Dispose(disposing);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
