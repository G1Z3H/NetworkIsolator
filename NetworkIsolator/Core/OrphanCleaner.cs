using System.IO;
using System.Diagnostics;
using NetworkIsolator.Models;
using NetworkIsolator.Localization;

namespace NetworkIsolator.Core;

/// <summary>
/// Removes firewall rules left behind by an abruptly killed tool, a crash or a restart.
/// A session is "orphaned" when its watcher no longer exists.
/// </summary>
public static class OrphanCleaner
{
    /// <summary>Returns the number of rules removed. Does nothing without admin rights.</summary>
    public static async Task<int> SweepAsync(CancellationToken ct = default)
    {
        if (!ElevationHelper.IsElevated()) return 0;

        var firewall = new FirewallManager();
        int removed = 0;

        foreach (SessionRecord record in SessionJournal.LoadAll())
        {
            if (SessionJournal.IsOwnerAlive(record)) continue;

            // The watcher is dead but the application may still be running: as long as it is,
            // we KEEP the rules (the application stays blocked). The next pass will clean them up.
            if (IsExeRunning(record.ExePath))
            {
                ActivityLog.Warn(Loc.T("Log_OrphanStillRunning", record.DisplayName));
                continue;
            }

            bool allGone = true;
            foreach (string rule in record.Rules)
            {
                try
                {
                    if (await firewall.RemoveRuleAsync(rule, ct).ConfigureAwait(false)) removed++;
                    else allGone = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    allGone = false;
                    ActivityLog.Warn(Loc.T("Log_OrphanRuleFailed", rule, ErrorTranslator.Describe(ex)));
                }
            }

            if (allGone)
            {
                SessionJournal.Delete(record.SessionId);
                ActivityLog.Ok(Loc.T("Log_OrphanCleaned", record.DisplayName, record.Rules.Count));
            }
        }
        return removed;
    }

    private static bool IsExeRunning(string exePath)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(exePath);
            foreach (Process p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    string? path = NativeMethods.TryGetProcessImagePath(p.Id);
                    if (path is not null && string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { /* enumeration impossible : on considere que non */ }
        return false;
    }
}
