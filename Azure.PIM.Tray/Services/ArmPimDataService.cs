using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

internal sealed class ArmPimDataService : IAsyncDisposable
{
    private readonly TokenCredential _credential;
    private readonly HttpClient      _http;
    private readonly ArmPimService   _armService;
    private readonly HashSet<string> _excludedSubs;

    private const string ArmBase       = "https://management.azure.com";
    private const string ArmAudience   = "https://management.azure.com/.default";
    private const string ArmApiVersion = "2020-10-01";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string TenantId          { get; }
    public string TenantDisplayName { get; }
    public string Email             { get; }

    public ArmPimDataService(TokenCredential credential, string tenantId, string tenantDisplayName, string email,
        IReadOnlyList<string>? excludedSubscriptions = null)
    {
        _credential       = credential;
        TenantId          = tenantId;
        TenantDisplayName = tenantDisplayName;
        Email             = email;

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        _armService = new ArmPimService(credential);
        _armService.OnError = (src, msg) =>
            AppLog.Error($"{src} ({tenantDisplayName} / {email})", msg);
        _excludedSubs = excludedSubscriptions is { Count: > 0 }
            ? excludedSubscriptions.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        _armService.ExcludedSubscriptions = _excludedSubs;
    }

    // ------------------------------------------------------------------
    // Pending approvals
    // ------------------------------------------------------------------

    public async Task<List<UnifiedPendingRequest>> GetArmPendingAsync(
        IReadOnlySet<string>? relevantSubscriptions = null, CancellationToken ct = default)
    {
        AppLog.Debug($"Pending ({TenantDisplayName})", "Fetching ARM pending requests");
        var result  = new List<UnifiedPendingRequest>();
        var armReqs = await _armService.GetPendingRequestsAsync(relevantSubscriptions, ct);
        AppLog.Debug($"Pending ({TenantDisplayName})", $"ARM returned {armReqs.Count} raw request(s)");
        foreach (var r in armReqs)
            result.Add(new UnifiedPendingRequest
            {
                Source           = PimSource.AzureRbac,
                TenantId         = TenantId,
                PrincipalName    = r.Properties?.ExpandedProperties?.Principal?.DisplayName
                                ?? r.Properties?.PrincipalId ?? "Unknown",
                RoleName         = r.Properties?.ExpandedProperties?.RoleDefinition?.DisplayName
                                ?? r.Properties?.RoleDefinitionId ?? "Unknown Role",
                ScopeDisplayName = r.Properties?.ExpandedProperties?.Scope?.DisplayName
                                ?? r.ArmScope ?? "Unknown Scope",
                RequestType      = r.Properties?.RequestType ?? "Unknown",
                Reason           = r.Properties?.Justification ?? "",
                CreatedOn            = r.Properties?.CreatedOn ?? DateTimeOffset.UtcNow,
                RequestorPrincipalId = r.Properties?.PrincipalId,
                ArmRequest           = r
            });
        return result;
    }

    // ------------------------------------------------------------------
    // Eligible roles
    // ------------------------------------------------------------------

    /// <summary>
    /// Called with subscription IDs that returned zero eligible roles so callers can
    /// auto-exclude them from future scans.
    /// </summary>
    public Action<List<string>>? OnEmptySubscriptions { get; set; }

