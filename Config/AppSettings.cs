using System.ComponentModel;

namespace AutoPickup.Config;

/// <summary>AutoPickup 运行配置（settings.json，位于数据目录，可 GUI 编辑）。
/// 所有参数都带 Category(中文分类)/DisplayName(中文名)/Description(说明)，参数页摊平展示、直接编辑。</summary>
public sealed class AppSettings
{
    [Category("1 游戏")] [DisplayName("游戏")] public GameSection Game { get; set; } = new();
    [Category("2 防火墙")] [DisplayName("防火墙")] public FirewallSection Firewall { get; set; } = new();
    [Category("3 音频")] [DisplayName("音频")] public AudioSection Audio { get; set; } = new();
    [Category("4 识别")] [DisplayName("识别")] public VisionSection Vision { get; set; } = new();
    [Category("5 自动化")] [DisplayName("自动化")] public AutomationSection Automation { get; set; } = new();
    [Category("6 快捷切换")] [DisplayName("快捷切换")] public QuickSwitchSection QuickSwitch { get; set; } = new();
    [Category("7 班次")] [DisplayName("班次")] public ShiftSection Shift { get; set; } = new();
    [Category("8 热键")] [DisplayName("热键")] public HotkeySection Hotkeys { get; set; } = new();

    [Category("9 覆盖层")] [DisplayName("覆盖层")] public OverlaySection Overlay { get; set; } = new();

    public sealed class GameSection
    {
        [Category("1 游戏")] [DisplayName("进程名")] [Description("GTAV 增强版进程名，默认 GTA5_Enhanced.exe")]
        public string ProcessName { get; set; } = "GTA5_Enhanced.exe";

        [Category("1 游戏")] [DisplayName("窗口类名")] [Description("游戏窗口类名，默认 sgaWindow")]
        public string WindowClass { get; set; } = "sgaWindow";

        [Category("1 游戏")] [DisplayName("窗口标题")] [Description("游戏窗口标题，默认 Grand Theft Auto V")]
        public string WindowTitle { get; set; } = "Grand Theft Auto V";
    }

    public sealed class FirewallSection
    {
        [Category("2 防火墙")] [DisplayName("规则名")] [Description("netsh 规则名，默认 AutoPickupBlock；封存档热键用的就是它")]
        public string RuleName { get; set; } = "AutoPickupBlock";

        [Category("2 防火墙")] [DisplayName("封锁域名")] [Description("封锁域名，启用时现解析全部 IPv4（云存档可能换 CDN IP）。默认两条：cs=云存档、ps=交易/玩家状态")]
        public List<string> BlockDomains { get; set; } = new()
        {
            "cs-gta5-prod.ros.rockstargames.com",
            "ps-gta5-prod.ros.rockstargames.com",
        };

        [Category("2 防火墙")] [DisplayName("封网网段(IP库)")] [Description("整段封锁(CIDR)，逗号分隔；默认留空。警告：实测封整个 /24 会掉线——192.81.241.0/24 里除了存档 .171，还有 auth .100、ugc .173、pod .15；192.81.245.0/24 是同一套 pod 服务的另一组入口。只有确认某小段是本游戏专用时才填")]
        public List<string> BlockCidrs { get; set; } = new();

        [Category("2 防火墙")] [DisplayName("附加固定IP")] [Description("额外封锁的单个 IPv4（已被上面网段覆盖的会自动略过），逗号分隔可多项")]
        public List<string> ExtraIps { get; set; } = new() { "192.81.241.171" };

        [Category("2 防火墙")] [DisplayName("IP刷新周期(小时)")] [Description("封网快路径“沿用上次解析的 IP”的有效期；超过它下次封网会重新 delete+add+解析域名。默认 6 小时")]
        public double IpRefreshHours { get; set; } = 6;
    }

    public sealed class AudioSection
    {
        [Category("3 音频")] [DisplayName("启用音频cue")] [Description("回环音量判定“下云”声音，默认开")]
        public bool Enable { get; set; } = true;

        [Category("3 音频")] [DisplayName("捕获设备")] [Description("下拉即当前活动的输出设备（实时枚举，含“(系统默认)”）；也可手打设备名片段。注意：只在程序启动那刻绑定，之后换默认设备不跟随")]
        [TypeConverter(typeof(CaptureDeviceConverter))]
        public string CaptureDevice { get; set; } = "";

