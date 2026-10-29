using System.IO;
using System.Text;
using NetworkIsolator.Models;

namespace NetworkIsolator.Core;

/// <summary>
/// Activity log: written to %APPDATA%\NetworkIsolator\logs and broadcast to the interface.
/// Never throws (a logging problem must never break a block).
/// </summary>
public static class ActivityLog
{
    private const int MaxMemoryEntries = 400;
    private const long MaxFileBytes = 1_000_000;

    private static readonly object Sync = new();
    private static readonly List<LogEntry> Recent = [];

    /// <summary>Raised for each new line (can come from any thread).</summary>
    public static event Action<LogEntry>? EntryAdded;

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Ok(string message) => Write(LogLevel.Ok, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>Copy of the latest lines (to fill the Log tab when the window opens).</summary>
    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Sync) return [.. Recent];
    }

    private static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);

        lock (Sync)
        {
            Recent.Add(entry);
            if (Recent.Count > MaxMemoryEntries) Recent.RemoveAt(0);
            AppendToFile(entry);
        }

        try { EntryAdded?.Invoke(entry); } catch { /* a faulty subscriber must not break the log */ }
    }

    private static void AppendToFile(LogEntry e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);

            // Simple rotation: only one old file is kept.
            var info = new FileInfo(AppPaths.LogFile);
            if (info.Exists && info.Length > MaxFileBytes)
                File.Move(AppPaths.LogFile, AppPaths.LogFile + ".old", overwrite: true);

            string tag = e.Level switch
            {
                LogLevel.Ok => "OK ",
                LogLevel.Warn => "WRN",
                LogLevel.Error => "ERR",
                _ => "INF"
            };
            string line = $"{e.Time:yyyy-MM-dd HH:mm:ss.fff} [{tag}] [pid {Environment.ProcessId}] {e.Message}{Environment.NewLine}";
            File.AppendAllText(AppPaths.LogFile, line, Encoding.UTF8);
        }
        catch
        {
            // Disk full, file locked... ignore.
        }
    }
}
