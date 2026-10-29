using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using NetworkIsolator.Core;
using NetworkIsolator.Localization;
using NetworkIsolator.Models;
using NetworkIsolator.ViewModels;

namespace NetworkIsolator;

public partial class MainWindow : Window
{
    private readonly ProfileManager _profiles = new();
    private readonly PermanentBlockManager _blocker = new();
    private readonly ObservableCollection<AppEntry> _apps = [];
    private List<string> _extraPaths = [];
    private int _refreshVersion;
    private readonly ObservableCollection<IsolatedApp> _running = [];
    private readonly ObservableCollection<LogEntry> _log = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private string _selectedExePath = "";
    private int _tick;
    private bool _syncingExternal;
    private bool _shutdownConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Elevated window: Windows (UIPI) blocks drag and drop from Explorer by default
        // (non-elevated). The 3 messages used by a file drop are allowed explicitly.
        if (ElevationHelper.IsElevated())
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            foreach (uint message in new uint[] { 0x0233 /* WM_DROPFILES */, 0x004A /* WM_COPYDATA */, 0x0049 /* WM_COPYGLOBALDATA */ })
                NativeMethods.ChangeWindowMessageFilterEx(hwnd, message, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdatePrivilegeBadge();
        SelectLanguageChip();
        Loc.Instance.PropertyChanged += OnLanguageChanged;

        LstApps.ItemsSource = _apps;
        LstRunning.ItemsSource = _running;
        LstLog.ItemsSource = _log;
        foreach (LogEntry entry in ActivityLog.Snapshot()) _log.Add(entry);
        ActivityLog.EntryAdded += OnLogEntry;

        RefreshProfiles();
        ShowPanel("apps");
        UpdateRunningUi();

        _timer.Tick += OnTick;
        _timer.Start();

        if (ElevationHelper.IsElevated())
        {
            try
            {
                int removed = await OrphanCleaner.SweepAsync();
                if (removed > 0) SetStatus(Loc.T("Status_OrphansCleaned", removed), true);
            }
            catch (Exception ex)
            {
                ActivityLog.Warn(Loc.T("Log_OrphanSweepFailed", ErrorTranslator.Describe(ex)));
            }
        }
        else
        {
            ActivityLog.Warn(Loc.T("Log_NotElevated"));
        }

        SyncExternalSessions();
    }

    // Language

    private void SelectLanguageChip()
    {
        RbLangFr.IsChecked = Loc.Instance.Current == AppLanguage.French;
        RbLangEn.IsChecked = Loc.Instance.Current == AppLanguage.English;
    }