        [Category("3 音频")] [DisplayName("采样间隔(ms)")] [Description("音量采样间隔，默认 100ms；越小越灵敏、开销略增")]
        public int SampleIntervalMs { get; set; } = 100;

        [Category("3 音频")] [DisplayName("平均音量阈值(%)")] [Description("静音基线后 1s 短窗的平均音量门限，默认 12%")]
        public double CueAvgPercent { get; set; } = 12;

        [Category("3 音频")] [DisplayName("峰值阈值(%)")] [Description("1s 短窗的峰值门限，默认 40%；换音频设备后可用 --audiotest 重新校准")]
        public double CueMaxPercent { get; set; } = 40;
    }

    public sealed class VisionSection
    {
        [Category("4 识别·工作像素")] [DisplayName("整帧工作像素上限(M)")] [Description("整帧兜底识别前按同一系数缩到该像素量（保持长宽比、只缩不放），默认 0.98M≈1280x768；4K 用它换速度")]
        public double OcrWorkPixelsM { get; set; } = 0.98;

        [Category("4 识别·工作像素")] [DisplayName("左下提示倍率")] [Description("左下 toast 小字放大倍率；实测 2 能把“保存失败”读全，3 反而变差，默认 2")]
        public double ToastUpscale { get; set; } = 2.0;

        [Category("4 识别·工作像素")] [DisplayName("tab条倍率")] [Description("tab 条【裁图重读】的放大倍率（自适应定位失败兜底、以及条带补充读取时用）；默认 1=不放大，读不到条带时可试 2")]
        public double TabUpscale { get; set; } = 1.0;

        [Category("4 识别·工作像素")] [DisplayName("中央弹窗倍率")] [Description("中央弹窗放大倍率；实测 2 更稳（原生也能读），默认 2")]
        public double DialogUpscale { get; set; } = 2.0;

        [Category("4 识别·工作像素")] [DisplayName("焦点行倍率")] [Description("列表焦点行放大倍率；行裁很小需要更高倍，默认 3")]
        public double FocusRowUpscale { get; set; } = 3.0;

        [Category("4 识别·工作像素")] [DisplayName("用双三次重采样")] [Description("勾选=双三次(质量更好，实测能把“保存失败”读全)；不勾=最近邻(快)。默认勾选")]
        public bool OcrUseBicubic { get; set; } = true;

        [Category("4 识别·工作像素")] [DisplayName("OCR输入日志")] [Description("每次 OCR 打印“站点/输入尺寸/缩放比/耗时”，默认开（便于调参和排障）")]
        public bool OcrLogInput { get; set; } = true;

        [Category("4 识别·横幅")] [DisplayName("主菜单搜索带(高%)")] [Description("主菜单横幅/手柄模板只搜画面上部该比例（默认 60）；调到 100 才是整帧（很慢）")]
        public double HomeSearchPercent { get; set; } = 60.0;

        [Category("4 识别·横幅")] [DisplayName("横幅工作高度(px)")] [Description("横幅/主菜单模板匹配前，把整帧等比缩到该高度再匹配（模板保持原尺寸）；默认 768=一套模板通吃所有分辨率且耗时恒定")]
        public int BannerWorkHeight { get; set; } = 768;

        [Category("4 识别·横幅")] [DisplayName("横幅搜索带(高%)")] [Description("模式横幅模板只搜索画面上部这个高度比例（百分比，默认 21）；主菜单横幅仍搜整帧。调大=多花时间换不漏检")]
        public double BannerSearchPercent { get; set; } = 21.0;

        [Category("4 识别·横幅")] [DisplayName("横幅模板耗时日志")] [Description("打印横幅模板匹配耗时（当前 Read 的主要耗时点），默认关")]
        public bool BannerTimingLog { get; set; } = false;

        [Category("4 识别·横幅")] [DisplayName("横幅优先用OCR")] [Description("勾选=模式判定不再跑昂贵的模板匹配，只用“横幅带 OCR 词证”（每帧省 2~4s）；取消则恢复 NCC+OCR 混合。默认勾选")]
        public bool BannerPreferOcr { get; set; } = true;

        [Category("4 识别·横幅")] [DisplayName("横幅OCR复核")] [Description("NCC 分数含糊(0.2~0.6)时，对横幅搜索带补一次 OCR 词证（在线模式/故事），默认开；NCC 明确时不动")]
        public bool BannerOcrFallback { get; set; } = true;

