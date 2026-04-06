using System.Collections.ObjectModel;
using System.Windows.Threading;
using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

public sealed class RefreshOrchestrator : IDisposable
{
    private static readonly TimeSpan PendingPollInterval  = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EligiblePollInterval = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Dispatcher _dispatcher;
    private readonly Func<List<ITenantContext>> _getTenants;
    private readonly CancellationToken _shutdownToken;

    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _eligibleTimer;

    public bool IsRefreshing => _refreshLock.CurrentCount == 0;
    public ObservableCollection<TenantStatusItem> RefreshItems { get; private set; } = [];

    /// <summary>
    /// Waits for any in-flight refresh to finish so callers can safely dispose old tenants.
    /// </summary>
    public async Task WaitForIdleAsync()
    {
        await _refreshLock.WaitAsync();
        _refreshLock.Release();
    }

    /// <summary>Raised on UI thread when new pending requests appear.</summary>
    public event Action<List<UnifiedPendingRequest>>? NewRequestsDetected;

    /// <summary>Raised on UI thread when a previously-pending request disappears.</summary>
    public event Action<UnifiedPendingRequest>? RequestCompleted;

    /// <summary>Raised on UI thread whenever pending count changes.</summary>
    public event Action<int>? PendingCountChanged;

    public RefreshOrchestrator(
        Dispatcher dispatcher,
        Func<List<ITenantContext>> getTenants,
        CancellationToken shutdownToken)
    {
        _dispatcher    = dispatcher;
        _getTenants    = getTenants;
        _shutdownToken = shutdownToken;
    }

    public void RebuildStatusItems(List<ITenantContext> tenants)
    {
        RefreshItems = new ObservableCollection<TenantStatusItem>(
            tenants.Select(t => new TenantStatusItem
            {
                TenantId   = t.TenantId,
                TenantName = t.TenantDisplayName,
                Status     = t.EligibleRoles.Count > 0
                    ? t.IsCacheExpired
                        ? $"Stale cache: {t.EligibleRoles.Count} eligible (refreshing\u2026)"
                        : $"Cached: {t.EligibleRoles.Count} eligible"
                    : "Idle"
            }));
    }

    public void StartTimers()
    {
        _pollTimer ??= new DispatcherTimer { Interval = PendingPollInterval };
        _pollTimer.Tick -= PollTick;
        _pollTimer.Tick += PollTick;
        _pollTimer.Start();

        _eligibleTimer ??= new DispatcherTimer { Interval = EligiblePollInterval };
        _eligibleTimer.Tick -= EligibleTick;
        _eligibleTimer.Tick += EligibleTick;
        _eligibleTimer.Start();
    }

    public void StopTimers()
    {
        _pollTimer?.Stop();
        _eligibleTimer?.Stop();
    }

    private async void PollTick(object? s, EventArgs e) => await RefreshPendingAsync();
    private async void EligibleTick(object? s, EventArgs e) => await RefreshEligibleAsync();

    public Task RefreshAsync() =>
        FanOutRefreshAsync((t, p, c) => t.RefreshAsync(p, c));

    public Task RefreshPendingAsync() =>
        FanOutRefreshAsync((t, p, c) => t.RefreshPendingAsync(p, c));

    public Task RefreshEligibleAsync() =>
        FanOutRefreshAsync((t, p, c) => t.RefreshEligibleAsync(p, c));

