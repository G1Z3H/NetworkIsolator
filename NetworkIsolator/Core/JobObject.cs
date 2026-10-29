using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetworkIsolator.Core;

/// <summary>
/// Windows job object: groups the application and ALL its descendants.
/// Used to (1) know when the process tree is completely closed,
/// (2) discover child processes to extend the block, (3) kill everything at once.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private const int MaxListedPids = 512;

    private readonly SafeFileHandle _handle; // SafeFileHandle simply calls CloseHandle

    private JobObject(SafeFileHandle handle) => _handle = handle;

    /// <summary>Creates a job, or returns null if Windows refuses.</summary>
    public static JobObject? TryCreate()
    {
        IntPtr raw = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (raw == IntPtr.Zero) return null;

        var job = new JobObject(new SafeFileHandle(raw, ownsHandle: true));
        job.AllowBreakaway();
        return job;
    }

    /// <summary>
    /// Allows processes that EXPLICITLY ask to leave the job (CREATE_BREAKAWAY_FROM_JOB).
    /// Without this, those requests fail (access denied) and some apps crash.
    /// Fails silently: it is only a compatibility setting.
    /// </summary>
    private void AllowBreakaway()
    {
        int size = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_BREAKAWAY_OK;
            Marshal.StructureToPtr(info, buffer, false);
            NativeMethods.SetInformationJobObject(
                _handle.DangerousGetHandle(), NativeMethods.JobObjectExtendedLimitInformation, buffer, size);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Adds a process to the job. False if Windows refuses (e.g. nested jobs not supported).</summary>
    public bool Assign(IntPtr processHandle) =>
        NativeMethods.AssignProcessToJobObject(_handle.DangerousGetHandle(), processHandle);

    /// <summary>
    /// Lists the live PIDs of the job. Returns false if the query fails
    /// (the caller must then fall back to the root process only).
    /// </summary>
    public bool TryGetProcessIds(out int[] pids)
    {
        // Native structure: DWORD assigned; DWORD inList; ULONG_PTR ids[]  (array at offset 8)
        int size = 8 + IntPtr.Size * MaxListedPids;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            bool ok = NativeMethods.QueryInformationJobObject(
                _handle.DangerousGetHandle(), NativeMethods.JobObjectBasicProcessIdList,
                buffer, size, out _);

            if (!ok && Marshal.GetLastWin32Error() != NativeMethods.ERROR_MORE_DATA)
            {
                pids = [];
                return false;
            }

            int inList = Marshal.ReadInt32(buffer, 4);
            var result = new int[inList];
            for (int i = 0; i < inList; i++)
                result[i] = (int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size);

            pids = result;
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Kills every process of the job (the application and its children).</summary>
    public void Terminate() =>
        NativeMethods.TerminateJobObject(_handle.DangerousGetHandle(), 1);

    public void Dispose() => _handle.Dispose();
}
