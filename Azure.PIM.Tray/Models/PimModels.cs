using System.Text.Json.Serialization;

namespace Azure.PIM.Tray.Models;

public class ODataCollection<T>
{
    [JsonPropertyName("value")]
    public List<T> Value { get; set; } = [];

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}

public class RoleAssignmentScheduleRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("justification")]
    public string? Justification { get; set; }

    [JsonPropertyName("approvalId")]
    public string? ApprovalId { get; set; }

    [JsonPropertyName("createdDateTime")]
    public DateTimeOffset? CreatedDateTime { get; set; }

    [JsonPropertyName("principalId")]
    public string? PrincipalId { get; set; }

    [JsonPropertyName("roleDefinitionId")]
    public string? RoleDefinitionId { get; set; }

    [JsonPropertyName("directoryScopeId")]
    public string? DirectoryScopeId { get; set; }

    [JsonPropertyName("principal")]
    public DirectoryPrincipal? Principal { get; set; }

    [JsonPropertyName("roleDefinition")]
    public RoleDefinition? RoleDefinition { get; set; }

    [JsonIgnore]
    public string SourceNamespace { get; set; } = "directory";
}

public class DirectoryPrincipal
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("userPrincipalName")]
    public string? UserPrincipalName { get; set; }
}

public class RoleDefinition
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}

// ---------------------------------------------------------------------------
// Azure Resource Manager — RBAC PIM models
// ---------------------------------------------------------------------------

public class ArmCollection<T>
{
    [JsonPropertyName("value")]
    public List<T> Value { get; set; } = [];

    [JsonPropertyName("nextLink")]
    public string? NextLink { get; set; }
}

public class ArmRoleAssignmentScheduleRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("properties")]
    public ArmRoleRequestProperties? Properties { get; set; }

    [JsonIgnore]
    public string? ArmScope { get; set; }
}

public class ArmRoleRequestProperties
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("requestType")]
    public string? RequestType { get; set; }

    [JsonPropertyName("justification")]
    public string? Justification { get; set; }

    [JsonPropertyName("approvalId")]
    public string? ApprovalId { get; set; }

    [JsonPropertyName("createdOn")]
    public DateTimeOffset? CreatedOn { get; set; }

    [JsonPropertyName("principalId")]
    public string? PrincipalId { get; set; }

    [JsonPropertyName("roleDefinitionId")]
    public string? RoleDefinitionId { get; set; }

    [JsonPropertyName("expandedProperties")]
    public ArmExpandedProperties? ExpandedProperties { get; set; }
}

public class ArmExpandedProperties
{
    [JsonPropertyName("principal")]
    public ArmPrincipal? Principal { get; set; }

    [JsonPropertyName("roleDefinition")]
    public ArmRoleDefinition? RoleDefinition { get; set; }

    [JsonPropertyName("scope")]
    public ArmScope? Scope { get; set; }
}

public class ArmPrincipal
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }
}

public class ArmRoleDefinition
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}

public class ArmScope
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public class ArmApproval
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("properties")]
    public ArmApprovalProperties? Properties { get; set; }
}

public class ArmApprovalProperties
{
    [JsonPropertyName("stages")]
    public List<ArmApprovalStage>? ApprovalStages { get; set; }
}

public class ArmApprovalStage
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("properties")]
    public ArmApprovalStageProperties? StageProperties { get; set; }

    [JsonIgnore] public string? ApprovalStageId => Name;
    [JsonIgnore] public string? Status          => StageProperties?.Status;
    [JsonIgnore] public bool    AssignedToMe    => StageProperties?.AssignedToMe ?? false;
}

public class ArmApprovalStageProperties
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("assignedToMe")]
    public bool AssignedToMe { get; set; }
}

public class Approval
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("steps")]
    public List<ApprovalStep>? Steps { get; set; }
}

public class ApprovalStep
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("assignedToMe")]
    public bool AssignedToMe { get; set; }

    [JsonPropertyName("reviewResult")]
    public string? ReviewResult { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("justification")]
    public string? Justification { get; set; }
}
