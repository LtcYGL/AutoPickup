using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoPickup.Config;

/// <summary>settings.json 读写（数据目录），缺失时写默认值。
/// 兼容旧 schema：Vision 里的像素坐标键（ListXLeft/ListYTop/TabStripYTop…）自动迁移为百分比键。</summary>
public sealed class SettingsStore
{
    private readonly string _dir;
    private readonly string _path;
    public string DataDir => _dir;

    // 旧像素键 → 新百分比键（乘数 100/参考尺寸；≥100 视为旧像素值，&lt;100 视为已是百分比）
    private static readonly (string Old, string New, double Ref)[] VisionMigrations =
    {
        ("ListXLeft", "ListLeftPercent", 1024),
        ("ListXRight", "ListRightPercent", 1024),
        ("ListYTop", "ListTopPercent", 768),
        ("ListYBottom", "ListBottomPercent", 768),
        ("TabStripYTop", "TabStripTopPercent", 768),
        ("TabStripYBottom", "TabStripBottomPercent", 768),
    };

    public SettingsStore(string dataDir)
    {
        _dir = dataDir;
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
                if (root is not null)
                {
                    MigrateVision(root);
                    var s = root.Deserialize<AppSettings>(
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (s is not null) return s;
                }
            }
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine("settings load failed: " + e.Message);
        }
        var fresh = new AppSettings();
        Save(fresh);
        return fresh;
    }

    /// <summary>把旧版像素区域（以及早期误写进百分比键的 768 基准像素值）折算成 0..100 百分比。</summary>
    private static void MigrateVision(JsonObject root)
    {
        if (root["Vision"] is not JsonObject vision) return;
        foreach (var (oldKey, newKey, reference) in VisionMigrations)
        {
            if (vision[oldKey] is not JsonValue ov || !ov.TryGetValue<double>(out var oldVal)) continue;
            double percent = oldVal * 100.0 / reference;
            if (!vision.ContainsKey(newKey)) vision[newKey] = percent;
            vision.Remove(oldKey);
        }
        foreach (var (_, newKey, reference) in VisionMigrations)
        {
            if (vision[newKey] is not JsonValue nv || !nv.TryGetValue<double>(out var val)) continue;
            // 百分比换算出界（>100）说明这份 JSON 里存的是旧像素值（例如某次迁移把 52 原样写进了百分比键）
            if (val > 100.0) vision[newKey] = val * 100.0 / reference;
        }
    }

    public void Save(AppSettings s)
    {
        try
        {
            var json = JsonSerializer.Serialize(s,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine("settings save failed: " + e.Message);
        }
    }
}
