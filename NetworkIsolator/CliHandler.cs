using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using NetworkIsolator.Core;
using NetworkIsolator.Models;
using NetworkIsolator.Localization;

namespace NetworkIsolator;

/// <summary>
/// Command-line mode, designed for Windows shortcuts:
/// every error is reported (console or message box) and returned as an exit code.
/// </summary>
public static class CliHandler
{
    // Exit codes
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitUsage = 2;
    private const int ExitNotFound = 3;
    private const int ExitElevationRefused = 4;
    private const int ExitFirewall = 5;
    private const int ExitLaunchFailed = 6;

    public static async Task<int> ExecuteAsync(string[] rawArgs, CliOptions options)
    {
        if (options.Error is not null)
        {
            CliOutput.Error(options.Error + Environment.NewLine + Environment.NewLine + UsageText());
            return ExitUsage;
        }

        try
        {
            return options.Command switch
            {
                CliCommand.Help => ShowHelp(),
                CliCommand.Version => ShowVersion(),
                CliCommand.List => ShowProfiles(),
                CliCommand.Export => await ExportAsync(options),
                CliCommand.Import => await ImportAsync(options),
                CliCommand.Shortcut => CreateShortcut(options),
                CliCommand.Cleanup => await CleanupAsync(rawArgs),
                CliCommand.Block => await SetPermanentBlockAsync(rawArgs, options, block: true),
                CliCommand.Unblock => await SetPermanentBlockAsync(rawArgs, options, block: false),
                CliCommand.Profile => await LaunchFromProfileAsync(rawArgs, options),
                CliCommand.Launch => await LaunchFromPathAsync(rawArgs, options),
                _ => ExitUsage
            };
        }
        catch (IsolationException ex)
        {
            CliOutput.Error(ex.Message);
            return ExitCodeFor(ex.Kind);
        }
        catch (Exception ex)
        {
            ActivityLog.Error("CLI: " + ex);
            CliOutput.Error(Loc.T("Cli_UnexpectedError", ErrorTranslator.Describe(ex)));
            return ExitError;
        }
    }

    private static Task<int> LaunchFromPathAsync(string[] rawArgs, CliOptions o)
    {
        string path;
        try { path = Path.GetFullPath(o.Target ?? ""); }
        catch
        {
            CliOutput.Error(Loc.T("Cli_InvalidPath", o.Target ?? ""));
            return Task.FromResult(ExitNotFound);
        }

        if (!File.Exists(path))
        {
            CliOutput.Error(Loc.T("Cli_FileNotFound", path));
            return Task.FromResult(ExitNotFound);
        }

        var profile = new LaunchProfile
        {
            Name = Path.GetFileNameWithoutExtension(path),
            ExePath = path
        };
        ApplyOverrides(profile, o);
        return RunIsolatedAsync(rawArgs, o, profile);
    }

    private static Task<int> LaunchFromProfileAsync(string[] rawArgs, CliOptions o)
    {
        LaunchProfile? found = new ProfileManager().FindProfile(o.Target!);
        if (found is null)
        {
            CliOutput.Error(Loc.T("Cli_ProfileNotFound", o.Target!));
            return Task.FromResult(ExitNotFound);
        }

        LaunchProfile profile = found.Clone();
        if (!File.Exists(profile.ExePath))
        {
            CliOutput.Error(Loc.T("Cli_ProfileExeMissing", profile.ExePath));
            return Task.FromResult(ExitNotFound);
        }

        // Extra command-line arguments: added to the profile's.
        string extra = o.AppArguments;
        o.AppArguments = string.Join(" ", new[] { profile.Arguments, extra }.Where(s => !string.IsNullOrWhiteSpace(s)));

        ApplyOverrides(profile, o);
        return RunIsolatedAsync(rawArgs, o, profile);
    }

    /// <summary>Command-line options win over the profile.</summary>
    private static void ApplyOverrides(LaunchProfile p, CliOptions o)
    {
        p.Arguments = o.AppArguments;
        if (o.WorkingDirectory is not null) p.WorkingDirectory = Path.GetFullPath(o.WorkingDirectory);
        if (o.Mode is { } mode) p.Mode = mode;
        if (o.BlockInbound is { } bi) p.BlockInbound = bi;
        if (o.EnvBlock is { } eb) p.EnableEnvBlock = eb;
        if (o.TrackChildren is { } tc) p.TrackChildren = tc;
        if (o.RunAsNormalUser is { } rn) p.RunAsNormalUser = rn;
    }