        [Category("4 识别·横幅")] [DisplayName("覆盖层画横幅")] [Description("覆盖层是否画出横幅模板搜索带与当前命中的模板框+分数，默认开")]
        public bool DrawBannerProbe { get; set; } = true;

        [Category("4 识别·横幅")] [DisplayName("缩放下限")] [Description("多尺度横幅模板搜索的最小缩放，默认 0.90")]
        public double ScaleMin { get; set; } = 0.90;

        [Category("4 识别·横幅")] [DisplayName("缩放上限")] [Description("最大缩放，默认 1.10")]
        public double ScaleMax { get; set; } = 1.10;

        [Category("4 识别·横幅")] [DisplayName("缩放步长")] [Description("尺度枚举步长，默认 0.05")]
        public double ScaleStep { get; set; } = 0.05;
        [Category("4 识别·列表")] [DisplayName("列表左%(宽)")] [Description("列表识别窗口左边界，占画面宽度百分比；默认 15%（1024x768 下的 x154）")]
        public double ListLeftPercent { get; set; } = 15.0;

        [Category("4 识别·列表")] [DisplayName("列表右%(宽)")] [Description("列表识别窗口右边界，占画面宽度百分比；默认 40%（1024x768 下的 x410）")]
        public double ListRightPercent { get; set; } = 40.0;

        [Category("4 识别·列表")] [DisplayName("列表上%(高)")] [Description("列表内容区上边界，占画面高度百分比；默认 21%（768 下的 y161）")]
        public double ListTopPercent { get; set; } = 21.0;

        [Category("4 识别·列表")] [DisplayName("列表下%(高)")] [Description("列表内容区下边界，占画面高度百分比；默认 89%（768 下的 y684）")]
        public double ListBottomPercent { get; set; } = 89.0;

        [Category("4 识别·列表")] [DisplayName("焦点行白阈值")] [Description("选中行白带亮度门限，默认 140")]
        public double RowWhiteThreshold { get; set; } = 140;

        [Category("4 识别·tab条")] [DisplayName("tab条上%(高)")] [Description("tab 条【兜底】区域上边界（占画面高度百分比）：正常走自适应定位（从整帧词里找 tab 行），只有定位失败时才用这个条带，默认 16.0%（768 下的 y123）")]
        public double TabStripTopPercent { get; set; } = 16.0;

        [Category("4 识别·tab条")] [DisplayName("tab条下%(高)")] [Description("tab 条【兜底】区域下边界，与上一项配对；默认 21.0%（768 下的 y161）")]
        public double TabStripBottomPercent { get; set; } = 21.0;

        [Category("4 识别·tab条")] [DisplayName("tab白块阈值")] [Description("选中 tab 纯白块亮度门限，默认 190")]
        public double TabWhiteThreshold { get; set; } = 190;

        [Category("4 识别·tab条")] [DisplayName("tab最小宽(px)")] [Description("白块最小宽度，用来过滤噪声，默认 26")]
        public int TabMinWidthPx { get; set; } = 26;

        [Category("4 识别·中央弹窗")] [DisplayName("中央裁剪左")] [Description("弹窗类(退出/切换确认/加载/断连)快速 OCR 的中央区域左边界(0..1)，默认 0.18")]
        public double DialogCropLeft { get; set; } = 0.18;

        [Category("4 识别·中央弹窗")] [DisplayName("中央裁剪右")] [Description("中央区域右边界，默认 0.82")]
        public double DialogCropRight { get; set; } = 0.82;

        [Category("4 识别·中央弹窗")] [DisplayName("中央裁剪上")] [Description("中央区域上边界，默认 0.26")]
        public double DialogCropTop { get; set; } = 0.26;

        [Category("4 识别·中央弹窗")] [DisplayName("中央裁剪下")] [Description("中央区域下边界，默认 0.74；未命中会自动回退整帧 OCR")]
        public double DialogCropBottom { get; set; } = 0.74;

        [Category("4 识别·提示条")] [DisplayName("左下提示右%(宽)")] [Description("“保存失败”toast 条带右边界，占画面宽度百分比；默认 18%")]
        public double ToastRightPercent { get; set; } = 18.0;

        [Category("4 识别·提示条")] [DisplayName("左下提示上%(高)")] [Description("toast 条带上边界，占画面高度百分比；默认 20%")]
        public double ToastTopPercent { get; set; } = 20.0;

