namespace NetworkIsolator.Models;

/// <summary>One line of the activity log (shown in the Log tab).</summary>
public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}
