namespace NetworkIsolator.Models;

/// <summary>
/// Persistent record of a temporary-block session (one JSON file per session).
/// Written BEFORE the firewall rules are created: if the tool is killed abruptly,
/// the next run knows exactly which orphaned rules to remove.
/// </summary>
public sealed class SessionRecord
{
    public string SessionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ExePath { get; set; } = "";

    /// <summary>PID of the tool (the "watcher") that owns the session.</summary>
    public int OwnerPid { get; set; }

    /// <summary>Start time of the watcher: protects against PID reuse.</summary>
    public DateTime OwnerStartUtc { get; set; }

    /// <summary>PID of the blocked application (0 until it is started).</summary>
    public int RootPid { get; set; }

    public IsolationMode Mode { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Exact names of all firewall rules created (or being created).</summary>
    public List<string> Rules { get; set; } = [];
}
