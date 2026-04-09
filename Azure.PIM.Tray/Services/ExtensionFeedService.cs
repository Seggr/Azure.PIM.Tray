using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Azure.PIM.Tray.Services;

public sealed record ExtensionEntry
{
    [JsonPropertyName("id")]          public string  Id          { get; init; } = "";
    [JsonPropertyName("name")]        public string  Name        { get; init; } = "";
    [JsonPropertyName("description")] public string  Description { get; init; } = "";
    [JsonPropertyName("author")]      public string  Author      { get; init; } = "";
    [JsonPropertyName("version")]     public string  Version     { get; init; } = "";
    [JsonPropertyName("minAppVersion")]      public string? MinAppVersion      { get; init; }
    [JsonPropertyName("downloadUrl")]        public string  DownloadUrl        { get; init; } = "";
}

/// <summary>
/// Fetches extension metadata from feed URLs, and handles install/remove
/// of plugin DLLs in the plugins/ directory.
/// </summary>
public sealed class ExtensionFeedService : IDisposable
{
    private static readonly string DefaultFeed =
        "https://raw.githubusercontent.com/Seggr/Azure.PIM.Tray/main/feed/extensions.json";

    private readonly HttpClient _http = new();
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Fetches all extensions from the configured feeds (or the default feed).
    /// </summary>
    public async Task<List<ExtensionEntry>> FetchAvailableAsync(
        IReadOnlyList<string>? feedUrls = null, CancellationToken ct = default)
    {
        var feeds = feedUrls is { Count: > 0 } ? feedUrls : [DefaultFeed];
        var results = new List<ExtensionEntry>();

        foreach (var url in feeds)
        {
            try
            {
                AppLog.Debug("Extensions", $"Fetching feed: {url}");
                var json = await _http.GetStringAsync(url, ct);
                var entries = JsonSerializer.Deserialize<List<ExtensionEntry>>(json, JsonOpts);
                if (entries is not null)
                    results.AddRange(entries);
            }
            catch (Exception ex)
            {
                AppLog.Warning("Extensions", $"Failed to fetch feed {url}: {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>
    /// Returns the list of installed plugin DLL file names (without path).
    /// </summary>
    public static List<string> GetInstalledPlugins()
    {
        var dir = PluginLoader.PluginsDir;
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.dll")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();
    }

    /// <summary>
    /// Downloads a plugin DLL from the given URL into the plugins/ directory.
    /// </summary>
    public async Task<bool> InstallAsync(ExtensionEntry extension, CancellationToken ct = default)
    {
        try
        {
            var dir = PluginLoader.PluginsDir;
            Directory.CreateDirectory(dir);

            var fileName = $"{extension.Id}.dll";
            var destPath = Path.Combine(dir, fileName);

            AppLog.Info("Extensions", $"Downloading {extension.Name} v{extension.Version}...");
            var bytes = await _http.GetByteArrayAsync(extension.DownloadUrl, ct);
            await File.WriteAllBytesAsync(destPath, bytes, ct);

            AppLog.Info("Extensions", $"Installed {extension.Name} to {fileName}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Extensions", $"Failed to install {extension.Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes a plugin DLL from the plugins/ directory.
    /// </summary>
    public static bool Remove(string extensionId)
    {
        try
        {
            var path = Path.Combine(PluginLoader.PluginsDir, $"{extensionId}.dll");
            if (File.Exists(path))
            {
                File.Delete(path);
                AppLog.Info("Extensions", $"Removed {extensionId}");
                return true;
            }

            AppLog.Warning("Extensions", $"Plugin file not found: {path}");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error("Extensions", $"Failed to remove {extensionId}: {ex.Message}");
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
