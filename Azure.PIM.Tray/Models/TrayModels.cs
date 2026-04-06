namespace Azure.PIM.Tray.Models;

public enum PimSource { EntraId, AzureRbac }

// ---------------------------------------------------------------------------
// Config
// ---------------------------------------------------------------------------

public record TrayConnection
{
    public required string TenantId            { get; init; }
    public required string Email               { get; init; }
    public string?         ClientId            { get; init; }
    public string?         TenantDisplayName   { get; init; }
    public List<string>    ExcludedSubscriptions { get; init; } = [];
}

public record TrayAppConfig
{
    public List<TrayConnection> Connections { get; init; } = [];
}

// ---------------------------------------------------------------------------
// Unified pending approval request (Entra ID or Azure RBAC)
// ---------------------------------------------------------------------------

public record UnifiedPendingRequest
{
    public required PimSource      Source           { get; init; }
    public required string         TenantId         { get; init; }
    public required string         PrincipalName    { get; init; }
    public required string         RoleName         { get; init; }
    public required string         ScopeDisplayName { get; init; }
    public required string         RequestType      { get; init; }
    public required string         Reason           { get; init; }
    public required DateTimeOffset CreatedOn        { get; init; }

    /// <summary>The object ID of the user who made the request.</summary>
    public string? RequestorPrincipalId { get; init; }

    // Entra-specific
    public string? EntraApprovalId { get; init; }

    // ARM-specific
    public ArmRoleAssignmentScheduleRequest? ArmRequest { get; init; }
}

// ---------------------------------------------------------------------------
// Unified eligible role (available for self-activation)
// ---------------------------------------------------------------------------

public record UnifiedEligibleRole
{
    public required PimSource  Source           { get; init; }
    public required string     TenantId         { get; init; }
    public required string     RoleName         { get; init; }
    public required string     ScopeDisplayName { get; init; }

    // Entra-specific
    public string? EntraScheduleId      { get; init; }
    public string? EntraRoleDefId       { get; init; }
    public string? EntraDirectoryScopeId { get; init; }
    public string? EntraPrincipalId     { get; init; }

    // ARM-specific
    public string? ArmScope                       { get; init; }
    public string? ArmRoleDefinitionId            { get; init; }
    public string? ArmPrincipalId                 { get; init; }
    public string? ArmLinkedEligibilityScheduleId { get; init; }
}
