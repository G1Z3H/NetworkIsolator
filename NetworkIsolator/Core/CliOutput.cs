using System.IO;
using System.Globalization;
using System.Text;
using System.Windows;
using Microsoft.Win32.SafeHandles;

namespace NetworkIsolator.Core;

/// <summary>
/// Command-line output. The tool is a windowed application (WinExe): without care, Console.WriteLine
/// prints nothing. So we attach to the parent terminal's console when there is one.
/// From a shortcut (no console), errors appear in a message box:
/// a failing shortcut is never silent.
/// </summary>
public static class CliOutput
{
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;

    private static TextWriter? _out;
    private static TextWriter? _err;

    /// <summary>True if a terminal (or a redirection) receives our output.</summary>
    public static bool HasConsole { get; private set; }

    public static void Initialize()
    {
        bool attached = NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        _out = OpenWriter(NativeMethods.STD_OUTPUT_HANDLE);
        _err = OpenWriter(NativeMethods.STD_ERROR_HANDLE);
        HasConsole = attached || (_out is not null && _out != TextWriter.Null);
    }

    public static void Info(string message)
    {
        if (HasConsole) _out?.WriteLine(ToConsoleText(message));
    }

    public static void Error(string message)
    {
        ActivityLog.Error(message);
        if (HasConsole) _err?.WriteLine("[X] " + ToConsoleText(message));
        else MessageBox.Show(message, "NetworkIsolator", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Shows a long text (help, list): console if possible, otherwise a message box.</summary>
    public static void Show(string title, string text)
    {
        if (HasConsole) _out?.WriteLine(ToConsoleText(text));
        else MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static TextWriter OpenWriter(int stdHandle)
    {
        try
        {
            IntPtr h = NativeMethods.GetStdHandle(stdHandle);
            bool owns = false;

            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                // No redirection: write directly to the attached console.
                h = NativeMethods.CreateFileW("CONOUT$", GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (h == new IntPtr(-1) || h == IntPtr.Zero) return TextWriter.Null;
                owns = true;
            }

            var stream = new FileStream(new SafeFileHandle(h, owns), FileAccess.Write, 1, false);
            return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            return TextWriter.Null;
        }
    }

    /// <summary>
    /// Windows consoles in cp850/cp437 display UTF-8 accents badly:
    /// they are removed for terminal output (the message box and the log keep the accents).
    /// </summary>
    private static string ToConsoleText(string text)
    {
        string decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch
            {
                '—' or '–' => '-',
                '’' => '\'',
                '«' or '»' => '"',
                '…' => '.',
                _ => c
            });
        }
        return sb.ToString();
    }
}
