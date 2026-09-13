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
        sb.AppendLine("【AutoPickup 使用向导 — GUI v0.3】");
        sb.AppendLine();
        sb.AppendLine("一、环境准备");
        sb.AppendLine("  · GTAV 增强版运行中：目标开局 = 故事模式自由漫游（不在纯主菜单/加载页）。");
        sb.AppendLine("  · 窗口/无边框窗口即可，分辨率任意（识别引擎按帧高等比适配）。");
        sb.AppendLine("  · 以管理员运行（防火墙规则需要）；数据目录与 settings.json 在 %LOCALAPPDATA% 下（见日志首行）。");
        sb.AppendLine("  · 线上已设室内出生点：快捷切换进的线上是公开战局，站室内即可，无影响。");
        sb.AppendLine();
        sb.AppendLine("二、[自检]页 — 用前必测");
        sb.AppendLine("  点[环境自检]看 游戏进程/窗口/抓帧/输入层(ViGEm)/音频/防火墙/OCR 是否就绪；");
        sb.AppendLine("  [识别测试] 抓当前画面看分层识别与 OCR；[状态机探测] 开关暂停菜单 2 轮自测。");
        sb.AppendLine("  防火墙按钮 = netsh 规则 AutoPickupBlock（只封存档服域名+IP 出站，不封游戏流量）。");
        sb.AppendLine();
        sb.AppendLine("三、模式切换 = 摇杆优先（[流程]页）");
        sb.AppendLine("  快捷切换（默认开）：ViGEm 手柄 按住 下方向键 + 右摇杆推 上=回故事 / 下=进线上 + 同帧松");
        sb.AppendLine("    → 游戏直接弹切换确认框 → 程序自动按 A 并继续（下云cue / 到达验证等，与暂停菜单路径一致）。");
        sb.AppendLine("  失败自动回退传统暂停菜单：进线上 → 仅限邀请战局；回故事 → 在线tab → 退至故事模式。");
        sb.AppendLine("  [只测手势↑/↓] 单独验证手势与确认弹窗（不进流程）；微调在[参数]页 QuickSwitch 节。");
        sb.AppendLine();
        sb.AppendLine("四、班次（[流程]页 → [开始班次]）");
        sb.AppendLine("  卡大仓逻辑：每轮 = 进线上 → 下云cue临界封网 → 等“保存失败”提示 → 回故事 → 恢复联网 → 停留。");
        sb.AppendLine("  轮数 / 启动前等待分钟 / 末轮留在线 / 是否封网 都在该页；[停止]在轮次间生效并先安全收尾。");
        sb.AppendLine();
        sb.AppendLine("五、[参数]页 — 全参数可调");
        sb.AppendLine("  分类树直改，[保存参数] 才写盘：等待时间(Timing/Shift)、音频cue阈值(Audio)、");
        sb.AppendLine("  OCR/识别窗口位置(Vision: ListX/ListY/TabStrip…)、输入方式(Keyboard|Gamepad)、");
        sb.AppendLine("  快捷切换(QuickSwitch: WheelOpenMs/LookHoldMs/StickMagnitude/DialogWaitSec/Attempts)。");
        sb.AppendLine("  说明：OnlineIsUp=false 是实机定稿方向（摇杆上=回故事 / 下=进线上），一般不要改。");
        sb.AppendLine();
        sb.AppendLine("小贴士：游戏失焦自动弹暂停是正常现象；程序会先探测“菜单已开”再决定是否按 Start，不会误 toggle。");
        return sb.ToString();
    }
}
