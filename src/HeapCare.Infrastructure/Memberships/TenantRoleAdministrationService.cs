using System.Text.Json;
using HeapCare.Application.Tenancy;
using HeapCare.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HeapCare.Infrastructure.Memberships;

public sealed class TenantRoleAdministrationService(
    HeapCareDbContext db,
    ITenantContext tenantContext,
    IOrganizationScopeContext organizationScopeContext,
    ITenantPermissionContext permissionContext) : ITenantRoleAdministrationService
{
    public Task<IReadOnlyList<TenantPermissionDefinition>> ListAssignablePermissionsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanManageMemberships())
            return Task.FromResult<IReadOnlyList<TenantPermissionDefinition>>(Array.Empty<TenantPermissionDefinition>());

        IReadOnlyList<TenantPermissionDefinition> permissions = TenantPermissionCatalog.Definitions
            .Where(definition =>
                definition.IsAssignable &&
                permissionContext.HasPermission(definition.Code))
            .OrderBy(definition => definition.Module, StringComparer.Ordinal)
            .ThenBy(definition => definition.Resource, StringComparer.Ordinal)
            .ThenBy(definition => definition.Action, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(permissions);
    }

    public async Task<IReadOnlyList<TenantRoleDto>> ListAsync(CancellationToken cancellationToken)
    {
        if (!CanManageMemberships())
            return Array.Empty<TenantRoleDto>();

        if (!TryGetTenantId(out var tenantId))
            return Array.Empty<TenantRoleDto>();

        var roles = await db.TenantRoles
            .AsNoTracking()
            .Where(value => value.TenantId == tenantId)
            .OrderBy(value => value.IsSystem ? 0 : 1)
            .ThenBy(value => value.Name)
            .ThenBy(value => value.Code)
            .ToListAsync(cancellationToken);

        if (roles.Count == 0)
            return Array.Empty<TenantRoleDto>();

        var roleIds = roles.Select(value => value.Id).ToArray();
        var permissions = await db.TenantRolePermissions
            .AsNoTracking()
            .Where(value => value.TenantId == tenantId && roleIds.Contains(value.RoleId))
            .Select(value => new { value.RoleId, value.PermissionCode })
            .ToListAsync(cancellationToken);
        var grantsByRole = permissions
            .GroupBy(value => value.RoleId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(value => value.PermissionCode)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray());

        // A membership administrator may see only roles whose complete grants
        // are within their own delegation authority. This prevents exposing
        // or offering role definitions they are not allowed to reproduce.
        return roles
            .Select(role => new
            {
                Role = role,
                Permissions = grantsByRole.GetValueOrDefault(role.Id) ?? Array.Empty<string>()
            })
            .Where(value =>
                value.Role.IsSystem
                    ? permissionContext.HasPermission(TenantPermissions.MembershipRoleAssignSystem)
                    : value.Permissions.Count > 0 &&
                      value.Permissions.All(permission =>
                        TenantPermissionCatalog.IsAssignable(permission) &&
                        permissionContext.HasPermission(permission)))
            .Select(value => ToDto(value.Role, value.Permissions))
            .ToArray();
    }

    public async Task<TenantRoleAdministrationResult<TenantRoleDto>> CreateAsync(
        TenantRoleCreateInput input,
        string actorIssuer,
        string actorSubject,
        CancellationToken cancellationToken)
    {
        if (!CanManageRoleDefinitions())
            return Failure<TenantRoleDto>(
                "Role definitions can be changed only by a tenant-wide membership administrator.",
                403);
        if (!TryGetTenantId(out var tenantId))
            return Failure<TenantRoleDto>("A validated tenant context is required.", 400);
        if (!ValidateActorAndReason(actorIssuer, actorSubject, input.Reason, out var validationError))
            return Failure<TenantRoleDto>(validationError!, 400);
        if (!ValidateDetails(input.Name, input.Description, out var normalizedName, out var normalizedDescription, out validationError))
            return Failure<TenantRoleDto>(validationError!, 400);
        if (!TryValidateDelegatedPermissions(input.PermissionCodes, out var permissionCodes, out validationError))
            return Failure<TenantRoleDto>(validationError!, 403);

        var role = new TenantRole
        {
            TenantId = tenantId,
            Code = CreateCustomRoleCode(),
            Name = normalizedName!,
            Description = normalizedDescription!,
            IsSystem = false,
            IsActive = true,
            PermissionsRevision = 1
        };

        db.TenantRoles.Add(role);
        foreach (var permissionCode in permissionCodes)
        {
            db.TenantRolePermissions.Add(new TenantRolePermission
            {
                TenantId = tenantId,
                RoleId = role.Id,
                PermissionCode = permissionCode
            });
        }

        db.TenantRoleChangeAudits.Add(CreateChangeAudit(
            tenantId,
            role.Id,
            "Created",
            actorIssuer,
            actorSubject,
            input.Reason!,
            string.Empty,
            role.Name,
            string.Empty,
            role.Description,
            Array.Empty<string>(),
            permissionCodes));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Failure<TenantRoleDto>("The custom role could not be created. Retry the request.", 409);
        }

        return TenantRoleAdministrationResult<TenantRoleDto>.Created(
            ToDto(role, permissionCodes));
    }

    public async Task<TenantRoleAdministrationResult<TenantRoleDto>> UpdateAsync(
        Guid roleId,
        TenantRoleUpdateInput input,
        string actorIssuer,
        string actorSubject,
        CancellationToken cancellationToken)
    {
        if (!CanManageRoleDefinitions())
            return Failure<TenantRoleDto>(
                "Role definitions can be changed only by a tenant-wide membership administrator.",
                403);
        if (!TryGetTenantId(out var tenantId))
            return Failure<TenantRoleDto>("A validated tenant context is required.", 400);
        if (roleId == Guid.Empty)
            return Failure<TenantRoleDto>("A valid role identifier is required.", 400);
        if (!ValidateActorAndReason(actorIssuer, actorSubject, input.Reason, out var validationError))
            return Failure<TenantRoleDto>(validationError!, 400);
        if (!ValidateDetails(input.Name, input.Description, out var normalizedName, out var normalizedDescription, out validationError))
            return Failure<TenantRoleDto>(validationError!, 400);
        if (!TryValidateDelegatedPermissions(input.PermissionCodes, out var newPermissions, out validationError))
            return Failure<TenantRoleDto>(validationError!, 403);

        if (!await LockRoleForUpdateAsync(tenantId, roleId, cancellationToken))
            return Failure<TenantRoleDto>("Tenant role not found.", 404);

        var role = await db.TenantRoles
            .SingleOrDefaultAsync(
                value => value.TenantId == tenantId && value.Id == roleId,
                cancellationToken);
        if (role is null)
            return Failure<TenantRoleDto>("Tenant role not found.", 404);
        if (role.IsSystem)
            return Failure<TenantRoleDto>("System roles are immutable. Create a custom role instead.", 409);

        var previousName = role.Name;
        var previousDescription = role.Description;
        var oldPermissions = await db.TenantRolePermissions
            .Where(value => value.TenantId == tenantId && value.RoleId == role.Id)
            .OrderBy(value => value.PermissionCode)
            .Select(value => value.PermissionCode)
            .ToArrayAsync(cancellationToken);

        var detailsChanged =
            !string.Equals(role.Name, normalizedName, StringComparison.Ordinal) ||
            !string.Equals(role.Description, normalizedDescription, StringComparison.Ordinal);
        var permissionsChanged = !oldPermissions.ToHashSet(StringComparer.Ordinal)
            .SetEquals(newPermissions);

        if (!detailsChanged && !permissionsChanged)
            return TenantRoleAdministrationResult<TenantRoleDto>.Success(ToDto(role, oldPermissions));

        role.Name = normalizedName!;
        role.Description = normalizedDescription!;
        var action = permissionsChanged ? "PermissionsUpdated" : "DetailsUpdated";

        if (permissionsChanged)
        {
            db.TenantRolePermissions.RemoveRange(
                db.TenantRolePermissions.Local.Where(value => value.RoleId == role.Id).ToArray());
            var persistedPermissions = await db.TenantRolePermissions
                .Where(value => value.TenantId == tenantId && value.RoleId == role.Id)
                .ToListAsync(cancellationToken);
            db.TenantRolePermissions.RemoveRange(persistedPermissions);

            // Delete and insert in two SaveChanges calls. The middleware's request
            // transaction keeps them atomic with the role update and the audit snapshot.
            await db.SaveChangesAsync(cancellationToken);

            role.PermissionsRevision = checked(role.PermissionsRevision + 1);
            foreach (var permissionCode in newPermissions)
            {
                db.TenantRolePermissions.Add(new TenantRolePermission
                {
                    TenantId = tenantId,
                    RoleId = role.Id,
                    PermissionCode = permissionCode
                });
            }
        }

        db.TenantRoleChangeAudits.Add(CreateChangeAudit(
            tenantId,
            role.Id,
            action,
            actorIssuer,
            actorSubject,
            input.Reason!,
            previousName,
            normalizedName!,
            previousDescription,
            normalizedDescription!,
            oldPermissions,
            newPermissions));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Failure<TenantRoleDto>("The role changed concurrently. Reload it and retry.", 409);
        }
        catch (DbUpdateException)
        {
            return Failure<TenantRoleDto>("The role update could not be persisted. Retry the request.", 409);
        }

        return TenantRoleAdministrationResult<TenantRoleDto>.Success(
            ToDto(role, newPermissions));
    }

    public async Task<TenantRoleAdministrationResult<TenantRoleDto>> SetActiveAsync(
        Guid roleId,
        bool isActive,
        string? reason,
        string actorIssuer,
        string actorSubject,
        CancellationToken cancellationToken)
    {
        if (!CanManageRoleDefinitions())
            return Failure<TenantRoleDto>(
                "Role definitions can be changed only by a tenant-wide membership administrator.",
                403);
        if (!TryGetTenantId(out var tenantId))
            return Failure<TenantRoleDto>("A validated tenant context is required.", 400);
        if (roleId == Guid.Empty)
            return Failure<TenantRoleDto>("A valid role identifier is required.", 400);
        if (!ValidateActorAndReason(actorIssuer, actorSubject, reason, out var validationError))
            return Failure<TenantRoleDto>(validationError!, 400);

        if (!await LockRoleForUpdateAsync(tenantId, roleId, cancellationToken))
            return Failure<TenantRoleDto>("Tenant role not found.", 404);

        var role = await db.TenantRoles
            .SingleOrDefaultAsync(
                value => value.TenantId == tenantId && value.Id == roleId,
                cancellationToken);
        if (role is null)
            return Failure<TenantRoleDto>("Tenant role not found.", 404);
        if (role.IsSystem)
            return Failure<TenantRoleDto>("System roles cannot be deactivated.", 409);
        if (role.IsActive == isActive)
            return Failure<TenantRoleDto>("The role is already in the requested state.", 409);

        var hasActiveMemberships = await db.TenantMemberships
            .AnyAsync(
                value => value.TenantId == tenantId &&
                         value.Role == role.Code &&
                         value.IsActive,
                cancellationToken);
        if (!isActive && hasActiveMemberships)
            return Failure<TenantRoleDto>(
                "Reassign or deactivate active memberships before deactivating this role.",
                409);

        var permissions = await db.TenantRolePermissions
            .Where(value => value.TenantId == tenantId && value.RoleId == role.Id)
            .OrderBy(value => value.PermissionCode)
            .Select(value => value.PermissionCode)
            .ToArrayAsync(cancellationToken);

        role.IsActive = isActive;
        db.TenantRoleChangeAudits.Add(CreateChangeAudit(
            tenantId,
            role.Id,
            isActive ? "Reactivated" : "Deactivated",
            actorIssuer,
            actorSubject,
            reason!,
            role.Name,
            role.Name,
            role.Description,
            role.Description,
            permissions,
            permissions));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Failure<TenantRoleDto>("The role changed concurrently. Reload it and retry.", 409);
        }

        return TenantRoleAdministrationResult<TenantRoleDto>.Success(ToDto(role, permissions));
    }

    private async Task<bool> LockRoleForUpdateAsync(
        Guid tenantId,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction;
        if (transaction is null)
            throw new InvalidOperationException("Role updates require the audited request transaction.");

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            SELECT "Id" FROM "tenant_roles"
            WHERE "TenantId" = @tenantId AND "Id" = @roleId
            FOR UPDATE
            """;

        var tenantParameter = command.CreateParameter();
        tenantParameter.ParameterName = "tenantId";
        tenantParameter.Value = tenantId;
        command.Parameters.Add(tenantParameter);

        var roleParameter = command.CreateParameter();
        roleParameter.ParameterName = "roleId";
        roleParameter.Value = roleId;
        command.Parameters.Add(roleParameter);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }

    private bool CanManageMemberships() =>
        organizationScopeContext.MembershipId.HasValue &&
        permissionContext.HasPermission(TenantPermissions.MembershipManage);

    private bool CanManageRoleDefinitions() =>
        CanManageMemberships() && organizationScopeContext.IsTenantWide;

    private bool TryGetTenantId(out Guid tenantId)
    {
        tenantId = tenantContext.TenantId.GetValueOrDefault();
        return tenantId != Guid.Empty;
    }

    private bool TryValidateDelegatedPermissions(
        IReadOnlyList<string>? requested,
        out IReadOnlyList<string> normalized,
        out string? error)
    {
        normalized = Array.Empty<string>();
        error = null;

        if (requested is null || requested.Count == 0 || requested.Count > 100)
        {
            error = "Select at least one and no more than 100 permissions.";
            return false;
        }

        var values = requested
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        if (values.Length != requested.Count ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            error = "Permission codes must be non-empty and unique.";
            return false;
        }

        if (values.Any(value => !TenantPermissionCatalog.IsAssignable(value)))
        {
            error = "One or more requested permissions are unknown or gated from assignment.";
            return false;
        }

        if (values.Any(value => !permissionContext.HasPermission(value)))
        {
            error = "A tenant administrator cannot delegate permissions they do not currently hold.";
            return false;
        }

        normalized = values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return true;
    }

    private static bool ValidateDetails(
        string? nameInput,
        string? descriptionInput,
        out string? name,
        out string? description,
        out string? error)
    {
        name = nameInput?.Trim();
        description = descriptionInput?.Trim();
        error = null;

        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        {
            error = "Role name is required and must not exceed 100 characters.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(description) || description.Length > 500)
        {
            error = "Role description is required and must not exceed 500 characters.";
            return false;
        }

        return true;
    }

    private static bool ValidateActorAndReason(
        string actorIssuer,
        string actorSubject,
        string? reasonInput,
        out string? error)
    {
        var reason = reasonInput?.Trim();
        error = null;
        if (string.IsNullOrWhiteSpace(actorIssuer) || actorIssuer.Length > 255 ||
            string.IsNullOrWhiteSpace(actorSubject) || actorSubject.Length > 255)
        {
            error = "A validated actor issuer and subject are required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10 || reason.Length > 500)
        {
            error = "A change reason between 10 and 500 characters is required.";
            return false;
        }

        return true;
    }

    private static string CreateCustomRoleCode() =>
        "custom_" + Guid.NewGuid().ToString("N")[..20];

    private static TenantRoleChangeAudit CreateChangeAudit(
        Guid tenantId,
        Guid roleId,
        string action,
        string actorIssuer,
        string actorSubject,
        string reason,
        string previousName,
        string newName,
        string previousDescription,
        string newDescription,
        IReadOnlyList<string> previousPermissions,
        IReadOnlyList<string> newPermissions) =>
        new()
        {
            TenantId = tenantId,
            RoleId = roleId,
            Action = action,
            ActorIssuer = actorIssuer,
            ActorSubject = actorSubject,
            Reason = reason.Trim(),
            PreviousName = previousName,
            NewName = newName,
            PreviousDescription = previousDescription,
            NewDescription = newDescription,
            PreviousPermissionCodesJson = JsonSerializer.Serialize(previousPermissions),
            NewPermissionCodesJson = JsonSerializer.Serialize(newPermissions)
        };

    private static TenantRoleDto ToDto(TenantRole role, IReadOnlyList<string> permissions) =>
        new(
            role.Id,
            role.Code,
            role.Name,
            role.Description,
            role.IsSystem,
            role.IsActive,
            permissions,
            role.CreatedAtUtc,
            role.UpdatedAtUtc);

    private static TenantRoleAdministrationResult<T> Failure<T>(string error, int statusCode) =>
        TenantRoleAdministrationResult<T>.Failure(error, statusCode);
}
