using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NetworkIsolator.Models;
using NetworkIsolator.Localization;

namespace NetworkIsolator.Core;

/// <summary>
/// Starts the target application. Two strategies, tried in order:
///  1) Desktop token (like a double-click in Explorer): the app does NOT inherit the tool's admin rights.
///     Essential for Steam/DRM, mapped network drives, drag and drop, etc.
///     The process starts SUSPENDED: it is put in the job object before its first instruction.
///  2) Plain Process.Start if strategy 1 fails.
/// </summary>
internal static class ProcessSpawner
{
    public static SpawnedProcess Spawn(LaunchProfile profile, string exePath, string workDir, Action<string> warn)
    {
        if (profile.RunAsNormalUser && ElevationHelper.IsElevated())
        {
            try
            {
                return SpawnWithDesktopToken(profile, exePath, workDir);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
            {
                warn(Loc.T("Warn_NeedsAdminApp"));
            }
            catch (Exception ex)
            {
                warn(Loc.T("Warn_NormalUserFailed", ErrorTranslator.Describe(ex)));
            }
        }

        return SpawnClassic(profile, exePath, workDir);
    }

    private static SpawnedProcess SpawnClassic(LaunchProfile profile, string exePath, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = profile.Arguments ?? "",
            WorkingDirectory = workDir,
            UseShellExecute = false // required to pass environment variables
        };

        if (profile.EnableEnvBlock)
            EnvironmentBlocker.Apply(psi.Environment);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException(Loc.T("Err_ProcessStartNull"));

        return new SpawnedProcess(process, "Process.Start");
    }

    private static SpawnedProcess SpawnWithDesktopToken(LaunchProfile profile, string exePath, string workDir)
    {
        IntPtr token = IntPtr.Zero;
        IntPtr envPtr = IntPtr.Zero;
        bool envFromWindows = false;

        try
        {
            token = GetDesktopUserToken();

            // The desktop user's environment (not the sometimes different one of the elevated process).
            var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (NativeMethods.CreateEnvironmentBlock(out IntPtr winEnv, token, false))
            {
                try { ReadEnvironmentBlock(winEnv, env); envFromWindows = true; }
                finally { NativeMethods.DestroyEnvironmentBlock(winEnv); }
            }
            if (!envFromWindows)
            {
                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                    if (e.Key is string k) env[k] = e.Value as string;
            }

            if (profile.EnableEnvBlock)
                EnvironmentBlocker.Apply(env);

            envPtr = BuildEnvironmentBlock(env);

            var si = new NativeMethods.STARTUPINFO { lpDesktop = @"winsta0\default" };
            si.cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>();

            string commandLine = CommandLineBuilder.ForExecutable(exePath, profile.Arguments);

            bool ok = NativeMethods.CreateProcessWithTokenW(
                token, 0, exePath, commandLine,
                NativeMethods.CREATE_SUSPENDED | NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                envPtr, workDir, ref si, out var pi);

            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return new SpawnedProcess(pi.hProcess, pi.hThread, pi.dwProcessId, Loc.T("Method_NormalUser"));
        }
        finally
        {
            if (envPtr != IntPtr.Zero) Marshal.FreeHGlobal(envPtr);
            if (token != IntPtr.Zero) NativeMethods.CloseHandle(token);
        }
    }

    /// <summary>Gets a duplicated primary token of the shell process (explorer.exe).</summary>
    private static IntPtr GetDesktopUserToken()
    {
        IntPtr shell = NativeMethods.GetShellWindow();
        if (shell == IntPtr.Zero)
            throw new InvalidOperationException(Loc.T("Err_NoShell"));

        NativeMethods.GetWindowThreadProcessId(shell, out uint shellPid);
        if (shellPid == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
        if (hProcess == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        IntPtr hToken = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(hProcess, NativeMethods.TOKEN_DUPLICATE, out hToken))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            if (!NativeMethods.DuplicateTokenEx(
                    hToken, NativeMethods.MAXIMUM_ALLOWED, IntPtr.Zero,
                    NativeMethods.SecurityImpersonation, NativeMethods.TokenPrimary, out IntPtr primary))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return primary;
        }
        finally
        {
            if (hToken != IntPtr.Zero) NativeMethods.CloseHandle(hToken);
            NativeMethods.CloseHandle(hProcess);
        }
    }

    /// <summary>Reads a native "A=1\0B=2\0\0" block into a dictionary.</summary>
    private static void ReadEnvironmentBlock(IntPtr block, IDictionary<string, string?> target)
    {
        int offset = 0;
        while (true)
        {
            string? entry = Marshal.PtrToStringUni(block + offset);
            if (string.IsNullOrEmpty(entry)) break;
            offset += (entry.Length + 1) * sizeof(char);

            // Entries like "=C:=C:\..." start with '=': look for the separator after index 0.
            int eq = entry.IndexOf('=', 1);
            if (eq > 0) target[entry[..eq]] = entry[(eq + 1)..];
        }
    }

    /// <summary>Builds a sorted Unicode block ending with a double zero (memory to free with FreeHGlobal).</summary>
    private static IntPtr BuildEnvironmentBlock(IDictionary<string, string?> env)
    {
        var sb = new StringBuilder();
        foreach (var kv in env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(kv.Key).Append('=').Append(kv.Value ?? "").Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }
}
