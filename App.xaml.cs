using System.Diagnostics;
using System.Windows;
using TidyDesktop.Boxes;
using Forms = System.Windows.Forms;

namespace TidyDesktop;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private BoxManager? _manager;
    private Forms.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            AppData.Log($"Unhandled: {args.Exception}");
            args.Handled = true;
        };
        // Errors on other threads would otherwise end the app without a trace.
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppData.Log($"Unhandled (fatal): {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppData.Log($"Unobserved task error: {args.Exception}");
            args.SetObserved();
        };
        AppData.Log($"Starting TidyDesktop {Version} on Windows {Environment.OSVersion.Version}");
        Lang.Set(AppData.LoadConfig().Language);

        _singleInstance = new Mutex(true, "TidyDesktop.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show(Lang.T("app.alreadyRunning"), "TidyDesktop");
            Shutdown();
            return;
        }

        _manager = new BoxManager();

        // Command-line utilities that run once and exit.
        if (e.Args.Contains("--dump", StringComparer.OrdinalIgnoreCase))
        {
            RunOnce(_manager.DumpDiagnostics);
            return;
        }
        if (e.Args.Contains("--restore-icons", StringComparer.OrdinalIgnoreCase))
        {
            RunOnce(() => _manager.RestoreBackup());
            return;
        }

        try
        {
            _manager.Start();
        }
        catch (Exception ex)
        {
            AppData.Log($"Startup failed: {ex}");
            MessageBox.Show(Lang.T("app.couldNotConnectDesktop", ex.Message), "TidyDesktop");
            Shutdown();
            return;
        }

        _manager.SettingsRequested += OpenSettings;
        _tray = CreateTray();
        AutoStart.Refresh();
        // Notifications can be turned off, so the first run says hello in a window that cannot be missed.
        if (_manager.IsFirstRun)
        {
            var welcome = new WelcomeWindow();
            if (welcome.ShowDialog() == true)
            {
                if (welcome.ShouldAutoStart)
                {
                    try
                    {
                        AutoStart.Set(true);
                    }
                    catch (Exception ex)
                    {
                        AppData.Log($"Could not enable auto start on welcome: {ex.Message}");
                    }
                }
                if (welcome.ShouldAutoOrganize)
                {
                    _manager.AutoOrganize();
                }
            }
        }

        if (e.Args.Contains("--organize", StringComparer.OrdinalIgnoreCase))
            _manager.AutoOrganize();
        if (e.Args.Contains("--arrange-left", StringComparer.OrdinalIgnoreCase))
            _manager.ArrangeBoxes(fromRight: false);
        if (e.Args.Contains("--arrange-right", StringComparer.OrdinalIgnoreCase))
            _manager.ArrangeBoxes(fromRight: true);
    }

    public const string Website = "www.404architect.com";

    /// <summary>The version shown to users, such as "1.0.0".</summary>
    public static string Version =>
        typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    private void RunOnce(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppData.Log($"Command failed: {ex}");
        }
        Shutdown();
    }

    private Forms.NotifyIcon CreateTray()
    {
        var tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "TidyDesktop",
            ContextMenuStrip = CreateMenu(),
            Visible = true,
        };
        tray.DoubleClick += (_, _) =>
        {
            if (_manager!.DesktopHidden)
                _manager.SetDesktopHidden(false);
            else
                _manager.CreateBox();
        };
        _manager!.Notify += (title, text) => ShowBalloon(title, text);
        _manager.DesktopHiddenChanged += hidden =>
        {
            tray.Text = hidden ? Lang.T("app.trayHidden") : "TidyDesktop";
            // An empty desktop can look like everything was deleted, so always say how to get it back.
            if (hidden)
                ShowBalloon(Lang.T("app.desktopHidden"),
                    Lang.T("app.hiddenNotice"));
        };
        tray.BalloonTipClicked += (_, _) =>
        {
            // Clicking the "hidden" notice brings the desktop back; any other notice opens the
            // history panel, which has the details and a way to undo.
            if (_manager!.DesktopHidden)
                _manager.SetDesktopHidden(false);
            else
                _manager.SetHistoryPanelVisible(true);
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left && _manager!.DesktopHidden)
                _manager.SetDesktopHidden(false);
        };
        return tray;
    }

    private Forms.ContextMenuStrip CreateMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem($"TidyDesktop {Version}") { Enabled = false });
        menu.Items.Add(Lang.T("app.madeBy", Website), null, (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo($"https://{Website}") { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                AppData.Log($"Could not open {Website}: {ex.Message}");
            }
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Lang.T("app.settings"), null, (_, _) => OpenSettings());
        menu.Items.Add(Lang.T("app.newBox"), null, (_, _) => _manager!.CreateBox());
        menu.Items.Add(Lang.T("app.sortIntoBoxesBy"), null, (_, _) => AutoOrganize());
        menu.Items.Add(Lang.T("app.lineUpLeft"), null, (_, _) => _manager!.ArrangeBoxes(fromRight: false));
        menu.Items.Add(Lang.T("app.lineUpRight"), null, (_, _) => _manager!.ArrangeBoxes(fromRight: true));
        menu.Items.Add(Lang.T("app.fileRules"), null, (_, _) => _manager!.OpenRules());
        var history = new Forms.ToolStripMenuItem(Lang.T("app.showRecentChanges")) { CheckOnClick = true };
        history.Click += (_, _) => _manager!.SetHistoryPanelVisible(history.Checked);
        menu.Items.Add(history);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var hide = new Forms.ToolStripMenuItem();
        hide.Click += (_, _) => _manager!.SetDesktopHidden(!_manager.DesktopHidden);
        menu.Items.Add(hide);
        var doubleClick = new Forms.ToolStripMenuItem(Lang.T("app.doubleClickEmptyDesktop")) { CheckOnClick = true };
        doubleClick.Click += (_, _) =>
        {
            _manager!.DoubleClickHides = doubleClick.Checked;
            if (doubleClick.Checked)
                ShowBalloon(Lang.T("app.doubleClickHideOn"),
                    Lang.T("app.doubleClickNotice"));
        };
        menu.Items.Add(doubleClick);
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add(Lang.T("app.saveLayoutNow"), null, (_, _) => _manager!.TakeSnapshot(Lang.T("app.savedByHand")));
        var snapshots = new Forms.ToolStripMenuItem(Lang.T("app.goBackSavedLayout"));
        snapshots.DropDownItems.Add("-");
        snapshots.DropDownOpening += (_, _) => FillSnapshotMenu(snapshots);
        menu.Items.Add(snapshots);
        menu.Items.Add(Lang.T("app.removeAllBoxesAnd"), null, (_, _) => RestoreIcons());
        menu.Opening += (_, _) =>
        {
            hide.Text = _manager!.DesktopHidden ? Lang.T("app.showDesktop") : Lang.T("app.hideDesktop");
            doubleClick.Checked = _manager.DoubleClickHides;
            history.Checked = _manager.HistoryPanelVisible;
        };
        var autoStart = new Forms.ToolStripMenuItem(Lang.T("app.startWithWindows")) { CheckOnClick = true };
        autoStart.Click += (_, _) =>
        {
            try
            {
                AutoStart.Set(autoStart.Checked);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                AppData.Log($"Could not change auto start: {ex.Message}");
                MessageBox.Show(Lang.T("app.couldNotChangeStart", ex.Message), "TidyDesktop");
            }
        };
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(autoStart);
        menu.Opening += (_, _) => autoStart.Checked = AutoStart.IsEnabled;
        menu.Items.Add(CreateLanguageMenu());
        menu.Items.Add(Lang.T("app.openSettingsFolder"), null, (_, _) =>
            Process.Start(new ProcessStartInfo(AppData.Directory) { UseShellExecute = true }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Lang.T("app.exit"), null, (_, _) => Shutdown());
        return menu;
    }

    /// <summary>"Same as Windows" plus every language there is a file for.</summary>
    private Forms.ToolStripMenuItem CreateLanguageMenu()
    {
        var menu = new Forms.ToolStripMenuItem("Language / 언어");
        var system = new Forms.ToolStripMenuItem(Lang.T("app.sameAsWindows")) { Checked = Lang.Choice == null };
        system.Click += (_, _) => ChangeLanguage(null);
        menu.DropDownItems.Add(system);
        menu.DropDownItems.Add(new Forms.ToolStripSeparator());
        foreach (var (code, name) in Lang.Available)
        {
            var item = new Forms.ToolStripMenuItem(name) { Checked = Lang.Choice == code };
            item.Click += (_, _) => ChangeLanguage(code);
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    private SettingsWindow? _settingsWindow;

    public void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_manager!, ChangeLanguage, RestoreIcons);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void ChangeLanguage(string? code)
    {
        Lang.Set(code);
        _manager!.SetLanguage(code);
        // Menus are built with their text, so the tray menu is made again.
        var old = _tray!.ContextMenuStrip;
        _tray.ContextMenuStrip = CreateMenu();
        old?.Dispose();

        if (_settingsWindow != null)
        {
            _settingsWindow.Close();
            _settingsWindow = null;
            OpenSettings();
        }
    }

    private void ShowBalloon(string title, string text) =>
        _tray?.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);

    /// <summary>
    /// The tray icon at the tray's size, so Windows does not have to shrink a large image. It is a
    /// simpler picture than the app icon, which turns to mush at 16 px.
    /// </summary>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("TidyDesktopTray.ico");
        return stream == null
            ? System.Drawing.SystemIcons.Application
            : new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    private void FillSnapshotMenu(Forms.ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        var saved = AppData.LoadSnapshots();
        if (saved.Count == 0)
        {
            menu.DropDownItems.Add(new Forms.ToolStripMenuItem(Lang.T("app.noSavedLayouts")) { Enabled = false });
            return;
        }
        foreach (var (path, snapshot) in saved)
        {
            var when = Lang.Date(snapshot.TakenAt, "format.shortDateTime");
            var text = Lang.T("app.snapshotItem", when, snapshot.Reason, snapshot.Boxes.Count);
            menu.DropDownItems.Add(text, null, (_, _) =>
            {
                var answer = MessageBox.Show(
                    Lang.T("app.snapshotConfirm", when),
                    "TidyDesktop", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.OK)
                    _manager!.RestoreSnapshot(path);
            });
        }
    }

    private void AutoOrganize()
    {
        var answer = MessageBox.Show(
            Lang.T("app.sortByKindConfirm"),
            "TidyDesktop", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
            return;
        var sorted = _manager!.AutoOrganize();
        MessageBox.Show(Lang.T("app.sortedIcons", sorted), "TidyDesktop");
    }

    private void RestoreIcons()
    {
        var answer = MessageBox.Show(
            Lang.T("app.restoreConfirm"),
            "TidyDesktop", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
            return;
        if (!_manager!.RestoreBackup())
            MessageBox.Show(Lang.T("app.thereNoSavedBackup"), "TidyDesktop");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _manager?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
