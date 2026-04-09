using Azure.Core;

namespace Azure.PIM.Tray.Extensibility;

/// <summary>
/// Contract that all tray plugins must implement. The main app discovers
/// classes implementing this interface in DLLs found in the plugins/ folder.
/// </summary>
public interface ITrayPlugin : IAsyncDisposable
{
    /// <summary>Stable identifier used in config and feed matching (e.g. "Azure.PIM.Tray.Ext.Laps").</summary>
    string Id { get; }

    /// <summary>Display name shown in the tray menu and Settings.</summary>
    string Name { get; }

    /// <summary>
    /// Graph API delegated permissions this plugin requires (e.g. "DeviceLocalCredential.Read.All").
    /// The host app uses these to configure app registrations and grant admin consent.
    /// </summary>
    IReadOnlyList<string> RequiredGraphPermissions { get; }

    /// <summary>
    /// Optional well-known GUIDs for permissions that may not appear in the Graph SP's
    /// oauth2PermissionScopes list. Keys are permission names, values are scope IDs.
    /// The host app falls back to these when the Graph SP lookup misses a scope.
    /// </summary>
    IReadOnlyDictionary<string, string> RequiredGraphPermissionIds => new Dictionary<string, string>();

    /// <summary>
    /// Entra ID role display names required for this plugin to function
    /// (e.g. "Cloud Device Administrator"). The host app checks if the user has
    /// any of these roles active and grays out the menu item if not.
    /// </summary>
    IReadOnlyList<string> RequiredRoles { get; }

    /// <summary>
    /// Called once per tenant when the plugin is loaded.
    /// Use this to store credentials, check availability, and pre-fetch data.
    /// </summary>
    Task InitializeAsync(PluginTenantContext tenant, CancellationToken ct = default);

    /// <summary>
    /// Returns whether the plugin is available for the given tenant.
    /// A plugin may disable itself if the tenant doesn't support its features
    /// (e.g. no LAPS devices found). Called after <see cref="InitializeAsync"/>.
    /// </summary>
    bool IsAvailable(string tenantId);

    /// <summary>
    /// Called each time the tray menu is built. The plugin should add its
    /// items (submenus, search boxes, etc.) via the menu builder.
    /// </summary>
    void BuildMenu(IPluginMenuBuilder menu, string tenantId);
}
