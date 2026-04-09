using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.PIM.Tray.Extensibility;

namespace Azure.PIM.Tray.Ext.Laps;

public sealed class LapsPlugin : ITrayPlugin
{
    public string Id   => "Azure.PIM.Tray.Ext.Laps";
    public string Name => "\ud83d\udd10  LAPS Passwords";
    public IReadOnlyList<string> RequiredGraphPermissions => ["DeviceLocalCredential.Read.All"];
    public IReadOnlyDictionary<string, string> RequiredGraphPermissionIds => new Dictionary<string, string>
    {
        ["DeviceLocalCredential.Read.All"] = "280b3b69-0437-44b1-bc20-3b2fca1ee3e9"
    };
    public IReadOnlyList<string> RequiredRoles => ["Cloud Device Administrator"];

    private readonly ConcurrentDictionary<string, TenantState> _tenants = new();
    private readonly HttpClient _http;

    private const string GraphBase     = "https://graph.microsoft.com/v1.0";
    private const string GraphAudience = "https://graph.microsoft.com/.default";

    public LapsPlugin()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Azure.PIM.Tray.Ext.Laps/1.0");
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task InitializeAsync(PluginTenantContext tenant, CancellationToken ct = default)
    {
        tenant.Log(PluginLogLevel.Info, "LAPS",
            $"Initializing for {tenant.TenantDisplayName}");

        var state = new TenantState(tenant);
        _tenants[tenant.TenantId] = state;

        // Probe for LAPS support — try to fetch one device
        try
        {
            await state.LoadDevicesAsync(_http, ct);

            if (state.Devices.Count == 0)
            {
                state.LapsDetected = false;
                tenant.Log(PluginLogLevel.Info, "LAPS",
                    $"No LAPS devices found for {tenant.TenantDisplayName} \u2014 disabling for this tenant");
            }
            else
            {
                state.LapsDetected = true;
                tenant.Log(PluginLogLevel.Info, "LAPS",
                    $"Loaded {state.Devices.Count} LAPS device(s) for {tenant.TenantDisplayName}");
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden
                                                             or System.Net.HttpStatusCode.Unauthorized)
        {
            // Permission denied — LAPS may exist but we can't access it yet
            state.LapsDetected = false;
            tenant.Log(PluginLogLevel.Warning, "LAPS",
                $"No access to LAPS for {tenant.TenantDisplayName} (403) \u2014 check permissions");
        }
        catch (Exception ex)
        {
            state.LapsDetected = false;
            tenant.Log(PluginLogLevel.Warning, "LAPS",
                $"Failed to detect LAPS for {tenant.TenantDisplayName}: {ex.Message}");
        }
    }

    public bool IsAvailable(string tenantId)
    {
        return _tenants.TryGetValue(tenantId, out var state) && state.LapsDetected;
    }

    public void BuildMenu(IPluginMenuBuilder menu, string tenantId)
    {
        if (!_tenants.TryGetValue(tenantId, out var state)) return;
        if (!state.LapsDetected) return;

        menu.AddSubmenu($"\ud83d\udd10  LAPS Passwords ({state.Tenant.TenantDisplayName})", sub =>
        {
            var allDevices = state.Devices;

            if (allDevices.Count == 0)
            {
                sub.AddItem("(no LAPS devices found)", isDisabled: true);
                sub.AddItem("\u21ba  Reload devices", onClick: () =>
                {
                    sub.CloseMenu();
                    _ = Task.Run(async () =>
                    {
                        state.Tenant.Log(PluginLogLevel.Info, "LAPS",
                            $"Reloading devices for {state.Tenant.TenantDisplayName}...");
                        try
                        {
                            await state.LoadDevicesAsync(_http);
                            state.LapsDetected = state.Devices.Count > 0;
                            state.Tenant.Log(PluginLogLevel.Info, "LAPS",
                                $"Reloaded {state.Devices.Count} device(s) for {state.Tenant.TenantDisplayName}");
                        }
                        catch (Exception ex)
                        {
                            state.Tenant.Log(PluginLogLevel.Error, "LAPS",
                                $"Reload failed for {state.Tenant.TenantDisplayName}: {ex.Message}");
                        }
                    });
                });
                return;
            }

            sub.AddSearchBox("Search devices...", query =>
                RebuildDeviceList(sub, state, allDevices, query));

            RebuildDeviceList(sub, state, allDevices, "");
        });
    }

    private void RebuildDeviceList(
        IPluginMenuBuilder sub, TenantState state,
        List<LapsDevice> allDevices, string query)
    {
        sub.RemoveItemsAfter(1);

        var filtered = string.IsNullOrWhiteSpace(query)
            ? allDevices
            : allDevices.Where(d =>
                d.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        if (filtered.Count == 0)
        {
            sub.AddItem("(no matches)", isDisabled: true);
            return;
        }

        foreach (var device in filtered.Take(50))
        {
            var d = device;
            var menuRef = sub;
            sub.AddItem(d.DisplayName, onClick: () =>
            {
                state.Tenant.Log(PluginLogLevel.Info, "LAPS",
                    $"Retrieving password for {d.DisplayName}...");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var password = await GetPasswordAsync(state.Tenant, d.DeviceId);
                        if (password is not null)
                        {
                            menuRef.CopyAndNotify(password,
                                $"\ud83d\udd10 LAPS password for {d.DisplayName} copied to clipboard.");
                            state.Tenant.Log(PluginLogLevel.Info, "LAPS",
                                $"Password for {d.DisplayName} copied to clipboard");
                        }
                        else
                        {
                            state.Tenant.Log(PluginLogLevel.Warning, "LAPS",
                                $"No password returned for {d.DisplayName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        state.Tenant.Log(PluginLogLevel.Error, "LAPS",
                            $"Failed to retrieve password for {d.DisplayName}: {ex.Message}");
                    }
                });
            });
        }

        if (filtered.Count > 50)
            sub.AddItem($"({filtered.Count - 50} more \u2014 refine search)", isDisabled: true);
    }

