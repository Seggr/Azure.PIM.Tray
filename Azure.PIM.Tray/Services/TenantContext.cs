using Azure.Core;
using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

public sealed class TenantContext : ITenantContext
{
    private readonly PimDataService  _svc;
    private readonly Action<IReadOnlyList<UnifiedEligibleRole>>? _onCacheSave;
    private readonly SemaphoreSlim   _refreshLock         = new(1, 1);
    private readonly SemaphoreSlim   _eligibleRefreshLock = new(1, 1);

    private volatile IReadOnlyList<UnifiedPendingRequest> _entraPending  = [];
    private volatile IReadOnlyList<UnifiedPendingRequest> _armPending    = [];
    private volatile IReadOnlyList<UnifiedEligibleRole>   _entraEligible;
    private volatile IReadOnlyList<UnifiedEligibleRole>   _armEligible   = [];

    private volatile string _lastRefreshStatus = "Not refreshed yet";

    public event Action? DataChanged;

    public string         TenantId          => Connection.TenantId;
    public string         TenantDisplayName => Connection.TenantDisplayName ?? Connection.TenantId;
    public TrayConnection Connection         { get; }

    public bool IsCacheExpired { get; }

    public IReadOnlyList<UnifiedPendingRequest> PendingRequests =>
        _entraPending.Count == 0 ? _armPending :
        _armPending.Count   == 0 ? _entraPending :
        [.. _entraPending, .. _armPending];

    public IReadOnlyList<UnifiedEligibleRole> EligibleRoles =>
        _entraEligible.Count == 0 ? _armEligible :
        _armEligible.Count   == 0 ? _entraEligible :
        [.. _entraEligible, .. _armEligible];

    public string LastRefreshStatus => _lastRefreshStatus;

    public TenantContext(TrayConnection connection, TokenCredential credential,
        Action<IReadOnlyList<UnifiedEligibleRole>>? onCacheSave = null,
        Action<TrayConnection>? onConnectionChanged = null)
    {
        Connection   = connection;
        _onCacheSave = onCacheSave;

        var cached = TenantRoleCache.Load(connection.TenantId, out bool isExpired);
        IsCacheExpired  = isExpired;
        _entraEligible  = cached.Where(r => r.Source == PimSource.EntraId).ToList();
        _armEligible    = cached.Where(r => r.Source == PimSource.AzureRbac).ToList();

        if (cached.Count > 0)
            AppLog.Info($"Cache ({connection.TenantDisplayName ?? connection.TenantId})",
                $"Loaded {cached.Count} eligible role(s) from disk{(isExpired ? " (expired)" : "")}");

        _svc = new PimDataService(credential, connection.TenantId,
            connection.TenantDisplayName ?? connection.TenantId,
            connection.Email,
            connection.ExcludedSubscriptions);

        _svc.OnEmptySubscriptions = emptySubs =>
        {
            var current = connection.ExcludedSubscriptions.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = emptySubs.Where(id => current.Add(id)).ToList();
            if (added.Count == 0) return;

            var updated = connection with { ExcludedSubscriptions = [.. current] };
            onConnectionChanged?.Invoke(updated);
        };
    }

