using Azure.Core;

namespace Azure.PIM.Tray.Extensibility;

public enum PluginLogLevel { Debug, Info, Warning, Error }

/// <summary>
/// Provides a plugin with the credentials and identity for a single tenant.
/// </summary>
public sealed class PluginTenantContext
{
    public TokenCredential Credential       { get; }
    public string          TenantId         { get; }
    public string          TenantDisplayName { get; }
    public string          Email            { get; }

    /// <summary>Log delegate provided by the host app.</summary>
    public Action<PluginLogLevel, string, string> Log { get; }

    public PluginTenantContext(
        TokenCredential credential, string tenantId, string tenantDisplayName, string email,
        Action<PluginLogLevel, string, string>? log = null)
    {
        Credential       = credential;
        TenantId         = tenantId;
        TenantDisplayName = tenantDisplayName;
        Email            = email;
        Log              = log ?? ((_, _, _) => { });
    }
}
