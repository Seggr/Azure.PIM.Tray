using System.Windows;
using Azure.PIM.Tray.Models;
using Azure.PIM.Tray.Services;
using Azure.PIM.Tray.Windows;

namespace Azure.PIM.Tray;

public partial class App : System.Windows.Application
{
    private static System.Threading.Mutex? _mutex;
    private readonly CancellationTokenSource _appCts = new();

    private List<ITenantContext> _tenants = [];
    private TrayAppConfig       _config  = new();

    private TrayIconManager?     _trayIcon;
    private RefreshOrchestrator? _refresher;
    private ActivationWatcher?   _watcher;
    private ContextMenuBuilder?  _contextMenu;

    private LogViewerWindow? _logViewerWindow;
    private ManageWindow?    _manageWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new System.Threading.Mutex(true, "Azure.PIM.Tray", out bool isNew);
        if (!isNew)
        {
            System.Windows.MessageBox.Show(
                "PIM Request Manager is already running.",
                "Already running", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _config = ConnectionService.LoadConfig();
        AppLog.LogToDisk = _config.LogToDisk;
        BuildServicesInitial();

        // Tray icon
        _trayIcon = new TrayIconManager(Dispatcher);
        _trayIcon.Initialize();

        // Activation watcher
        _watcher = new ActivationWatcher(_appCts.Token);

        // Refresh orchestrator
        _refresher = new RefreshOrchestrator(Dispatcher, () => _tenants, _appCts.Token);
        _refresher.RebuildStatusItems(_tenants);

        // Context menu
        _contextMenu = new ContextMenuBuilder(
            () => _tenants,
            _refresher,
            _watcher,
            _trayIcon,
            OpenApprovalWindow,
            OpenActivateWindow,
            OpenManageWindow,
            OpenLogViewerWindow,
            Shutdown);

        // Wire events
        _trayIcon.TrayClicked    += _contextMenu.ShowContextMenu;
        _trayIcon.BalloonClicked += OnBalloonClicked;

        _refresher.NewRequestsDetected += _trayIcon.ShowBalloon;
        _refresher.RequestCompleted    += _trayIcon.NotifyRequestCompleted;
        _refresher.PendingCountChanged += _trayIcon.UpdateTrayIcon;

        _watcher.RefreshRequested += () => _refresher.RefreshAsync();

        // Detect session unlock / resume from sleep to proactively re-authenticate
        Microsoft.Win32.SystemEvents.SessionSwitch    += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Start
        _refresher.StartTimers();
        AppLog.Info("App", $"Starting up \u2014 {_tenants.Count} tenant(s) configured");
        if (AppLog.LogToDisk)
            AppLog.Info("App", $"Disk log: {AppLog.LogFilePath}");
        _ = _refresher.RefreshAsync();

        if (_config.Connections.Count == 0)
            OpenManageWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("App", "Shutting down");
        Microsoft.Win32.SystemEvents.SessionSwitch    -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _appCts.Cancel();
        _refresher?.Dispose();
        _trayIcon?.Dispose();
        foreach (var tenant in _tenants)
            _ = tenant.DisposeAsync();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }

    // ------------------------------------------------------------------
    // Service lifecycle
    // ------------------------------------------------------------------

    private void BuildServicesInitial()
    {
        _tenants = [.. TenantContextFactory.CreateAll(_config)];
        AppLog.Info("App", $"Services rebuilt \u2014 {_tenants.Count} tenant context(s)");
    }

    private async Task BuildServicesAsync()
    {
        // Stop timers and wait for in-flight refreshes to finish before disposing
        _refresher?.StopTimers();
        if (_refresher is not null)
            await _refresher.WaitForIdleAsync();

        var old = _tenants;
        _tenants = [.. TenantContextFactory.CreateAll(_config)];
        AppLog.Info("App", $"Services rebuilt \u2014 {_tenants.Count} tenant context(s)");

        _refresher?.RebuildStatusItems(_tenants);
        _refresher?.StartTimers();

        // Now safe to dispose old contexts
        foreach (var tenant in old)
            await tenant.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Balloon click routing
    // ------------------------------------------------------------------

    private void OnBalloonClicked(bool wasError, List<UnifiedPendingRequest> lastRequests)
    {
        if (wasError)
            OpenLogViewerWindow();
        else if (lastRequests.Count == 1)
            OpenApprovalWindow(lastRequests[0]);
        else if (lastRequests.Count > 1)
            _contextMenu!.ShowContextMenu();
    }

    // ------------------------------------------------------------------
    // Window launchers
    // ------------------------------------------------------------------

    private void OpenApprovalWindow(UnifiedPendingRequest req)
    {
        var tenant = _tenants.FirstOrDefault(t => t.TenantId == req.TenantId);
        if (tenant is null) return;

        Dispatcher.InvokeAsync(async () =>
        {
            var win = new ApprovalWindow(req, tenant);
            win.ShowDialog();
            if (_refresher is not null)
                await _refresher.RefreshAsync();
        });
    }

    private void OpenActivateWindow(UnifiedEligibleRole role)
    {
        var tenant = _tenants.FirstOrDefault(t => t.TenantId == role.TenantId);
        if (tenant is null) return;

        Dispatcher.InvokeAsync(async () =>
        {
            var win = new ActivateWindow(role, tenant);
            win.ShowDialog();
            var pollUrl = win.ActivationPollUrl;
            if (pollUrl is not null && _watcher is not null && _trayIcon is not null)
                _ = _watcher.WatchAndNotifyAsync(tenant, pollUrl, role.RoleName, role.Source, _trayIcon);
            if (_refresher is not null)
                await _refresher.RefreshAsync();
        });
    }

    private void OpenLogViewerWindow()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_logViewerWindow is not null)
            {
                _logViewerWindow.Activate();
                return;
            }

            try
            {
                var win = new LogViewerWindow();
                win.Closed += (_, _) => _logViewerWindow = null;
                _logViewerWindow = win;
                win.Show();
            }
            catch (Exception ex)
            {
                _logViewerWindow = null;
                AppLog.Error("LogViewer", $"Failed to open: {ex.Message}");
            }
        });
    }

    private void OpenManageWindow()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_manageWindow is not null)
            {
                _manageWindow.Activate();
                return;
            }

            var win = new ManageWindow(_config);
            win.ConfigChanged += async (_, _) =>
            {
                _config = ConnectionService.LoadConfig();
                await BuildServicesAsync();
                if (_refresher is not null)
                    _ = _refresher.RefreshAsync();
            };
            win.Closed += (_, _) => _manageWindow = null;
            _manageWindow = win;
            win.ShowDialog();
        });
    }

    // ------------------------------------------------------------------
    // Session / power event handlers
    // ------------------------------------------------------------------

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock)
        {
            AppLog.Info("App", "Session unlocked \u2014 triggering refresh");
            Dispatcher.InvokeAsync(() => { _ = _refresher?.RefreshAsync(); });
        }
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            AppLog.Info("App", "Resumed from sleep \u2014 triggering refresh");
            Dispatcher.InvokeAsync(() => { _ = _refresher?.RefreshAsync(); });
        }
    }
}
