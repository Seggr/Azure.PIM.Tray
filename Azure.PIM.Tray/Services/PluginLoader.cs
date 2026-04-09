using System.IO;
using System.Runtime.Loader;
using Azure.PIM.Tray.Extensibility;

namespace Azure.PIM.Tray.Services;

/// <summary>
/// Discovers, loads, and manages <see cref="ITrayPlugin"/> instances
/// from DLLs in the plugins/ directory next to the application.
/// </summary>
public sealed class PluginLoader : IAsyncDisposable
{
    private readonly List<ITrayPlugin> _plugins = [];

    public IReadOnlyList<ITrayPlugin> Plugins => _plugins;

    public static string PluginsDir =>
        Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>
    /// Scans the plugins/ directory for DLLs containing <see cref="ITrayPlugin"/>
    /// implementations and instantiates them.
    /// </summary>
    public void DiscoverPlugins()
    {
        var dir = PluginsDir;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            AppLog.Debug("Plugins", $"Created plugins directory: {dir}");
            return;
        }

        foreach (var dll in Directory.GetFiles(dir, "*.dll"))
        {
            try
            {
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dll));
                var pluginTypes = asm.GetTypes()
                    .Where(t => t.IsClass && !t.IsAbstract &&
                                typeof(ITrayPlugin).IsAssignableFrom(t));

                foreach (var type in pluginTypes)
                {
                    if (Activator.CreateInstance(type) is ITrayPlugin plugin)
                    {
                        _plugins.Add(plugin);
                        AppLog.Info("Plugins", $"Loaded: {plugin.Name} from {Path.GetFileName(dll)}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Plugins", $"Failed to load {Path.GetFileName(dll)}: {ex.Message}");
            }
        }

        AppLog.Info("Plugins", $"Discovered {_plugins.Count} plugin(s) from {dir}");
    }

    /// <summary>
    /// Initializes all loaded plugins with each tenant's credentials.
    /// </summary>
    public async Task InitializeAsync(
        IReadOnlyList<ITenantContext> tenants, CancellationToken ct = default)
    {
        foreach (var plugin in _plugins)
        {
            var pId = plugin.Id;

            foreach (var tenant in tenants)
            {
                if (tenant.Connection.DisabledExtensions.Contains(pId, StringComparer.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var ctx = new PluginTenantContext(
                        ConnectionService.CreateCredential(tenant.Connection),
                        tenant.TenantId,
                        tenant.TenantDisplayName,
                        tenant.Connection.Email,
                        log: (level, source, msg) => AppLog.Add(
                            level switch
                            {
                                Extensibility.PluginLogLevel.Debug   => LogLevel.Debug,
                                Extensibility.PluginLogLevel.Info    => LogLevel.Info,
                                Extensibility.PluginLogLevel.Warning => LogLevel.Warning,
                                Extensibility.PluginLogLevel.Error   => LogLevel.Error,
                                _ => LogLevel.Info
                            }, source, msg));

                    await plugin.InitializeAsync(ctx, ct);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Plugins",
                        $"{plugin.Name} failed to initialize for {tenant.TenantDisplayName}: {ex.Message}");
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var plugin in _plugins)
        {
            try { await plugin.DisposeAsync(); }
            catch { /* best effort */ }
        }
        _plugins.Clear();
    }
}
