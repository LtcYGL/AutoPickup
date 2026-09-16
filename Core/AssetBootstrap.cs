using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AutoPickup.Core;

/// <summary>
/// 单 exe 资源引导：模板/原生 dll/默认流程都**编译进 exe**，首次运行时释放到
/// <c>%LOCALAPPDATA%\AutoPickup\</c> 下（不往 exe 旁边落文件，保证"一个 exe"）。
///
/// 更新策略按资源类别区分，靠 <c>.assets.json</c> 记录"上一个内置版本"的哈希来判断用户有没有改过：
/// <list type="bullet">
/// <item><b>templates</b>：用户资源，**永不覆盖**（用户可自行替换/新增）。</item>
/// <item><b>flows</b>：默认流程，**跟着 exe 更新**——文件与记录哈希一致（用户没动过）就覆盖；
/// 用户改过则原样保留，内置新版另存为 <c>xxx.default.json</c>；旧安装没有记录时先备份 <c>.bak</c> 再覆盖。</item>
/// <item><b>native</b>：二进制依赖，与内置不同就直接覆盖（用户不会去改它）。</item>
/// </list>
/// 这样修掉了"新版 exe 配着旧流程跑"的坑：旧流程留在 AppData 里会让新逻辑看着像没生效。
/// </summary>
public static class AssetBootstrap
{
    private const string NativeDll = "ViGEmClient.dll";
    private const string MarkerFile = ".assets.json";
    private static bool _nativeResolved;

    /// <summary>引导期提示。此刻日志系统还没起来，先攒着，等 AppRuntime 起来一次性写进日志。</summary>
    private static readonly List<string> _notices = new();

    /// <summary>取走并清空引导期提示（给日志系统用）。</summary>
    public static List<string> DrainNotices()
    {
        var l = new List<string>(_notices);
        _notices.Clear();
        return l;
    }

    /// <summary>模板目录（AppData 下；由嵌入式资源释放）。</summary>
    public static string TemplatesDir(string dataDir) => Path.Combine(dataDir, "templates");

    /// <summary>原生 dll 目录。</summary>
    public static string NativeDir(string dataDir) => Path.Combine(dataDir, "native");

    /// <summary>默认流程目录（AppData 下，可被用户覆盖/新增）。</summary>
    public static string FlowsDir(string dataDir) => Path.Combine(dataDir, "flows");

    private enum Policy { Keep, Smart, Replace }

    /// <summary>释放/更新全部嵌入式资源（幂等；返回新写入的文件数）。</summary>
    public static int ExtractAll(string dataDir, Action<string>? log = null)
    {
        _notices.Clear();
        var marker = LoadMarker(dataDir);
        int n = 0;
        n += ExtractDir(dataDir, "templates", TemplatesDir(dataDir), log, marker, Policy.Keep);
        n += ExtractDir(dataDir, "flows", FlowsDir(dataDir), log, marker, Policy.Smart);
        n += ExtractDir(dataDir, "native", NativeDir(dataDir), log, marker, Policy.Replace);
        SaveMarker(dataDir, marker);
        return n;
    }

