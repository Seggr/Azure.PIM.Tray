using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

public interface ITenantContext : IAsyncDisposable
{
    string        TenantId          { get; }
    string        TenantDisplayName { get; }
    TrayConnection Connection        { get; }

    IReadOnlyList<UnifiedPendingRequest> PendingRequests { get; }
    IReadOnlyList<UnifiedEligibleRole> EligibleRoles { get; }
    IReadOnlySet<string> ActiveRoleNames { get; }
    string LastRefreshStatus { get; }
    bool IsCacheExpired { get; }

    event Action? DataChanged;

    Task RefreshPendingAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task RefreshEligibleAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task RefreshAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    Task<(bool Success, string Message)> ApproveAsync(
        UnifiedPendingRequest request, string justification, CancellationToken ct = default);

    Task<(bool Success, string Message, string? PollUrl)> ActivateRoleAsync(
        UnifiedEligibleRole role, TimeSpan duration, string justification,
        CancellationToken ct = default);

    Task<string> WatchActivationAsync(string pollUrl, PimSource source, CancellationToken ct = default);

    Task<bool?> CheckApprovalRequiredAsync(UnifiedEligibleRole role, CancellationToken ct = default);
}