        [Category("4 识别·提示条")] [DisplayName("左下提示下%(高)")] [Description("toast 条带下边界，占画面高度百分比；默认 85%")]
        public double ToastBottomPercent { get; set; } = 85.0;

        [Category("4 识别·文本判据")] [DisplayName("模糊匹配阈值(like)")] [Description("流程里 like 判据（如 selectedtab like 在线）的重合度门限 0~1：实测文本里命中目标字的比例达到它才算成立，默认 0.6。匹配太松/太严时调它")]
        public double LikeThreshold { get; set; } = 0.6;

        [Category("4 识别·文本判据")] [DisplayName("保存失败关键词")] [Description("判定“保存失败”的字符组，格式 组1|组2|…：**每组都要至少命中一个字**才算（抗 OCR 丢字/错字）。默认 “保存荏|失矢败”（荏、矢 是常见误读）。留空=永不判为保存失败")]
        public string ToastSaveFailWords { get; set; } = "保存荏|失矢败";

        [Category("4 识别·文本判据")] [DisplayName("到货关键词")] [Description("判定“到货（已获取）”的字符组，格式同上一项。默认 “获取得|已己员工”。原文换了写法（例如“已送达”）就改这里，不用重编程序")]
        public string ToastCargoWords { get; set; } = "获取得|已己员工";
    }

    public sealed class AutomationSection
    {
        [Category("5 自动化")] [DisplayName("按键按住(ms)")] [Description("每次按键按住时长，默认 400ms")]
        public int PressMs { get; set; } = 400;

        [Category("5 自动化")] [DisplayName("输入方式")] [Description("Gamepad=ViGEm 虚拟手柄（推荐，不需要游戏在前台；需装 ViGEmBus 驱动）；Keyboard=键盘 Q/E+Enter+方向键。改完点[保存参数]后需重启程序才生效")]
        public string InputMode { get; set; } = "Gamepad";

        [Category("5 自动化")] [DisplayName("模式重读间隔(ms)")] [Description("确认到达线上/线下时读取画面的间隔，默认 400ms；调大可减轻 OCR 压力，调小反应更灵敏")]
        public int ModeReReadMs { get; set; } = 400;

        [Category("5 自动化")] [DisplayName("按Start最小间隔(ms)")] [Description("确认模式时两次按 Start 的最小间隔，默认 2000ms；太短会把菜单开了又关")]
        public int ModeStartPressMinMs { get; set; } = 2000;

        [Category("5 自动化")] [DisplayName("菜单已开等待(ms)")] [Description("暂停菜单已开着时的重读间隔（等切换完成），默认 800ms")]
        public int ModeMenuOpenSleepMs { get; set; } = 800;
    }

    public sealed class QuickSwitchSection
    {
        [Category("6 快捷切换")] [DisplayName("按住↓时长(ms)")] [Description("按住下方向键后等待轮盘出现的时长，默认 600ms")]
        public int WheelOpenMs { get; set; } = 600;

        [Category("6 快捷切换")] [DisplayName("推杆时长(ms)")] [Description("右摇杆推向目标方向的持续时长，默认 600ms")]
        public int LookHoldMs { get; set; } = 600;

        [Category("6 快捷切换")] [DisplayName("推杆幅度(0..1)")] [Description("右摇杆幅度，默认 0.7；推不满可调大")]
        public double StickMagnitude { get; set; } = 0.7;
    }

    public sealed class ShiftSection
    {
        [Category("7 班次")] [DisplayName("轮数")] [Description("本次班次要跑几轮；大仓拿满通常 85~95 轮，可分批跑。默认 2")]
        public int Count { get; set; } = 2;

        [Category("7 班次")] [DisplayName("启动前等待(分钟)")] [Description("启动前先等待的分钟数（取货计时 48 分钟，超过即可）；0=立即，默认 0")]
        public int WaitStartMins { get; set; } = 0;

        [Category("7 班次")] [DisplayName("使用封网")] [Description("true=进线上后封存档服 IP 阻断云存档，默认开")]
        public bool UseFirewall { get; set; } = true;

        [Category("7 班次")] [DisplayName("提示等待(s)")] [Description("封网后等左下角提示的秒数（“已获取”=到货 或 “保存失败”任一读到即算过）；两个都没读到才判本轮失败并停止班次。默认 45s")]
        public int SaveFailWaitSec { get; set; } = 45;