    private static async Task<int> RunIsolatedAsync(string[] rawArgs, CliOptions o, LaunchProfile profile)
    {
        // 1. Elevation: a single UAC prompt, then the elevated copy does all the work.
        int? relaunched = await RelaunchIfNotElevatedAsync(rawArgs, o.Wait, Loc.T("Cli_ReasonLaunch"));
        if (relaunched is { } exitCode) return exitCode;

        // 2. Live feedback if a terminal is present.
        if (CliOutput.HasConsole)
        {
            ActivityLog.EntryAdded += e =>
            {
                if (e.Level != LogLevel.Error) CliOutput.Info(e.Message);
            };
        }

        // 3. Clean up any orphaned rules from a previous session.
        int swept = await OrphanCleaner.SweepAsync();
        if (swept > 0) ActivityLog.Ok(Loc.T("Cli_SweptRules", swept));

        // 4. Start the application, then wait for the process tree to end.
        using var launcher = new AppContainerLauncher(profile);
        await launcher.StartAsync();

        if (CliOutput.HasConsole)
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;      // let the cleanup finish properly
                launcher.Kill();
            };
            CliOutput.Info(Loc.T("Cli_Waiting"));
        }

        await launcher.Completion;
        return ExitOk;
    }

    private static int ExitCodeFor(IsolationErrorKind kind) => kind switch
    {
        IsolationErrorKind.ElevationRequired => ExitElevationRefused,
        IsolationErrorKind.InvalidExecutable => ExitNotFound,
        IsolationErrorKind.FirewallUnavailable or IsolationErrorKind.FirewallRuleFailed => ExitFirewall,
        IsolationErrorKind.LaunchFailed => ExitLaunchFailed,
        _ => ExitError
    };

    private static int ShowProfiles()
    {
        var profiles = new ProfileManager().Profiles;
        if (profiles.Count == 0)
        {
            CliOutput.Show(Loc.T("Cli_ProfilesTitle"), Loc.T("Cli_NoProfile"));
            return ExitOk;
        }

        var sb = new StringBuilder(Loc.T("Cli_ProfilesHeader")).AppendLine();
        foreach (LaunchProfile p in profiles)
            sb.AppendLine($"  {p.Name,-24} {Loc.T(p.Mode == IsolationMode.AllowLan ? "Cli_ModeLan" : "Cli_ModeAll")}  {p.ExePath}");
        CliOutput.Show(Loc.T("Cli_ProfilesTitle"), sb.ToString().TrimEnd());
        return ExitOk;
    }

    private static async Task<int> ExportAsync(CliOptions o)
    {
        string file = Path.GetFullPath(o.Target!);
        await new ProfileManager().ExportAsync(file);
        CliOutput.Info(Loc.T("Cli_Exported", file));
        return ExitOk;
    }

    private static async Task<int> ImportAsync(CliOptions o)
    {
        string file = Path.GetFullPath(o.Target!);
        if (!File.Exists(file))
        {
            CliOutput.Error(Loc.T("Cli_FileNotFound", file));
            return ExitNotFound;
        }

        var (added, updated) = await new ProfileManager().ImportAsync(file);
        CliOutput.Info(Loc.T("Cli_Imported", added, updated));
        return ExitOk;
    }

    private static int CreateShortcut(CliOptions o)
    {
        LaunchProfile? p = new ProfileManager().FindProfile(o.Target!);
        if (p is null)
        {
            CliOutput.Error(Loc.T("Cli_ProfileNotFound", o.Target!));
            return ExitNotFound;
        }

        string link = ShortcutCreator.CreateOnDesktop(p);
        CliOutput.Info(Loc.T("Cli_ShortcutCreated", link));
        return ExitOk;
    }

    private static async Task<int> CleanupAsync(string[] rawArgs)
    {
        int? relaunched = await RelaunchIfNotElevatedAsync(rawArgs, wait: false, Loc.T("Cli_ReasonCleanup"));
        if (relaunched is { } exitCode) return exitCode;

        int removed = await OrphanCleaner.SweepAsync();
        string message = Loc.T("Cli_CleanupDone", removed);
        ActivityLog.Ok(message);
        CliOutput.Show("NetworkIsolator", message);
        return ExitOk;
    }

    /// <summary>--block / --unblock: applies or removes a profile's permanent block.</summary>
    private static async Task<int> SetPermanentBlockAsync(string[] rawArgs, CliOptions o, bool block)
    {
        LaunchProfile? profile = new ProfileManager().FindProfile(o.Target!);
        if (profile is null)
        {
            CliOutput.Error(Loc.T("Cli_ProfileNotFound", o.Target!));
            return ExitNotFound;
        }

        int? relaunched = await RelaunchIfNotElevatedAsync(rawArgs, o.Wait, Loc.T("Cli_ReasonFirewall"));
        if (relaunched is { } exitCode) return exitCode;

        var manager = new PermanentBlockManager();
        if (block)
        {
            await manager.ApplyAsync(profile);
            string done = Loc.T("Cli_Blocked", profile.Name);
            ActivityLog.Ok(done);
            CliOutput.Show("NetworkIsolator", done);
            return ExitOk;
        }

        bool removed = await manager.RemoveAsync(profile);
        string text = removed
            ? Loc.T("Cli_Unblocked", profile.Name)
            : Loc.T("Cli_UnblockPartial", profile.Name);
        if (removed) ActivityLog.Ok(text); else ActivityLog.Warn(text);
        CliOutput.Show("NetworkIsolator", text);
        return removed ? ExitOk : ExitFirewall;
    }

    /// <summary>
    /// If the tool is not elevated: restarts an elevated copy (UAC prompt) and returns the exit code to use.
    /// Returns null when already administrator (the caller then continues normally).
    /// </summary>
    private static async Task<int?> RelaunchIfNotElevatedAsync(string[] rawArgs, bool wait, string reason)
    {
        if (ElevationHelper.IsElevated()) return null;

        CliOutput.Info(Loc.T("Cli_ElevationNeeded"));
        Process? elevated = ElevationHelper.RelaunchElevated(rawArgs);
        if (elevated is null)
        {
            CliOutput.Error(Loc.T("Cli_ElevationRefused", reason));
            return ExitElevationRefused;
        }

        using (elevated)
        {
            if (!wait) return ExitOk;
            try
            {
                await elevated.WaitForExitAsync();
                return elevated.ExitCode;
            }
            catch { return ExitOk; }
        }
    }

    private static int ShowVersion()
    {
        string v = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        CliOutput.Show("NetworkIsolator", "NetworkIsolator " + v);
        return ExitOk;
    }

    private static int ShowHelp()
    {
        CliOutput.Show(Loc.T("Cli_HelpTitle"), UsageText());
        return ExitOk;
    }

    private static string UsageText() => Loc.T("Cli_Usage").Replace("\n", Environment.NewLine);
}