    private async Task FanOutRefreshAsync(
        Func<ITenantContext, IProgress<string>, CancellationToken, Task> perTenant)
    {
        var tenants = _getTenants();
        if (tenants.Count == 0) return;
        if (!await _refreshLock.WaitAsync(0, _shutdownToken)) return;

        await _dispatcher.InvokeAsync(() =>
        {
            foreach (var item in RefreshItems) item.Status = "Fetching...";
        });

        var previousById = tenants
            .SelectMany(t => t.PendingRequests)
            .Where(r => (r.EntraApprovalId ?? r.ArmRequest?.Name) is not null)
            .ToDictionary(
                r => (r.EntraApprovalId ?? r.ArmRequest!.Name)!,
                r => r,
                StringComparer.OrdinalIgnoreCase);

        try
        {
            var tasks = tenants.Select(tenant =>
            {
                var statusItem = RefreshItems.FirstOrDefault(x => x.TenantId == tenant.TenantId);
                var progress = new Progress<string>(msg =>
                {
                    if (statusItem is not null) statusItem.Status = msg;
                });
                return Task.Run(async () =>
                {
                    try { await perTenant(tenant, progress, _shutdownToken); }
                    catch (Exception ex)
                    {
                        AppLog.Warning("Refresh", $"{tenant.TenantDisplayName}: {ex.Message}");
                    }
                }, _shutdownToken);
            });

            await Task.WhenAll(tasks);

            var allPending = tenants.SelectMany(t => t.PendingRequests).ToList();
            var currentIds = allPending
                .Select(r => r.EntraApprovalId ?? r.ArmRequest?.Name)
                .Where(id => id is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var newRequests = allPending
                .Where(r => !previousById.ContainsKey(r.EntraApprovalId ?? r.ArmRequest?.Name ?? string.Empty))
                .ToList();
            var completedReqs = previousById.Values
                .Where(r => !currentIds.Contains(r.EntraApprovalId ?? r.ArmRequest?.Name ?? string.Empty))
                .ToList();

            await _dispatcher.InvokeAsync(() =>
            {
                PendingCountChanged?.Invoke(allPending.Count);
                if (newRequests.Count > 0)
                {
                    AppLog.Info("Pending", $"{newRequests.Count} new pending request(s) detected");
                    NewRequestsDetected?.Invoke(newRequests);
                }
                foreach (var req in completedReqs)
                {
                    AppLog.Info("Pending", $"Request completed: {req.PrincipalName} \u2014 {req.RoleName} [{req.Source}]");
                    RequestCompleted?.Invoke(req);
                }
            });
        }
        finally { _refreshLock.Release(); }
    }

    public async Task RefreshTenantAsync(ITenantContext tenant)
    {
        try
        {
            var previousById = tenant.PendingRequests
                .Where(r => (r.EntraApprovalId ?? r.ArmRequest?.Name) is not null)
                .ToDictionary(
                    r => (r.EntraApprovalId ?? r.ArmRequest!.Name)!,
                    r => r,
                    StringComparer.OrdinalIgnoreCase);

            var statusItem = RefreshItems.FirstOrDefault(x => x.TenantId == tenant.TenantId);
            var progress = new Progress<string>(msg =>
            {
                if (statusItem is not null) statusItem.Status = msg;
            });
            await tenant.RefreshAsync(progress, _shutdownToken);

            var currentIds = tenant.PendingRequests
                .Select(r => r.EntraApprovalId ?? r.ArmRequest?.Name)
                .Where(id => id is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var completedReqs = previousById.Values
                .Where(r => !currentIds.Contains(r.EntraApprovalId ?? r.ArmRequest?.Name ?? string.Empty))
                .ToList();

            await _dispatcher.InvokeAsync(() =>
            {
                PendingCountChanged?.Invoke(_getTenants().Sum(t => t.PendingRequests.Count));
                foreach (var req in completedReqs)
                    RequestCompleted?.Invoke(req);
            });
        }
        catch (Exception ex)
        {
            AppLog.Warning("Refresh", $"{tenant.TenantDisplayName}: {ex.Message}");
        }
    }

    public async Task RefreshPendingTenantAsync(ITenantContext tenant)
    {
        try
        {
            AppLog.Debug("Refresh", $"Pending refresh: {tenant.TenantDisplayName}");
            var statusItem = RefreshItems.FirstOrDefault(x => x.TenantId == tenant.TenantId);
            var progress = new Progress<string>(msg =>
            {
                if (statusItem is not null) statusItem.Status = msg;
            });
            await tenant.RefreshPendingAsync(progress, _shutdownToken);
            var count = tenant.PendingRequests.Count;
            AppLog.Info("Refresh", $"{tenant.TenantDisplayName}: {count} pending request(s) after refresh");
            await _dispatcher.InvokeAsync(() =>
                PendingCountChanged?.Invoke(_getTenants().Sum(t => t.PendingRequests.Count)));
        }
        catch (Exception ex)
        {
            AppLog.Warning("Refresh", $"{tenant.TenantDisplayName} pending: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _pollTimer?.Stop();
        _eligibleTimer?.Stop();
    }
}