    private async Task<string?> GetPasswordAsync(PluginTenantContext tenant, string deviceId)
    {
        // Requires: DeviceLocalCredential.Read.All — retrieve device local admin password
        var token = (await tenant.Credential.GetTokenAsync(
            new TokenRequestContext([GraphAudience]), default)).Token;

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{GraphBase}/directory/deviceLocalCredentials/{deviceId}?$select=credentials");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("User-Agent", "Azure.PIM.Tray.Ext.Laps/1.0");

        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            tenant.Log(PluginLogLevel.Error, "LAPS",
                $"GET deviceLocalCredentials/{deviceId} returned {(int)resp.StatusCode}: {body}");
            return null;
        }

        var respBody = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respBody);

        if (doc.RootElement.TryGetProperty("credentials", out var creds) &&
            creds.GetArrayLength() > 0)
        {
            var latest = creds[creds.GetArrayLength() - 1];
            if (latest.TryGetProperty("passwordBase64", out var pw))
            {
                var bytes = Convert.FromBase64String(pw.GetString()!);
                return System.Text.Encoding.Unicode.GetString(bytes);
            }
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------

    internal sealed class TenantState
    {
        public PluginTenantContext Tenant { get; }
        public List<LapsDevice> Devices { get; private set; } = [];
        public bool LapsDetected { get; set; }

        public TenantState(PluginTenantContext tenant) => Tenant = tenant;

        public async Task LoadDevicesAsync(HttpClient http, CancellationToken ct = default)
        {
            // Requires: DeviceLocalCredential.Read.All — list LAPS-managed devices
            Tenant.Log(PluginLogLevel.Debug, "LAPS",
                $"Fetching device list for {Tenant.TenantDisplayName}...");

            var token = (await Tenant.Credential.GetTokenAsync(
                new TokenRequestContext([GraphAudience]), ct)).Token;

            var devices = new List<LapsDevice>();
            string? nextLink = $"{GraphBase}/directory/deviceLocalCredentials?$select=id,deviceName&$top=999";

            while (nextLink is not null)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, nextLink);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.TryAddWithoutValidation("User-Agent", "Azure.PIM.Tray.Ext.Laps/1.0");

                var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    Tenant.Log(PluginLogLevel.Error, "LAPS",
                        $"Device list fetch failed ({(int)resp.StatusCode}): {body}");
                    throw new HttpRequestException(
                        $"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
                }

                var respBody = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(respBody);

                if (doc.RootElement.TryGetProperty("value", out var arr))
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var id   = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                        var name = item.TryGetProperty("deviceName", out var nameProp) ? nameProp.GetString() : null;
                        if (id is not null && name is not null)
                            devices.Add(new LapsDevice(id, name));
                    }
                }

                nextLink = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl)
                    ? nl.GetString() : null;
            }

            Devices = devices.OrderBy(d => d.DisplayName).ToList();
            Tenant.Log(PluginLogLevel.Debug, "LAPS",
                $"Device list loaded: {Devices.Count} device(s)");
        }
    }

    internal sealed record LapsDevice(string DeviceId, string DisplayName);
}
