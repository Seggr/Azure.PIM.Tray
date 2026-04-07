using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

internal sealed class GraphPimDataService : IAsyncDisposable
{
    private readonly TokenCredential _credential;
    private readonly HttpClient      _http;
    private readonly PimService      _pimService;

    private const string GraphBase     = "https://graph.microsoft.com/v1.0";
    private const string GraphBaseBeta = "https://graph.microsoft.com/beta";
    private const string GraphAudience = "https://graph.microsoft.com/.default";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private string? _myPrincipalId;

    public string TenantId          { get; }
    public string TenantDisplayName { get; }
    public string Email             { get; }

    public GraphPimDataService(TokenCredential credential, string tenantId, string tenantDisplayName, string email)
    {
        _credential       = credential;
        TenantId          = tenantId;
        TenantDisplayName = tenantDisplayName;
        Email             = email;

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        _pimService = new PimService(credential);
        _pimService.OnError = (src, msg) =>
            AppLog.Error($"{src} ({tenantDisplayName} / {email})", msg);
    }

    // ------------------------------------------------------------------
    // Pending approvals
    // ------------------------------------------------------------------

    public async Task<List<UnifiedPendingRequest>> GetEntraPendingAsync(
        CancellationToken ct = default)
    {
        AppLog.Debug($"Pending ({TenantDisplayName})", "Fetching Entra pending requests");
        var result    = new List<UnifiedPendingRequest>();
        var entraReqs = await _pimService.GetPendingRequestsAsync(ct);
        AppLog.Debug($"Pending ({TenantDisplayName})", $"Entra returned {entraReqs.Count} raw request(s)");
        foreach (var r in entraReqs)
            result.Add(new UnifiedPendingRequest
            {
                Source           = PimSource.EntraId,
                TenantId         = TenantId,
                PrincipalName    = r.Principal?.DisplayName
                                ?? r.Principal?.UserPrincipalName
                                ?? r.PrincipalId ?? "Unknown",
                RoleName         = r.RoleDefinition?.DisplayName
                                ?? r.RoleDefinitionId ?? "Unknown Role",
                ScopeDisplayName = r.DirectoryScopeId ?? "/",
                RequestType      = r.Action ?? "Unknown",
                Reason           = r.Justification ?? "",
                CreatedOn            = r.CreatedDateTime ?? DateTimeOffset.UtcNow,
                RequestorPrincipalId = r.PrincipalId,
                EntraApprovalId      = r.ApprovalId
            });
        return result;
    }

    // ------------------------------------------------------------------
    // Principal ID
    // ------------------------------------------------------------------

    public async Task<string?> GetMyPrincipalIdAsync(CancellationToken ct = default)
    {
        if (_myPrincipalId is not null) return _myPrincipalId;
        try
        {
            var token  = await GetGraphTokenAsync(ct);
            var me     = await GraphGetAsync<MeResult>(token,
                $"{GraphBase}/me?$select=id", ct);
            _myPrincipalId = me?.Id;
        }
        catch (Exception ex)
        {
            AppLog.Error($"GetMe ({TenantDisplayName})", $"Token or API call failed for {Email}: {ex.Message}");
            throw;
        }
        if (_myPrincipalId is null)
            AppLog.Warning($"GetMe ({TenantDisplayName})",
                $"API returned null principal ID for {Email}");
        else
            AppLog.Info($"GetMe ({TenantDisplayName})", $"Principal ID resolved for {Email}: {_myPrincipalId}");
        return _myPrincipalId;
    }

    // ------------------------------------------------------------------
    // Eligible roles
    // ------------------------------------------------------------------

