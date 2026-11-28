using HeapCare.Application.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HeapCare.Infrastructure.Persistence;

public sealed class TenantMembershipReader(HeapCareDbContext db) : ITenantMembershipReader
{
    public async Task<TenantMembershipAuthorization?> FindActiveMembershipAsync(
        Guid tenantId,
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(issuer) ||
            string.IsNullOrWhiteSpace(subject) || issuer.Length > 255 || subject.Length > 255)
            return null;

        var membership = await db.TenantMemberships
            .IgnoreQueryFilters()
            .Include(value => value.Scopes)
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.TenantId == tenantId &&
                         value.Issuer == issuer &&
                         value.Subject == subject &&
                         value.IsActive &&
                         db.Tenants.Any(tenant => tenant.Id == tenantId && tenant.Status == TenantStatuses.Active),
                cancellationToken);

        if (membership is null)
            return null;

        var role = await db.TenantRoles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(value => value.TenantId == tenantId &&
                            value.Code == membership.Role &&
                            value.IsActive)
            .Select(value => new { value.Id, value.Code })
            .SingleOrDefaultAsync(cancellationToken);

        if (role is null)
            return null;

        var permissions = await db.TenantRolePermissions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(value => value.TenantId == tenantId && value.RoleId == role.Id)
            .OrderBy(value => value.PermissionCode)
            .Select(value => value.PermissionCode)
            .ToArrayAsync(cancellationToken);

        // Unknown or explicitly gated permissions must fail closed. In particular,
        // IPD permissions are catalogue-known but intentionally non-assignable.
        if (permissions.Length == 0 ||
            permissions.Any(permission => !TenantPermissionCatalog.IsAssignable(permission)))
            return null;

        var scopes = membership.Scopes
            .OrderBy(scope => scope.ScopeType)
            .ThenBy(scope => scope.HospitalId)
            .ThenBy(scope => scope.DepartmentId)
            .Select(scope => new OrganizationScope(scope.ScopeType, scope.HospitalId, scope.DepartmentId))
            .ToArray();

        return new TenantMembershipAuthorization(membership.Id, role.Code, scopes, permissions);
    }

    public async Task<TenantMembershipPage<UserTenantContextDto>> ListActiveTenantContextsAsync(
        string issuer,
        string subject,
        TenantMembershipPageRequest page,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issuer) || issuer.Length > 255 ||
            string.IsNullOrWhiteSpace(subject) || subject.Length > 255)
        {
            return new TenantMembershipPage<UserTenantContextDto>(
                Array.Empty<UserTenantContextDto>(), page.SafePage, page.SafePageSize, 0);
        }

        var memberships = db.TenantMemberships
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(value =>
                value.Issuer == issuer &&
                value.Subject == subject &&
                value.IsActive &&
                db.Tenants.IgnoreQueryFilters().Any(tenant =>
                    tenant.Id == value.TenantId &&
                    tenant.Status == TenantStatuses.Active));

        var totalCount = await memberships.CountAsync(cancellationToken);
        var pageMemberships = await memberships
            .Include(value => value.Scopes)
            .OrderBy(value => value.TenantId)
            .ThenBy(value => value.Id)
            .Skip(page.Skip)
            .Take(page.SafePageSize)
            .ToListAsync(cancellationToken);

        if (pageMemberships.Count == 0)
        {
            return new TenantMembershipPage<UserTenantContextDto>(
                Array.Empty<UserTenantContextDto>(), page.SafePage, page.SafePageSize, totalCount);
        }

        var tenantIds = pageMemberships.Select(value => value.TenantId).Distinct().ToArray();
        var tenants = await db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(value => tenantIds.Contains(value.Id) && value.Status == TenantStatuses.Active)
            .Select(value => new { value.Id, value.Name, value.Slug })
            .ToDictionaryAsync(value => value.Id, cancellationToken);

        var roleCodes = pageMemberships.Select(value => value.Role).Distinct(StringComparer.Ordinal).ToArray();
        var roles = await db.TenantRoles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(value => tenantIds.Contains(value.TenantId) &&
                            roleCodes.Contains(value.Code) &&
                            value.IsActive)
            .Select(value => new { value.TenantId, value.Code, value.Id })
            .ToListAsync(cancellationToken);
        var roleIds = roles.Select(value => value.Id).ToArray();
        var grants = await db.TenantRolePermissions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(value => tenantIds.Contains(value.TenantId) && roleIds.Contains(value.RoleId))
            .Select(value => new { value.TenantId, value.RoleId, value.PermissionCode })
            .ToListAsync(cancellationToken);

        var rolesByTenantAndCode = roles.ToDictionary(
            value => (value.TenantId, value.Code),
            value => value);
        var grantsByRoleId = grants
            .GroupBy(value => value.RoleId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(value => value.PermissionCode)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray());

        var items = pageMemberships
            .Where(value => tenants.ContainsKey(value.TenantId))
            .Select(value =>
            {
                var tenant = tenants[value.TenantId];
                if (!rolesByTenantAndCode.TryGetValue((value.TenantId, value.Role), out var role) ||
                    !grantsByRoleId.TryGetValue(role.Id, out var permissions) ||
                    permissions.Count == 0 ||
                    permissions.Any(permission => !TenantPermissionCatalog.IsAssignable(permission)))
                    return null;

                var scopes = value.Scopes
                    .OrderBy(scope => scope.ScopeType)
                    .ThenBy(scope => scope.HospitalId)
                    .ThenBy(scope => scope.DepartmentId)
                    .Select(scope => new OrganizationScope(
                        scope.ScopeType, scope.HospitalId, scope.DepartmentId))
                    .ToArray();

                return new UserTenantContextDto(
                    value.TenantId, tenant.Name, tenant.Slug, value.Role, scopes, permissions);
            })
            .Where(value => value is not null)
            .Select(value => value!)
            .ToArray();

        return new TenantMembershipPage<UserTenantContextDto>(
            items, page.SafePage, page.SafePageSize, totalCount);
    }
}
