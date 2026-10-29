using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Win32;
using NetworkIsolator.Models;
using NetworkIsolator.Localization;

namespace NetworkIsolator.Core;

/// <summary>
/// Creation / removal / verification of Windows Firewall rules through netsh.
/// Every rule is a BLOCK rule targeting an exe's path,
/// named "NetIsolator_..."; temporary ones are removed as soon as the application closes.
/// </summary>
public sealed class FirewallManager
{
    public const string RulePrefix = "NetIsolator_";

    /// <summary>
    /// "LAN allowed" mode: "block everything + allow the LAN" is impossible (a block always
    /// wins over an allow). So we block the opposite: all PUBLIC addresses.
    /// Stays allowed: 10/8, 172.16/12, 192.168/16, 169.254/16 (link-local), 127/8 (loopback), 224/4 (multicast),
    /// and in IPv6 everything that is not 2000::/3 (fe80::/10, fc00::/7, ::1, ff00::/8).
    /// </summary>
    public const string PublicAddressRanges =
        "0.0.0.0-9.255.255.255," +
        "11.0.0.0-126.255.255.255," +
        "128.0.0.0-169.253.255.255," +
        "169.255.0.0-172.15.255.255," +
        "172.32.0.0-192.167.255.255," +
        "192.169.0.0-223.255.255.255," +
        "2000::/3";

    // netsh dislikes simultaneous calls on the same rule store: calls are serialized.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(25);
    private static readonly Encoding OutputEncoding = ResolveOemEncoding();

    private readonly record struct NetshResult(int ExitCode, string Output);

    /// <summary>Creates a block rule for an exe, then verifies that it really exists.</summary>
    /// <param name="inbound">false = outgoing traffic, true = incoming traffic.</param>
    /// <param name="remoteAddresses">null = all addresses; otherwise a netsh list (LAN mode).</param>
    public async Task AddBlockRuleAsync(string name, string exePath, bool inbound, string? remoteAddresses, CancellationToken ct = default)
    {
        // Idempotent: if a rule with the same name is lying around, remove it first.
        await RunNetshAsync($"advfirewall firewall delete rule name=\"{name}\"", retries: 1, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.Append($"advfirewall firewall add rule name=\"{name}\"");
        sb.Append(inbound ? " dir=in" : " dir=out");
        sb.Append(" action=block");
        sb.Append($" program=\"{exePath}\"");   // quotes: handles spaces and accents
        sb.Append(" enable=yes profile=any");
        if (!string.IsNullOrEmpty(remoteAddresses))
            sb.Append($" remoteip={remoteAddresses}");

        NetshResult add = await RunNetshAsync(sb.ToString(), retries: 3, ct).ConfigureAwait(false);
        if (add.ExitCode != 0)
            throw new IsolationException(IsolationErrorKind.FirewallRuleFailed,
                Loc.T("Err_FwRefused", Shorten(add.Output)));

        if (!await RuleExistsAsync(name, ct).ConfigureAwait(false))
            throw new IsolationException(IsolationErrorKind.FirewallRuleFailed,
                Loc.T("Err_FwRuleMissing", name));
    }

    /// <summary>Removes a rule, then verifies. True = the rule no longer exists.</summary>
    public async Task<bool> RemoveRuleAsync(string name, CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await RunNetshAsync($"advfirewall firewall delete rule name=\"{name}\"", retries: 1, ct).ConfigureAwait(false);
            if (!await RuleExistsAsync(name, ct).ConfigureAwait(false)) return true;
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>True if a rule with this name exists (exit code 0 AND the name appears in the output).</summary>
    public async Task<bool> RuleExistsAsync(string name, CancellationToken ct = default)
    {
        NetshResult r = await RunNetshAsync($"advfirewall firewall show rule name=\"{name}\"", retries: 1, ct).ConfigureAwait(false);
        return r.ExitCode == 0 && r.Output.Contains(name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Disabled firewall profiles (Domain / Private / Public), read from the registry
    /// (independent of the Windows language). A rule has NO effect on a disabled profile.
    /// </summary>
    public static IReadOnlyList<string> GetDisabledProfiles()
    {
        var disabled = new List<string>();
        (string Key, string Label)[] profiles =
        [
            ("DomainProfile", Loc.T("Fw_Domain")),
            ("StandardProfile", Loc.T("Fw_Private")),
            ("PublicProfile", Loc.T("Fw_Public"))
        ];

        foreach (var (key, label) in profiles)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\" + key);
                if (k?.GetValue("EnableFirewall") is int enabled && enabled == 0)
                    disabled.Add(label);
            }
            catch
            {
                // read failed: say nothing rather than raise a false alarm
            }
        }
        return disabled;
    }

    /// <summary>Short ASCII label taken from the exe name, to recognize rules in wf.msc.</summary>
    public static string RuleLabel(string path)
    {
        var sb = new StringBuilder();
        foreach (char c in Path.GetFileNameWithoutExtension(path))
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            sb.Append(ok ? c : '_');
            if (sb.Length >= 24) break;
        }
        return sb.Length == 0 ? "app" : sb.ToString();
    }

    private static async Task<NetshResult> RunNetshAsync(string arguments, int retries, CancellationToken ct)
    {
        NetshResult last = default;

        for (int attempt = 1; attempt <= retries; attempt++)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                last = await RunNetshOnceAsync(arguments, ct).ConfigureAwait(false);
            }
            finally { Gate.Release(); }

            if (last.ExitCode == 0 || attempt == retries) break;
            await Task.Delay(300 * attempt, ct).ConfigureAwait(false);
        }
        return last;
    }

    private static async Task<NetshResult> RunNetshOnceAsync(string arguments, CancellationToken ct)
    {
        string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
        var psi = new ProcessStartInfo(netsh, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OutputEncoding,
            StandardErrorEncoding = OutputEncoding
        };

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException(Loc.T("Err_NetshNotStarted"));
        }
        catch (Exception ex)
        {
            throw new IsolationException(IsolationErrorKind.FirewallUnavailable,
                Loc.T("Err_NetshLaunch", ErrorTranslator.Describe(ex)), ex);
        }

        using (process)
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(NetshTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new IsolationException(IsolationErrorKind.FirewallUnavailable,
                    Loc.T("Err_NetshTimeout", (int)NetshTimeout.TotalSeconds));
            }

            string text = (await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false)).Trim();
            return new NetshResult(process.ExitCode, text);
        }
    }

    private static string Shorten(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 220 ? text : text[..220] + "…";
    }

    /// <summary>netsh writes in the console's OEM code page (850 in France), not UTF-8.</summary>
    private static Encoding ResolveOemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.Latin1;
        }
    }
}
