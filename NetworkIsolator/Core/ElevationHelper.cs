using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using NetworkIsolator.Models;
using NetworkIsolator.Localization;

namespace NetworkIsolator.Core;

/// <summary>Administrator detection and restart of the tool through UAC.</summary>
public static class ElevationHelper
{
    private static bool? _isElevated;

    /// <summary>True if the current process runs with an elevated administrator token.</summary>
    public static bool IsElevated()
    {
        if (_isElevated is { } cached) return cached;

        using var identity = WindowsIdentity.GetCurrent();
        _isElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        return _isElevated.Value;
    }

    /// <summary>
    /// Restarts the tool itself (same arguments) with the "runas" verb.
    /// Returns the elevated process, or null if the user declined UAC.
    /// </summary>
    public static Process? RelaunchElevated(IEnumerable<string> args)
    {
        string? self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self))
            throw new IsolationException(IsolationErrorKind.ElevationRequired,
                Loc.T("Err_ElevationPath"));

        var psi = new ProcessStartInfo
        {
            FileName = self,
            Arguments = CommandLineBuilder.Join(args),
            UseShellExecute = true,           // required for the runas verb
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory // keeps relative paths working
        };

        try
        {
            return Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null; // "The operation was canceled by the user"
        }
    }
}
