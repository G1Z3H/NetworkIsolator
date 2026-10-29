namespace NetworkIsolator.Core;

/// <summary>
/// List of the active sessions of THIS process. Allows cleaning everything up when
/// the application closes, when Windows signs out, or when the process ends normally.
/// </summary>
public static class SessionRegistry
{
    private static readonly object Sync = new();
    private static readonly List<AppContainerLauncher> Active = [];

    public static void Register(AppContainerLauncher launcher)
    {
        lock (Sync) Active.Add(launcher);
    }

    public static void Unregister(AppContainerLauncher launcher)
    {
        lock (Sync) Active.Remove(launcher);
    }

    public static int Count
    {
        get { lock (Sync) return Active.Count; }
    }

    /// <summary>Stops and cleans up every session (blocking: removes the firewall rules).</summary>
    public static void DisposeAll()
    {
        AppContainerLauncher[] copy;
        lock (Sync) copy = [.. Active];

        foreach (var launcher in copy)
        {
            try { launcher.Dispose(); } catch { /* keep going with the others */ }
        }
    }
}
