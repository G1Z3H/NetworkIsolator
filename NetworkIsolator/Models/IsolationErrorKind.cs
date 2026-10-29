namespace NetworkIsolator.Models;

/// <summary>Kind of blocking error (also used to choose the command-line exit code).</summary>
public enum IsolationErrorKind
{
    Unknown = 0,
    ElevationRequired = 1,
    InvalidExecutable = 2,
    FirewallUnavailable = 3,
    FirewallRuleFailed = 4,
    LaunchFailed = 5
}
