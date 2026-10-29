using System.Text.Json.Serialization;
using NetworkIsolator.Localization;

namespace NetworkIsolator.Models;

/// <summary>A saved profile: everything needed to block or start an application again with one click.</summary>
public class LaunchProfile
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string? WorkingDirectory { get; set; }

    /// <summary>Block everything, or allow the LAN and block only the Internet.</summary>
    public IsolationMode Mode { get; set; } = IsolationMode.BlockAll;

    /// <summary>Also creates an inbound block rule (overrides the "allow" rules added by installers).</summary>
    public bool BlockInbound { get; set; } = true;

    /// <summary>Injects a dead proxy (HTTP_PROXY...) into the application's environment.</summary>
    public bool EnableEnvBlock { get; set; } = true;

    /// <summary>Automatically extends the block to child processes started by the application.</summary>
    public bool TrackChildren { get; set; } = true;

    /// <summary>Starts the application WITHOUT admin rights (like a normal double-click) even if the tool is elevated.</summary>
    public bool RunAsNormalUser { get; set; } = true;

    /// <summary>
    /// Other programs blocked together with this one (for example a suite = main program + helpers).
    /// The permanent block applies to ExePath AND to each of these paths.
    /// </summary>
    public List<string> ExtraExePaths { get; set; } = [];

    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastLaunchedAt { get; set; }

    [JsonIgnore]
    public string Summary =>
        Loc.T(Mode == IsolationMode.AllowLan ? "Mode_Summary_Lan" : "Mode_Summary_All")
        + (BlockInbound ? Loc.T("Summary_Inbound") : "");

    /// <summary>Independent copy (the list of related programs is duplicated, not shared).</summary>
    public LaunchProfile Clone()
    {
        var copy = (LaunchProfile)MemberwiseClone();
        copy.ExtraExePaths = [.. ExtraExePaths];
        return copy;
    }
}
