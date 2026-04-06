using System.IO;
using System.Text.Json;
using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

/// <summary>
/// Persists per-tenant eligible roles to disk so the tray app can show them
/// immediately on startup before the first network refresh completes.
/// </summary>
internal static class TenantRoleCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PimRequestManager", "cache");

    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private static string CachePath(string tenantId) =>
        Path.Combine(CacheDir, $"{tenantId}-eligible.json");

    public static List<UnifiedEligibleRole> Load(string tenantId, out bool isExpired)
    {
        isExpired = false;
        var path  = CachePath(tenantId);
        if (!File.Exists(path)) return [];
        try
        {
            var json    = File.ReadAllText(path);
            var wrapper = JsonSerializer.Deserialize<CacheWrapper>(json, JsonOpts);
            if (wrapper is null) return [];

            if (DateTimeOffset.UtcNow - wrapper.CachedAt > MaxAge)
            {
                isExpired = true;
                return wrapper.Roles ?? [];
            }

            return wrapper.Roles ?? [];
        }
        catch
        {
            AppLog.Warning("Cache", $"Corrupt cache file for tenant {tenantId} — will refresh from network");
            return [];
        }
    }

    public static void Save(string tenantId, IReadOnlyList<UnifiedEligibleRole> roles)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var wrapper = new CacheWrapper { CachedAt = DateTimeOffset.UtcNow, Roles = [.. roles] };
            File.WriteAllText(CachePath(tenantId),
                JsonSerializer.Serialize(wrapper, JsonOpts));
        }
        catch { /* non-fatal — disk cache is best-effort */ }
    }

    public static void Evict(string tenantId)
    {
        try { File.Delete(CachePath(tenantId)); }
        catch { /* non-fatal */ }
    }

    private sealed class CacheWrapper
    {
        public DateTimeOffset          CachedAt { get; set; }
        public List<UnifiedEligibleRole>? Roles { get; set; }
    }
}
