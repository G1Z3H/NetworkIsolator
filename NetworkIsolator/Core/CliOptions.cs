using NetworkIsolator.Models;
using NetworkIsolator.Localization;

namespace NetworkIsolator.Core;

/// <summary>Result of parsing the command line.</summary>
public sealed class CliOptions
{
    public CliCommand Command { get; set; } = CliCommand.Gui;

    /// <summary>Path of the exe (Launch / Gui), profile name (Profile / Shortcut) or file (Export / Import).</summary>
    public string? Target { get; set; }

    /// <summary>Arguments to pass to the application (already in command-line format).</summary>
    public string AppArguments { get; set; } = "";

    public string? WorkingDirectory { get; set; }

    // null = "leave alone" (keep the profile's value or the default)
    public IsolationMode? Mode { get; set; }
    public bool? BlockInbound { get; set; }
    public bool? EnvBlock { get; set; }
    public bool? TrackChildren { get; set; }
    public bool? RunAsNormalUser { get; set; }

    /// <summary>Wait for the application to exit before returning (for .bat scripts).</summary>
    public bool Wait { get; set; }

    /// <summary>Syntax error message, or null if all is well.</summary>
    public string? Error { get; set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        var appArgs = new List<string>();
        bool rawAppArgsSet = false;

        string? Next(ref int i, string option)
        {
            if (i + 1 < args.Length) return args[++i];
            o.Error = Loc.T("Err_OptionNeedsValue", option);
            return null;
        }

        for (int i = 0; i < args.Length && o.Error is null; i++)
        {
            string a = args[i];

            // "--": everything after it is passed to the application unchanged.
            if (a == "--")
            {
                appArgs.AddRange(args.Skip(i + 1));
                break;
            }

            switch (a.ToLowerInvariant())
            {
                case "--gui": o.Command = CliCommand.Gui; break;
                case "--help": case "-h": case "/?": case "-?": o.Command = CliCommand.Help; break;
                case "--version": o.Command = CliCommand.Version; break;
                case "--list": o.Command = CliCommand.List; break;
                case "--cleanup": o.Command = CliCommand.Cleanup; break;

                case "--profile": o.Command = CliCommand.Profile; o.Target = Next(ref i, a); break;
                case "--block": o.Command = CliCommand.Block; o.Target = Next(ref i, a); break;
                case "--unblock": o.Command = CliCommand.Unblock; o.Target = Next(ref i, a); break;
                case "--shortcut": o.Command = CliCommand.Shortcut; o.Target = Next(ref i, a); break;
                case "--export": o.Command = CliCommand.Export; o.Target = Next(ref i, a); break;
                case "--import": o.Command = CliCommand.Import; o.Target = Next(ref i, a); break;

                // Old internal syntax (--isolated <exe>) kept for old shortcuts.
                case "--isolated": o.Command = CliCommand.Launch; o.Target = Next(ref i, a); break;

                case "--args":
                    // Raw text already in command-line format (old syntax).
                    string? raw = Next(ref i, a);
                    if (raw is not null) { appArgs.Add(raw); rawAppArgsSet = true; }
                    break;

                case "--cwd": o.WorkingDirectory = Next(ref i, a); break;

                case "--lan": o.Mode = IsolationMode.AllowLan; break;
                case "--block-all": o.Mode = IsolationMode.BlockAll; break;
                case "--inbound": o.BlockInbound = true; break;
                case "--no-inbound": o.BlockInbound = false; break;
                case "--proxy-env": o.EnvBlock = true; break;
                case "--no-proxy-env": o.EnvBlock = false; break;
                case "--children": o.TrackChildren = true; break;
                case "--no-children": o.TrackChildren = false; break;
                case "--normal-user": o.RunAsNormalUser = true; break;
                case "--keep-admin": o.RunAsNormalUser = false; break;
                case "--wait": o.Wait = true; break;

                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        o.Error = Loc.T("Err_UnknownOption", a);
                    }
                    else if (o.Target is null && (o.Command is CliCommand.Gui or CliCommand.Launch))
                    {
                        // First free argument = the exe to block.
                        o.Command = CliCommand.Launch;
                        o.Target = a.Trim('"');
                    }
                    else
                    {
                        // The following ones are for the application (for example the file to open in MPC-HC).
                        appArgs.Add(a);
                    }
                    break;
            }
        }

        // --args already provides a full line; otherwise each argument is quoted.
        o.AppArguments = rawAppArgsSet && appArgs.Count == 1
            ? appArgs[0]
            : CommandLineBuilder.Join(appArgs);

        if (o.Error is null)
        {
            if (o.Command is CliCommand.Profile or CliCommand.Shortcut or CliCommand.Export or CliCommand.Import
                or CliCommand.Block or CliCommand.Unblock
                && string.IsNullOrWhiteSpace(o.Target))
                o.Error = Loc.T("Err_MissingValue");
        }

        return o;
    }
}
