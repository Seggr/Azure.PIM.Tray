using System.Collections.Concurrent;
using System.Windows.Forms;
using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

public sealed class ActivationWatcher
{
    private sealed record WatchEntry(string RoleName, string TenantId, PimSource Source);

    private readonly ConcurrentDictionary<string, WatchEntry> _watching = new();
    private readonly CancellationToken _shutdownToken;

    /// <summary>Raised whenever the set of watched activations changes (for hourglass UI).</summary>
    public event Action? WatchingChanged;

    /// <summary>Raised when an activation finishes so the caller can trigger a refresh.</summary>
    public event Func<Task>? RefreshRequested;

    public ActivationWatcher(CancellationToken shutdownToken)
    {
        _shutdownToken = shutdownToken;
    }

    public bool IsRoleWatched(UnifiedEligibleRole role) =>
        _watching.Values.Any(w =>
            w.TenantId == role.TenantId &&
            w.RoleName == role.RoleName &&
            w.Source == role.Source);

    public void Subscribe(Action handler) => WatchingChanged += handler;
    public void Unsubscribe(Action handler) => WatchingChanged -= handler;

    public async Task WatchAndNotifyAsync(
        ITenantContext tenant, string pollUrl, string roleName, PimSource source,
        TrayIconManager trayIcon)
    {
        AppLog.Info("Activation", $"Starting poll for '{roleName}' [{source}] ({tenant.TenantDisplayName})");
        _watching[pollUrl] = new WatchEntry(roleName, tenant.TenantId, source);
        WatchingChanged?.Invoke();

        try
        {
            var status = await tenant.WatchActivationAsync(pollUrl, source, _shutdownToken);

            var logLevel = status is "Provisioned" or "Granted" ? LogLevel.Info : LogLevel.Warning;
            AppLog.Add(logLevel, "Activation", $"'{roleName}' [{source}]: {status}");

            var (icon, msg) = status switch
            {
                "Provisioned" or "Granted"
                              => (ToolTipIcon.Info,    $"\u2713 {roleName} is now active."),
                "Denied"      => (ToolTipIcon.Warning, $"\u2717 {roleName} was denied."),
                "Failed"      => (ToolTipIcon.Error,   $"\u2717 {roleName} activation failed."),
                "Canceled"    => (ToolTipIcon.Warning, $"\u2717 {roleName} was canceled."),
                "Revoked"     => (ToolTipIcon.Warning, $"\u2717 {roleName} was revoked."),
                "TimedOut"    => (ToolTipIcon.Warning, $"\u26a0 {roleName}: polling stopped."),
                _             => (ToolTipIcon.Info,    $"{roleName}: {status}")
            };

            trayIcon.ShowActivationBalloon(msg, icon);
        }
        catch (OperationCanceledException) { /* app shutting down */ }
        catch (Exception ex)
        {
            AppLog.Error("Activation", $"Poll error for '{roleName}': {ex.Message}");
        }
        finally
        {
            _watching.TryRemove(pollUrl, out _);
            WatchingChanged?.Invoke();
            if (RefreshRequested is not null)
                _ = RefreshRequested.Invoke();
        }
    }
}
