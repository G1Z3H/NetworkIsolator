using System.IO;
using System.Security.Cryptography;
using System.Text;
using NetworkIsolator.Models;
using NetworkIsolator.Localization;

namespace NetworkIsolator.Core;

/// <summary>
/// PERMANENT block: firewall rules that stay in place after the tool is closed.
/// Rule names are deterministic (derived from the exe path): so they can always be
/// found, checked or removed, without a tracking file.
/// Format: NetIsolator_P_{hash}_{name}_{out|in}
/// </summary>
public sealed class PermanentBlockManager
{
    private const string PermanentPrefix = FirewallManager.RulePrefix + "P_";

    private readonly FirewallManager _firewall = new();
    private readonly string _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>All exes concerned by a profile (main + related), without duplicates.</summary>
    public static IReadOnlyList<string> AllExePaths(LaunchProfile profile)
    {
        var result = new List<string>();
        foreach (string raw in new[] { profile.ExePath }.Concat(profile.ExtraExePaths))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full;
            try { full = Path.GetFullPath(raw.Trim().Trim('"')); }
            catch { continue; }
            if (!result.Contains(full, StringComparer.OrdinalIgnoreCase)) result.Add(full);
        }
        return result;
    }

    /// <summary>Rule name for an exe: always the same for the same path.</summary>
    public static string RuleName(string exePath, bool inbound)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(exePath.ToLowerInvariant()));
        string id = Convert.ToHexString(hash, 0, 4);
        return $"{PermanentPrefix}{id}_{FirewallManager.RuleLabel(exePath)}_{(inbound ? "in" : "out")}";
    }

    /// <summary>Creates (or recreates, it is idempotent) a profile's permanent rules.</summary>
    public async Task ApplyAsync(LaunchProfile profile, CancellationToken ct = default)
    {
        RequireAdmin();

        IReadOnlyList<string> paths = AllExePaths(profile);
        if (paths.Count == 0)
            throw new IsolationException(IsolationErrorKind.InvalidExecutable, Loc.T("Err_NothingToBlock"));

        foreach (string path in paths) EnsureAllowedTarget(path);

        if (FirewallManager.GetDisabledProfiles().Count >= 3)
            throw new IsolationException(IsolationErrorKind.FirewallUnavailable,
                Loc.T("Err_FwOffPermanent"));

        string? remote = profile.Mode == IsolationMode.AllowLan ? FirewallManager.PublicAddressRanges : null;
        int blocked = 0;

        foreach (string path in paths)
        {
            if (!File.Exists(path))
            {
                ActivityLog.Warn(Loc.T("Log_MissingSkipped", path));
                continue;
            }

            await _firewall.AddBlockRuleAsync(RuleName(path, inbound: false), path, inbound: false, remote, ct).ConfigureAwait(false);

            if (profile.BlockInbound)
                await _firewall.AddBlockRuleAsync(RuleName(path, inbound: true), path, inbound: true, remote, ct).ConfigureAwait(false);
            else
                await _firewall.RemoveRuleAsync(RuleName(path, inbound: true), ct).ConfigureAwait(false); // setting changed since the last block

            blocked++;
            ActivityLog.Ok(Loc.T("Log_BlockActive", Path.GetFileName(path)));
        }

        if (blocked == 0)
            throw new IsolationException(IsolationErrorKind.InvalidExecutable,
                Loc.T("Err_NoneExist"));
    }

    /// <summary>Removes a profile's permanent rules. True if none is left.</summary>
    public async Task<bool> RemoveAsync(LaunchProfile profile, CancellationToken ct = default)
    {
        RequireAdmin();

        bool allRemoved = true;
        foreach (string path in AllExePaths(profile))
        {
            bool outRemoved = await _firewall.RemoveRuleAsync(RuleName(path, inbound: false), ct).ConfigureAwait(false);
            bool inRemoved = await _firewall.RemoveRuleAsync(RuleName(path, inbound: true), ct).ConfigureAwait(false);
            if (!outRemoved || !inRemoved) allRemoved = false;
            else ActivityLog.Ok(Loc.T("Log_BlockRemoved", Path.GetFileName(path)));
        }
        return allRemoved;
    }

    /// <summary>Reads the real state in the firewall (does not need admin rights).</summary>
    public async Task<BlockState> CheckAsync(LaunchProfile profile, CancellationToken ct = default)
    {
        int total = 0, blocked = 0;

        foreach (string path in AllExePaths(profile))
        {
            bool ruleExists = await _firewall.RuleExistsAsync(RuleName(path, inbound: false), ct).ConfigureAwait(false);

            // An uninstalled program with no rule does not count (otherwise it would be "partial" forever).
            if (!ruleExists && !File.Exists(path)) continue;

            total++;
            if (ruleExists) blocked++;
        }

        if (total == 0 || blocked == 0) return BlockState.Free;
        return blocked == total ? BlockState.Blocked : BlockState.Partial;
    }

    private static void RequireAdmin()
    {
        if (!ElevationHelper.IsElevated())
            throw new IsolationException(IsolationErrorKind.ElevationRequired,
                Loc.T("Err_NeedAdminFirewall"));
    }

    /// <summary>Refuses Windows programs: blocking them can cut the Internet or break system features.</summary>
    private void EnsureAllowedTarget(string path)
    {
        if (path.StartsWith(_windowsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IsolationException(IsolationErrorKind.InvalidExecutable,
                Loc.T("Err_WindowsProgram", Path.GetFileName(path)));
    }
}
