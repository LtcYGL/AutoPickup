using AutoPickup;
using AutoPickup.Logging;

namespace AutoPickup.Ui;

/// <summary>“使用向导”图文帮助：对应 GUI v0.3 三页（自检/参数/流程）+ 摇杆优先快捷切换说明。</summary>
public sealed class WizardForm : Form
{
    private readonly AppRuntime _rt;

    public WizardForm(AppRuntime rt)
    {
        _rt = rt;
        Text = "AutoPickup 使用向导";
        Width = 900;
        Height = 660;
        StartPosition = FormStartPosition.CenterParent;

        var txt = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Microsoft YaHei UI", 10f),
        };
        txt.Text = BuildHelpText();
        Controls.Add(txt);
    }

    private string BuildHelpText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("【AutoPickup 使用向导】");
        sb.AppendLine();
        sb.AppendLine("一、环境准备");
        sb.AppendLine("  · GTAV 增强版运行中，目标开局 = 故事模式自由漫游（不在纯主菜单/加载页）。");
        sb.AppendLine("  · 窗口 / 无边框窗口即可，分辨率任意（识别按帧高等比适配）。");
        sb.AppendLine("  · 以管理员运行（防火墙规则需要）；数据与 settings.json 在 %LOCALAPPDATA%\\AutoPickup（见日志首行）。");
        sb.AppendLine("  · 线上已设室内出生点：切换进的是公开战局，站室内即可。");
        sb.AppendLine();
        sb.AppendLine("二、[自检]页 — 用前必测");
        sb.AppendLine("  · 两排实时灯：游戏进程 / 游戏窗口 / 抓帧 / 输入层(ViGEm) / 音频cue / 防火墙 / 封存档 / OCR引擎 / 模板库。");
        sb.AppendLine("  · [显示OCR区域(F6)] 在游戏窗口上画出各识别区域与 OCR 词框；[识别时存样本] 把每次抓帧存到 samples 供排查。");
        sb.AppendLine("  · [添加规则]/[删除规则] 管理 netsh 规则（只封云存档/交易域名与 IP 的出站，游戏不掉线）；[封存档 开/关(F7)] 即时切换。");
        sb.AppendLine("  · [结束游戏进程(F8)] = 立即强杀 GTA5（游戏来不及写存档），用于需要硬中断同步的场合。");
        sb.AppendLine("  · [打开数据目录]/[清理临时文件]：查看与清理 frames/samples/diag 与日志历史卷（不动设置与模板）。");
        sb.AppendLine();
        sb.AppendLine("三、[流程]页 — 模式切换与班次");
        sb.AppendLine("  · [进入线上]/[回到故事]：跑与班次同一套原子流程（轮盘手势优先，失败自动回退暂停菜单），可单独验证。");
        sb.AppendLine("  · [开始班次]：每轮 = 进线上 → 下云 cue 临界封网 → 等到货(已获取)/保存失败提示 → 提示后等待 → 回故事 → 恢复联网 → 轮间停留。");
        sb.AppendLine("  · 轮数 / 启动前等待 / 是否封网 在本页；[停止] 在轮次间生效，当前轮先安全收尾（回线下 + 恢复联网）。");
        sb.AppendLine();
        sb.AppendLine("四、[参数]页 — 全部可调项（改完点[保存参数]才写盘；右下角是所选参数的说明，[恢复默认]可复位）");
        sb.AppendLine("  · 2 防火墙：规则名 / 封锁域名 / 封网网段(IP库) / 附加固定IP / IP刷新周期。");
        sb.AppendLine("  · 3 音频：启动、捕获设备、采样间隔、平均音量阈值(%)、峰值阈值(%) —— 下云 cue 不灵时先调这两个阈值。");
        sb.AppendLine("  · 4 识别：工作像素与各区域倍率、横幅、列表/tab条/中央弹窗/提示条位置、文本判据(like 阈值与提示关键词)。");
        sb.AppendLine("  · 5 自动化：按键按住 / 输入方式 / 模式确认节奏。　6 快捷切换：轮盘手势三个参数。");
        sb.AppendLine("  · 7 班次：轮数、启动前等待、使用封网、提示等待、轮间停留、单轮重试、下云cue等待、封网延迟、提示后等待。");
        sb.AppendLine("  · 8 热键：启用开关 + F6/F7/F8 三键（可改）。　9 覆盖层：开关、区域框、标签、左上角操作提示、停留时长、班次中隐藏。");
        sb.AppendLine();
        sb.AppendLine("小贴士：游戏失焦自动弹暂停是正常现象；程序先探测“菜单已开”再决定是否按 Start，不会误 toggle。");
        return sb.ToString();
    }
}
