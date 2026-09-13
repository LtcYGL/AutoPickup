using AutoPickup.Config;

namespace AutoPickup.Core.Flow;

/// <summary>原子的一个参数（GUI 用来生成编辑框）。</summary>
public sealed record AtomParam(string Name, string Type, string Default, string Description);

/// <summary>一个可拖拽的原子（观察 / 动作 / 判据）。</summary>
public sealed record AtomDef(
    string Id, string Kind, string Title, string Description,
    IReadOnlyList<AtomParam> Params, string Snippet);

/// <summary>
/// 原子目录：引擎与 GUI 的**唯一真源**。GUI 左侧调色板、右侧步骤参数编辑都从这里生成，
/// 避免“界面写死的清单”和“引擎实际支持的原语”漂移。
/// </summary>
public static class AtomCatalog
{
    public static IReadOnlyList<AtomDef> All { get; } = Build();

    public static IEnumerable<AtomDef> ByKind(string kind)
        => All.Where(a => a.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));

    private static List<AtomDef> Build() => new()
    {
        // ---------------- 观察（纯读，不按键） ----------------
        new AtomDef("obs.mode", "observe", "观察：模式", "tab 条词证判定 story/online/unknown（三值）",
            new[] { new AtomParam("as", "string", "mode", "存进上下文的证据名") }, "(观察 mode)"),
        new AtomDef("obs.menuopen", "observe", "观察：菜单是否已开", "条带快检 + 模式词双证据",
            new[] { new AtomParam("as", "string", "menuopen", "证据名") }, "(观察 menuopen)"),
        new AtomDef("obs.dialog", "observe", "观察：确认弹窗", "中央弹窗是否命中退出/切换确认",
            new[] { new AtomParam("as", "string", "dialog", "证据名") }, "(观察 dialog)"),
        new AtomDef("obs.toast", "observe", "观察：左下提示", "savefail/cargo/sell/levelup/cloud/none/other",
            new[] { new AtomParam("as", "string", "toast", "证据名") }, "(观察 toast)"),
        new AtomDef("obs.selectedtab", "observe", "观察：选中的 tab", "当前选中 tab 名（供 tab 原子/判据用）",
            new[] { new AtomParam("as", "string", "selectedtab", "证据名") }, "(观察 selectedtab)"),
        new AtomDef("obs.focusrow", "observe", "观察：焦点行", "列表当前焦点项文字",
            new[] { new AtomParam("as", "string", "focusrow", "证据名") }, "(观察 focusrow)"),
        new AtomDef("obs.bannerstory", "observe", "观察：横幅分数(故事)", "NCC 分数（0~1）",
            new[] { new AtomParam("as", "string", "bannerstory", "证据名") }, "(观察 bannerstory)"),
        new AtomDef("obs.banneronline", "observe", "观察：横幅分数(在线)", "NCC 分数（0~1）",
            new[] { new AtomParam("as", "string", "banneronline", "证据名") }, "(观察 banneronline)"),

        // ---------------- 动作（只做，不自验） ----------------
        new AtomDef("act.tap", "action", "动作：按键", "按住 pressMs 后松开",
            new[]
            {
                new AtomParam("button", "enum", "A", "A/B/X/Y/DPadUp/DPadDown/DPadLeft/DPadRight/Start/Back/LeftShoulder/RightShoulder"),
                new AtomParam("times", "int", "1", "连按次数"),
                new AtomParam("ms", "int", "150", "单次按住毫秒"),
            }, "(按 A)"),
        new AtomDef("act.gesture", "action", "动作：轮盘手势", "按住↓+右摇杆推方向，触发线上/故事快捷切换",
            new[] { new AtomParam("dir", "enum", "up", "up=回故事(线下) / down=进线上") }, "(手势 up)"),
        new AtomDef("act.menu", "action", "动作：暂停菜单", "先看再动：已开就沿用，不按 Start（避免 toggle 关掉）",
            new[] { new AtomParam("mode", "enum", "open", "open=打开(已开则沿用) / close=关闭 / toggle=切换") }, "(菜单 open)"),
        new AtomDef("act.tab", "action", "动作：切 tab", "按当前选中位置决定 RB/LB 次数",
            new[] { new AtomParam("target", "enum", "在线", "地图/简讯/职业/统计/设置/游戏/好友/商店") }, "(切 tab 在线)"),
        new AtomDef("act.navigate", "action", "动作：列表导航", "把焦点挪到目标项（逐次方向键；成功由焦点行证据判）",
            new[]
            {
                new AtomParam("name", "string", "", "目标项文字（模糊匹配）"),
                new AtomParam("target", "enum", "DPadDown", "DPadDown / DPadUp"),
                new AtomParam("times", "int", "15", "最多走几步（防跑飞）"),
            }, "(导航到 退至故事模式)"),
        new AtomDef("act.firewall", "action", "动作：封网/恢复", "封云存档或恢复联网；结果记 fact:firewall",
            new[] { new AtomParam("enable", "bool", "true", "true=封 / false=恢复") }, "(封网 true)"),
        new AtomDef("act.sleep", "action", "动作：等待", "纯等待（回放按虚拟时间推进）",
            new[] { new AtomParam("ms", "int", "1000", "毫秒") }, "(等待 1000ms)"),
        new AtomDef("act.noop", "action", "动作：什么都不做", "只用于“纯观察”的步骤",
            Array.Empty<AtomParam>(), "(无动作)"),

        // ---------------- 判据（Gate） ----------------
        new AtomDef("gate.ctx", "gate", "判据：引用已有证据", "对之前任一步观察到的证据求值",
            new[]
            {
                new AtomParam("name", "string", "mode", "证据名（如 mode/toast/dialog）"),
                new AtomParam("op", "enum", "eq", "eq/ne/in/nin/gt/ge/lt/le/truthy/falsy"),
                new AtomParam("value", "string", "story", "比较值"),
            }, "ctx:mode eq story"),
        new AtomDef("gate.obs", "gate", "判据：现观察", "对当前帧做一次观察再比较",
            new[]
            {
                new AtomParam("name", "string", "toast", "观察名"),
                new AtomParam("op", "enum", "eq", "eq/ne/in/gt/ge/lt/le"),
                new AtomParam("value", "string", "savefail", "比较值"),
            }, "obs:toast eq savefail"),
        new AtomDef("gate.fact", "gate", "判据：引擎事实", "对引擎记录的事实求值（如 firewall）",
            new[]
            {
                new AtomParam("name", "string", "firewall", "事实名"),
                new AtomParam("op", "enum", "eq", "eq/ne/truthy/falsy"),
                new AtomParam("value", "string", "true", "比较值"),
            }, "fact:firewall eq true"),

        // ---------------- 控制 ----------------
        new AtomDef("ctl.retry", "control", "控制：重试", "本步最多执行几次、每次间隔",
            new[]
            {
                new AtomParam("max", "int", "2", "最多执行次数（1=不重试）"),
                new AtomParam("intervalMs", "int", "1500", "重试间隔毫秒"),
            }, "retry 2x1500ms"),
        new AtomDef("ctl.timeout", "control", "控制：超时", "在超时窗口内反复重新观察，直到判据满足",
            new[] { new AtomParam("timeoutSec", "int", "20", "超时秒数") }, "timeout 20s"),
        new AtomDef("ctl.onfail", "control", "控制：失败怎么办", "判据不满足且重试用尽后的行为",
            new[] { new AtomParam("onFail", "enum", "abort", "abort=中止 / skip=跳过本步 / continue=继续下一步 / rollback=回卷") }, "onFail abort"),
    };

    /// <summary>按 Id 取原子。</summary>
    public static AtomDef? Get(string id) => All.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