    public async Task<List<UnifiedEligibleRole>> GetEntraEligibleRolesAsync(
        string myId, CancellationToken ct = default)
    {
        var result = new List<UnifiedEligibleRole>();
        var token  = await GetGraphTokenAsync(ct);
        var filter = Uri.EscapeDataString($"principalId eq '{myId}'");
        var url    = $"{GraphBase}/roleManagement/directory/roleEligibilitySchedules"
                   + $"?$filter={filter}&$expand=roleDefinition";
        var page   = await GraphGetAsync<ODataCollection<EntraEligibilitySchedule>>(
            token, url, ct);

        if (page is null)
            throw new HttpRequestException(
                $"Graph API failed to return eligible roles for {TenantDisplayName} / {Email} — see log for details");

        foreach (var s in page.Value ?? [])
        {
            if (s.RoleDefinition is null) continue;
            result.Add(new UnifiedEligibleRole
            {
                Source                = PimSource.EntraId,
                TenantId              = TenantId,
                RoleName              = s.RoleDefinition.DisplayName ?? s.RoleDefinitionId ?? "Unknown",
                ScopeDisplayName      = s.DirectoryScopeId ?? "/",
                EntraScheduleId       = s.Id,
                EntraRoleDefId        = s.RoleDefinitionId,
                EntraDirectoryScopeId = s.DirectoryScopeId,
                EntraPrincipalId      = myId
            });
        }

        // Supplemental: group-based eligibility via beta endpoint
        try
        {
            var directIds   = result.Select(r => r.EntraScheduleId).ToHashSet();
            var transFilter = Uri.EscapeDataString($"assignedTo('{myId}')");
            var transUrl    = $"{GraphBaseBeta}/roleManagement/directory/roleEligibilityScheduleInstances"
                            + $"?$filter={transFilter}&$expand=roleDefinition";
            var transPage   = await GraphGetAsyncOrThrow<ODataCollection<EntraEligibilitySchedule>>(
                token, transUrl, ct);

            foreach (var s in transPage?.Value ?? [])
            {
                if (s.RoleDefinition is null) continue;
                var scheduleId = s.Id;
                if (scheduleId is not null && directIds.Contains(scheduleId)) continue;
                result.Add(new UnifiedEligibleRole
                {
                    Source                = PimSource.EntraId,
                    TenantId              = TenantId,
                    RoleName              = s.RoleDefinition.DisplayName ?? s.RoleDefinitionId ?? "Unknown",
                    ScopeDisplayName      = s.DirectoryScopeId ?? "/",
                    EntraScheduleId       = scheduleId,
                    EntraRoleDefId        = s.RoleDefinitionId,
                    EntraDirectoryScopeId = s.DirectoryScopeId,
                    EntraPrincipalId      = myId
                });
            }
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode == 400)
        {
            AppLog.Info($"Entra Eligible ({TenantDisplayName})",
                "assignedTo() not supported (400) — group-based eligibility skipped");
        }

        AppLog.Info($"Entra Eligible ({TenantDisplayName})",
            $"Fetched {result.Count} eligible role(s)");
        return result;
    }

    // ------------------------------------------------------------------
    // Approve
    // ------------------------------------------------------------------