    private void Lang_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Loc.Instance.SetLanguage(ReferenceEquals(sender, RbLangFr) ? AppLanguage.French : AppLanguage.English);
    }

    /// <summary>XAML texts follow automatically; texts built in code are refreshed here.</summary>
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Loc.Current)) return;

        UpdatePrivilegeBadge();
        UpdateRunningUi();
        foreach (AppEntry entry in _apps) entry.RefreshTexts();
        foreach (IsolatedApp app in _running) app.Refresh();

        if (string.IsNullOrEmpty(_selectedExePath)) TxtDropLabel.Text = Loc.T("Drop_Label");
        if (_extraPaths.Count > 0) SetExtraPaths(_extraPaths);
        SetStatus(Loc.T("Status_Ready"), true);
    }

    private void UpdatePrivilegeBadge()
    {
        if (ElevationHelper.IsElevated())
        {
            TxtPrivilege.Text = Loc.T("Priv_Admin");
            TxtPrivilege.Foreground = (Brush)FindResource("AccentGreen");
            BadgePrivileges.Cursor = System.Windows.Input.Cursors.Arrow;
            BadgePrivileges.ToolTip = Loc.T("Priv_AdminTip");
        }
        else
        {
            TxtPrivilege.Text = Loc.T("Priv_User");
            TxtPrivilege.Foreground = (Brush)FindResource("AccentYellow");
            BadgePrivileges.ToolTip = Loc.T("Priv_UserTip");
        }
    }

    private void BadgePrivileges_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!ElevationHelper.IsElevated()) AskElevate();
    }

    /// <summary>Offers to restart the tool as administrator (one UAC prompt).</summary>
    private void AskElevate()
    {
        var answer = MessageBox.Show(this,
            Loc.T("Msg_AskElevate"),
            "NetworkIsolator", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            Process? elevated = ElevationHelper.RelaunchElevated(["--gui"]);
            if (elevated is null)
            {
                SetStatus(Loc.T("Status_ElevationRefused"), false);
                return;
            }
            elevated.Dispose();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("Status_ElevationFailed", ErrorTranslator.Describe(ex)), false);
        }
    }

    private static string? FirstExe(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        return (e.Data.GetData(DataFormats.FileDrop) as string[])?
            .FirstOrDefault(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return; // leave text drops to the text boxes

        string? exe = FirstExe(e);
        e.Effects = exe is not null ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.BorderBrush = (Brush)FindResource(exe is not null ? "AccentBlue" : "BgSurface0");
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) =>
        DropZone.BorderBrush = (Brush)FindResource("BgSurface0");

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = (Brush)FindResource("BgSurface0");
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;

        string? exe = FirstExe(e);
        if (exe is not null) SetSelectedExe(exe);
        else SetStatus(Loc.T("Status_DropExe"), false);
    }

    private void DropZone_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => BrowseForExe();

    private void BtnBrowse_Click(object sender, RoutedEventArgs e) => BrowseForExe();

    private void BrowseForExe()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Dlg_PickExe"),
            Filter = Loc.T("Dlg_ExeFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) SetSelectedExe(dialog.FileName);
    }

    private void SetSelectedExe(string path)
    {
        _selectedExePath = path;
        SetExtraPaths([]);
        TxtDropPath.Text = path;
        TxtDropPath.Visibility = Visibility.Visible;
        TxtDropLabel.Text = Path.GetFileName(path);
        TxtProfileName.Text = "";
        BtnBlock.IsEnabled = true;
        BtnLaunch.IsEnabled = true;
        BtnSave.IsEnabled = true;
        SetStatus(Loc.T("Status_AppChosen", Path.GetFileName(path)), true);
    }

    private void SetExtraPaths(IEnumerable<string> paths)
    {
        _extraPaths = [.. paths];
        TxtExtra.Text = Loc.T("Extra_Programs", _extraPaths.Count);
        TxtExtra.Visibility = _extraPaths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private LaunchProfile BuildProfileFromForm() => new()
    {
        Name = TxtProfileName.Text.Trim(),
        ExePath = _selectedExePath,
        Arguments = TxtArgs.Text.Trim(),
        ExtraExePaths = [.. _extraPaths],
        WorkingDirectory = Path.GetDirectoryName(_selectedExePath),
        Mode = RbAllowLan.IsChecked == true ? IsolationMode.AllowLan : IsolationMode.BlockAll,
        BlockInbound = ChkInbound.IsChecked == true,
        EnableEnvBlock = ChkEnvBlock.IsChecked == true,
        TrackChildren = ChkChildren.IsChecked == true,
        RunAsNormalUser = ChkNormalUser.IsChecked == true
    };

    private void LoadProfileIntoForm(LaunchProfile p)
    {
        SetSelectedExe(p.ExePath);
        TxtProfileName.Text = p.Name;
        TxtArgs.Text = p.Arguments;
        RbAllowLan.IsChecked = p.Mode == IsolationMode.AllowLan;
        RbBlockAll.IsChecked = p.Mode != IsolationMode.AllowLan;
        ChkInbound.IsChecked = p.BlockInbound;
        ChkEnvBlock.IsChecked = p.EnableEnvBlock;
        ChkChildren.IsChecked = p.TrackChildren;
        ChkNormalUser.IsChecked = p.RunAsNormalUser;
        SetExtraPaths(p.ExtraExePaths);
    }

    private async void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedExePath) || !File.Exists(_selectedExePath))
        {
            SetStatus(Loc.T("Status_PickValidExe"), false);
            return;
        }
        await LaunchAsync(BuildProfileFromForm(), savedProfileName: null);
    }

    private async Task LaunchAsync(LaunchProfile profile, string? savedProfileName)
    {
        if (!File.Exists(profile.ExePath))
        {
            SetStatus(Loc.T("Status_NotFound", profile.ExePath), false);
            return;
        }

        if (!ElevationHelper.IsElevated())
        {
            SetStatus(Loc.T("Status_NeedAdminLaunch"), false);
            AskElevate();
            return;
        }

        BtnLaunch.IsEnabled = false;
        SetStatus(Loc.T("Status_Cutting"), null);

        var launcher = new AppContainerLauncher(profile);
        var item = new IsolatedApp(launcher);
        launcher.Changed += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            item.Refresh();
            UpdateRunningUi();
        });

        try
        {
            await launcher.StartAsync();
        }
        catch (Exception ex)
        {
            launcher.Dispose();
            SetStatus(ErrorTranslator.Describe(ex), false);
            return;
        }
        finally
        {
            BtnLaunch.IsEnabled = !string.IsNullOrEmpty(_selectedExePath);
        }

        _running.Add(item);
        UpdateRunningUi();
        SetStatus(Loc.T("Status_Launched", item.Name, launcher.RootPid), true);
        RbNavRunning.IsChecked = true; // switch to the "Running" tab

        if (savedProfileName is not null)
            _ = _profiles.MarkLaunchedAsync(savedProfileName);

        _ = WatchSessionAsync(launcher, item);
    }

    /// <summary>Waits for a session to end, then removes it from the list.</summary>
    private async Task WatchSessionAsync(AppContainerLauncher launcher, IsolatedApp item)
    {
        await launcher.Completion;

        _running.Remove(item);
        UpdateRunningUi();
        launcher.Dispose(); // already cleaned up: fast

        if (!_shutdownConfirmed)
            SetStatus(Loc.T("Status_Closed", item.Name), true);
    }

    private void BtnStopApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: IsolatedApp app }) return;
        SetStatus(Loc.T("Status_Stopping", app.Name), null);
        app.Stop();
    }

    private void BtnStopAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (IsolatedApp app in _running.Where(a => a.CanStop).ToList())
            app.Stop();
        SetStatus(Loc.T("Status_StoppingAll"), null);
    }

    private void UpdateRunningUi()
    {
        int n = _running.Count;
        TxtRunningCount.Text = n.ToString();
        TxtEmptyRunning.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        LstRunning.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnStopAll.IsEnabled = _running.Any(a => a.CanStop);
        RbNavRunning.Content = Loc.T(n > 0 ? "Nav_RunningCount" : "Nav_Running", n);
        TxtRunningSummary.Text = n > 0 ? Loc.T("Running_Summary", n) : "";
    }

    private void OnTick(object? sender, EventArgs e)
    {
        foreach (IsolatedApp app in _running) app.RefreshElapsed();
        if (++_tick % 3 == 0) SyncExternalSessions();
    }

    /// <summary>Also shows applications started by shortcuts or the command line (other processes of the tool).</summary>
    private async void SyncExternalSessions()
    {
        if (_syncingExternal) return;
        _syncingExternal = true;
        try
        {
            List<SessionRecord> records = await Task.Run(() => SessionJournal.LoadAll()
                .Where(r => r.OwnerPid != Environment.ProcessId && r.RootPid > 0 && SessionJournal.IsOwnerAlive(r))
                .ToList());

            var liveKeys = records.Select(r => r.SessionId).ToHashSet();
            foreach (SessionRecord r in records)
            {
                IsolatedApp? existing = _running.FirstOrDefault(a => a.IsExternal && a.Key == r.SessionId);
                if (existing is null) _running.Add(new IsolatedApp(r));
                else existing.UpdateRecord(r);
            }

            foreach (IsolatedApp gone in _running.Where(a => a.IsExternal && !liveKeys.Contains(a.Key)).ToList())
                _running.Remove(gone);

            UpdateRunningUi();
        }
        catch
        {
            // syncing is purely informative
        }
        finally
        {
            _syncingExternal = false;
        }
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (!HasValidSelection()) return;

        try
        {
            LaunchProfile saved = await _profiles.UpsertAsync(BuildProfileFromForm());
            TxtProfileName.Text = saved.Name;
            RefreshProfiles();
            SetStatus(Loc.T("Status_AddedNotBlocked", saved.Name), true);
            RbNavApps.IsChecked = true;
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("Status_AddFailed", ErrorTranslator.Describe(ex)), false);
        }
    }

    private bool HasValidSelection()
    {
        if (!string.IsNullOrEmpty(_selectedExePath) && File.Exists(_selectedExePath)) return true;
        SetStatus(Loc.T("Status_PickValidExe"), false);
        return false;
    }

    /// <summary>Saves the form's profile, then creates the permanent block.</summary>
    private async void BtnBlock_Click(object sender, RoutedEventArgs e)
    {
        if (!HasValidSelection()) return;

        LaunchProfile form = BuildProfileFromForm();
        if (!ConfirmBlock(form.Name.Length > 0 ? form.Name : Path.GetFileNameWithoutExtension(form.ExePath), form)) return;

        if (!ElevationHelper.IsElevated())
        {
            AskElevate();
            return;
        }

        BtnBlock.IsEnabled = false;
        try
        {
            LaunchProfile saved = await _profiles.UpsertAsync(form);
            TxtProfileName.Text = saved.Name;
            SetStatus(Loc.T("Status_Creating"), null);
            await _blocker.ApplyAsync(saved);
            RefreshProfiles();
            RbNavApps.IsChecked = true;
            SetStatus(Loc.T("Status_NowBlocked", saved.Name), true);
        }
        catch (Exception ex)
        {
            RefreshProfiles();
            SetStatus(ErrorTranslator.Describe(ex), false);
        }
        finally
        {
            BtnBlock.IsEnabled = !string.IsNullOrEmpty(_selectedExePath);
        }
    }

    private bool ConfirmBlock(string name, LaunchProfile profile)
    {
        int count = PermanentBlockManager.AllExePaths(profile).Count;
        string scope = Loc.T(profile.Mode == IsolationMode.AllowLan ? "Msg_ScopeLan" : "Msg_ScopeAll");
        string programs = count > 1 ? Loc.T("Msg_ProgramCount", count) : "";

        var answer = MessageBox.Show(this, Loc.T("Msg_ConfirmBlock", name, scope, programs),
            "NetworkIsolator", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes;
    }

    /// <summary>The "Block for good / Unblock" button of a card.</summary>
    private async void BtnLock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppEntry entry } || entry.IsBusy) return;

        bool unblock = entry.State is BlockState.Blocked or BlockState.Partial;

        if (!unblock && !ConfirmBlock(entry.Name, entry.Profile)) return;
        if (!ElevationHelper.IsElevated())
        {
            AskElevate();
            return;
        }

        entry.SetBusy(true);
        try
        {
            if (unblock)
            {
                bool clean = await _blocker.RemoveAsync(entry.Profile);
                SetStatus(clean
                    ? Loc.T("Status_Unblocked", entry.Name)
                    : Loc.T("Status_UnblockPartial", entry.Name), clean);
            }
            else
            {
                await _blocker.ApplyAsync(entry.Profile);
                SetStatus(Loc.T("Status_NowBlockedShort", entry.Name), true);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ErrorTranslator.Describe(ex), false);
        }
        finally
        {
            entry.SetBusy(false);
            await RecheckAsync(entry);
        }
    }

    private async Task RecheckAsync(AppEntry entry)
    {
        try { entry.SetState(await _blocker.CheckAsync(entry.Profile)); }
        catch { entry.SetState(BlockState.Unknown); }
    }

    private void BtnQuickLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppEntry entry }) return;
        LaunchSavedProfile(entry.Profile);
    }

    private void LaunchSavedProfile(LaunchProfile profile)
    {
        if (!File.Exists(profile.ExePath))
        {
            SetStatus(Loc.T("Status_NotFound", profile.ExePath), false);
            return;
        }
        _ = LaunchAsync(profile.Clone(), profile.Name);
    }

    private void BtnEditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppEntry entry }) return;
        LoadProfileIntoForm(entry.Profile);
        RbNavAdd.IsChecked = true;
        SetStatus(Loc.T("Status_Loaded", entry.Name), true);
    }

    private void BtnGoAdd_Click(object sender, RoutedEventArgs e) => RbNavAdd.IsChecked = true;

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshProfiles();
        SetStatus(Loc.T("Status_Refreshed"), true);
    }

    private void BtnShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppEntry entry }) return;
        try
        {
            string link = ShortcutCreator.CreateOnDesktop(entry.Profile);
            SetStatus(Loc.T("Status_ShortcutCreated", Path.GetFileName(link)), true);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("Status_ShortcutFailed", ErrorTranslator.Describe(ex)), false);
        }
    }

    private async void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppEntry entry } || entry.IsBusy) return;

        bool hasRules = entry.State != BlockState.Free;
        var answer = MessageBox.Show(this,
            Loc.T(hasRules ? "Msg_DeleteBlocked" : "Msg_DeleteFree", entry.Name),
            "NetworkIsolator", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        entry.SetBusy(true);
        try
        {
            if (hasRules)
            {
                if (!ElevationHelper.IsElevated())
                {
                    AskElevate();
                    return;
                }

                if (!await _blocker.RemoveAsync(entry.Profile))
                {
                    SetStatus(Loc.T("Status_DeleteKept"), false);
                    return;
                }
            }

            await _profiles.DeleteProfileAsync(entry.Name);
            RefreshProfiles();
            SetStatus(Loc.T(hasRules ? "Status_RemovedUnblocked" : "Status_Removed", entry.Name), true);
        }
        catch (Exception ex)
        {
            SetStatus(ErrorTranslator.Describe(ex), false);
        }
        finally
        {
            entry.SetBusy(false);
        }
    }

    private void LstApps_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (LstApps.SelectedItem is AppEntry entry) LaunchSavedProfile(entry.Profile);
    }

    private static bool IsInsideButton(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is ButtonBase) return true;
            d = (d is Visual) || (d is Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("Dlg_ExportTitle"),
            Filter = Loc.T("Dlg_JsonFilter"),
            FileName = Loc.T("Dlg_ExportName")
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            await _profiles.ExportAsync(dialog.FileName);
            SetStatus(Loc.T("Status_Exported", _profiles.Profiles.Count, Path.GetFileName(dialog.FileName)), true);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("Status_ExportFailed", ErrorTranslator.Describe(ex)), false);
        }
    }

    private async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Dlg_ImportTitle"),
            Filter = Loc.T("Dlg_JsonFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var (added, updated) = await _profiles.ImportAsync(dialog.FileName);
            RefreshProfiles();
            SetStatus(Loc.T("Status_Imported", added, updated), true);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("Status_ImportFailed", ErrorTranslator.Describe(ex)), false);
        }
    }

    private void RefreshProfiles()
    {
        _apps.Clear();
        foreach (LaunchProfile p in _profiles.Profiles) _apps.Add(new AppEntry(p));

        int n = _apps.Count;
        TxtAppCount.Text = n.ToString();
        PanelEmptyApps.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        LstApps.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;

        _ = RefreshStatesAsync();
    }

    /// <summary>Reads the real firewall state of each application (a version counter drops outdated reads).</summary>
    private async Task RefreshStatesAsync()
    {
        int version = ++_refreshVersion;
        foreach (AppEntry entry in _apps.ToList())
        {
            BlockState state;
            try { state = await _blocker.CheckAsync(entry.Profile); }
            catch { state = BlockState.Unknown; }

            if (version != _refreshVersion) return;
            entry.SetState(state);
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // the event is also raised while the XAML loads
        if (sender is RadioButton { Tag: string key }) ShowPanel(key);
    }

    private void ShowPanel(string key)
    {
        PanelApps.Visibility = key == "apps" ? Visibility.Visible : Visibility.Collapsed;
        PanelAdd.Visibility = key == "add" ? Visibility.Visible : Visibility.Collapsed;
        PanelHelp.Visibility = key == "help" ? Visibility.Visible : Visibility.Collapsed;
        PanelRunning.Visibility = key == "running" ? Visibility.Visible : Visibility.Collapsed;
        PanelLog.Visibility = key == "log" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLogEntry(LogEntry entry)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _log.Add(entry);
            while (_log.Count > 400) _log.RemoveAt(0);
            LstLog.ScrollIntoView(entry);

            // Live feedback in the status bar (rule created, application started, application closed...).
            switch (entry.Level)
            {
                case LogLevel.Ok: SetStatus(entry.Message, true); break;
                case LogLevel.Warn: SetStatus(entry.Message, null); break;
                case LogLevel.Error: SetStatus(entry.Message, false); break;
            }
        });
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            string args = File.Exists(AppPaths.LogFile)
                ? $"/select,\"{AppPaths.LogFile}\""
                : $"\"{AppPaths.LogsDir}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("Status_LogFolderFailed", ex.Message), false);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownConfirmed || SessionRegistry.Count == 0)
        {
            _timer.Stop();
            ActivityLog.EntryAdded -= OnLogEntry;
            return;
        }

        var answer = MessageBox.Show(this,
            Loc.T("Msg_ConfirmClose", SessionRegistry.Count),
            "NetworkIsolator", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        e.Cancel = true; // we close the window ourselves once cleanup is done
        if (answer == MessageBoxResult.Yes) _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        IsEnabled = false;
        SetStatus(Loc.T("Status_ShuttingDown"), null);

        await Task.Run(SessionRegistry.DisposeAll);

        _shutdownConfirmed = true;
        Close();
    }

    private void SetStatus(string message, bool? isOk)
    {
        TxtStatus.Text = message;
        Brush brush = (Brush)FindResource(isOk switch
        {
            true => "AccentGreen",
            false => "AccentRed",
            null => "AccentYellow"
        });
        StatusDot.Fill = brush;
        TxtStatus.Foreground = brush;
    }
}
