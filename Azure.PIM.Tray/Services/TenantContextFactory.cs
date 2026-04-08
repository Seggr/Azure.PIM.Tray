using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

public static class TenantContextFactory
{
    public static ITenantContext Create(TrayConnection connection)
    {
        var credential = ConnectionService.CreateCredential(connection);
        return new TenantContext(connection, credential,
            onCacheSave: roles => TenantRoleCache.Save(connection.TenantId, roles),
            onConnectionChanged: updated =>
            {
                var config = ConnectionService.LoadConfig();
                var idx = config.Connections.FindIndex(c =>
                    string.Equals(c.TenantId, updated.TenantId, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    config.Connections[idx] = updated;
                    ConnectionService.SaveConfig(config);
                }
            });
    }

    public static IReadOnlyList<ITenantContext> CreateAll(TrayAppConfig config)
        => config.Connections
            .Where(c => !string.IsNullOrWhiteSpace(c.ClientId))
            .Select(Create)
            .ToList();
}
