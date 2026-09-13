using System.Drawing;
using System.Drawing.Imaging;
using AutoPickup.Logging;

namespace AutoPickup.Core.Vision;

/// <summary>扫描 assets/templates 目录（可含子目录作为 Group），文件名前缀也可推断组。</summary>
public sealed class TemplateBank
{
    private readonly List<TemplateDef> _templates = new();
    public IReadOnlyList<TemplateDef> Templates => _templates;

    private readonly LogBus _log;

    public TemplateBank(LogBus log) => _log = log;

    public bool LoadFromDirectory(string dir)
    {
        _templates.Clear();
        if (!Directory.Exists(dir))
        {
            _log.Warn("模板目录不存在: " + dir, "Vision");
            return false;
        }
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".png" or ".bmp")) continue;
            var tpl = LoadOne(file);
            if (tpl is not null)
            {
                _templates.Add(tpl);
                count++;
            }
        }
        _log.Info("模板库加载: " + count + " 张，来自 " + dir, "Vision");
        return count > 0;
    }

    private TemplateDef? LoadOne(string file)
    {
        try
        {
            using var bmp = new Bitmap(file);
            int w = bmp.Width, h = bmp.Height;
            if (w < 4 || h < 4) return null;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var argb = new byte[w * h * 4];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, argb, 0, argb.Length);
            bmp.UnlockBits(data);
            // argb 内存序为 BGRA
            var gray = Imaging.BgraToGray(argb, w, h);
            var rel = Path.GetRelativePath(Path.GetDirectoryName(file)!, file);
            var group = InferGroup(Path.GetFileName(file));
            var name = Path.GetFileNameWithoutExtension(file);
            return new TemplateDef
            {
                Group = group,
                Name = name,
                Gray = gray,
                Width = w,
                Height = h,
            };
        }
        catch (Exception e)
        {
            _log.Warn("模板加载失败 " + file + " : " + e.Message, "Vision");
            return null;
        }
    }

    /// <summary>文件名前缀启发式分组（与原始 gtaz imgs 命名风格一致）。</summary>
    public static string InferGroup(string fileName)
    {
        string n = fileName;
        if (n.StartsWith("title_GrandTheftAutoV")) return "mode_story";
        if (n.StartsWith("title_GrandTheftAuto")) return "mode_online";
        if (n.StartsWith("home_")) return "mode_home";
        if (n.StartsWith("exit_") || n.StartsWith("故事_exit_")) return "exit";
        if (n.StartsWith("edge_") || n.StartsWith("baredge_") || n.StartsWith("故事_edge_")) return "hint";
        if (n.StartsWith("hint_")) return "hint";
        if (n.StartsWith("故事_header_")) return "story_header";
        if (n.StartsWith("header_")) return "online_header";
        if (n.StartsWith("故事_focus_")) return "story_focus";
        if (n.StartsWith("focus_")) return "online_focus";
        if (n.StartsWith("故事_item_")) return "story_item";
        if (n.StartsWith("item_")) return "online_item";
        if (n.StartsWith("故事_list_")) return "story_list";
        if (n.StartsWith("list_")) return "online_list";
        if (n.StartsWith("barfocus_")) return "home_focus";
        if (n.StartsWith("bar_")) return "home_header";
        return "misc";
    }

    public IReadOnlyList<TemplateDef> ByGroup(string group)
        => _templates.Where(t => t.Group == group).ToList();
}
