using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using NetworkIsolator.Models;

namespace NetworkIsolator.Core;

/// <summary>
/// One JSON file per session in %APPDATA%\NetworkIsolator\sessions.
/// One file per session = no locking between the window, command-line shortcuts and the "watchers".
/// </summary>
public static class SessionJournal
{
    private static string PathFor(string sessionId) => Path.Combine(AppPaths.SessionsDir, sessionId + ".json");

    /// <summary>Atomic write (temporary file, then replace): never a half-written JSON.</summary>
    public static void Save(SessionRecord record)
    {
        Directory.CreateDirectory(AppPaths.SessionsDir);
        string path = PathFor(record.SessionId);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, JsonDefaults.Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string sessionId)
    {
        try { File.Delete(PathFor(sessionId)); } catch { /* deja supprime */ }
    }

    /// <summary>All known sessions. Unreadable files are ignored.</summary>
    public static IReadOnlyList<SessionRecord> LoadAll()
    {
        var result = new List<SessionRecord>();
        if (!Directory.Exists(AppPaths.SessionsDir)) return result;

        foreach (string file in Directory.EnumerateFiles(AppPaths.SessionsDir, "*.json"))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(file), JsonDefaults.Options);
                if (rec is { SessionId.Length: > 0 }) result.Add(rec);
            }
            catch
            {
                // file being written or corrupted: ignore it
            }
        }
        return result;
    }

    /// <summary>
    /// Is the watcher that owns the session still alive?
    /// Compares PID AND start time (a PID can be reassigned to another program).
    /// </summary>
    public static bool IsOwnerAlive(SessionRecord record)
    {
        try
        {
            using var p = Process.GetProcessById(record.OwnerPid);
            try
            {
                return Math.Abs((p.StartTime.ToUniversalTime() - record.OwnerStartUtc).TotalSeconds) < 5;
            }
            catch (Win32Exception)
            {
                return true; // read denied: to be safe, consider it alive
            }
            catch (InvalidOperationException)
            {
                return false; // the process has just ended
            }
        }
        catch (ArgumentException)
        {
            return false; // no process with this PID
        }
    }
}
