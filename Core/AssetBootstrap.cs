using System.Reflection;
using System.Runtime.InteropServices;

namespace AutoPickup.Core;

/// <summary>
/// 单 exe 资源引导：模板/原生 dll/默认流程都**编译进 exe**，首次运行时释放到
/// <c>%LOCALAPPDATA%\AutoPickup\</c> 下（不往 exe 旁边落文件，保证“一个 exe”）。
/// 释放是**带版本标记的一次性**行为：已存在的文件不覆盖，用户的替换/新增不会被冲掉。
/// </summary>
public static class AssetBootstrap
{
    private const string NativeDll = "ViGEmClient.dll";
    private static bool _nativeResolved;

    /// <summary>模板目录（AppData 下；由嵌入式资源释放）。</summary>
    public static string TemplatesDir(string dataDir) => Path.Combine(dataDir, "templates");

    /// <summary>原生 dll 目录。</summary>
    public static string NativeDir(string dataDir) => Path.Combine(dataDir, "native");

    /// <summary>默认流程目录（AppData 下，可被用户覆盖/新增）。</summary>
    public static string FlowsDir(string dataDir) => Path.Combine(dataDir, "flows");

    /// <summary>释放全部嵌入式资源（幂等；返回释放了几个文件）。</summary>
    public static int ExtractAll(string dataDir, Action<string>? log = null)
    {
        int n = 0;
        n += ExtractDir(dataDir, "templates", TemplatesDir(dataDir), log);
        n += ExtractDir(dataDir, "flows", FlowsDir(dataDir), log);
        n += ExtractDir(dataDir, "native", NativeDir(dataDir), log);
        return n;
    }

    private static int ExtractDir(string dataDir, string resPrefix, string targetDir, Action<string>? log)
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(resPrefix + ".", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (names.Count == 0) return 0;
        Directory.CreateDirectory(targetDir);
        int written = 0;
        foreach (var res in names)
        {
            // 资源名 = <前缀>.<相对路径，点替换了分隔符>；这里用后缀匹配真实文件名
            string file = res[(resPrefix.Length + 1)..];
            string outPath = Path.Combine(targetDir, file);
            if (File.Exists(outPath)) continue;      // 不覆盖：保留用户替换/新增
            try
            {
                using var s = asm.GetManifestResourceStream(res);
                if (s is null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                using var f = File.Create(outPath);
                s.CopyTo(f);
                written++;
            }
            catch (Exception e) { log?.Invoke("释放资源失败 " + file + ": " + e.Message); }
        }
        if (written > 0) log?.Invoke("首次运行：已释放 " + written + " 个内置资源 → " + targetDir);
        return written;
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
