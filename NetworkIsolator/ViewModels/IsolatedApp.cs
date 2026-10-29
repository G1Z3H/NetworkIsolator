using System.ComponentModel;
using System.Diagnostics;
using NetworkIsolator.Core;
using NetworkIsolator.Models;
using NetworkIsolator.Localization;

namespace NetworkIsolator.ViewModels;

/// <summary>
/// One row of the "Running" tab. Two origins:
///  - a session started from this window (we have the launcher);
///  - a session started elsewhere (shortcut / command line), discovered through the session journal.
/// </summary>
public sealed class IsolatedApp : INotifyPropertyChanged
{
    private readonly AppContainerLauncher? _launcher;
    private SessionRecord? _record;

    public IsolatedApp(AppContainerLauncher launcher) => _launcher = launcher;

    public IsolatedApp(SessionRecord record) => _record = record;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsExternal => _launcher is null;
    public string Key => _launcher?.SessionId ?? _record!.SessionId;
    public string Name => _launcher?.DisplayName ?? _record!.DisplayName;
    public string ExePath => _launcher?.ExePath ?? _record!.ExePath;
    public AppContainerLauncher? Launcher => _launcher;

    public IsolationState State => _launcher?.State ?? IsolationState.Isolated;

    public bool CanStop => State is IsolationState.Starting or IsolationState.Isolated;

    public string StateText => State switch
    {
        IsolationState.Starting => Loc.T("State_Starting"),
        IsolationState.Isolated => Loc.T("State_Running"),
        IsolationState.Stopping => Loc.T("State_Stopping"),
        IsolationState.Stopped => Loc.T("State_Stopped"),
        IsolationState.Failed => Loc.T("State_Failed"),
        _ => Loc.T("State_Ready")
    };

    public string Detail
    {
        get
        {
            if (_launcher is { } l)
            {
                string mode = Loc.T(l.Profile.Mode == IsolationMode.AllowLan ? "Run_Detail_Lan" : "Run_Detail_All");
                string who = l.RunsAsNormalUser ? "" : Loc.T("Run_AsAdmin");
                return Loc.T("Run_DetailOwn", l.RootPid, l.ProcessCount, l.RuleCount, mode, who);
            }

            SessionRecord r = _record!;
            string m = Loc.T(r.Mode == IsolationMode.AllowLan ? "Run_Detail_Lan" : "Run_Detail_All");
            return Loc.T("Run_DetailExternal", r.RootPid, r.Rules.Count, m);
        }
    }

    public string Elapsed
    {
        get
        {
            DateTime start = _launcher?.StartedAt ?? _record!.CreatedUtc.ToLocalTime();
            TimeSpan t = DateTime.Now - start;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }
    }

    /// <summary>Updates the data of an external session (the journal may have changed).</summary>
    public void UpdateRecord(SessionRecord record)
    {
        _record = record;
        Refresh();
    }

    /// <summary>Closes the application (and its children). The rules are removed by its watcher.</summary>
    public void Stop()
    {
        if (_launcher is not null)
        {
            _launcher.Kill();
            return;
        }

        // External session: kill the process tree, but only if the PID is still our exe.
        SessionRecord r = _record!;
        if (r.RootPid <= 0) return;
        try
        {
            string? path = NativeMethods.TryGetProcessImagePath(r.RootPid);
            if (path is null || !string.Equals(path, r.ExePath, StringComparison.OrdinalIgnoreCase)) return;

            using var p = Process.GetProcessById(r.RootPid);
            p.Kill(entireProcessTree: true);
            ActivityLog.Warn(Loc.T("Log_ExternalStop", r.DisplayName, r.SessionId));
        }
        catch (Exception ex)
        {
            ActivityLog.Warn(Loc.T("Log_StopFailed", ex.Message));
        }
    }

    /// <summary>Tells the interface that anything may have changed.</summary>
    public void Refresh()
    {
        foreach (string name in new[] { nameof(State), nameof(CanStop), nameof(StateText), nameof(Detail), nameof(Elapsed) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Notifies only the timer (called every second).</summary>
    public void RefreshElapsed() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Elapsed)));
}