        [Category("7 班次")] [DisplayName("轮间停留(s)")] [Description("回到线下后、下一轮之前的停留，默认 10s")]
        public int BetweenHoldSec { get; set; } = 10;

        [Category("7 班次")] [DisplayName("单轮重试次数")] [Description("某一轮没完成时先恢复同步再重试的次数，默认 3；用完仍失败则记录并继续下一轮（只有“没读到保存失败”才会停止整批）")]
        public int RoundRetries { get; set; } = 3;

        [Category("7 班次")] [DisplayName("下云cue等待(s)")] [Description("等待“下云”声音 cue 的超时；命中即刻封网，超时按兜底补封。默认 240s")]
        public int CueTimeoutSec { get; set; } = 240;

        [Category("7 班次")] [DisplayName("封网延迟(cue→封网)(ms)")] [Description("“下云”cue 命中后、真正启用封网规则前的等待毫秒数：默认 150ms；0=命中即刻封，调大=稍晚封网（用于试验“晚一点是否更容易撞上存档尝试”）")]
        public int CueToBlockMs { get; set; } = 150;

        [Category("7 班次")] [DisplayName("提示后等待(s)")] [Description("读到“已获取”或“保存失败”之后、再切线下的硬等待秒数，默认 6，上限 3600。论坛经验：几个仓库的到货信息通常要 6~7 秒才全部弹出，等齐了再走")]
        public int AfterHintWaitSec { get; set; } = 6;
    }

    /// <summary>游戏窗口覆盖层（参数页开关）：外部点击穿透窗口，从不注入/抢焦点/影响输入输出。
    /// 用途一：OCR 区域可视化调参（F10 或自检页按钮）；用途二：F11 封存档/恢复的成功提示（游戏左上角短暂浮现）。</summary>
    public sealed class OverlaySection
    {
        [Category("9 覆盖层")] [DisplayName("启用覆盖层")] [Description("总开关；关闭后热键与按钮都不再显示覆盖层，默认开")]
        public bool Enabled { get; set; } = true;

        [Category("9 覆盖层")] [DisplayName("显示识别区域")] [Description("覆盖层是否绘制 OCR 区域框（列表/tab条/左下提示/中央裁剪），调参用，默认开")]
        public bool DrawRegions { get; set; } = true;

        [Category("9 覆盖层")] [DisplayName("区域文字标签")] [Description("是否在区域框旁标注中文名，默认开")]
        public bool ShowLabels { get; set; } = true;

        [Category("9 覆盖层")] [DisplayName("左上角操作提示")] [Description("F7 封存档/解除、F8 结束游戏进程时，在游戏左上角短暂显示提示，默认开")]
        public bool ToastOnAction { get; set; } = true;

        [Category("9 覆盖层")] [DisplayName("提示停留(ms)")] [Description("左上角提示显示时长，默认 2500ms")]
        public int ToastMs { get; set; } = 2500;

        [Category("9 覆盖层")] [DisplayName("班次中自动隐藏")] [Description("班次运行期间强制隐藏覆盖层（避免任何抓帧/视觉干扰），默认开")]
        public bool HideDuringShift { get; set; } = true;

        [Category("9 覆盖层")] [DisplayName("标签字号")] [Description("区域标签字号，默认 12")]
        public int FontSize { get; set; } = 12;
    }

    public sealed class HotkeySection
    {
        [Category("8 热键")] [DisplayName("启用全局热键")] [Description("启用后可在任意焦点下用热键即时切换“封存档”/结束游戏进程，默认开")]
        public bool Enabled { get; set; } = true;

        [Category("8 热键")] [DisplayName("封存档热键")] [Description("默认 F7；可填 F1..F12 或字母（.NET Keys 名称），改完点[保存参数]即时生效")]
        public string BlockSaveKey { get; set; } = "F7";

        [Category("8 热键")] [DisplayName("OCR调参热键")] [Description("默认 F6；全局按下=开/关游戏窗口上的 OCR 区域可视化覆盖层")]
        public string TuneOverlayKey { get; set; } = "F6";

        [Category("8 热键")] [DisplayName("结束游戏进程热键")] [Description("默认 F8；全局按下=立即强杀 GTA5 进程（等效“结束进程”，游戏来不及写存档），用于需要硬掉线/中断同步的场合")]
        public string KillGameKey { get; set; } = "F8";
    }
}