    public async Task<List<UnifiedEligibleRole>> GetArmEligibleRolesAsync(
        string myId, CancellationToken ct = default)
    {
        // Requires: user_impersonation (ARM) — list eligible Azure RBAC roles across subscriptions
        var token  = await GetArmTokenAsync(ct);
        var subs   = await ListSubscriptionsAsync(token, ct);
        AppLog.Info($"ARM Eligible ({TenantDisplayName})",
            $"Found {subs.Count} subscription(s) to scan");

        var result   = new List<UnifiedEligibleRole>();
        var emptySubs = new List<string>();

        const int batchSize = 2;
        foreach (var batch in subs.Chunk(batchSize))
        {
            var tasks = batch.Select(async subId =>
            {
                var scope  = $"/subscriptions/{subId}";
                var filter = Uri.EscapeDataString($"assignedTo('{myId}')");
                var url    = $"{ArmBase}{scope}/providers/Microsoft.Authorization"
                           + $"/roleEligibilitySchedules?api-version={ArmApiVersion}"
                           + $"&$filter={filter}&$expand=expandedProperties";

                var page = await ArmGetAsync<ArmCollection<ArmEligibilitySchedule>>(token, url, ct);
                var roles = (page?.Value ?? []).Select(s => new UnifiedEligibleRole
                {
                    Source                         = PimSource.AzureRbac,
                    TenantId                       = TenantId,
                    RoleName                       = s.Properties?.ExpandedProperties?.RoleDefinition?.DisplayName
                                                  ?? s.Properties?.RoleDefinitionId ?? "Unknown",
                    ScopeDisplayName               = s.Properties?.ExpandedProperties?.Scope?.DisplayName
                                                  ?? scope,
                    ArmScope                       = scope,
                    ArmRoleDefinitionId            = s.Properties?.RoleDefinitionId,
                    ArmPrincipalId                 = myId,
                    ArmLinkedEligibilityScheduleId = s.Id
                }).ToList();

                return (subId, roles);
            });

            foreach (var (subId, roles) in await Task.WhenAll(tasks))
            {
                if (roles.Count == 0)
                    emptySubs.Add(subId);
                else
                    result.AddRange(roles);
            }

            if (batch.Length == batchSize)
                await Task.Delay(500, ct);
        }

        if (emptySubs.Count > 0)
        {
            foreach (var subId in emptySubs)
                AppLog.Info($"ARM Eligible ({TenantDisplayName})",
                    $"Auto-excluding subscription {subId} \u2014 no eligible roles");
            OnEmptySubscriptions?.Invoke(emptySubs);
        }

        AppLog.Info($"ARM Eligible ({TenantDisplayName})",
            $"Fetched {result.Count} eligible role(s) across {subs.Count} subscription(s)" +
            (emptySubs.Count > 0 ? $" ({emptySubs.Count} excluded)" : ""));
        return result;
    }

    // ------------------------------------------------------------------
    // Approve
    // ------------------------------------------------------------------

