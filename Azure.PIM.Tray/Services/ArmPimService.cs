using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

/// <summary>
/// Handles Azure RBAC PIM approvals via Azure Resource Manager.
/// Tray-only: no console output, errors routed via <see cref="OnError"/>.
/// Includes nextLink pagination for subscription listing.
/// </summary>
internal sealed class ArmPimService : IAsyncDisposable
{
    private readonly TokenCredential _credential;
    private readonly HttpClient      _httpClient;

    private const string ArmBase            = "https://management.azure.com";
    private const string ArmAudience        = "https://management.azure.com/.default";
    private const string ApiVersion         = "2020-10-01";
    private const string ApprovalApiVersion = "2021-01-01-preview";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public Action<string, string>? OnError { get; set; }
    public IReadOnlySet<string> ExcludedSubscriptions { get; set; } = new HashSet<string>();

    public ArmPimService(TokenCredential credential)
    {
        _credential = credential;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<List<ArmRoleAssignmentScheduleRequest>> GetPendingRequestsAsync(
        IReadOnlySet<string>? relevantSubscriptions = null, CancellationToken ct = default)
    {
        var allSubs = await ListSubscriptionsAsync(ct);
        List<SubInfo> toScan;

        if (relevantSubscriptions is { Count: > 0 })
        {
            toScan = [];
            foreach (var sub in allSubs)
            {
                if (relevantSubscriptions.Contains(sub.Id))
                    toScan.Add(sub);
                else
                    AppLog.Debug("ARM Pending", $"Skipping subscription (no eligible roles): {sub.Name} ({sub.Id})");
            }
        }
        else
        {
            toScan = allSubs;
        }

        var results = new List<ArmRoleAssignmentScheduleRequest>();

        // Process in small batches with a pause between each to avoid ARM 429s.
        const int batchSize = 2;
        foreach (var batch in toScan.Chunk(batchSize))
        {
            var tasks = batch.Select(async sub =>
            {
                var scope = $"/subscriptions/{sub.Id}";
                try
                {
                    return await FetchPendingForScopeAsync(scope, ct);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
                {
                    return [];
                }
            });

            foreach (var list in await Task.WhenAll(tasks))
                results.AddRange(list);

            if (batch.Length == batchSize)
                await Task.Delay(500, ct);
        }

        return results;
    }

    public async Task<ArmApproval?> GetApprovalAsync(
        string scope, string approvalId, CancellationToken ct = default)
    {
        var url = approvalId.StartsWith("/providers/", StringComparison.OrdinalIgnoreCase)
            ? $"{ArmBase}{scope}{approvalId}?api-version={ApprovalApiVersion}"
            : $"{ArmBase}{scope}/providers/Microsoft.Authorization/roleAssignmentApprovals/{approvalId}?api-version={ApprovalApiVersion}";
        return await GetAsync<ArmApproval>(url, ct);
    }

    public async Task<bool> ApproveStageAsync(
        string scope, string approvalId, string stageId,
        string justification, CancellationToken ct = default)
    {
        var url = approvalId.StartsWith("/providers/", StringComparison.OrdinalIgnoreCase)
            ? $"{ArmBase}{scope}{approvalId}/stages/{stageId}?api-version={ApprovalApiVersion}"
            : $"{ArmBase}{scope}/providers/Microsoft.Authorization/roleAssignmentApprovals/{approvalId}/stages/{stageId}?api-version={ApprovalApiVersion}";

        return await PatchAsync(url, new
        {
            properties = new
            {
                reviewResult  = "Approve",
                justification
            }
        }, ct);
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private readonly record struct SubInfo(string Id, string Name);

    /// <summary>
    /// Lists all enabled/warned subscriptions with nextLink pagination.
    /// </summary>
    private async Task<List<SubInfo>> ListSubscriptionsAsync(CancellationToken ct)
    {
        var results = new List<SubInfo>();
        string? nextLink = $"{ArmBase}/subscriptions?api-version=2022-12-01";
        bool firstPage = true;

        while (nextLink is not null)
        {
            var page = await GetAsync<ArmCollection<SubscriptionEntry>>(nextLink, ct);
            if (page is null)
            {
                if (firstPage)
                    throw new HttpRequestException(
                        "Failed to list subscriptions (HTTP error) — check log for details");
                break;
            }

            firstPage = false;
            foreach (var s in page.Value.Where(s => s.State is "Enabled" or "Warned" && s.SubscriptionId is not null))
            {
                if (ExcludedSubscriptions.Contains(s.SubscriptionId!))
                {
                    AppLog.Debug("ARM", $"Subscription excluded by user: {s.DisplayName ?? s.SubscriptionId!} ({s.SubscriptionId})");
                    continue;
                }
                results.Add(new SubInfo(s.SubscriptionId!, s.DisplayName ?? s.SubscriptionId!));
            }

            nextLink = page.NextLink;
        }

        return results;
    }

    private async Task<List<ArmRoleAssignmentScheduleRequest>> FetchPendingForScopeAsync(
        string scope, CancellationToken ct)
    {
        var filter  = Uri.EscapeDataString("status eq 'PendingApproval'");
        var url     = $"{ArmBase}{scope}/providers/Microsoft.Authorization" +
                      $"/roleAssignmentScheduleRequests?api-version={ApiVersion}" +
                      $"&$filter={filter}&$expand=expandedProperties";

        var results = new List<ArmRoleAssignmentScheduleRequest>();
        string? nextLink = url;

        while (nextLink is not null)
        {
            var page = await GetAsync<ArmCollection<ArmRoleAssignmentScheduleRequest>>(nextLink, ct);
            if (page is null) break;

            foreach (var r in page.Value)
                r.ArmScope = scope;

            results.AddRange(page.Value);
            nextLink = page.NextLink;
        }

        return results;
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        var ctx = new TokenRequestContext([ArmAudience]);
        return (await _credential.GetTokenAsync(ctx, ct)).Token;
    }

    private static bool IsTransient(HttpStatusCode code) =>
        code is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static TimeSpan ParseRetryAfter(HttpResponseMessage resp, int attempt)
    {
        var delay = resp.Headers.RetryAfter?.Delta
                 ?? (resp.Headers.TryGetValues("Retry-After", out var vals)
                         && int.TryParse(vals.FirstOrDefault(), out var secs)
                     ? TimeSpan.FromSeconds(secs)
                     : (TimeSpan?)null)
                 ?? TimeSpan.FromSeconds(Math.Min(5 * Math.Pow(2, attempt), 60));
        return delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct, int maxRetries = 6)
    {
        const int baseDelayMs = 500;

        for (int attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound) return default;

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxRetries)
            {
                var delay = ParseRetryAfter(response, attempt);
                AppLog.Warning("ARM", $"Rate limited (429), waiting {delay.TotalSeconds:0}s (attempt {attempt + 1}/{maxRetries}): {url}");
                await Task.Delay(delay, ct);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                if (IsTransient(response.StatusCode) && attempt < maxRetries)
                {
                    var delay = baseDelayMs * (1 << attempt);
                    AppLog.Warning("ARM", $"Transient {(int)response.StatusCode} on attempt {attempt + 1}, retrying in {delay}ms: {url}");
                    await Task.Delay(delay, ct);
                    continue;
                }

                var errMsg = ExtractErrorMessage(body) ?? $"HTTP {(int)response.StatusCode}: {url}";
                OnError?.Invoke($"ARM GET {(int)response.StatusCode}", errMsg);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    throw new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden);
                return default;
            }

            return JsonSerializer.Deserialize<T>(body, JsonOpts);
        }
    }

