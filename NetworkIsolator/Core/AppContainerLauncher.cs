using System.IO;
using System.Diagnostics;
using System.Text;
using NetworkIsolator.Models;
using NetworkIsolator.Localization;

namespace NetworkIsolator.Core;

/// <summary>
/// A session = one application start behind targeted firewall rules.
/// (The class name is historical; there is no AppContainer any more: everything goes through the firewall.)
///
/// Flow: validation → journal (written FIRST) → firewall rules → start the app (paused)
/// → add to the job object → resume → watch the process tree → remove the rules.
/// The rules exist BEFORE the application's first instruction: nothing leaks at start-up.
/// </summary>
public sealed class AppContainerLauncher : IDisposable
{
    private const int PollIntervalMs = 300;

    private readonly LaunchProfile _profile;
    private readonly FirewallManager _firewall = new();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _coveredPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private SessionRecord _record = new();
    private JobObject? _job;
    private SpawnedProcess? _root;
    private volatile bool _jobUsable;
    private volatile IsolationState _state = IsolationState.Created;
    private int _processCount;
    private int _ruleCount;
    private int _ruleSeq;
    private int _cleanedUp;
    private int _disposed;

    public AppContainerLauncher(LaunchProfile profile)
    {
        _profile = profile.Clone();
        ExePath = _profile.ExePath;
        DisplayName = string.IsNullOrWhiteSpace(_profile.Name)
            ? Path.GetFileNameWithoutExtension(_profile.ExePath)
            : _profile.Name;
    }

    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..8];
    public string DisplayName { get; }
    public string ExePath { get; private set; }
    public LaunchProfile Profile => _profile;
    public int RootPid { get; private set; }
    public DateTime StartedAt { get; private set; } = DateTime.Now;
    public bool RunsAsNormalUser { get; private set; }
    public IsolationState State => _state;
    public int ProcessCount => _processCount;
    public int RuleCount => _ruleCount;

    /// <summary>Completes when the process tree is closed AND the rules are removed.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Raised on every change (state, process count, rules). Any thread.</summary>
    public event EventHandler? Changed;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_state != IsolationState.Created)
            throw new InvalidOperationException(Loc.T("Err_SessionStarted"));

        SetState(IsolationState.Starting);
        try
        {
            Validate();

            string workDir = !string.IsNullOrWhiteSpace(_profile.WorkingDirectory) && Directory.Exists(_profile.WorkingDirectory)
                ? _profile.WorkingDirectory
                : Path.GetDirectoryName(ExePath)!;

            // A rule on a disabled profile does nothing: better to say so clearly.
            var disabled = FirewallManager.GetDisabledProfiles();
            if (disabled.Count >= 3)
                throw new IsolationException(IsolationErrorKind.FirewallUnavailable,
                    Loc.T("Err_FirewallAllOff"));
            if (disabled.Count > 0)
                Log(LogLevel.Warn, Loc.T("Log_FirewallProfileOff", string.Join(", ", disabled)));

            // 1. Journal first (write-ahead): we know what to clean up even after a crash.
            using (var self = Process.GetCurrentProcess())
            {
                lock (_sync)
                {
                    _record = new SessionRecord
                    {
                        SessionId = SessionId,
                        DisplayName = DisplayName,
                        ExePath = ExePath,
                        OwnerPid = Environment.ProcessId,
                        OwnerStartUtc = self.StartTime.ToUniversalTime(),
                        Mode = _profile.Mode,
                        CreatedUtc = DateTime.UtcNow
                    };
                    SessionJournal.Save(_record);
                }
            }

            // 2. Firewall rules BEFORE starting the application.
            await AddRulesForAsync(ExePath, ct).ConfigureAwait(false);
            Log(LogLevel.Ok, Loc.T(_profile.Mode == IsolationMode.AllowLan ? "Log_RulesActiveLan" : "Log_RulesActiveAll", _ruleCount));

            // 3. Start (paused if possible), then attach to the job object.
            _job = JobObject.TryCreate();
            _root = await Task.Run(
                () => ProcessSpawner.Spawn(_profile, ExePath, workDir, m => Log(LogLevel.Warn, m)), ct)
                .ConfigureAwait(false);

            RootPid = _root.ProcessId;
            RunsAsNormalUser = _root.IsDeElevated;

            _jobUsable = _job is not null && _job.Assign(_root.ProcessHandle);
            if (!_jobUsable)
                Log(LogLevel.Warn, Loc.T("Log_ChildTrackingOff"));

            lock (_sync)
            {
                _record.RootPid = RootPid;
                SessionJournal.Save(_record);
            }

            _root.Resume(); // no-op if the application was started by Process.Start

            StartedAt = DateTime.Now;
            SessionRegistry.Register(this);
            SetState(IsolationState.Isolated);
            Log(LogLevel.Ok, Loc.T("Log_AppStarted", RootPid, _root.Method));

            _ = Task.Run(MonitorAsync, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await AbortStartAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await AbortStartAsync().ConfigureAwait(false);
            string message = ErrorTranslator.Describe(ex);
            Log(LogLevel.Error, Loc.T("Log_LaunchFailed", message));

            if (ex is IsolationException) throw;
            throw new IsolationException(IsolationErrorKind.LaunchFailed, message, ex);
        }
    }

    private void Validate()
    {
        if (!ElevationHelper.IsElevated())
            throw new IsolationException(IsolationErrorKind.ElevationRequired,
                Loc.T("Err_NeedAdminRules"));

        if (string.IsNullOrWhiteSpace(_profile.ExePath))
            throw new IsolationException(IsolationErrorKind.InvalidExecutable, Loc.T("Err_NoExe"));

        string full;
        try { full = Path.GetFullPath(_profile.ExePath.Trim().Trim('"')); }
        catch (Exception ex)
        {
            throw new IsolationException(IsolationErrorKind.InvalidExecutable,
                Loc.T("Err_InvalidPath", _profile.ExePath), ex);
        }

        if (!File.Exists(full))
            throw new IsolationException(IsolationErrorKind.InvalidExecutable, Loc.T("Err_FileNotFound", full));

        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IsolationException(IsolationErrorKind.InvalidExecutable,
                Loc.T("Err_OnlyExe"));

        ExePath = full;
    }

    private async Task AbortStartAsync()
    {
        try { if (_jobUsable) _job?.Terminate(); } catch { }
        try { _root?.Kill(); } catch { }
        await CleanupAsync().ConfigureAwait(false);
        SetState(IsolationState.Failed);
        _completion.TrySetResult();
    }

    /// <summary>Creates the rules (outbound, and inbound if requested) for an exe, once per path.</summary>
    private async Task AddRulesForAsync(string path, CancellationToken ct)
    {
        lock (_sync)
        {
            if (!_coveredPaths.Add(path)) return;
        }

        string baseName = $"{FirewallManager.RulePrefix}{SessionId}_{Interlocked.Increment(ref _ruleSeq)}_{FirewallManager.RuleLabel(path)}";
        string? remote = _profile.Mode == IsolationMode.AllowLan ? FirewallManager.PublicAddressRanges : null;

        await AddOneRuleAsync(baseName + "_out", path, inbound: false, remote, ct).ConfigureAwait(false);
        if (_profile.BlockInbound)
            await AddOneRuleAsync(baseName + "_in", path, inbound: true, remote, ct).ConfigureAwait(false);
    }

    private async Task AddOneRuleAsync(string ruleName, string path, bool inbound, string? remote, CancellationToken ct)
    {
        // The name is written to the journal BEFORE calling netsh.
        lock (_sync)
        {
            _record.Rules.Add(ruleName);
            SessionJournal.Save(_record);
        }

        await _firewall.AddBlockRuleAsync(ruleName, path, inbound, remote, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _ruleCount);
        RaiseChanged();
    }

    private async Task MonitorAsync()
    {
        var seen = new HashSet<int>();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    int[] pids = GetLivePids();
                    bool changed = pids.Length != _processCount;
                    _processCount = pids.Length;

                    if (pids.Length == 0) break; // no process left: the application is closed

                    foreach (int pid in pids)
                        if (seen.Add(pid))
                            await OnProcessDetectedAsync(pid).ConfigureAwait(false);

                    if (changed) RaiseChanged();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A one-off error must not stop the watching: otherwise the rules
                    // would be removed while the application is still running.
                    Log(LogLevel.Warn, "Surveillance : " + ErrorTranslator.Describe(ex));
                }

                await Task.Delay(PollIntervalMs, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* arret demande */ }
        finally
        {
            await FinishAsync().ConfigureAwait(false);
        }
    }

    private int[] GetLivePids()
    {
        if (_jobUsable && _job is not null && _job.TryGetProcessIds(out int[] ids))
            return ids;

        return _root is { HasExited: false } ? [RootPid] : [];
    }

    /// <summary>A new process appears in the job: extend the block to its exe.</summary>
    private async Task OnProcessDetectedAsync(int pid)
    {
        if (pid == RootPid || !_profile.TrackChildren) return;

        string? path = NativeMethods.TryGetProcessImagePath(pid);
        if (path is null) return;

        string name = Path.GetFileName(path);

        // Windows binaries (cmd, conhost, WerFault...) are shared with the whole system:
        // a rule on their path would also block other programs.
        if (path.StartsWith(_windowsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Log(LogLevel.Info, Loc.T("Log_ChildSystemIgnored", name, pid));
            return;
        }

        Log(LogLevel.Info, Loc.T("Log_ChildDetected", name, pid));
        try
        {
            await AddRulesForAsync(path, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(LogLevel.Warn, Loc.T("Log_ChildRuleFailed", name, ErrorTranslator.Describe(ex)));
        }
    }

    private async Task FinishAsync()
    {
        SetState(IsolationState.Stopping);

        double seconds = (DateTime.Now - StartedAt).TotalSeconds;
        if (seconds < 3)
            Log(LogLevel.Warn, Loc.T("Log_QuickClose"));

        await CleanupAsync().ConfigureAwait(false);

        _processCount = 0;
        SessionRegistry.Unregister(this);
        SetState(IsolationState.Stopped);
        _completion.TrySetResult();
    }

    /// <summary>Closes the application and all its processes. Rule cleanup follows automatically.</summary>
    public void Kill()
    {
        if (_state is not (IsolationState.Starting or IsolationState.Isolated)) return;

        Log(LogLevel.Warn, Loc.T("Log_StopRequested"));
        try
        {
            if (_jobUsable) _job?.Terminate();
            else _root?.Kill();
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warn, Loc.T("Log_StopError", ex.Message));
        }
    }

    /// <summary>Removes all rules of the session (idempotent), then releases the handles.</summary>
    private async Task CleanupAsync()
    {
        if (Interlocked.Exchange(ref _cleanedUp, 1) == 1) return;

        string[] rules;
        lock (_sync) rules = [.. _record.Rules];

        int removed = 0, failed = 0;
        foreach (string rule in rules)
        {
            try
            {
                if (await _firewall.RemoveRuleAsync(rule).ConfigureAwait(false)) removed++;
                else failed++;
            }
            catch (Exception ex)
            {
                failed++;
                Log(LogLevel.Warn, Loc.T("Log_RuleDeleteFailed", rule, ErrorTranslator.Describe(ex)));
            }
        }

        if (failed == 0)
        {
            if (_record.SessionId.Length > 0) SessionJournal.Delete(_record.SessionId);
            if (rules.Length > 0)
                Log(LogLevel.Ok, Loc.T("Log_SessionClosed", removed));
        }
        else
        {
            Log(LogLevel.Warn, Loc.T("Log_RulesLeft", failed));
        }

        try { _root?.Dispose(); } catch { }
        try { _job?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        if (_state == IsolationState.Created)
        {
            // Never started: nothing to stop, only a possible journal to clean up.
            _completion.TrySetResult();
        }
        else
        {
            Kill();
            _cts.Cancel();
            try { _completion.Task.Wait(TimeSpan.FromSeconds(20)); } catch { }
        }

        // Safety net if the watcher has not finished (Task.Run: does not block the UI thread).
        try { Task.Run(CleanupAsync).Wait(TimeSpan.FromSeconds(20)); } catch { }

        SessionRegistry.Unregister(this);
        _cts.Dispose();
    }

    private void SetState(IsolationState state)
    {
        _state = state;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); } catch { /* l'UI ne doit pas casser l'isolation */ }
    }

    private void Log(LogLevel level, string message)
    {
        string text = $"[{DisplayName} #{SessionId}] {message}";
        switch (level)
        {
            case LogLevel.Ok: ActivityLog.Ok(text); break;
            case LogLevel.Warn: ActivityLog.Warn(text); break;
            case LogLevel.Error: ActivityLog.Error(text); break;
            default: ActivityLog.Info(text); break;
        }
    }
}
