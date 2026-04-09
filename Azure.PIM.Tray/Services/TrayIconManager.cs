using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;
using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

public sealed class TrayIconManager : IDisposable
{
    private const int BalloonTimeoutMs = 8000;

    private NotifyIcon?      _notifyIcon;
    private DispatcherTimer? _errorBalloonDebounce;
    private bool             _lastBalloonWasError;
    private List<UnifiedPendingRequest> _lastBalloonRequests = [];
    private readonly Dispatcher _dispatcher;

    /// <summary>Raised when the user clicks the tray icon.</summary>
    public event Action? TrayClicked;

    /// <summary>Raised when a balloon tip is clicked.</summary>
    public event Action<bool, List<UnifiedPendingRequest>>? BalloonClicked;

    public TrayIconManager(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Initialize()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon    = CreateTrayIcon(),
            Visible = true,
            Text    = "PIM Request Manager"
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
                TrayClicked?.Invoke();
        };
        _notifyIcon.BalloonTipClicked += (_, _) =>
            BalloonClicked?.Invoke(_lastBalloonWasError, _lastBalloonRequests);

        AppLog.EntryAdded += () =>
        {
            var last = AppLog.GetAll().LastOrDefault();
            if (last is null || last.Level < LogLevel.Error) return;

            _dispatcher.InvokeAsync(() =>
            {
                if (_errorBalloonDebounce is null)
                {
                    _errorBalloonDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    _errorBalloonDebounce.Tick += (_, _) =>
                    {
                        _errorBalloonDebounce.Stop();
                        ShowErrorBalloon();
                    };
                }
                _errorBalloonDebounce.Stop();
                _errorBalloonDebounce.Start();
            });
        };
    }

    private int     _lastPendingCount;
    private string? _updateVersion;

    public void UpdateTrayIcon(int pendingCount)
    {
        _lastPendingCount = pendingCount;
        RefreshIconAndTooltip();
    }

    public void NotifyUpdateAvailable(string version)
    {
        _updateVersion = version;
        _notifyIcon?.ShowBalloonTip(
            8000, "PIM Request Manager",
            $"Version {version} is available \u2014 open Settings to update.",
            ToolTipIcon.Info);
        RefreshIconAndTooltip();
    }

    private void RefreshIconAndTooltip()
    {
        if (_notifyIcon is null) return;

        var tooltip = _lastPendingCount > 0
            ? $"PIM Request Manager \u2014 {_lastPendingCount} pending approval(s)"
            : "PIM Request Manager \u2014 no pending approvals";
        if (_updateVersion is not null)
            tooltip += $"\nUpdate v{_updateVersion} available";

        _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        _notifyIcon.Icon = CreateTrayIcon(_lastPendingCount > 0, _updateVersion is not null);
    }

    public void ShowBalloon(List<UnifiedPendingRequest> newRequests)
    {
        _lastBalloonWasError = false;
        _lastBalloonRequests = newRequests;
        _notifyIcon?.ShowBalloonTip(
            BalloonTimeoutMs, "PIM Request Manager",
            $"{newRequests.Count} new PIM request(s) awaiting your approval.",
            ToolTipIcon.Info);
    }

    public void NotifyRequestCompleted(UnifiedPendingRequest req)
    {
        var source = req.Source == PimSource.EntraId ? "Entra ID" : "Azure RBAC";
        var msg = $"{req.PrincipalName}\u2019s request for \"{req.RoleName}\" [{source}] "
                + "is no longer pending \u2014 it was approved or completed.";
        _lastBalloonWasError = false;
        _notifyIcon?.ShowBalloonTip(BalloonTimeoutMs, "PIM Request Manager \u2014 Request Completed", msg, ToolTipIcon.Info);
    }

    public void ShowActivationBalloon(string message, ToolTipIcon icon)
    {
        _lastBalloonWasError = false;
        _notifyIcon?.ShowBalloonTip(BalloonTimeoutMs, "PIM Request Manager", message, icon);
    }

    private void ShowErrorBalloon()
    {
        var errors = AppLog.ErrorCount;
        var warns  = AppLog.WarningCount;
        var msg = errors > 0
            ? $"{errors} error{(errors == 1 ? "" : "s")} and {warns} warning{(warns == 1 ? "" : "s")}. Click to open Log Viewer."
            : $"{warns} warning{(warns == 1 ? "" : "s")}. Click to open Log Viewer.";
        _lastBalloonWasError = true;
        _notifyIcon?.ShowBalloonTip(BalloonTimeoutMs, "PIM Request Manager \u2014 Log", msg, ToolTipIcon.Warning);
    }

    internal static Icon CreateTrayIcon(bool hasPending = false, bool hasUpdate = false)
    {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            var bg = hasPending
                ? Color.FromArgb(0xCC, 0x44, 0x00)   // orange — pending approvals
                : hasUpdate
                    ? Color.FromArgb(0x00, 0x88, 0x44) // green — update available
                    : Color.FromArgb(0x00, 0x66, 0xCC); // blue — normal
            using var brush = new SolidBrush(bg);
            g.FillEllipse(brush, 1, 1, 14, 14);
            using var font = new Font("Arial", 8f, System.Drawing.FontStyle.Bold);
            g.DrawString("P", font, Brushes.White, 3f, 2f);
        }
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        _errorBalloonDebounce?.Stop();
        _notifyIcon?.Dispose();
    }
}
