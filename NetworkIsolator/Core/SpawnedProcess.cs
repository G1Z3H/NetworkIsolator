using System.Diagnostics;

namespace NetworkIsolator.Core;

/// <summary>
/// Root process started by the tool. Owns its native handles.
/// Two possible origins: user token (started suspended) or a plain Process.Start.
/// </summary>
internal sealed class SpawnedProcess : IDisposable
{
    private readonly Process? _managed;
    private IntPtr _processHandle;
    private IntPtr _threadHandle;
    private bool _disposed;

    public int ProcessId { get; }

    /// <summary>Text for the log: how the application was started.</summary>
    public string Method { get; }

    /// <summary>True if the application runs with normal (non-admin) user rights.</summary>
    public bool IsDeElevated { get; }

    /// <summary>Process handle (valid as long as the object is not disposed).</summary>
    public IntPtr ProcessHandle => _managed is not null
        ? _managed.SafeHandle.DangerousGetHandle()
        : _processHandle;

    /// <summary>Constructor for a process created through CreateProcessWithTokenW (suspended).</summary>
    public SpawnedProcess(IntPtr processHandle, IntPtr threadHandle, int pid, string method)
    {
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        ProcessId = pid;
        Method = method;
        IsDeElevated = true;
    }

    /// <summary>Constructor for a process created through Process.Start.</summary>
    public SpawnedProcess(Process process, string method)
    {
        _managed = process;
        ProcessId = process.Id;
        Method = method;
        IsDeElevated = false;
    }

    /// <summary>Resumes the main thread (no-op for Process.Start, already running).</summary>
    public void Resume()
    {
        if (_threadHandle == IntPtr.Zero) return;
        NativeMethods.ResumeThread(_threadHandle);
        NativeMethods.CloseHandle(_threadHandle);
        _threadHandle = IntPtr.Zero;
    }

    public bool HasExited
    {
        get
        {
            if (_disposed) return true;
            if (_managed is not null)
            {
                try { return _managed.HasExited; } catch { return true; }
            }
            return NativeMethods.WaitForSingleObject(_processHandle, 0) == NativeMethods.WAIT_OBJECT_0;
        }
    }

    /// <summary>Kills the root process and its descendants (fallback when the job object is unavailable).</summary>
    public void Kill()
    {
        try
        {
            using var p = Process.GetProcessById(ProcessId);
            p.Kill(entireProcessTree: true);
        }
        catch
        {
            // already ended
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _managed?.Dispose();
        if (_threadHandle != IntPtr.Zero) NativeMethods.CloseHandle(_threadHandle);
        if (_processHandle != IntPtr.Zero) NativeMethods.CloseHandle(_processHandle);
        _threadHandle = IntPtr.Zero;
        _processHandle = IntPtr.Zero;
    }
}
