using AutoPickup;
using System.Drawing;

namespace AutoPickup.Ui;

/// <summary>
/// “使用向导”图文帮助：开局准备 + 游戏内必须确认的设置 + 卡大仓流程 + 排障顺序。
/// 正文用**极简 Markdown 子集**（#/## 标题 · **粗体** · 反引号行内代码 · - 列表 · 1. 编号 · --- 分隔线 · 空行分段），
/// 由本文件里的 RenderMarkdown 就地渲染成 RichTextBox —— 不引第三方渲染库、不加 WebView2，
/// 只为让这份长文档有层次、能自动换行；正文仍是一份人可读、可自行修改的纯文本。
/// </summary>
public sealed class WizardForm : Form
{
    private readonly AppRuntime _rt;

    private static readonly Color C_Head = Color.FromArgb(31, 96, 176);
    private static readonly Color C_Text = Color.FromArgb(38, 42, 50);
    private static readonly Color C_Muted = Color.FromArgb(120, 128, 140);
    private static readonly Color C_Rule = Color.FromArgb(202, 210, 222);

    private static readonly Font FBody = new("Microsoft YaHei UI", 10f);
    private static readonly Font FBold = new("Microsoft YaHei UI", 10f, FontStyle.Bold);
    private static readonly Font FCode = new("Consolas", 9.5f);
    private static readonly Font FH1 = new("Microsoft YaHei UI", 13f, FontStyle.Bold);
    private static readonly Font FH2 = new("Microsoft YaHei UI", 11.5f, FontStyle.Bold);

    /// <summary>反引号（行内代码定界符）。源码里不写裸反引号，免得被工具链当成模板串。</summary>
    private const char BT = (char)96;

