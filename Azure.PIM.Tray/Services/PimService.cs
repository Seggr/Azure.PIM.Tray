using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

/// <summary>
/// Wraps the Microsoft Graph PIM APIs for listing pending approval requests and approving them.
/// Tray-only: no console output, errors routed via <see cref="OnError"/>.
/// </summary>
internal sealed class PimService : IAsyncDisposable
{
    private readonly TokenCredential _credential;
    private readonly HttpClient      _httpClient;

    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web);

    private const string GraphBase     = "https://graph.microsoft.com/v1.0";
    private const string GraphBaseBeta = "https://graph.microsoft.com/beta";
    private const string GraphAudience = "https://graph.microsoft.com/.default";

    public Action<string, string>? OnError { get; set; }

    public PimService(TokenCredential credential)
    {
        _credential = credential;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<List<RoleAssignmentScheduleRequest>> GetPendingRequestsAsync(
        CancellationToken ct = default)
    {
        var url = GraphBase
            + "/roleManagement/directory/roleAssignmentScheduleRequests"
            + "?$filter=status eq 'PendingApproval'"
            + "&$expand=principal,roleDefinition";

        var results = new List<RoleAssignmentScheduleRequest>();
        string? nextLink = url;
        bool firstPage = true;

        while (nextLink is not null)
        {
            var page = await GetAsync<ODataCollection<RoleAssignmentScheduleRequest>>(
                nextLink, GraphAudience, ct);

            if (page is null)
            {
                if (firstPage)
                    throw new HttpRequestException(
                        "Failed to fetch pending requests (HTTP error) — check log for details");
                break;
            }

            firstPage = false;
            foreach (var r in page.Value)
                r.SourceNamespace = "directory";

            results.AddRange(page.Value);
            nextLink = page.NextLink;
        }

        return results;
    }

    public async Task<Approval?> GetApprovalAsync(
        string approvalId, string namespacePath, CancellationToken ct = default)
    {
        var url = $"{GraphBaseBeta}/roleManagement/{namespacePath}/roleAssignmentApprovals/{approvalId}?$expand=steps";
        return await GetAsync<Approval>(url, GraphAudience, ct);
    }

    public async Task<bool> ApproveStepAsync(
        string approvalId, string stepId, string justification,
        string namespacePath, CancellationToken ct = default)
    {
        var url = $"{GraphBaseBeta}/roleManagement/{namespacePath}/roleAssignmentApprovals/{approvalId}/steps/{stepId}";
        return await PatchAsync(url, GraphAudience,
            new { reviewResult = "Approve", justification }, ct);
    }

    // ------------------------------------------------------------------
    // HTTP helpers
    // ------------------------------------------------------------------

    private async Task<string> GetTokenAsync(string audience, CancellationToken ct)
    {
        var ctx = new TokenRequestContext([audience]);
        return (await _credential.GetTokenAsync(ctx, ct)).Token;
    }

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

    private async Task<T?> GetAsync<T>(string url, string audience, CancellationToken ct, int maxRetries = 3)
    {
        for (int attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(audience, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return default;

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxRetries)
            {
                var delay = ParseRetryAfter(response, attempt);
                OnError?.Invoke($"Graph GET 429",
                    $"Rate limited, waiting {delay.TotalSeconds:0}s (attempt {attempt + 1}/{maxRetries})\n  URL: {url}");
                await Task.Delay(delay, ct);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var errMsg = ExtractGraphErrorCode(body) ?? $"HTTP {(int)response.StatusCode}: {url}";
                OnError?.Invoke($"Graph GET {(int)response.StatusCode}", errMsg);

                if (response.StatusCode == HttpStatusCode.Forbidden)
                    throw new HttpRequestException(errMsg, null, HttpStatusCode.Forbidden);
                return default;
            }

            return JsonSerializer.Deserialize<T>(body, JsonOpts);
        }
    }

    private async Task<bool> PatchAsync(string url, string audience, object payload, CancellationToken ct, int maxRetries = 3)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);

        for (int attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(audience, ct);
            using var request = new HttpRequestMessage(HttpMethod.Patch, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxRetries)
            {
                var delay = ParseRetryAfter(response, attempt);
                OnError?.Invoke($"Graph PATCH 429",
                    $"Rate limited, waiting {delay.TotalSeconds:0}s (attempt {attempt + 1}/{maxRetries})\n  URL: {url}");
                await Task.Delay(delay, ct);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var errMsg = ExtractGraphErrorCode(body) ?? $"HTTP {(int)response.StatusCode}: {url}";
                OnError?.Invoke($"Graph PATCH {(int)response.StatusCode}", errMsg);
                return false;
            }

            return true;
        }
    }

    private static string? ExtractGraphErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.TryGetProperty("message", out var msg)) return msg.GetString();
                if (err.TryGetProperty("code",    out var code)) return code.GetString();
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
}
