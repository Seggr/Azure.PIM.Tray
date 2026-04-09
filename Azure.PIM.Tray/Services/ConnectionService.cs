using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

internal static class ConnectionService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PimRequestManager");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "tray-config.json");

    private const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    private const string AppDisplayName   = "PIM Request Manager";
    private const string GraphBase        = "https://graph.microsoft.com/v1.0";

    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string MicrosoftGraphAppId   = "00000003-0000-0000-c000-000000000000";
    private const string AzureServiceMgmtAppId = "797f4846-ba00-4fd7-ba43-dac1f8f63013";

    private static readonly string[] RequiredGraphScopes =
    [
        "User.Read",
        "RoleAssignmentSchedule.ReadWrite.Directory",
        "RoleEligibilitySchedule.Read.Directory",
        "PrivilegedAccess.ReadWrite.AzureAD",
        "RoleManagement.Read.Directory"
    ];

    // ------------------------------------------------------------------
    // Config persistence
    // ------------------------------------------------------------------

    public static TrayAppConfig LoadConfig()
    {
        if (!File.Exists(ConfigPath)) return new TrayAppConfig();
        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<TrayAppConfig>(json, JsonOpts) ?? new TrayAppConfig();
        }
        catch { return new TrayAppConfig(); }
    }

    public static void SaveConfig(TrayAppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOpts));
    }

    // ------------------------------------------------------------------
    // Subscription listing (for ManageWindow)
    // ------------------------------------------------------------------

    public record SubscriptionInfo(string Id, string DisplayName);

    private static string SubCachePath(string tenantId) =>
        Path.Combine(ConfigDir, $"subs-{tenantId}.json");

    /// <summary>
    /// Returns cached subscriptions immediately if available, or fetches from ARM and caches.
    /// </summary>
    public static async Task<List<SubscriptionInfo>> ListSubscriptionsAsync(
        TrayConnection connection, CancellationToken ct = default)
    {
        // Try cache first
        var cachePath = SubCachePath(connection.TenantId);
        if (File.Exists(cachePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<List<SubscriptionInfo>>(
                    await File.ReadAllTextAsync(cachePath, ct), JsonOpts);
                if (cached is { Count: > 0 })
                {
                    // Refresh in background for next time
                    _ = Task.Run(() => FetchAndCacheSubscriptionsAsync(connection, cachePath, CancellationToken.None));
                    return cached;
                }
            }
            catch { /* cache corrupt — fetch fresh */ }
        }

        return await FetchAndCacheSubscriptionsAsync(connection, cachePath, ct);
    }

    private static async Task<List<SubscriptionInfo>> FetchAndCacheSubscriptionsAsync(
        TrayConnection connection, string cachePath, CancellationToken ct)
    {
        var cred  = CreateCredential(connection);
        var token = (await cred.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]), ct)).Token;

        var results = new List<SubscriptionInfo>();
        string? nextLink = "https://management.azure.com/subscriptions?api-version=2022-12-01";

        using var http = new HttpClient();
        while (nextLink is not null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, nextLink);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) break;

            var body = await resp.Content.ReadAsStringAsync(ct);
            var doc  = JsonDocument.Parse(body);

            foreach (var sub in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var state = sub.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (state is not ("Enabled" or "Warned")) continue;
                var id   = sub.GetProperty("subscriptionId").GetString();
                var name = sub.TryGetProperty("displayName", out var n) ? n.GetString() : id;
                if (id is not null)
                    results.Add(new SubscriptionInfo(id, name ?? id));
            }

            nextLink = doc.RootElement.TryGetProperty("nextLink", out var nl) ? nl.GetString() : null;
        }

        var sorted = results.OrderBy(s => s.DisplayName).ToList();

        // Cache to disk
        try
        {
            Directory.CreateDirectory(ConfigDir);
            await File.WriteAllTextAsync(cachePath,
                JsonSerializer.Serialize(sorted, JsonOpts), CancellationToken.None);
        }
        catch { /* non-critical */ }

        return sorted;
    }

    // ------------------------------------------------------------------
    // Discovery
    // ------------------------------------------------------------------

    public static async Task<TrayConnection> DiscoverAsync(
        string tenantId, string email, CancellationToken ct = default)
    {
        var cacheName = $"PimRequestManager_AdminCheck_{tenantId}";
        var cacheOpts = new TokenCachePersistenceOptions { Name = cacheName };

        var cred = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            ClientId                     = AzureCliClientId,
            TenantId                     = tenantId,
            LoginHint                    = email,
            TokenCachePersistenceOptions = cacheOpts
        });

        var token = (await cred.GetTokenAsync(
            new TokenRequestContext(["https://graph.microsoft.com/.default"]), ct)).Token;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Add("ConsistencyLevel", "eventual");

        var filter   = Uri.EscapeDataString($"displayName eq '{AppDisplayName}'");
        var resp     = await http.GetAsync(
            $"{GraphBase}/applications?$filter={filter}&$select=appId,displayName,notes", ct);
        var body     = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Graph query failed ({resp.StatusCode}): {body}");

        using var doc  = JsonDocument.Parse(body);
        var apps       = doc.RootElement.GetProperty("value");
        string? appId  = null;
        foreach (var app in apps.EnumerateArray())
        {
            appId = app.TryGetProperty("appId", out var v) ? v.GetString() : null;
            if (appId is not null) break;
        }

        if (appId is null)
            throw new InvalidOperationException(
                $"No app registration named \"{AppDisplayName}\" found in tenant {tenantId}. " +
                $"Run the provisioner from the console app first.");

        string? tenantName = null;
        try
        {
            var orgResp = await http.GetAsync(
                $"{GraphBase}/organization?$select=displayName", ct);
            if (orgResp.IsSuccessStatusCode)
            {
                var orgBody = await orgResp.Content.ReadAsStringAsync(ct);
                using var orgDoc = JsonDocument.Parse(orgBody);
                if (orgDoc.RootElement.TryGetProperty("value", out var orgs)
                    && orgs.GetArrayLength() > 0
                    && orgs[0].TryGetProperty("displayName", out var dn))
                    tenantName = dn.GetString();
            }
        }
        catch { /* non-fatal */ }

        return new TrayConnection
        {
            TenantId          = tenantId,
            Email             = email,
            ClientId          = appId,
            TenantDisplayName = tenantName ?? tenantId
        };
    }

    // ------------------------------------------------------------------
    // Credential factory
    // ------------------------------------------------------------------

    public static TokenCredential CreateCredential(TrayConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.ClientId))
            throw new InvalidOperationException(
                "Connection has no ClientId — run discovery first.");

        var cacheName = $"PimRequestManager_{connection.ClientId}_{connection.TenantId}";
        var cacheOpts = new TokenCachePersistenceOptions { Name = cacheName };

        return new SerializedTokenCredential(
            new ChainedTokenCredential(
                new SharedTokenCacheCredential(new SharedTokenCacheCredentialOptions
                {
                    ClientId                     = connection.ClientId,
                    TenantId                     = connection.TenantId,
                    Username                     = connection.Email,
                    TokenCachePersistenceOptions = cacheOpts
                }),
                new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
                {
                    ClientId                     = connection.ClientId,
                    TenantId                     = connection.TenantId,
                    LoginHint                    = connection.Email,
                    TokenCachePersistenceOptions = cacheOpts
                })
            ));
    }

    // ------------------------------------------------------------------
    // Interactive sign-in
    // ------------------------------------------------------------------

    public static async Task SignInAsync(TrayConnection connection, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connection.ClientId))
            throw new InvalidOperationException("Connection has no ClientId.");

        var cacheName = $"PimRequestManager_{connection.ClientId}_{connection.TenantId}";
        var cacheOpts = new TokenCachePersistenceOptions { Name = cacheName };

        var cred = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            ClientId                     = connection.ClientId,
            TenantId                     = connection.TenantId,
            LoginHint                    = connection.Email,
            TokenCachePersistenceOptions = cacheOpts
        });

        await cred.GetTokenAsync(
            new TokenRequestContext(["https://graph.microsoft.com/.default"]), ct);
    }

    /// <summary>
    /// Forces a fresh interactive sign-in for both Graph and ARM scopes,
    /// ensuring the token cache is populated with tokens that include all
    /// currently consented permissions.
    /// </summary>
    public static async Task ReloadTokensAsync(TrayConnection connection, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connection.ClientId))
            throw new InvalidOperationException("Connection has no ClientId.");

        var cacheName = $"PimRequestManager_{connection.ClientId}_{connection.TenantId}";
        var cacheOpts = new TokenCachePersistenceOptions { Name = cacheName };

        var cred = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            ClientId                     = connection.ClientId,
            TenantId                     = connection.TenantId,
            LoginHint                    = connection.Email,
            TokenCachePersistenceOptions = cacheOpts
        });

        AppLog.Info("Auth", $"Reloading Graph token for {connection.Email}...");
        await cred.GetTokenAsync(
            new TokenRequestContext(["https://graph.microsoft.com/.default"]), ct);

        AppLog.Info("Auth", $"Reloading ARM token for {connection.Email}...");
        await cred.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]), ct);

        AppLog.Info("Auth", $"Token reload complete for {connection.Email}");
    }

    // ------------------------------------------------------------------
    // Permission check
    // ------------------------------------------------------------------

    public static async Task<string> CheckPermissionsAsync(
        TrayConnection connection, IReadOnlyList<string>? additionalGraphScopes = null,
        IReadOnlyDictionary<string, string>? additionalScopeIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connection.ClientId))
            return "No app registered";

        string token;
        try { token = await GetAdminTokenAsync(connection, ct); }
        catch (Exception ex) { return $"Auth failed: {ex.Message}"; }

        using var http = BuildAdminHttpClient(token);

        var filter = Uri.EscapeDataString($"appId eq '{connection.ClientId}'");
        var appResp = await http.GetAsync(
            $"{GraphBase}/applications?$filter={filter}&$select=id,appId,requiredResourceAccess", ct);
        if (!appResp.IsSuccessStatusCode)
        {
            var errBody = await appResp.Content.ReadAsStringAsync(ct);
            return $"Graph query failed — {DescribeFailure(appResp.StatusCode, errBody)}";
        }

        var appBody = await appResp.Content.ReadAsStringAsync(ct);
        using var appDoc = JsonDocument.Parse(appBody);
        if (!appDoc.RootElement.TryGetProperty("value", out var appArr)
            || appArr.GetArrayLength() == 0)
            return "App not found";

        var app = appArr[0];

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasArmScope  = false;
        if (app.TryGetProperty("requiredResourceAccess", out var rraArr))
        {
            foreach (var rra in rraArr.EnumerateArray())
            {
                var resId = rra.TryGetProperty("resourceAppId", out var rid) ? rid.GetString() : null;
                if (rra.TryGetProperty("resourceAccess", out var raArr))
                {
                    foreach (var ra in raArr.EnumerateArray())
                    {
                        var id = ra.TryGetProperty("id", out var v) ? v.GetString() : null;
                        if (id is null) continue;
                        if (string.Equals(resId, MicrosoftGraphAppId, StringComparison.OrdinalIgnoreCase))
                            registeredIds.Add(id);
                        else if (string.Equals(resId, AzureServiceMgmtAppId, StringComparison.OrdinalIgnoreCase))
                            hasArmScope = true;
                    }
                }
            }
        }

        var spResp = await http.GetAsync(
            $"{GraphBase}/servicePrincipals?$filter=appId eq '{MicrosoftGraphAppId}'&$select=id,oauth2PermissionScopes",
            ct);
        if (!spResp.IsSuccessStatusCode)
        {
            var errBody = await spResp.Content.ReadAsStringAsync(ct);
            return $"Graph SP lookup failed — {DescribeFailure(spResp.StatusCode, errBody)}";
        }

        var spBody = await spResp.Content.ReadAsStringAsync(ct);
        using var spDoc = JsonDocument.Parse(spBody);
        var spArr     = spDoc.RootElement.GetProperty("value");
        var graphSpId = spArr[0].GetProperty("id").GetString()!;

        var nameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (spArr[0].TryGetProperty("oauth2PermissionScopes", out var scopes))
        {
            foreach (var s in scopes.EnumerateArray())
            {
                var id  = s.TryGetProperty("id",    out var i) ? i.GetString() : null;
                var val = s.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (id is not null && val is not null) nameToId[val] = id;
            }
        }

        var allCheckScopes = additionalGraphScopes is { Count: > 0 }
            ? RequiredGraphScopes.Concat(additionalGraphScopes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : RequiredGraphScopes;

        var missingRegistration = allCheckScopes
            .Where(name =>
            {
                var id = nameToId.TryGetValue(name, out var sid) ? sid
                       : additionalScopeIds is not null && additionalScopeIds.TryGetValue(name, out var pId) ? pId
                       : null;
                return id is not null && !registeredIds.Contains(id);
            })
            .ToList();

        var spFilter   = Uri.EscapeDataString($"appId eq '{connection.ClientId}'");
        var appSpResp  = await http.GetAsync(
            $"{GraphBase}/servicePrincipals?$filter={spFilter}&$select=id", ct);
        var grantedScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (appSpResp.IsSuccessStatusCode)
        {
            var appSpBody = await appSpResp.Content.ReadAsStringAsync(ct);
            using var appSpDoc = JsonDocument.Parse(appSpBody);
            var appSpArr  = appSpDoc.RootElement.GetProperty("value");
            if (appSpArr.GetArrayLength() > 0)
            {
                var appSpId    = appSpArr[0].GetProperty("id").GetString()!;
                var grantFilter = Uri.EscapeDataString(
                    $"clientId eq '{appSpId}' and resourceId eq '{graphSpId}'");
                var grantResp  = await http.GetAsync(
                    $"{GraphBase}/oauth2PermissionGrants?$filter={grantFilter}", ct);
                if (grantResp.IsSuccessStatusCode)
                {
                    var grantBody = await grantResp.Content.ReadAsStringAsync(ct);
                    using var grantDoc = JsonDocument.Parse(grantBody);
                    var grants    = grantDoc.RootElement.GetProperty("value");
                    if (grants.GetArrayLength() > 0
                        && grants[0].TryGetProperty("scope", out var scopeVal))
                    {
                        foreach (var s in scopeVal.GetString()!
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            grantedScopes.Add(s);
                    }
                }
            }
        }

        var missingConsent = allCheckScopes
            .Where(name => !grantedScopes.Contains(name))
            .ToList();

        if (missingRegistration.Count == 0 && missingConsent.Count == 0 && hasArmScope)
            return "\u2713 OK";

        var issues = new List<string>();
        if (!hasArmScope)
            issues.Add("ARM scope missing");
        if (missingRegistration.Count > 0)
            issues.Add($"Not registered: {string.Join(", ", missingRegistration)}");
        if (missingConsent.Count > 0)
            issues.Add($"No consent: {string.Join(", ", missingConsent)}");

        return string.Join(" | ", issues);
    }

    // ------------------------------------------------------------------
    // Fix permissions
    // ------------------------------------------------------------------

    public static async Task<string> FixPermissionsAsync(
        TrayConnection connection, IReadOnlyList<string>? additionalGraphScopes = null,
        IReadOnlyDictionary<string, string>? additionalScopeIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connection.ClientId))
            return "No app registered";

        string token;
        try { token = await GetAdminTokenAsync(connection, ct); }
        catch (Exception ex) { return $"Auth failed: {ex.Message}"; }

        using var http = BuildAdminHttpClient(token);

        var filter  = Uri.EscapeDataString($"appId eq '{connection.ClientId}'");
        var appResp = await http.GetAsync(
            $"{GraphBase}/applications?$filter={filter}&$select=id,appId,requiredResourceAccess", ct);
        if (!appResp.IsSuccessStatusCode)
        {
            var b = await appResp.Content.ReadAsStringAsync(ct);
            return $"App lookup failed — {DescribeFailure(appResp.StatusCode, b)}";
        }
        var appRoot    = JsonNode.Parse(await appResp.Content.ReadAsStringAsync(ct))!;
        var appArrNode = appRoot["value"]!.AsArray();
        if (appArrNode.Count == 0) return "App not found in tenant";
        var appNode     = appArrNode[0]!.AsObject();
        var appObjectId = appNode["id"]!.GetValue<string>();

        var graphSpResp = await http.GetAsync(
            $"{GraphBase}/servicePrincipals?$filter=appId eq '{MicrosoftGraphAppId}'" +
            "&$select=id,oauth2PermissionScopes", ct);
        if (!graphSpResp.IsSuccessStatusCode)
        {
            var b = await graphSpResp.Content.ReadAsStringAsync(ct);
            return $"Graph SP lookup failed — {DescribeFailure(graphSpResp.StatusCode, b)}";
        }
        var graphSpRoot = JsonNode.Parse(await graphSpResp.Content.ReadAsStringAsync(ct))!;
        var graphSpArr  = graphSpRoot["value"]!.AsArray();
        var graphSpId   = graphSpArr[0]!["id"]!.GetValue<string>();
        var nameToId    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (graphSpArr[0]!["oauth2PermissionScopes"] is JsonArray scopesArr)
            foreach (var s in scopesArr)
            {
                var id  = s?["id"]?.GetValue<string>();
                var val = s?["value"]?.GetValue<string>();
                if (id is not null && val is not null) nameToId[val] = id;
            }

        var armSpResp = await http.GetAsync(
            $"{GraphBase}/servicePrincipals?$filter=appId eq '{AzureServiceMgmtAppId}'" +
            "&$select=id,oauth2PermissionScopes", ct);
        string? armSpId    = null;
        string  armScopeId = "41094075-9dad-400e-a0bd-54e686782033";
        if (armSpResp.IsSuccessStatusCode)
        {
            var armSpRoot = JsonNode.Parse(await armSpResp.Content.ReadAsStringAsync(ct))!;
            var armSpArr  = armSpRoot["value"]!.AsArray();
            if (armSpArr.Count > 0)
            {
                armSpId = armSpArr[0]!["id"]?.GetValue<string>();
                if (armSpArr[0]!["oauth2PermissionScopes"] is JsonArray armScopes)
                    foreach (var s in armScopes)
                        if (s?["value"]?.GetValue<string>() == "user_impersonation"
                            && s["id"]?.GetValue<string>() is string sid)
                        { armScopeId = sid; break; }
            }
        }

        // Build exact required permissions — replaces everything (removes stale/legacy)
        var allScopes = additionalGraphScopes is { Count: > 0 }
            ? RequiredGraphScopes.Concat(additionalGraphScopes).Distinct(StringComparer.OrdinalIgnoreCase)
            : RequiredGraphScopes.AsEnumerable();
        AppLog.Debug("FixPermissions", $"Graph SP has {nameToId.Count} delegated scopes available");
        var graphAccess = new List<(string Id, string Type)>();
        foreach (var name in allScopes)
        {
            if (nameToId.TryGetValue(name, out var sid))
                graphAccess.Add((sid, "Scope"));
            else if (additionalScopeIds is not null && additionalScopeIds.TryGetValue(name, out var pluginId))
            {
                graphAccess.Add((pluginId, "Scope"));
                AppLog.Info("FixPermissions", $"Scope '{name}' not in Graph SP \u2014 using plugin-supplied ID {pluginId}");
            }
            else
                AppLog.Warning("FixPermissions", $"Scope '{name}' not found \u2014 skipped");
        }

        static JsonObject MakeRra(string appId, IEnumerable<(string Id, string Type)> items) => new()
        {
            ["resourceAppId"] = appId,
            ["resourceAccess"] = new JsonArray(items
                .Select(x => (JsonNode)new JsonObject { ["id"] = x.Id, ["type"] = x.Type })
                .ToArray())
        };
        var updatedRra = new JsonArray();
        updatedRra.Add(MakeRra(MicrosoftGraphAppId, graphAccess));
        if (armSpId is not null)
            updatedRra.Add(MakeRra(AzureServiceMgmtAppId, [(armScopeId, "Scope")]));

        var patchPayload = new JsonObject { ["requiredResourceAccess"] = updatedRra }.ToJsonString();
        AppLog.Debug("FixPermissions", $"PATCH /applications/{appObjectId}: {patchPayload}");

        using var patchReq = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"{GraphBase}/applications/{appObjectId}")
        {
            Content = new StringContent(patchPayload, Encoding.UTF8, "application/json")
        };
        var patchResp = await http.SendAsync(patchReq, ct);
        if (!patchResp.IsSuccessStatusCode)
        {
            var b = await patchResp.Content.ReadAsStringAsync(ct);
            return $"Failed to update permissions — {DescribeFailure(patchResp.StatusCode, b)}";
        }
        AppLog.Info("FixPermissions", $"Updated app manifest: {graphAccess.Count} Graph + 1 ARM scope(s)");

        var appSpFilter = Uri.EscapeDataString($"appId eq '{connection.ClientId}'");
        var appSpResp   = await http.GetAsync(
            $"{GraphBase}/servicePrincipals?$filter={appSpFilter}&$select=id", ct);
        if (!appSpResp.IsSuccessStatusCode)
            return "Permissions registered — admin consent step failed (app SP not found)";
        var appSpRoot = JsonNode.Parse(await appSpResp.Content.ReadAsStringAsync(ct))!;
        var appSpArr  = appSpRoot["value"]!.AsArray();
        if (appSpArr.Count == 0)
            return "Permissions registered — app service principal not found; grant consent in Azure Portal";
        var appSpId = appSpArr[0]!["id"]!.GetValue<string>();

        var graphScopes = string.Join(" ", allScopes);
        AppLog.Info("FixPermissions", $"Setting Graph consent to: {graphScopes}");
        await GrantAdminConsentAsync(http, appSpId, graphSpId, graphScopes, ct);
        if (armSpId is not null)
        {
            AppLog.Info("FixPermissions", "Setting ARM consent to: user_impersonation");
            await GrantAdminConsentAsync(http, appSpId, armSpId, "user_impersonation", ct);
        }

        return "\u2713 Permissions updated and admin consent granted (removed stale permissions)";
    }

    private static async Task GrantAdminConsentAsync(
        HttpClient http, string appSpId, string resourceSpId, string scopes, CancellationToken ct)
    {
        var grantFilter = Uri.EscapeDataString(
            $"clientId eq '{appSpId}' and resourceId eq '{resourceSpId}'");
        var listResp = await http.GetAsync(
            $"{GraphBase}/oauth2PermissionGrants?$filter={grantFilter}", ct);
        if (listResp.IsSuccessStatusCode)
        {
            var listRoot = JsonNode.Parse(await listResp.Content.ReadAsStringAsync(ct))!;
            var grants   = listRoot["value"]!.AsArray();
            if (grants.Count > 0)
            {
                var grantId = grants[0]!["id"]!.GetValue<string>();
                // Replace with exact required scopes (removes stale permissions)
                await http.PatchAsync(
                    $"{GraphBase}/oauth2PermissionGrants/{grantId}",
                    new StringContent(
                        new JsonObject { ["scope"] = scopes }.ToJsonString(),
                        Encoding.UTF8, "application/json"),
                    ct);
                return;
            }
        }
        await http.PostAsync(
            $"{GraphBase}/oauth2PermissionGrants",
            new StringContent(
                new JsonObject
                {
                    ["clientId"]    = appSpId,
                    ["consentType"] = "AllPrincipals",
                    ["resourceId"]  = resourceSpId,
                    ["scope"]       = scopes
                }.ToJsonString(),
                Encoding.UTF8, "application/json"),
            ct);
    }

    // ------------------------------------------------------------------
    // Shared admin credential / HTTP helpers
    // ------------------------------------------------------------------

    private static async Task<string> GetAdminTokenAsync(
        TrayConnection connection, CancellationToken ct)
    {
        var cacheName = $"PimRequestManager_AdminCheck_{connection.TenantId}";
        var cacheOpts = new TokenCachePersistenceOptions { Name = cacheName };
        var cred = new ChainedTokenCredential(
            new SharedTokenCacheCredential(new SharedTokenCacheCredentialOptions
            {
                ClientId                     = AzureCliClientId,
                TenantId                     = connection.TenantId,
                Username                     = connection.Email,
                TokenCachePersistenceOptions = cacheOpts
            }),
            new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
            {
                ClientId                     = AzureCliClientId,
                TenantId                     = connection.TenantId,
                LoginHint                    = connection.Email,
                TokenCachePersistenceOptions = cacheOpts
            })
        );
        return (await cred.GetTokenAsync(
            new TokenRequestContext(["https://graph.microsoft.com/.default"]), ct)).Token;
    }

    private static HttpClient BuildAdminHttpClient(string token)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Add("ConsistencyLevel", "eventual");
        return http;
    }

    private static string DescribeFailure(System.Net.HttpStatusCode status, string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("code",    out var c) ? c.GetString() : null;
                var msg  = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (code is not null && msg is not null) return $"{(int)status} {code}: {msg}";
                if (msg  is not null) return $"{(int)status}: {msg}";
                if (code is not null) return $"{(int)status} {code}";
            }
        }
        catch { }
        return $"HTTP {(int)status}";
    }
}
