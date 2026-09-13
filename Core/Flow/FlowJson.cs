using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoPickup.Core.Flow;

/// <summary>条件列表转换器：接受对象形式或字符串简写（"ctx:mode eq story"）混用。</summary>
public sealed class ConditionListConverter : JsonConverter<List<FlowJson.CondDto>>
{
    public override List<FlowJson.CondDto> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = new List<FlowJson.CondDto>();
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("条件必须是数组");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
                list.Add(new FlowJson.CondDto { Expr = reader.GetString() });
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                var dto = JsonSerializer.Deserialize<FlowJson.CondDto>(ref reader, options);
                if (dto is not null) list.Add(dto);
            }
            else throw new JsonException("条件项必须是字符串或对象");
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<FlowJson.CondDto> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, options);
}

/// <summary>
/// 流程 JSON 读写（M4 的声明式 DSL）。格式见 flows/*.json：
/// { name, description, steps:[{ id, note, require:[cond], observe:[名], action:{op,...}, pre:[cond], expect:[cond],
///   timeoutSec, retry:{max,intervalMs}, onFail }], cleanup:[同 steps] }
/// cond 简写可用字符串："ctx:mode eq story"（Kind:Name op value）。
/// </summary>
public static class FlowJson
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static FlowProgram Load(string path)
    {
        var dto = JsonSerializer.Deserialize<ProgramDto>(File.ReadAllText(path), Opt)
                  ?? throw new InvalidOperationException("流程解析失败: " + path);
        return new FlowProgram
        {
            Name = dto.Name ?? Path.GetFileNameWithoutExtension(path),
            Description = dto.Description ?? "",
            Steps = (dto.Steps ?? new()).Select(MapStep).ToList(),
            Cleanup = (dto.Cleanup ?? new()).Select(MapStep).ToList(),
        };
    }

    private static FlowStep MapStep(StepDto d) => new()
    {
        Id = d.Id ?? "step",
        Title = d.Title ?? "",
        Note = d.Note ?? "",
        Require = MapConds(d.Require),
        Observe = d.Observe ?? new(),
        Action = new ActionSpec
        {
            Op = d.Action?.Op ?? "noop",
            Button = d.Action?.Button ?? "A",
            Dir = d.Action?.Dir ?? "up",
            Ms = d.Action?.Ms ?? 150,
            Times = d.Action?.Times ?? 1,
            Enable = d.Action?.Enable ?? true,
            Mode = d.Action?.Mode ?? "open",
            Target = d.Action?.Target ?? "",
            Name = d.Action?.Name ?? "",
        },
        Pre = MapConds(d.Pre),
        Expect = MapConds(d.Expect),
        TimeoutSec = d.TimeoutSec ?? 0,
        Retry = new RetrySpec { Max = d.Retry?.Max ?? 1, IntervalMs = d.Retry?.IntervalMs ?? 1500 },
        OnFail = d.OnFail ?? "abort",
    };

    private static List<Condition> MapConds(List<CondDto>? src)
    {
        var list = new List<Condition>();
        if (src is null) return list;
        foreach (var c in src)
        {
            if (!string.IsNullOrWhiteSpace(c.Kind) || !string.IsNullOrWhiteSpace(c.Name))
            {
                list.Add(new Condition
                {
                    Kind = c.Kind ?? "ctx",
                    Name = c.Name ?? "",
                    Op = c.Op ?? "eq",
                    Value = c.Value,
                    Note = c.Note,
                });
            }
            else if (!string.IsNullOrWhiteSpace(c.Expr))
            {
                list.Add(ParseShort(c.Expr!));
            }
        }
        return list;
    }

    /// <summary>简写："ctx:mode eq story" / "obs:bannerstory ge 0.6" / "fact:steps gt 5"</summary>
    public static Condition ParseShort(string expr)
    {
        var parts = expr.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string head = parts.Length > 0 ? parts[0] : "ctx:";
        int colon = head.IndexOf(':');
        string kind = colon > 0 ? head[..colon] : "ctx";
        string name = colon > 0 ? head[(colon + 1)..] : head;
        string op = parts.Length > 1 ? parts[1] : "truthy";
        string? val = parts.Length > 2 ? string.Join(' ', parts.Skip(2)) : null;
        return new Condition { Kind = kind, Name = name, Op = op, Value = val, Note = expr };
    }

    /// <summary>条件项（对象形式；也由转换器从字符串简写构造）。</summary>
    public sealed class CondDto
    {
        public string? Expr { get; set; }
        public string? Kind { get; set; }
        public string? Name { get; set; }
        public string? Op { get; set; }
        public string? Value { get; set; }
        public string? Note { get; set; }
    }

    private sealed class ProgramDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<StepDto>? Steps { get; set; }
        public List<StepDto>? Cleanup { get; set; }
    }

    private sealed class StepDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Note { get; set; }
        [JsonConverter(typeof(ConditionListConverter))] public List<CondDto>? Require { get; set; }
        public List<string>? Observe { get; set; }
        public ActionDto? Action { get; set; }
        [JsonConverter(typeof(ConditionListConverter))] public List<CondDto>? Pre { get; set; }
        [JsonConverter(typeof(ConditionListConverter))] public List<CondDto>? Expect { get; set; }
        public int? TimeoutSec { get; set; }
        public RetryDto? Retry { get; set; }
        public string? OnFail { get; set; }
    }

    private sealed class ActionDto
    {
        public string? Op { get; set; }
        public string? Button { get; set; }
        public string? Dir { get; set; }
        public int? Ms { get; set; }
        public int? Times { get; set; }
        public bool? Enable { get; set; }
        public string? Mode { get; set; }
        public string? Target { get; set; }
        public string? Name { get; set; }
    }

    private sealed class RetryDto
    {
        public int? Max { get; set; }
        public int? IntervalMs { get; set; }
    }

}