    public WizardForm(AppRuntime rt)
    {
        _rt = rt;
        Text = "AutoPickup 使用向导";
        Width = 980;
        Height = 780;
        StartPosition = FormStartPosition.CenterParent;

        var txt = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(250, 251, 253),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Font = FBody,
            DetectUrls = false,
            TabStop = false,
        };
        RenderMarkdown(txt, HelpMarkdown());
        txt.SelectionStart = 0;
        Controls.Add(txt);
    }

    // ================= 极简 Markdown 渲染（无第三方依赖） =================

    private static void Append(RichTextBox box, string s, Color color, Font font)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionColor = color;
        box.SelectionFont = font;
        box.AppendText(s);
    }

    /// <summary>行内标记：**粗体** 与 反引号内的代码；其余按普通文本。</summary>
    private static void AppendInline(RichTextBox box, string text, Color color)
    {
        int i = 0;
        while (i < text.Length)
        {
            int b = text.IndexOf("**", i, StringComparison.Ordinal);
            int c = text.IndexOf(BT, i);
            int at = -1; bool bold = false;
            if (b >= 0 && (c < 0 || b <= c)) { at = b; bold = true; }
            else if (c >= 0) { at = c; }
            if (at < 0) { Append(box, text.Substring(i), color, FBody); break; }
            if (at > i) Append(box, text.Substring(i, at - i), color, FBody);
            string delim = bold ? "**" : BT.ToString();
            int end = text.IndexOf(delim, at + delim.Length, StringComparison.Ordinal);
            if (end < 0) { Append(box, text.Substring(at), color, FBody); break; }
            string inner = text.Substring(at + delim.Length, end - at - delim.Length);
            if (bold) Append(box, inner, color, FBold);
            else Append(box, inner, Color.FromArgb(46, 78, 120), FCode);
            i = end + delim.Length;
        }
    }

    /// <summary>逐行渲染：## 标题 / - 列表 / 1. 编号 / --- 分隔线 / 空行分段。</summary>
    private static void RenderMarkdown(RichTextBox box, string md)
    {
        box.Clear();
        foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) { Append(box, "\n", C_Text, FBody); continue; }
            if (line.StartsWith("---", StringComparison.Ordinal))
            {
                Append(box, new string('─', 64) + "\n", C_Rule, FBody);
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Append(box, "\n" + line.Substring(3) + "\n", C_Head, FH2);
                continue;
            }
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                Append(box, line.Substring(2) + "\n", C_Head, FH1);
                continue;
            }
            if (line.StartsWith("  - ", StringComparison.Ordinal))
            {
                Append(box, "        · ", C_Muted, FBody);
                AppendInline(box, line.Substring(4), C_Muted);
                Append(box, "\n", C_Text, FBody);
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                Append(box, "  • ", C_Head, FBold);
                AppendInline(box, line.Substring(2), C_Text);
                Append(box, "\n", C_Text, FBody);
                continue;
            }
            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                Append(box, "  ", C_Text, FBody);
                AppendInline(box, line.Substring(2), C_Muted);
                Append(box, "\n", C_Text, FBody);
                continue;
            }
            Append(box, "  ", C_Text, FBody);
            AppendInline(box, line, C_Text);
            Append(box, "\n", C_Text, FBody);
        }
    }

    // ================= 正文（可自行修改：改这里即可） =================

    private static string HelpMarkdown() => """
# AutoPickup 使用向导 —— GTAV 挂机取货助手

## 一、开始之前：资产与准备
- 需要已有 **CEO 办公室**，以及**特种货物仓库**（想卡几个仓就备几个）。仓库数量与容量都无所谓，不影响结果。
- 手柄驱动：装 **ViGEmBus** 后，自检页「输入层」应显示 ViGEm 手柄 = 绿（自检页 **[手柄驱动下载]** 打开官网，程序内不安装）。**没装不会自动降级** —— 按键会被忽略并提示。两条路二选一：① 装驱动；② 参数页「5 自动化 · 输入方式」改成 `Keyboard`（键盘 Q/E/Enter/方向键，**要求游戏窗口在前台**）。两条路改完都要重启程序。
- 以管理员运行本程序（防火墙规则需要管理员权限）。数据与 settings.json 在 `%LOCALAPPDATA%\AutoPickup`，日志首行会打印该路径。

## 二、游戏内必须先确认的设置
- **显示模式**：窗口化 或 无边框窗口。**不要用独占全屏** —— 独占全屏下识别框覆盖层不显示，抓帧也可能拿不到画面。
- **失焦暂停**：设为 **否**（关）。开着的话，你一切到别的窗口游戏就停住，整轮卡死。
- **失焦静音**：设为 **否**（关）。开着的话，游戏不在前台时完全不出声，「下云」cue 就抓不到 —— 表现是一直等到 240 秒超时补封。
- 上面两项，**线下（故事模式）与线上（GTA 在线模式）的设置页是分开的，两边都要设成「否」**。
  - 踩过的坑：重装 Rockstar Social Club 会把游戏设置重置回默认，这两项跟着变回「是」，于是 cue 再也抓不到。抓不到下云声时先回来查这两项，再怀疑程序。
- **音频输出**：用**系统默认**输出设备，别在运行中切换默认设备、也别给游戏单独指定输出（工具在启动那一刻绑定当时的默认设备，之后不跟随）。
- **线上出生点**：设成**室内/公寓**（进战局即安全，不会被其他玩家干扰）。

## 三、首次上机：先各自验证一次
- 自检页：状态灯全绿（游戏进程 / 游戏窗口 / 抓帧 / 输入层 / 音频cue / 防火墙 / OCR引擎 / 模板库）。按 **F6** 可在游戏窗口上叠加识别区域与 OCR 词框，确认框选位置正确。
- 流程页：先单独点 **[进入线上]**、**[回到故事]** 各跑一遍，确认动作与识别都正常，再开正式班次。
- **[封存档 开/关 (F7)]** 可随时手动验证封网：只拦云存档 / 交易域名的出站，游戏不会掉线。

## 四、卡大仓流程（正式操作）
1. 去仓库，让仓库里的员工发货：特种货物仓库 `$7500`/次，机库 `$25000`/次。要卡几个仓就去几个仓，各自派满。
2. 所有发货必须落在同一个取货周期内（游戏内两天 = 现实 48 分钟），在周期内派遣完毕。
3. 把线上出生点设成室内，然后退出到线下（故事模式）。
4. 在流程页设置 **「启动前等待」**：以**最后一个员工出发的时间**为起点，等 48 分钟即可开始；为稳妥建议 **50–55 分钟**。到点后点 **[开始班次]**。
5. 容量参考：大仓 111 箱通常 **85–95 轮**拿满；机库满仓 50 箱一般 **40+ 轮**。仓库容量不同不影响，不会造成存档冲突或错位。

## 五、一轮里程序做什么
  清场 → 进线上（轮盘手势，失败自动回退暂停菜单）→ 等「下云」声音 cue → 立刻封网（只封存档与交易域名的出站）→ 确认到达在线 → 等到货「已获取」或云存档「保存失败」→ 提示后等待（默认 6 秒）→ 回故事 → 恢复联网 → 轮间停留，再下一轮。

## 六、热键与常见问题
- **F6** 显示/隐藏识别框；**F7** 封存档 开/关；**F8** 立即结束游戏进程（游戏来不及写存档，用于需要硬中断同步的场合）。
- **[停止]** 在轮次之间生效；当前轮会先安全收尾（回线下 + 恢复联网），不会把你留在断网状态。
- 日志：界面默认只显示主干（流程/任务/界面/防火墙…），勾选 **「底层细节」** 可看 OCR 与观察词集等排查行；日志文件始终全量记录。
- 参数页速查：`2 防火墙`（域名·IP·刷新周期）· `3 音频`（设备与 cue 阈值）· `4 识别`（区域百分比与倍率）· `5 自动化`（按键时长、输入方式）· `6 快捷切换`（轮盘手势）· `7 班次`（轮数/等待/重试）· `8 热键` · `9 覆盖层`。

## 七、下云 cue 抓不到时，按这个顺序查
1. 游戏里查**第二部分的失焦暂停 / 失焦静音**（线下 + 线上都要是「否」）—— 这是最常见的原因。
2. 自检页看「音频cue」「输入层」两盏灯是否绿；「音频cue」不绿多半是参数页把音频 cue 关了、或捕获设备选错。
3. 参数页「3 音频 · 捕获设备」用系统默认（或明确的正确设备）；**程序启动后换设备不跟随，改完要重启**。
4. 仍需排查时用命令行：`AutoPickup.exe --audiotest 30`（边进战局边看实时音量），必要时微调「平均音量阈值 / 峰值阈值」。
""";
}