    public async Task<(bool Success, string Message)> ApproveAsync(
        UnifiedPendingRequest request, string justification, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.EntraApprovalId))
            return (false, "Request has no ApprovalId.");

        Approval? approval;
        try
        {
            approval = await _pimService.GetApprovalAsync(
                request.EntraApprovalId, "directory", ct);
        }
        catch (Exception ex) { return (false, ex.Message); }

        var inProgressSteps = approval?.Steps?.Where(s => s.Status == "InProgress").ToList() ?? [];
        var step = inProgressSteps.FirstOrDefault(s => s.AssignedToMe);

        if (step?.Id is null)
        {
            if (inProgressSteps.Count > 0)
                return (false, "This request requires approval by a different approver \u2014 it is not assigned to you.");
            return (false, "No pending approval step found for this request.");
        }

        try
        {
            var ok = await _pimService.ApproveStepAsync(
                request.EntraApprovalId, step.Id, justification, "directory", ct);
            return ok ? (true, "Approved.") : (false, "Approval call failed.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ------------------------------------------------------------------
    // Self-activate
    // ------------------------------------------------------------------

    public async Task<(bool Success, string Message, string? PollUrl)> ActivateRoleAsync(
        UnifiedEligibleRole role, TimeSpan duration, string justification, CancellationToken ct)
    {
        var myId = await GetMyPrincipalIdAsync(ct);
        if (myId is null) return (false, "Could not determine your user ID.", null);

        var token   = await GetGraphTokenAsync(ct);
        var url     = $"{GraphBase}/roleManagement/directory/roleAssignmentScheduleRequests";
        var payload = new
        {
            action             = "selfActivate",
            principalId        = myId,
            roleDefinitionId   = role.EntraRoleDefId,
            directoryScopeId   = role.EntraDirectoryScopeId ?? "/",
            justification,
            scheduleInfo = new
            {
                startDateTime = (string?)null,
                expiration = new
                {
                    type     = "afterDuration",
                    duration = $"PT{(int)duration.TotalMinutes}M"
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return (false, ExtractErrorMessage(body) ?? $"HTTP {(int)resp.StatusCode}", null);

        string? pollUrl = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("id", out var idEl)
                && idEl.GetString() is string reqId)
                pollUrl = $"{GraphBase}/roleManagement/directory" +
                          $"/roleAssignmentScheduleRequests/{reqId}?$select=id,status";
        }
        catch { /* best-effort */ }

        return (true, "Role activation requested.", pollUrl);
    }

    // ------------------------------------------------------------------
    // Policy check — is approval required?
    // ------------------------------------------------------------------

    public async Task<bool?> CheckApprovalRequiredAsync(string roleDefId, CancellationToken ct)
    {
        try
        {
            var token  = await GetGraphTokenAsync(ct);
            var filter = Uri.EscapeDataString(
                $"scopeId eq '/' and scopeType eq 'DirectoryRole' and roleDefinitionId eq '{roleDefId}'");
            var assignmentUrl = $"{GraphBase}/policies/roleManagementPolicyAssignments" +
                                $"?$filter={filter}&$select=policyId";

            var assignResp = await GraphGetAsync<ODataCollection<PolicyAssignment>>(token, assignmentUrl, ct);
            var policyId   = assignResp?.Value?.FirstOrDefault()?.PolicyId;
            if (policyId is null) return null;

            var ruleUrl  = $"{GraphBase}/policies/roleManagementPolicies/{policyId}/rules/Approval_EndUser_Assignment";
            var ruleResp = await GraphGetAsync<ApprovalRule>(token, ruleUrl, ct);
            return ruleResp?.Setting?.IsApprovalRequired;
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var pollCt = linked.Token;

        while (!pollCt.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), pollCt); }
            catch (OperationCanceledException) { break; }

            try
            {
                var token  = await GetGraphTokenAsync(pollCt);
                var resp   = await GraphGetAsync<EntraScheduleRequestStatus>(token, pollUrl, pollCt);
                var status = resp?.Status ?? "Unknown";

                if (status is "Provisioned" or "Granted" or "Denied" or "Failed" or "Revoked" or "Canceled")
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

    private async Task<string> GetGraphTokenAsync(CancellationToken ct)
    {
        AppLog.Debug($"Auth ({TenantDisplayName})", $"Acquiring Graph token for {Email}");
        var tr = await _credential.GetTokenAsync(
            new TokenRequestContext([GraphAudience]), ct);
        AppLog.Debug($"Auth ({TenantDisplayName})", $"Graph token acquired for {Email} (expires {tr.ExpiresOn:HH:mm:ss})");
        return tr.Token;
    }

    private async Task<T?> GraphGetAsync<T>(string token, string url, CancellationToken ct)
    {
        const int maxRetries = 3;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt == 0)
                AppLog.Debug($"HTTP ({TenantDisplayName})", $"GET {url}");
            else
                AppLog.Warning($"HTTP ({TenantDisplayName})", $"GET {url} (retry {attempt}/{maxRetries})");

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await _http.SendAsync(req, ct);

            if (resp.IsSuccessStatusCode)
            {
                AppLog.Debug($"HTTP ({TenantDisplayName})", $"{(int)resp.StatusCode} OK <- {url}");
                var body = await resp.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<T>(body, JsonOpts);
            }

            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxRetries)
            {
                var delay = resp.Headers.RetryAfter?.Delta
                         ?? (resp.Headers.TryGetValues("Retry-After", out var raVals)
                                 && int.TryParse(raVals.FirstOrDefault(), out var raSecs)
                             ? TimeSpan.FromSeconds(raSecs)
                             : (TimeSpan?)null)
                         ?? TimeSpan.FromSeconds(Math.Min(5 * Math.Pow(2, attempt), 60));
                if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                AppLog.Warning($"Graph GET 429 ({TenantDisplayName})",
                    $"Rate limited, waiting {delay.TotalSeconds:0}s (attempt {attempt + 1}/{maxRetries})\n  URL: {url}");
                await Task.Delay(delay, ct);
                continue;
            }

            var isServerError = (int)resp.StatusCode >= 500;
            if (isServerError && attempt < maxRetries)
            {
                var delay = TimeSpan.FromSeconds(3 * (attempt + 1));
                AppLog.Warning($"Graph GET {(int)resp.StatusCode} ({TenantDisplayName})",
                    $"Server error — retrying in {delay.TotalSeconds:0}s (attempt {attempt + 1}/{maxRetries})\n  URL: {url}");
                await Task.Delay(delay, ct);
                continue;
            }

            var errBody = await resp.Content.ReadAsStringAsync(ct);
            var errMsg  = ExtractErrorMessage(errBody) ?? "No error detail";
            AppLog.Error($"Graph GET {(int)resp.StatusCode} ({TenantDisplayName})",
                $"{errMsg}\n  URL: {url}");
            return default;
        }
        return default;
    }

    private async Task<T?> GraphGetAsyncOrThrow<T>(string token, string url, CancellationToken ct)
    {
        AppLog.Debug($"HTTP ({TenantDisplayName})", $"GET {url}");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                ExtractErrorMessage(body) ?? $"HTTP {(int)resp.StatusCode}",
                null, resp.StatusCode);
        }
        AppLog.Debug($"HTTP ({TenantDisplayName})", $"{(int)resp.StatusCode} OK <- {url}");
        var body2 = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(body2, JsonOpts);
    }

    internal static string? ExtractErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.TryGetProperty("message", out var m)) return m.GetString();
                if (err.TryGetProperty("code",    out var c)) return c.GetString();
            }
            if (doc.RootElement.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch { }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _pimService.DisposeAsync();
        _http.Dispose();
    }

    // ------------------------------------------------------------------
    // Private model classes
    // ------------------------------------------------------------------

    private sealed class MeResult
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
    }

    private sealed class EntraEligibilitySchedule
    {
        [JsonPropertyName("id")]               public string? Id               { get; init; }
        [JsonPropertyName("principalId")]      public string? PrincipalId      { get; init; }
        [JsonPropertyName("roleDefinitionId")] public string? RoleDefinitionId { get; init; }
        [JsonPropertyName("directoryScopeId")] public string? DirectoryScopeId { get; init; }
        [JsonPropertyName("roleDefinition")]   public RoleDefinition? RoleDefinition { get; init; }
    }

    private sealed class EntraScheduleRequestStatus
    {
        [JsonPropertyName("id")]     public string? Id     { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
    }

    private sealed class PolicyAssignment
    {
        [JsonPropertyName("policyId")] public string? PolicyId { get; init; }
    }

    private sealed class ApprovalRule
    {
        [JsonPropertyName("setting")] public ApprovalSetting? Setting { get; init; }
    }

    private sealed class ApprovalSetting
    {
        [JsonPropertyName("isApprovalRequired")] public bool? IsApprovalRequired { get; init; }
    }
}
