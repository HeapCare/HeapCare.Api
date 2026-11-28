namespace HeapCare.Infrastructure.Persistence;

/// <summary>
/// A tenant-owned role definition. System roles are seeded from the canonical
/// application catalogue and are immutable; tenant custom roles may be edited
/// only by a tenant-wide administrator.
/// </summary>
public sealed class TenantRole : TenantOwnedEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Assigns one code-owned permission identifier to a tenant role.
/// Permission metadata remains code-owned; grants are tenant-persisted.
/// </summary>
public sealed class TenantRolePermission : TenantOwnedEntity
{
    public Guid RoleId { get; set; }
    public string PermissionCode { get; set; } = string.Empty;
}

/// <summary>
/// Append-only before/after record for tenant role administration.
/// Permission snapshots contain permission identifiers only, never patient data.
/// </summary>
public sealed class TenantRoleChangeAudit : TenantOwnedEntity
{
    public Guid RoleId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string ActorIssuer { get; set; } = string.Empty;
    public string ActorSubject { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string PreviousPermissionCodesJson { get; set; } = "[]";
    public string NewPermissionCodesJson { get; set; } = "[]";
}
