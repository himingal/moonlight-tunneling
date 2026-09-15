namespace MingalTunnel.Core;

public enum LogLevel { Info, Success, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>In-memory feed for the log panel plus a small rotating file.</summary>
public static class AppLog
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private static readonly object FileLock = new();
    private static readonly List<LogEntry> Recent = [];
    private static string? _lastMessage;
    private static DateTime _lastTime;
    private static int _repeats;

    public static event Action<LogEntry>? Added;

    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Recent) return Recent.ToList();
    }

    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Success(string m) => Write(LogLevel.Success, m);
    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m) => Write(LogLevel.Error, m);

    public static void Write(LogLevel level, string message)
    {
        var now = DateTime.Now;
        lock (Recent)
        {
            // A message repeating in a tight loop (a UI error re-raising itself,
            // say) must not flood the panel and fill the disk: collapse it.
            if (message == _lastMessage && now - _lastTime < TimeSpan.FromSeconds(10))
            {
                _repeats++;
                _lastTime = now;
                return;
            }
            if (_repeats > 0)
            {
                var summary = new LogEntry(now, LogLevel.Warn, $"(previous message repeated {_repeats} more time(s))");
                _repeats = 0;
                Append(summary);
            }
            _lastMessage = message;
            _lastTime = now;
        }
        Append(new LogEntry(now, level, message));
    }

    private static void Append(LogEntry e)
    {
        lock (Recent)
        {
            Recent.Add(e);
            if (Recent.Count > 500) Recent.RemoveRange(0, Recent.Count - 500);
        }
        try
        {
            lock (FileLock)
            {
                var path = AppPaths.AppLogFile;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxFileBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{e.Time:yyyy-MM-dd HH:mm:ss} [{e.Level}] {e.Message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never take the app down
        }
        Added?.Invoke(e);
    }
}
