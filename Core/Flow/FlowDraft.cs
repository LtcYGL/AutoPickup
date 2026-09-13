using System.Text;
using System.Text.Json;

namespace AutoPickup.Core.Flow;

/// <summary>草稿里的一步：原子 Id + 参数值 + 判据字符串（简写形式，与 flows/*.json 一致）。</summary>
public sealed class DraftStep
{
    public string AtomId { get; set; } = "act.noop";
    public Dictionary<string, string> Args { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Require { get; set; } = new();
    public List<string> Expect { get; set; } = new();
    public List<string> Observe { get; set; } = new();
    public int RetryMax { get; set; } = 1;
    public int RetryIntervalMs { get; set; } = 1500;
    public int TimeoutSec { get; set; }
    public string OnFail { get; set; } = "abort";
    public string Note { get; set; } = "";
    public string Id { get; set; } = "";
}

/// <summary>
/// 流程草稿：GUI 编辑的就是它，序列化出来就是现有 DSL（flows/*.json）。
/// 因此“拖拽搭出来的流程”和“手写 JSON”完全等价，可互相打开。
/// </summary>
public sealed class FlowDraft
{
    public string Name { get; set; } = "untitled";
    public string Description { get; set; } = "";
    public List<DraftStep> Steps { get; } = new();
    public List<DraftStep> Cleanup { get; } = new();

    public DraftStep Add(AtomDef def)
    {
        var s = new DraftStep { AtomId = def.Id, Id = ShortId(def.Id) };
        foreach (var p in def.Params) s.Args[p.Name] = p.Default;
        if (def.Id.StartsWith("gate.")) s.Expect.Add(def.Snippet);
        if (def.Id == "obs.mode" || def.Id == "obs.toast" || def.Id == "obs.dialog" || def.Id == "obs.menuopen")
            s.Observe.Add(SnippetObserve(def));
        Steps.Add(s);
        return s;
    }

    private static string SnippetObserve(AtomDef def) => def.Id switch
    {
        "obs.mode" => "mode",
        "obs.toast" => "toast",
        "obs.dialog" => "dialog",
        "obs.menuopen" => "menuopen",
        _ => "mode",
    };

    public void Remove(DraftStep s) { Steps.Remove(s); Cleanup.Remove(s); }

    public void Move(DraftStep s, int delta)
    {
        int i = Steps.IndexOf(s);
        if (i < 0) return;
        int j = Math.Clamp(i + delta, 0, Steps.Count - 1);
        if (i == j) return;
        Steps.RemoveAt(i);
        Steps.Insert(j, s);
    }

    private static string ShortId(string atomId)
    {
        int dot = atomId.IndexOf('.');
        return dot >= 0 ? atomId[(dot + 1)..] : atomId;
    }

    /// <summary>编译成引擎可跑的 FlowProgram。</summary>
    public FlowProgram Compile() => new()
    {
        Name = Name,
        Description = Description,
        Steps = Steps.Select(CompileStep).ToList(),
        Cleanup = Cleanup.Select(CompileStep).ToList(),
    };

    private static FlowStep CompileStep(DraftStep s)
    {
        var def = AtomCatalog.Get(s.AtomId);
        string op = def?.Id switch
        {
            "act.tap" => "tap",
            "act.gesture" => "gesture",
            "act.menu" => "menu",
            "act.tab" => "tab",
            "act.navigate" => "navigate",
            "act.firewall" => "firewall",
            "act.sleep" => "sleep",
            _ => "noop",
        };
        string Arg(string k, string dflt) => s.Args.TryGetValue(k, out var v) ? v : dflt;
        return new FlowStep
        {
            Id = string.IsNullOrEmpty(s.Id) ? "step" : s.Id,
            Note = s.Note,
            Require = s.Require.Select(ConditionParse).ToList(),
            Observe = s.Observe.ToList(),
            Action = new ActionSpec
            {
                Op = op,
                Button = Arg("button", "A"),
                Dir = Arg("dir", "up"),
                Ms = int.TryParse(Arg("ms", "150"), out int ms) ? ms : 150,
                Times = int.TryParse(Arg("times", "1"), out int t) ? t : 1,
                Enable = !Arg("enable", "true").Equals("false", StringComparison.OrdinalIgnoreCase),
                Mode = Arg("mode", "open"),
                Target = Arg("target", "在线"),
                Name = Arg("name", ""),
            },
            Expect = s.Expect.Select(ConditionParse).ToList(),
            TimeoutSec = s.TimeoutSec,
            Retry = new RetrySpec { Max = Math.Max(1, s.RetryMax), IntervalMs = s.RetryIntervalMs },
            OnFail = s.OnFail,
        };
    }

    private static Condition ConditionParse(string expr) => FlowJson.ParseShort(expr);

