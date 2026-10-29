using System.IO;
namespace NetworkIsolator.Core;

/// <summary>All the tool's data paths, in one place.</summary>
public static class AppPaths
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NetworkIsolator");

    public static readonly string ProfilesFile = Path.Combine(DataDir, "profiles.json");
    public static readonly string LanguageFile = Path.Combine(DataDir, "language.txt");
    public static readonly string SessionsDir = Path.Combine(DataDir, "sessions");
    public static readonly string LogsDir = Path.Combine(DataDir, "logs");
    public static readonly string LogFile = Path.Combine(LogsDir, "networkisolator.log");
}
