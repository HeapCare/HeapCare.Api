using HeapCare.Application.Tenancy;
using HeapCare.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeapCare.Infrastructure.Memberships;

/// <summary>
/// Creates the five immutable system roles for a tenant provisioned after the
/// persisted-RBAC migration. Existing tenants are seeded by the migration itself.
/// </summary>
internal static class TenantRoleBootstrapper
{
    public static async Task EnsureSystemRolesAsync(
        HeapCareDbContext db,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("A tenant identifier is required.", nameof(tenantId));

        var existingRoles = await db.TenantRoles
            .IgnoreQueryFilters()
            .Where(value => value.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        if (existingRoles.Count > 0)
        {
            var expectedCodes = TenantPermissionCatalog.SystemRoles
                .Select(value => value.Code)
                .ToHashSet(StringComparer.Ordinal);
            var existingSystemCodes = existingRoles
                .Where(value => value.IsSystem)
                .Select(value => value.Code)
                .ToHashSet(StringComparer.Ordinal);

            if (!expectedCodes.SetEquals(existingSystemCodes))
                throw new InvalidOperationException(
                    "Tenant system roles are incomplete. Refusing to provision an administrator against a partial RBAC seed.");

            var roleIds = existingRoles.Where(value => value.IsSystem).Select(value => value.Id).ToArray();
            var persistedGrants = await db.TenantRolePermissions
                .IgnoreQueryFilters()
                .Where(value => value.TenantId == tenantId && roleIds.Contains(value.RoleId))
                .Select(value => new { value.RoleId, value.PermissionCode })
                .ToListAsync(cancellationToken);

            foreach (var definition in TenantPermissionCatalog.SystemRoles)
            {
                var role = existingRoles.Single(value => value.Code == definition.Code && value.IsSystem);
                if (!role.IsActive)
                    throw new InvalidOperationException("A system role was unexpectedly deactivated.");

                var actual = persistedGrants
                    .Where(value => value.RoleId == role.Id)
                    .Select(value => value.PermissionCode)
                    .ToHashSet(StringComparer.Ordinal);
                if (!actual.SetEquals(definition.PermissionCodes))
                    throw new InvalidOperationException(
                        $"System role '{definition.Code}' does not match its canonical baseline grants.");
            }

            return;
        }

        foreach (var definition in TenantPermissionCatalog.SystemRoles)
        {
            var role = new TenantRole
            {
                TenantId = tenantId,
                Code = definition.Code,
                Name = definition.DisplayName,
                Description = definition.Description,
                IsSystem = true,
                IsActive = true
            };
            db.TenantRoles.Add(role);

            foreach (var permissionCode in definition.PermissionCodes)
            {
                db.TenantRolePermissions.Add(new TenantRolePermission
                {
                    TenantId = tenantId,
                    RoleId = role.Id,
                    PermissionCode = permissionCode
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
