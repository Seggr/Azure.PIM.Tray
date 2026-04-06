using Azure.Core;
using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

internal sealed class PimDataService : IAsyncDisposable
{
    private readonly GraphPimDataService _graph;
    private readonly ArmPimDataService   _arm;

    public string TenantId          { get; }
    public string TenantDisplayName { get; }
    public string Email             { get; }

    public PimDataService(TokenCredential credential, string tenantId, string tenantDisplayName, string email,
        IReadOnlyList<string>? excludedSubscriptions = null)
    {
        TenantId          = tenantId;
        TenantDisplayName = tenantDisplayName;
        Email             = email;
        _graph = new GraphPimDataService(credential, tenantId, tenantDisplayName, email);
        _arm   = new ArmPimDataService(credential, tenantId, tenantDisplayName, email, excludedSubscriptions);
    }

    public Task<List<UnifiedPendingRequest>> GetEntraPendingAsync(CancellationToken ct = default)
        => _graph.GetEntraPendingAsync(ct);

    public Task<List<UnifiedPendingRequest>> GetArmPendingAsync(
        IReadOnlySet<string>? relevantSubscriptions = null, CancellationToken ct = default)
        => _arm.GetArmPendingAsync(relevantSubscriptions, ct);

    public Task<string?> GetMyPrincipalIdAsync(CancellationToken ct = default)
        => _graph.GetMyPrincipalIdAsync(ct);

    public Task<List<UnifiedEligibleRole>> GetEntraEligibleRolesAsync(
        string myId, CancellationToken ct = default)
        => _graph.GetEntraEligibleRolesAsync(myId, ct);

    public Task<List<UnifiedEligibleRole>> GetArmEligibleRolesAsync(
        string myId, CancellationToken ct = default)
        => _arm.GetArmEligibleRolesAsync(myId, ct);

    public Task<(bool Success, string Message)> ApproveAsync(
        UnifiedPendingRequest request, string justification, CancellationToken ct = default)
        => request.Source == PimSource.EntraId
            ? _graph.ApproveAsync(request, justification, ct)
            : _arm.ApproveAsync(request, justification, ct);

    public async Task<(bool Success, string Message, string? PollUrl)> ActivateRoleAsync(
        UnifiedEligibleRole role, TimeSpan duration, string justification,
        CancellationToken ct = default)
    {
        if (role.Source == PimSource.EntraId)
            return await _graph.ActivateRoleAsync(role, duration, justification, ct);

        var myId = await _graph.GetMyPrincipalIdAsync(ct);
        if (myId is null) return (false, "Could not determine your user ID.", null);
        return await _arm.ActivateRoleAsync(role, duration, justification, myId, ct);
    }

    public Task<string> PollActivationAsync(
        string pollUrl, PimSource source, CancellationToken ct = default)
        => source == PimSource.AzureRbac
            ? _arm.PollActivationAsync(pollUrl, ct)
            : _graph.PollActivationAsync(pollUrl, ct);

    public async ValueTask DisposeAsync()
    {
        await _graph.DisposeAsync();
        await _arm.DisposeAsync();
    }
}
