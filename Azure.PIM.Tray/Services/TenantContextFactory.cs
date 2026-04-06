using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

public static class TenantContextFactory
{
    public static ITenantContext Create(TrayConnection connection)
    {
        var credential = ConnectionService.CreateCredential(connection);
        return new TenantContext(connection, credential,
            onCacheSave: roles => TenantRoleCache.Save(connection.TenantId, roles));
    }

    public static IReadOnlyList<ITenantContext> CreateAll(TrayAppConfig config)
        => config.Connections
            .Where(c => !string.IsNullOrWhiteSpace(c.ClientId))
            .Select(Create)
            .ToList();
}
