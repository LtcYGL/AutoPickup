namespace AutoPickup.Logging;

public enum LogLevel { Info, Okay, Warn, Error, Hint }

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Source, string Message)
{
    public override string ToString()
        => $"[{Timestamp:HH:mm:ss.fff}] [{Level}] {Source}: {Message}";
}

/// <summary>进程内日志总线：UI 订阅 + 文件落盘。</summary>
public sealed class LogBus : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter? _file;
    public event Action<LogEntry>? EntryAdded;

    /// <summary>单个日志文件上限；超过就滚动，只保留 1 份历史（避免无限增殖）。</summary>
    private const long MaxLogBytes = 3 * 1024 * 1024;

    public LogBus(string? filePath = null)
    {
        if (filePath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                Rotate(filePath);
                _file = new StreamWriter(filePath, append: true, new System.Text.UTF8Encoding(true))
                { AutoFlush = true };
            }
            catch { _file = null; }
        }
    }

    /// <summary>启动时滚动：当前日志超限就改名为 .1（旧的 .1 被覆盖），保持最多两份。</summary>
    private static void Rotate(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length < MaxLogBytes) return;
            string bak = filePath + ".1";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(filePath, bak);
        }
        catch { }
    }

    public void Log(string source, string message, LogLevel level = LogLevel.Info)
    {
        var entry = new LogEntry(DateTime.Now, level, source, message);
        lock (_lock)
        {
            _file?.WriteLine(entry.ToString());
            EntryAdded?.Invoke(entry);
        }
    }

    public void Info(string s, string src = "App") => Log(src, s, LogLevel.Info);
    public void Okay(string s, string src = "App") => Log(src, s, LogLevel.Okay);
    public void Warn(string s, string src = "App") => Log(src, s, LogLevel.Warn);
    public void Error(string s, string src = "App") => Log(src, s, LogLevel.Error);
    public void Hint(string s, string src = "App") => Log(src, s, LogLevel.Hint);

    public void Dispose()
    {
        lock (_lock) { _file?.Dispose(); }
    }
}