    /// <summary>写出成 flows/*.json（与手写格式一致，可直接被 FlowJson.Load 读回）。</summary>
    public void Save(string path)
    {
        var dto = new
        {
            name = Name,
            description = Description,
            steps = Steps.Select(ToDto).ToList(),
            cleanup = Cleanup.Select(ToDto).ToList(),
        };
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static object ToDto(DraftStep s) => new
    {
        id = s.Id,
        note = s.Note,
        require = s.Require,
        observe = s.Observe,
        action = ActionDto(s),
        expect = s.Expect,
        timeoutSec = s.TimeoutSec,
        retry = new { max = s.RetryMax, intervalMs = s.RetryIntervalMs },
        onFail = s.OnFail,
    };

    private static object ActionDto(DraftStep s)
    {
        var def = AtomCatalog.Get(s.AtomId);
        string op = def?.Id switch
        {
            "act.tap" => "tap",
            "act.gesture" => "gesture",
            "act.menu" => "menu",
            "act.tab" => "tab",
            "act.navigate" => "navigate",
            "act.firewall" => "firewall",
            "act.sleep" => "sleep",
            _ => "noop",
        };
        string Arg(string k, string dflt) => s.Args.TryGetValue(k, out var v) ? v : dflt;
        var d = new Dictionary<string, object> { ["op"] = op };
        switch (op)
        {
            case "tap": d["button"] = Arg("button", "A"); d["times"] = int.Parse(Arg("times", "1")); d["ms"] = int.Parse(Arg("ms", "150")); break;
            case "gesture": d["dir"] = Arg("dir", "up"); break;
            case "menu": d["mode"] = Arg("mode", "open"); break;
            case "tab": d["target"] = Arg("target", "在线"); break;
            case "navigate": d["name"] = Arg("name", ""); d["target"] = Arg("target", "DPadDown"); d["times"] = int.Parse(Arg("times", "15")); break;
            case "firewall": d["enable"] = !Arg("enable", "true").Equals("false", StringComparison.OrdinalIgnoreCase); break;
            case "sleep": d["ms"] = int.Parse(Arg("ms", "1000")); break;
        }
        return d;
    }

    /// <summary>从已存在的流程 JSON 读回来编辑（与 FlowJson.Load 同一份格式）。</summary>
    public static FlowDraft Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var d = new FlowDraft
        {
            Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "untitled" : "untitled",
            Description = root.TryGetProperty("description", out var de) ? de.GetString() ?? "" : "",
        };
        if (root.TryGetProperty("steps", out var steps)) foreach (var se in steps.EnumerateArray()) d.Steps.Add(FromJson(se));
        if (root.TryGetProperty("cleanup", out var cl)) foreach (var se in cl.EnumerateArray()) d.Cleanup.Add(FromJson(se));
        return d;
    }

    private static DraftStep FromJson(JsonElement se)
    {
        var s = new DraftStep { AtomId = GuessAtom(se) };
        if (se.TryGetProperty("id", out var id)) s.Id = id.GetString() ?? "";
        if (se.TryGetProperty("note", out var no)) s.Note = no.GetString() ?? "";
        if (se.TryGetProperty("observe", out var ob)) foreach (var o in ob.EnumerateArray()) s.Observe.Add(o.GetString() ?? "");
        if (se.TryGetProperty("require", out var rq)) foreach (var o in rq.EnumerateArray()) s.Require.Add(Str(o));
        if (se.TryGetProperty("expect", out var ex)) foreach (var o in ex.EnumerateArray()) s.Expect.Add(Str(o));
        if (se.TryGetProperty("timeoutSec", out var to) && to.TryGetInt32(out int tv)) s.TimeoutSec = tv;
        if (se.TryGetProperty("onFail", out var of)) s.OnFail = of.GetString() ?? "abort";
        if (se.TryGetProperty("retry", out var rt))
        {
            if (rt.TryGetProperty("max", out var mx) && mx.TryGetInt32(out int mv)) s.RetryMax = mv;
            if (rt.TryGetProperty("intervalMs", out var iv) && iv.TryGetInt32(out int ivv)) s.RetryIntervalMs = ivv;
        }
        if (se.TryGetProperty("action", out var ac))
        {
            foreach (var p in ac.EnumerateObject())
            {
                if (p.NameEquals("op")) continue;
                s.Args[p.Name] = p.Value.ValueKind == JsonValueKind.True ? "true"
                    : p.Value.ValueKind == JsonValueKind.False ? "false"
                    : p.Value.ToString();
            }
        }
        return s;
    }

    private static string Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString();

    private static string GuessAtom(JsonElement se)
    {
        if (!se.TryGetProperty("action", out var ac)) return "act.noop";
        string op = ac.TryGetProperty("op", out var o) ? o.GetString() ?? "noop" : "noop";
        return op switch
        {
            "tap" => "act.tap",
            "gesture" => "act.gesture",
            "menu" => "act.menu",
            "tab" => "act.tab",
            "navigate" => "act.navigate",
            "firewall" => "act.firewall",
            "sleep" => "act.sleep",
            _ => "act.noop",
        };
    }
}
