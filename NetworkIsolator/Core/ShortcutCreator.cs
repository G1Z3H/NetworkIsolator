using System.IO;
using System.Runtime.InteropServices;
using NetworkIsolator.Models;
using NetworkIsolator.Localization;

namespace NetworkIsolator.Core;

/// <summary>
/// Creates a Windows shortcut (.lnk) that starts a profile offline with a double-click.
/// Uses WScript.Shell with late binding: no dependency to add.
/// </summary>
public static class ShortcutCreator
{
    /// <summary>Creates the shortcut on the Desktop and returns its path.</summary>
    public static string CreateOnDesktop(LaunchProfile profile)
    {
        string? self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self))
            throw new InvalidOperationException(Loc.T("Err_ToolPathUnknown"));

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string linkPath = Path.Combine(desktop, MakeFileSafe(profile.Name) + " " + Loc.T("Shortcut_Suffix") + ".lnk");

        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException(Loc.T("Err_WScriptMissing"));

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(linkPath);
        try
        {
            link.TargetPath = self;
            link.Arguments = "--profile " + CommandLineBuilder.Quote(profile.Name);
            link.WorkingDirectory = Path.GetDirectoryName(profile.ExePath) ?? "";
            link.IconLocation = profile.ExePath + ",0";   // same icon as the application
            link.Description = Loc.T("Shortcut_Description", profile.Name);
            link.WindowStyle = 7;                          // minimized: no flash on screen
            link.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }

        MarkRunAsAdministrator(linkPath);
        return linkPath;
    }

    /// <summary>
    /// Ticks the shortcut's "Run as administrator" box (RunAsUser bit of the LinkFlags,
    /// byte 0x15 of the .lnk header): a single UAC prompt, with no intermediate process.
    /// </summary>
    private static void MarkRunAsAdministrator(string linkPath)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(linkPath);
            if (bytes.Length > 0x15)
            {
                bytes[0x15] |= 0x20;
                File.WriteAllBytes(linkPath, bytes);
            }
        }
        catch
        {
            // Not a problem: the tool elevates itself when it starts.
        }
    }

    private static string MakeFileSafe(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Application" : name.Trim();
    }
}