    public async Task<(bool Success, string Message)> ApproveAsync(
        UnifiedPendingRequest request, string justification, CancellationToken ct)
    {
        var raw = request.ArmRequest;
        if (raw is null) return (false, "Missing ARM request data.");

        var scope      = raw.ArmScope
                      ?? raw.Properties?.ExpandedProperties?.Scope?.Id
                      ?? "unknown";
        var approvalId = raw.Properties?.ApprovalId;
        if (string.IsNullOrEmpty(approvalId))
            return (false, "Request has no ApprovalId.");

        ArmApproval? approval;
        try
        {
            approval = await _armService.GetApprovalAsync(scope, approvalId, ct);
        }
        catch (Exception ex) { return (false, ex.Message); }

        if (approval is null)
            return (false, "Could not retrieve approval object from ARM.");

        var pendingStages = approval.Properties?.ApprovalStages?
            .Where(s => s.Status is not ("Approved" or "Denied" or "Canceled" or "Completed"))
            .ToList() ?? [];
        var stage = pendingStages.FirstOrDefault(s => s.AssignedToMe)
                 ?? pendingStages.FirstOrDefault();

        if (stage?.ApprovalStageId is null)
            return (false, "No actionable stage found.");

        if (!stage.AssignedToMe)
            return (false, "This request requires approval by a different approver \u2014 it is not assigned to you.");

        try
        {
            var ok = await _armService.ApproveStageAsync(
                scope, approvalId, stage.ApprovalStageId, justification, ct);
            return ok ? (true, "Approved.") : (false, "Approval call failed.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ------------------------------------------------------------------
    // Self-activate
    // ------------------------------------------------------------------

    public async Task<(bool Success, string Message, string? PollUrl)> ActivateRoleAsync(
        UnifiedEligibleRole role, TimeSpan duration, string justification,
        string myId, CancellationToken ct)
    {
        // Requires: user_impersonation (ARM) — submit Azure RBAC self-activation requests
        var token     = await GetArmTokenAsync(ct);
        var scope     = role.ArmScope ?? "unknown";
        var requestId = Guid.NewGuid().ToString();
        var url       = $"{ArmBase}{scope}/providers/Microsoft.Authorization"
                      + $"/roleAssignmentScheduleRequests/{requestId}?api-version={ArmApiVersion}";

        var payload = new
        {
            properties = new
            {
                roleDefinitionId                = role.ArmRoleDefinitionId,
                principalId                     = myId,
                requestType                     = "SelfActivate",
                linkedRoleEligibilityScheduleId = role.ArmLinkedEligibilityScheduleId,
                justification,
                scheduleInfo = new
                {
                    startDateTime = (string?)null,
                    expiration = new
                    {
                        type     = "AfterDuration",
                        duration = $"PT{(int)duration.TotalMinutes}M"
                    }
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return (false, GraphPimDataService.ExtractErrorMessage(body) ?? $"HTTP {(int)resp.StatusCode}", null);

        return (true, "Role activation requested.", url);
    }

    // ------------------------------------------------------------------
    // Policy check — is approval required?
    // ------------------------------------------------------------------

    public async Task<bool?> CheckApprovalRequiredAsync(string scope, string roleDefId, CancellationToken ct)
    {
        // Requires: user_impersonation (ARM) — read ARM PIM policy to check if approval is needed
        try
        {
            var token  = await GetArmTokenAsync(ct);
            var filter = Uri.EscapeDataString($"roleDefinitionId eq '{roleDefId}'");
            var url    = $"{ArmBase}{scope}/providers/Microsoft.Authorization" +
                         $"/roleManagementPolicyAssignments?api-version={ArmApiVersion}&$filter={filter}";

            var assignResp = await ArmGetAsync<ArmCollection<ArmPolicyAssignment>>(token, url, ct);
            var policyId   = assignResp?.Value?.FirstOrDefault()?.Properties?.PolicyId;
            if (policyId is null) return null;

            var policyUrl  = $"{ArmBase}{policyId}?api-version={ArmApiVersion}";
            var policyResp = await ArmGetAsync<ArmPolicyResponse>(token, policyUrl, ct);
            var approvalRule = policyResp?.Properties?.EffectiveRules?
                .FirstOrDefault(r => string.Equals(r.Id, "Approval_EndUser_Assignment", StringComparison.OrdinalIgnoreCase));
            return approvalRule?.Setting?.IsApprovalRequired;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"ApprovalCheck ({TenantDisplayName})", $"Failed: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Activation polling
    // ------------------------------------------------------------------

    public async Task<string> PollActivationAsync(string pollUrl, CancellationToken ct)
    {
        // Requires: user_impersonation (ARM) — poll activation request status
        var started = DateTimeOffset.UtcNow;
        var slowPhase = false;

        while (!ct.IsCancellationRequested)
        {
            var elapsed = DateTimeOffset.UtcNow - started;
            if (!slowPhase && elapsed >= TimeSpan.FromMinutes(5))
            {
                slowPhase = true;
                AppLog.Debug($"Poll ({TenantDisplayName})", $"Switching to 60s poll interval for {pollUrl}");
            }

            var delay = slowPhase ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(10);

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }

            try
            {
                var token  = await GetArmTokenAsync(ct);
                var resp   = await ArmGetAsync<ArmScheduleRequestStatus>(token, pollUrl, ct);
                var status = resp?.Properties?.Status ?? "Unknown";

                if (status is "Provisioned" or "Granted" or "Denied" or "Failed" or "Revoked" or "Canceled")
                    return status;

                // In slow phase, stop polling if no longer pending approval
                if (slowPhase && status is not "PendingApproval")
                    return status;
            }
            catch (OperationCanceledException) { break; }
            catch { /* transient error — keep polling */ }
        }

        return "TimedOut";
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private async Task<string> GetArmTokenAsync(CancellationToken ct)
    {
        AppLog.Debug($"Auth ({TenantDisplayName})", $"Acquiring ARM token for {Email}");
        var tr = await _credential.GetTokenAsync(
            new TokenRequestContext([ArmAudience]), ct);
        AppLog.Debug($"Auth ({TenantDisplayName})", $"ARM token acquired for {Email} (expires {tr.ExpiresOn:HH:mm:ss})");
        return tr.Token;
    }

    /// <summary>
    /// Lists all enabled/warned subscriptions with nextLink pagination.
    /// </summary>
    private async Task<List<string>> ListSubscriptionsAsync(string armToken, CancellationToken ct)
    {
        var results = new List<string>();
        string? nextLink = $"{ArmBase}/subscriptions?api-version=2022-12-01";

        while (nextLink is not null)
        {
            var page = await ArmGetAsync<ArmCollection<SubscriptionEntry>>(armToken, nextLink, ct);
            if (page is null) break;

            results.AddRange(page.Value
                .Where(s => s.State is "Enabled" or "Warned"
                         && !string.IsNullOrEmpty(s.SubscriptionId)
                         && !_excludedSubs.Contains(s.SubscriptionId!))
                .Select(s => s.SubscriptionId!));

            nextLink = page.NextLink;
        }

        return results;
    }

    private async Task<T?> ArmGetAsync<T>(string token, string url, CancellationToken ct)
    {
        const int maxRetries = 6;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            AppLog.Debug($"HTTP ({TenantDisplayName})",
                attempt == 0 ? $"GET {url}" : $"GET {url} (retry {attempt}/{maxRetries})");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await _http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt == maxRetries)
                {
                    AppLog.Error($"ARM GET 429 ({TenantDisplayName})",
                        $"Rate limited after {maxRetries} retries\n  URL: {url}");
                    return default;
                }
                var retryAfter = resp.Headers.RetryAfter?.Delta
                              ?? (resp.Headers.TryGetValues("Retry-After", out var vals)
                                      && int.TryParse(vals.FirstOrDefault(), out var secs)
                                  ? TimeSpan.FromSeconds(secs)
                                  : (TimeSpan?)null)
                              ?? TimeSpan.FromSeconds(20 * Math.Pow(2, attempt));
                if (retryAfter < TimeSpan.FromSeconds(20))
                    retryAfter = TimeSpan.FromSeconds(20);
                AppLog.Warning($"ARM GET 429 ({TenantDisplayName})",
                    $"Rate limited, waiting {retryAfter.TotalSeconds:0}s (attempt {attempt + 1}/{maxRetries})");
                await Task.Delay(retryAfter, ct);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                var body   = await resp.Content.ReadAsStringAsync(ct);
                var errMsg = GraphPimDataService.ExtractErrorMessage(body) ?? "No error detail";
                AppLog.Error($"ARM GET {(int)resp.StatusCode} ({TenantDisplayName})",
                    $"{errMsg}\n  URL: {url}");
                return default;
            }

            var body3 = await resp.Content.ReadAsStringAsync(ct);
            AppLog.Debug($"HTTP ({TenantDisplayName})", $"{(int)resp.StatusCode} OK <- {url}");
            return JsonSerializer.Deserialize<T>(body3, JsonOpts);
        }
        return default;
    }

    public async ValueTask DisposeAsync()
    {
        await _armService.DisposeAsync();
        _http.Dispose();
    }

    // ------------------------------------------------------------------
    // Private model classes
    // ------------------------------------------------------------------

    private sealed class ArmEligibilitySchedule
    {
        [JsonPropertyName("id")]         public string? Id         { get; init; }
        [JsonPropertyName("name")]       public string? Name       { get; init; }
        [JsonPropertyName("properties")] public ArmEligibilityProps? Properties { get; init; }
    }

    private sealed class ArmEligibilityProps
    {
        [JsonPropertyName("principalId")]        public string? PrincipalId      { get; init; }
        [JsonPropertyName("roleDefinitionId")]   public string? RoleDefinitionId { get; init; }
        [JsonPropertyName("expandedProperties")] public ArmExpandedProperties? ExpandedProperties { get; init; }
    }

    private sealed class SubscriptionEntry
    {
        [JsonPropertyName("subscriptionId")] public string? SubscriptionId { get; init; }
        [JsonPropertyName("state")]          public string? State          { get; init; }
    }

    private sealed class ArmScheduleRequestStatus
    {
        [JsonPropertyName("properties")] public ArmScheduleRequestProps? Properties { get; init; }
    }

    private sealed class ArmScheduleRequestProps
    {
        [JsonPropertyName("status")] public string? Status { get; init; }
    }

    private sealed class ArmPolicyAssignment
    {
        [JsonPropertyName("properties")] public ArmPolicyAssignmentProps? Properties { get; init; }
    }

    private sealed class ArmPolicyAssignmentProps
    {
        [JsonPropertyName("policyId")] public string? PolicyId { get; init; }
    }

    private sealed class ArmPolicyResponse
    {
        [JsonPropertyName("properties")] public ArmPolicyProps? Properties { get; init; }
    }

    private sealed class ArmPolicyProps
    {
        [JsonPropertyName("effectiveRules")] public List<ArmPolicyRule>? EffectiveRules { get; init; }
    }

    private sealed class ArmPolicyRule
    {
        [JsonPropertyName("id")]      public string? Id      { get; init; }
        [JsonPropertyName("setting")] public ArmApprovalSetting? Setting { get; init; }
    }

    private sealed class ArmApprovalSetting
    {
        [JsonPropertyName("isApprovalRequired")] public bool? IsApprovalRequired { get; init; }
    }
}
