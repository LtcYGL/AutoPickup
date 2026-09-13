using AutoPickup.Core.Capture;
using AutoPickup.Core.Vision;

namespace AutoPickup.Core.Flow;

/// <summary>流程所见的一切外部世界：抓帧 + 输入。回放时用假实现，引擎与流程本身不知道真假。</summary>
public interface IFlowHost
{
    string Name { get; }
    bool IsReplay { get; }

    /// <summary>当前帧（回放时是脚本里的当前帧）。</summary>
    Frame Capture();

    /// <summary>按键/摇杆（200ms 默认按住）。</summary>
    void Tap(string button);

    /// <summary>轮盘手势（方向 up/down）。</summary>
    void Gesture(string dir);

    /// <summary>等待；回放时可被压缩或忽略。</summary>
    void SleepMs(int ms);

    /// <summary>防火墙开/关（真实实现才有效）。</summary>
    bool Firewall(bool enable);

    /// <summary>
    /// 等待“下云”声音 cue（安静→响亮）。卡大仓的关键时机：cue 一响就要立刻封网。
    /// 回放/单帧宿主没有音频，直接返回 true（脚本化）。
    /// </summary>
    bool WaitCloudCue(int timeoutSec);
}

/// <summary>一次观察的结果。观察必须无副作用（不按键），可安全重复调用。</summary>
public sealed record Observed(string Kind, string Value, double? Score = null, string? Detail = null)
{
    public string Num => Value;
}

/// <summary>动作执行后的回执：只说明“做了什么”，不判断成败（成败由 Gate 判）。</summary>
public sealed record Acted(bool Done, string Detail);

/// <summary>一次条件求值的结果，带证据文本（便于日志与 GUI 显示）。</summary>
public sealed record CheckResult(bool Ok, string Evidence)
{
    public static CheckResult Pass(string ev) => new(true, ev);
    public static CheckResult Fail(string ev) => new(false, ev);
}

/// <summary>
/// 条件（Gate）。执行期间按顺序逐条判断，证据文本会写日志/覆盖层，便于“为什么走了这条分支”。
/// 顺序：ctx(历史证据) → obs(当前帧重新观察) → fact(引擎内状态)。
/// </summary>
public sealed class Condition
{
    public string Kind { get; init; } = "ctx";   // ctx | obs | fact
    public string Name { get; init; } = "";      // 观察名 / 事实名
    public string Op { get; init; } = "eq";      // eq/ne/in/nin/gt/ge/lt/le/truthy/falsy
    public string? Value { get; init; }
    public string? Note { get; init; }

    public override string ToString()
        => $"{Kind}({Name}) {Op}" + (Value is null ? "" : " " + Value) + (Note is null ? "" : $" [{Note}]");
}