    public async Task RefreshPendingAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!await _refreshLock.WaitAsync(0, ct))
        {
            AppLog.Debug($"Pending ({TenantDisplayName})", "Skipped — refresh already in progress");
            return;
        }
        try
        {
            AppLog.Info($"Pending ({TenantDisplayName})", "Refresh started");
            progress?.Report("Fetching pending\u2026");

            string? entraErr = null, armErr = null;

            async Task FetchEntraPending()
            {
                try   { _entraPending = await _svc.GetEntraPendingAsync(ct); }
                catch (Exception ex)
                {
                    entraErr = ex.Message;
                    AppLog.Error($"Entra Pending ({TenantDisplayName})", ex.Message);
                }
                DataChanged?.Invoke();
            }
            async Task FetchArmPending()
            {
                // Only scan subscriptions where we have eligible roles
                var relevantSubs = _armEligible
                    .Select(r => r.ArmScope)
                    .Where(s => s is not null && s.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
                    .Select(s => s!.Split('/')[2])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                try   { _armPending = await _svc.GetArmPendingAsync(relevantSubs.Count > 0 ? relevantSubs : null, ct); }
                catch (Exception ex)
                {
                    armErr = ex.Message;
                    AppLog.Error($"ARM Pending ({TenantDisplayName})", ex.Message);
                }
                DataChanged?.Invoke();
            }

            await Task.WhenAll(FetchEntraPending(), FetchArmPending());

            // Filter out requests made by the current user (can't self-approve)
            try
            {
                var myId = await _svc.GetMyPrincipalIdAsync(ct);
                if (myId is not null)
                {
                    var beforeEntra = _entraPending.Count;
                    var beforeArm   = _armPending.Count;
                    _entraPending = _entraPending
                        .Where(r => !string.Equals(r.RequestorPrincipalId, myId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    _armPending = _armPending
                        .Where(r => !string.Equals(r.RequestorPrincipalId, myId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var filtered = (beforeEntra - _entraPending.Count) + (beforeArm - _armPending.Count);
                    if (filtered > 0)
                        AppLog.Debug($"Pending ({TenantDisplayName})", $"Filtered out {filtered} self-request(s)");
                    DataChanged?.Invoke();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Pending ({TenantDisplayName})", $"Could not resolve user ID for self-filter: {ex.Message}");
            }

            var total = _entraPending.Count + _armPending.Count;
            _lastRefreshStatus = entraErr is null && armErr is null
                ? $"\u2713 {total} pending ({_entraPending.Count} Entra / {_armPending.Count} ARM)"
                : entraErr is not null && armErr is not null
                    ? $"\u274c Both sources failed \u2014 {entraErr}"
                    : total > 0
                        ? $"\u26a0 {total} pending (partial \u2014 {entraErr ?? armErr})"
                        : $"\u274c {entraErr ?? armErr}";
            AppLog.Info($"Pending ({TenantDisplayName})", _lastRefreshStatus);
            progress?.Report(_lastRefreshStatus);
        }
        catch (Exception ex)
        {
            _lastRefreshStatus = $"\u274c {ex.Message}";
            progress?.Report(_lastRefreshStatus);
            throw;
        }
        finally { _refreshLock.Release(); }
    }

    public async Task RefreshEligibleAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!await _eligibleRefreshLock.WaitAsync(0, ct))
        {
            AppLog.Debug($"Eligible ({TenantDisplayName})", "Skipped — refresh already in progress");
            return;
        }
        try
        {
            AppLog.Info($"Eligible ({TenantDisplayName})", "Refresh started");
            progress?.Report("Fetching eligible roles\u2026");

            var myId = await _svc.GetMyPrincipalIdAsync(ct)
                ?? throw new InvalidOperationException("Could not resolve user identity.");

            string? entraErr = null, armErr = null;

            async Task FetchEntraEligible()
            {
                try
                {
                    _entraEligible = await _svc.GetEntraEligibleRolesAsync(myId, ct);
                    DataChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    entraErr = ex.Message;
                    AppLog.Error($"Entra Eligible ({TenantDisplayName})", ex.Message);
                }
            }
            async Task FetchArmEligible()
            {
                try
                {
                    _armEligible = await _svc.GetArmEligibleRolesAsync(myId, ct);
                    DataChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    armErr = ex.Message;
                    AppLog.Error($"ARM Eligible ({TenantDisplayName})", ex.Message);
                }
            }

            await Task.WhenAll(FetchEntraEligible(), FetchArmEligible());
            _onCacheSave?.Invoke(EligibleRoles.ToList());

            var total = _entraEligible.Count + _armEligible.Count;
            _lastRefreshStatus = entraErr is null && armErr is null
                ? $"\u2713 {total} eligible ({_entraEligible.Count} Entra / {_armEligible.Count} ARM)"
                : entraErr is not null && armErr is not null
                    ? $"\u274c Both sources failed \u2014 {entraErr}"
                    : total > 0
                        ? $"\u26a0 {total} eligible (partial \u2014 {entraErr ?? armErr})"
                        : $"\u274c {entraErr ?? armErr}";
            AppLog.Info($"Eligible ({TenantDisplayName})", _lastRefreshStatus);
            progress?.Report(_lastRefreshStatus);
        }
        catch (Exception ex)
        {
            _lastRefreshStatus = $"\u274c {ex.Message}";
            progress?.Report(_lastRefreshStatus);
            throw;
        }
        finally { _eligibleRefreshLock.Release(); }
    }

    public async Task RefreshAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await Task.WhenAll(
            RefreshPendingAsync(progress, ct),
            RefreshEligibleAsync(null, ct));
    }

    public async Task<(bool Success, string Message)> ApproveAsync(
        UnifiedPendingRequest request, string justification, CancellationToken ct = default)
    {
        AppLog.Info($"Approve ({TenantDisplayName})",
            $"Approving: {request.PrincipalName} \u2014 {request.RoleName} [{request.Source}]");
        var result = await _svc.ApproveAsync(request, justification, ct);
        if (result.Success)
            AppLog.Info($"Approve ({TenantDisplayName})", "Approved successfully");
        else
            AppLog.Warning($"Approve ({TenantDisplayName})", result.Message);
        return result;
    }

    public async Task<(bool Success, string Message, string? PollUrl)> ActivateRoleAsync(
        UnifiedEligibleRole role, TimeSpan duration, string justification,
        CancellationToken ct = default)
    {
        AppLog.Info($"Activate ({TenantDisplayName})",
            $"Requesting activation: {role.RoleName} [{role.Source}] for {(int)duration.TotalMinutes} min");
        var result = await _svc.ActivateRoleAsync(role, duration, justification, ct);
        if (result.Success)
            AppLog.Info($"Activate ({TenantDisplayName})", $"Request submitted \u2014 poll URL: {result.PollUrl ?? "none"}");
        else
            AppLog.Warning($"Activate ({TenantDisplayName})", result.Message);
        return result;
    }

    public Task<string> WatchActivationAsync(
        string pollUrl, PimSource source, CancellationToken ct = default)
        => _svc.PollActivationAsync(pollUrl, source, ct);

    public Task<bool?> CheckApprovalRequiredAsync(
        UnifiedEligibleRole role, CancellationToken ct = default)
        => _svc.CheckApprovalRequiredAsync(role, ct);

    public ValueTask DisposeAsync()
    {
        _refreshLock.Dispose();
        _eligibleRefreshLock.Dispose();
        return _svc.DisposeAsync();
    }
}
