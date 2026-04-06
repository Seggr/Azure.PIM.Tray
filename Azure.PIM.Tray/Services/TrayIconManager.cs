using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

public sealed class TrayIconManager : IDisposable
{
    private NotifyIcon?      _notifyIcon;
    private Form?            _ownerForm;
    private DispatcherTimer? _errorBalloonDebounce;
    private bool             _lastBalloonWasError;
    private List<UnifiedPendingRequest> _lastBalloonRequests = [];
    private readonly Dispatcher _dispatcher;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Raised when the user clicks the tray icon.</summary>
    public event Action? TrayClicked;

    /// <summary>Raised when a balloon tip is clicked.</summary>
    public event Action<bool, List<UnifiedPendingRequest>>? BalloonClicked;

    public Form? OwnerForm => _ownerForm;

    public TrayIconManager(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Initialize()
    {
        _ownerForm = new Form
        {
            Width           = 0,
            Height          = 0,
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar   = false,
            Opacity         = 0,
            WindowState     = FormWindowState.Minimized
        };
        _ownerForm.Show();
        _ownerForm.Hide();

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
            if (last is null || last.Level < LogLevel.Warning) return;

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

    public void BringOwnerToForeground()
    {
        if (_ownerForm is not null)
            SetForegroundWindow(_ownerForm.Handle);
    }

    public void UpdateTrayIcon(int pendingCount)
    {
        if (_notifyIcon is null) return;
        _notifyIcon.Text = pendingCount > 0
            ? $"PIM Request Manager \u2014 {pendingCount} pending approval(s)"
            : "PIM Request Manager \u2014 no pending approvals";
        _notifyIcon.Icon = CreateTrayIcon(pendingCount > 0);
    }

    public void ShowBalloon(List<UnifiedPendingRequest> newRequests)
    {
        _lastBalloonWasError = false;
        _lastBalloonRequests = newRequests;
        _notifyIcon?.ShowBalloonTip(
            6000, "PIM Request Manager",
            $"{newRequests.Count} new PIM request(s) awaiting your approval.",
            ToolTipIcon.Info);
    }

    public void NotifyRequestCompleted(UnifiedPendingRequest req)
    {
        var source = req.Source == PimSource.EntraId ? "Entra ID" : "Azure RBAC";
        var msg = $"{req.PrincipalName}\u2019s request for \"{req.RoleName}\" [{source}] "
                + "is no longer pending \u2014 it was approved or completed.";
        _lastBalloonWasError = false;
        _notifyIcon?.ShowBalloonTip(10_000, "PIM Request Manager \u2014 Request Completed", msg, ToolTipIcon.Info);
    }

    public void ShowActivationBalloon(string message, ToolTipIcon icon)
    {
        _lastBalloonWasError = false;
        _notifyIcon?.ShowBalloonTip(8000, "PIM Request Manager", message, icon);
    }

    private void ShowErrorBalloon()
    {
        var errors = AppLog.ErrorCount;
        var warns  = AppLog.WarningCount;
        var msg = errors > 0
            ? $"{errors} error{(errors == 1 ? "" : "s")} and {warns} warning{(warns == 1 ? "" : "s")}. Click to open Log Viewer."
            : $"{warns} warning{(warns == 1 ? "" : "s")}. Click to open Log Viewer.";
        _lastBalloonWasError = true;
        _notifyIcon?.ShowBalloonTip(8_000, "PIM Request Manager \u2014 Log", msg, ToolTipIcon.Warning);
    }

    internal static Icon CreateTrayIcon(bool hasPending = false)
    {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            var bg = hasPending
                ? Color.FromArgb(0xCC, 0x44, 0x00)
                : Color.FromArgb(0x00, 0x66, 0xCC);
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
        _ownerForm?.Dispose();
    }
}