    private async Task<bool> PatchAsync(string url, object payload, CancellationToken ct, int maxRetries = 6)
    {
        const int baseDelayMs = 500;
        var json = JsonSerializer.Serialize(payload, JsonOpts);

        for (int attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Patch, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxRetries)
            {
                var delay = ParseRetryAfter(response, attempt);
                AppLog.Warning("ARM", $"Rate limited (429), waiting {delay.TotalSeconds:0}s (attempt {attempt + 1}/{maxRetries}): {url}");
                await Task.Delay(delay, ct);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                if (IsTransient(response.StatusCode) && attempt < maxRetries)
                {
                    var delay = baseDelayMs * (1 << attempt);
                    AppLog.Warning("ARM", $"Transient {(int)response.StatusCode} on attempt {attempt + 1}, retrying in {delay}ms: {url}");
                    await Task.Delay(delay, ct);
                    continue;
                }

                var errMsg = ExtractErrorMessage(body) ?? $"HTTP {(int)response.StatusCode}: {url}";
                OnError?.Invoke($"ARM PATCH {(int)response.StatusCode}", errMsg);
                return false;
            }

            return true;
        }
    }

    internal static string? ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.TryGetProperty("message", out var m)) return m.GetString();
                if (err.TryGetProperty("code",    out var c)) return c.GetString();
            }
        }
        catch { }
        return null;
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class SubscriptionEntry
    {
        [JsonPropertyName("subscriptionId")] public string? SubscriptionId  { get; init; }
        [JsonPropertyName("displayName")]    public string? DisplayName     { get; init; }
        [JsonPropertyName("state")]          public string? State           { get; init; }
    }
}