    private static int ExtractDir(string dataDir, string resPrefix, string targetDir,
        Action<string>? log, Dictionary<string, string> marker, Policy policy)
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(resPrefix + ".", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (names.Count == 0) return 0;
        Directory.CreateDirectory(targetDir);
        int created = 0, written = 0;
        foreach (var res in names)
        {
            // 资源名 = <前缀>.<相对路径，点替换了分隔符>；这里用后缀匹配真实文件名
            string file = res[(resPrefix.Length + 1)..];
            string outPath = Path.Combine(targetDir, file);
            string key = resPrefix + "/" + file;
            try
            {
                bool exists = File.Exists(outPath);
                // 用户资源：存在就不碰（连哈希都不算）
                if (exists && policy == Policy.Keep) continue;

                using var s = asm.GetManifestResourceStream(res);
                if (s is null) continue;

                if (!exists)
                {
                    byte[] fresh = ReadAll(s);
                    WriteFile(outPath, fresh);
                    marker[key] = Hash(fresh);
                    created++; written++;
                    continue;
                }

                // 已存在：比"AppData 当前内容"与"内置内容"
                string cur = HashFile(outPath);
                byte[] bytes = ReadAll(s);
                string emb = Hash(bytes);

                if (string.Equals(cur, emb, StringComparison.OrdinalIgnoreCase))
                {
                    marker[key] = emb;
                    if (policy == Policy.Smart)
                        Note(log, "内置流程校验：" + file + " 与内置版本一致（sha256 " + emb[..8] + "）");
                    continue;
                }

                if (policy == Policy.Replace)
                {
                    WriteFile(outPath, bytes);
                    marker[key] = emb;
                    written++;
                    Note(log, "内置原生库已更新：" + file);
                    continue;
                }

                // Smart（默认流程）：区分"用户没动过"与"用户改过"
                bool known = marker.TryGetValue(key, out string? prev);
                bool userEdited = known && !string.Equals(prev, cur, StringComparison.OrdinalIgnoreCase);
                if (userEdited)
                {
                    // 保留用户的文件，内置新版另存一份（不会被流程加载器读到）
                    string alt = Path.Combine(Path.GetDirectoryName(outPath)!, Path.GetFileNameWithoutExtension(file)
                        + ".default" + Path.GetExtension(file));
                    WriteFile(alt, bytes);
                    Note(log, "检测到你对 " + file + " 的修改，已保留原文件不动；内置新版另存为 " + Path.GetFileName(alt));
                }
                else
                {
                    if (!known) Backup(outPath, log);   // 旧安装没有版本记录：无法判断，先备份再更新
                    WriteFile(outPath, bytes);
                    Note(log, "内置流程已更新：" + file
                        + (known ? "（AppData 里是上一版内置文件）" : "（旧版已备份为 " + Path.GetFileName(outPath) + ".bak）"));
                }
                marker[key] = emb;
                written++;
            }
            catch (Exception e) { Note(log, "释放资源失败 " + file + ": " + e.Message); }
        }
        if (created > 0) Note(log, "首次运行：已释放 " + created + " 个内置资源 → " + targetDir);
        return written;
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>先写临时文件再原子替换，避免流程正在被另一个实例读取时读到半个文件。</summary>
    private static void WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    private static void Backup(string path, Action<string>? log)
    {
        try
        {
            string bak = path + ".bak";
            if (File.Exists(bak)) return;          // 第一次的备份留着，不被后续覆盖
            File.Copy(path, bak);
        }
        catch (Exception e) { Note(log, "备份 " + Path.GetFileName(path) + " 失败: " + e.Message); }
    }

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string HashFile(string p)
    {
        using var f = File.OpenRead(p);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    private static Dictionary<string, string> LoadMarker(string dataDir)
    {
        try
        {
            string p = Path.Combine(dataDir, MarkerFile);
            if (!File.Exists(p)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p));
            return d is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                             : new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static void SaveMarker(string dataDir, Dictionary<string, string> m)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, MarkerFile),
                JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 标记写不了不影响运行，只是下次会当作"旧安装"处理 */ }
    }

    private static void Note(Action<string>? log, string msg)
    {
        _notices.Add(msg);
        log?.Invoke(msg);
    }

    /// <summary>
    /// 让 P/Invoke 的 "ViGEmClient.dll" 指向 AppData 里的副本（单 exe 下 exe 旁没有这个 dll）。
    /// 用 DllImportResolver 精确重定向，找不到就回退系统默认查找（开发期 exe 旁有 dll 时仍可用）。
    /// </summary>
    public static void RegisterNativeResolver(string dataDir, Action<string>? log = null)
    {
        if (_nativeResolved) return;
        _nativeResolved = true;
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            NativeLibrary.SetDllImportResolver(asm, (name, _, _) =>
            {
                if (!name.Equals(NativeDll, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                string p = Path.Combine(NativeDir(dataDir), NativeDll);
                if (File.Exists(p) && NativeLibrary.TryLoad(p, out var h)) return h;
                // 开发期：exe 旁 / 工作区 assets\native
                foreach (var cand in new[]
                {
                    Path.Combine(AppContext.BaseDirectory, NativeDll),
                    Path.Combine(AppContext.BaseDirectory, "assets", "native", NativeDll),
                })
                    if (File.Exists(cand) && NativeLibrary.TryLoad(cand, out var h2)) return h2;
                log?.Invoke("未能加载 " + NativeDll + "（虚拟手柄将不可用，需 ViGEmBus 驱动）");
                return IntPtr.Zero;
            });
        }
        catch (Exception e) { log?.Invoke("注册原生库解析失败: " + e.Message); }
    }
}
