using System.Diagnostics;
using System.Windows;
using NetworkIsolator.Core;
using NetworkIsolator.Localization;

namespace NetworkIsolator;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // No automatic window (no StartupUri): we decide between GUI and command line ourselves.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        RegisterSafetyNets();

        try
        {
            CliOptions options = CliOptions.Parse(e.Args);

            if (options.Command == CliCommand.Gui && options.Error is null)
            {
                StartGui(e.Args);
                return;
            }

            // Command-line mode: no window, exit code returned to the shell.
            CliOutput.Initialize();
            int code = await CliHandler.ExecuteAsync(e.Args, options);
            Environment.ExitCode = code;
            Shutdown(code);
        }
        catch (Exception ex)
        {
            ActivityLog.Error(Loc.T("Log_StartupError", ex));
            MessageBox.Show(Loc.T("Msg_StartupError", ex.Message), "NetworkIsolator",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.ExitCode = 1;
            Shutdown(1);
        }
    }

    private void StartGui(string[] args)
    {
        // The window manages firewall rules, so it needs administrator rights from the start.
        // If UAC is declined, the window still opens in limited mode (list, log...).
        if (!ElevationHelper.IsElevated())
        {
            Process? elevated = TryRelaunchElevated(args);
            if (elevated is not null)
            {
                elevated.Dispose();
                Shutdown(0);
                return;
            }
        }

        var window = new MainWindow();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    private static Process? TryRelaunchElevated(string[] args)
    {
        try
        {
            return ElevationHelper.RelaunchElevated(args);
        }
        catch (Exception ex)
        {
            ActivityLog.Warn(Loc.T("Log_RelaunchFailed", ErrorTranslator.Describe(ex)));
            return null;
        }
    }

    /// <summary>
    /// Safety nets: whatever happens (closing, Windows sign-out, exception),
    /// we try to remove the temporary firewall rules before leaving.
    /// If the process is killed abruptly, OrphanCleaner takes care of it on the next start.
    /// </summary>
    private void RegisterSafetyNets()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            ActivityLog.Error(Loc.T("Log_UnhandledException", args.Exception));
            MessageBox.Show(Loc.T("Msg_UnexpectedError", args.Exception.Message), "NetworkIsolator",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            ActivityLog.Error(Loc.T("Log_FatalException", args.ExceptionObject));
            SessionRegistry.DisposeAll();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ActivityLog.Warn(Loc.T("Log_TaskError", args.Exception.GetBaseException().Message));
            args.SetObserved();
        };

        SessionEnding += (_, _) => SessionRegistry.DisposeAll();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SessionRegistry.DisposeAll();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SessionRegistry.DisposeAll();
        base.OnExit(e);
    }
}
