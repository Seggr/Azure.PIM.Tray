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

    public async Task<List<UnifiedEligibleRole>> GetArmEligibleRolesAsync(
        string myId, CancellationToken ct = default)
    {
        var token  = await GetArmTokenAsync(ct);
        var subs   = await ListSubscriptionsAsync(token, ct);
        AppLog.Info($"ARM Eligible ({TenantDisplayName})",
            $"Found {subs.Count} subscription(s) to scan");

        var result = new List<UnifiedEligibleRole>();

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
                return (page?.Value ?? []).Select(s => new UnifiedEligibleRole
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
            });

            foreach (var list in await Task.WhenAll(tasks))
                result.AddRange(list);

            if (batch.Length == batchSize)
                await Task.Delay(500, ct);
        }
        AppLog.Info($"ARM Eligible ({TenantDisplayName})",
            $"Fetched {result.Count} eligible role(s) across {subs.Count} subscription(s)");
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
    // Activation polling
    // ------------------------------------------------------------------

    public async Task<string> PollActivationAsync(string pollUrl, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var pollCt = linked.Token;

        while (!pollCt.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), pollCt); }
            catch (OperationCanceledException) { break; }

            try
            {
                var token  = await GetArmTokenAsync(pollCt);
                var resp   = await ArmGetAsync<ArmScheduleRequestStatus>(token, pollUrl, pollCt);
                var status = resp?.Properties?.Status ?? "Unknown";

                if (status is "Provisioned" or "Denied" or "Failed" or "Revoked" or "Canceled")
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
}
