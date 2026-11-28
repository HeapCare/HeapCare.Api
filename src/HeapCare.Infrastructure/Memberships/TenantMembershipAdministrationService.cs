using HeapCare.Application.Tenancy;
using HeapCare.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace HeapCare.Infrastructure.Memberships;

public sealed class TenantMembershipAdministrationService(
    HeapCareDbContext db,
    ITenantContext tenantContext,
    IOrganizationScopeContext organizationScopeContext,
    ITenantPermissionContext permissionContext) : ITenantMembershipAdministrationService
{
    public async Task<TenantMembershipPage<TenantMembershipDto>> ListAsync(
        TenantMembershipPageRequest page, CancellationToken cancellationToken)
    {
        IQueryable<TenantMembership> query = db.TenantMemberships.AsNoTracking();

        if (!organizationScopeContext.IsTenantWide)
        {
            var hospitalIds = organizationScopeContext.HospitalIds.ToArray();
            var departmentIds = organizationScopeContext.DepartmentIds.ToArray();

            // A scoped administrator may manage a target membership only when every
            // scope assigned to that target is contained in the caller's scope.
            // Keep this predicate in SQL so pagination never materializes the full
            // tenant membership table in application memory.
            query = query.Where(value =>
                value.Scopes.Any() &&
                !value.Scopes.Any(scope =>
                    scope.ScopeType == OrganizationScopeTypes.Tenant ||
                    (scope.ScopeType == OrganizationScopeTypes.Hospital &&
                     (!scope.HospitalId.HasValue ||
                      !hospitalIds.Contains(scope.HospitalId.Value))) ||
                    (scope.ScopeType == OrganizationScopeTypes.Department &&
                     (!scope.HospitalId.HasValue ||
                      !scope.DepartmentId.HasValue ||
                      (!hospitalIds.Contains(scope.HospitalId.Value) &&
                       !departmentIds.Contains(scope.DepartmentId.Value))))));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var members = await query
            .Include(value => value.Scopes)
            .OrderBy(value => value.Subject)
            .ThenBy(value => value.Id)
            .Skip(page.Skip)
            .Take(page.SafePageSize)
            .ToListAsync(cancellationToken);

        return new TenantMembershipPage<TenantMembershipDto>(
            members.Select(ToDto).ToArray(),
            page.SafePage,
            page.SafePageSize,
            totalCount);
    }

    public async Task<TenantMembershipResult<TenantMembershipDto>> CreateInitialAdministratorAsync(
        Guid tenantId,
        string? issuer,
        string? subject,
        string configuredIssuer,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
            return Failure<TenantMembershipDto>("A non-empty tenant identifier is required.", 400);

        if (!ValidateIdentity(issuer, subject, configuredIssuer,
                out var normalizedIssuer, out var normalizedSubject, out var identityError))
        {
            return Failure<TenantMembershipDto>(identityError!, 400);
        }

        // Lock before reading tenant status. If activation is concurrently in
        // progress, this transaction waits and then reads the committed lifecycle
        // value rather than relying on a stale pre-lock entity.
        if (!await LockTenantAsync(tenantId, cancellationToken))
            return Failure<TenantMembershipDto>("Tenant not found.", 404);

        var tenant = await db.Tenants.SingleOrDefaultAsync(
            value => value.Id == tenantId,
            cancellationToken);
        if (tenant is null)
            return Failure<TenantMembershipDto>("Tenant not found.", 404);

        tenantContext.SetTenant(tenantId);
        await TenantRoleBootstrapper.EnsureSystemRolesAsync(db, tenantId, cancellationToken);

        var existing = await db.TenantMemberships.IgnoreQueryFilters()
            .Include(value => value.Scopes)
            .SingleOrDefaultAsync(
                value => value.TenantId == tenantId &&
                         value.Issuer == normalizedIssuer &&
                         value.Subject == normalizedSubject,
                cancellationToken);

        if (existing is not null)
        {
            var isInitialAdmin =
                existing.IsActive &&
                existing.Role == TenantRoles.HospitalAdmin &&
                existing.Scopes.Count == 1 &&
                existing.Scopes.Any(scope => scope.ScopeType == OrganizationScopeTypes.Tenant);

            return isInitialAdmin
                ? TenantMembershipResult<TenantMembershipDto>.Success(ToDto(existing))
                : Failure<TenantMembershipDto>("A membership already exists for this identity.", 409);
        }

        if (tenant.Status != TenantStatuses.Provisioning)
            return Failure<TenantMembershipDto>(
                "The initial administrator can only be assigned while the tenant is provisioning.",
                409);

        var hasAnyMembership = await db.TenantMemberships.IgnoreQueryFilters()
            .AnyAsync(value => value.TenantId == tenantId, cancellationToken);
        if (hasAnyMembership)
        {
            return Failure<TenantMembershipDto>(
                "An initial administrator has already been provisioned for this tenant.",
                409);
        }

        var membership = new TenantMembership
        {
            TenantId = tenantId,
            Issuer = normalizedIssuer!,
            Subject = normalizedSubject!,
            Role = TenantRoles.HospitalAdmin,
            IsActive = true
        };
        AddScopes(membership, [new OrganizationScope(OrganizationScopeTypes.Tenant)]);
        db.TenantMemberships.Add(membership);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return Failure<TenantMembershipDto>(
                "A membership already exists for this tenant and identity.",
                409);
        }

        return new TenantMembershipResult<TenantMembershipDto>(
            ToDto(membership),
            Error: null,
            StatusCode: 201);
    }

    public async Task<TenantMembershipResult<TenantMembershipDto>> CreateAsync(
        TenantMembershipInput input,
        string configuredIssuer,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(out var tenantId))
            return Failure<TenantMembershipDto>("Tenant context is required.", 400);
        if (!IsTenantAdministrator())
            return Failure<TenantMembershipDto>(
                "Only a HospitalAdmin membership can manage tenant memberships.",
                403);
        if (!ValidateIdentity(input.Issuer, input.Subject, configuredIssuer,
                out var issuer, out var subject, out var identityError))
        {
            return Failure<TenantMembershipDto>(identityError!, 400);
        }
        var roleValidation = await ValidateRoleAssignmentAsync(input.Role, tenantId, cancellationToken);
        if (roleValidation.Error is not null)
            return Failure<TenantMembershipDto>(roleValidation.Error, roleValidation.StatusCode);

        var resolution = await ResolveScopesAsync(input.Scopes, cancellationToken);
        if (resolution.Error is not null)
            return Failure<TenantMembershipDto>(resolution.Error, resolution.StatusCode);
        if (!CanAssignScopes(resolution.Scopes))
            return Failure<TenantMembershipDto>(
                "The requested organization scope exceeds your assigned scope.",
                403);

        var membership = new TenantMembership
        {
            TenantId = tenantId,
            Issuer = issuer!,
            Subject = subject!,
            Role = input.Role!,
            IsActive = true
        };
        AddScopes(membership, resolution.Scopes);
        db.TenantMemberships.Add(membership);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return Failure<TenantMembershipDto>(
                "A membership already exists for this tenant and identity. Reactivate or update the existing membership instead.",
                409);
        }

        return TenantMembershipResult<TenantMembershipDto>.Success(ToDto(membership));
    }

    public async Task<TenantMembershipResult<TenantMembershipDto>> UpdateAsync(
        Guid membershipId,
        TenantMembershipUpdateInput input,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(out var tenantId))
            return Failure<TenantMembershipDto>("Tenant context is required.", 400);
        if (!IsTenantAdministrator())
            return Failure<TenantMembershipDto>(
                "Only a HospitalAdmin membership can manage tenant memberships.",
                403);
        if (membershipId == Guid.Empty)
            return Failure<TenantMembershipDto>("A non-empty membership identifier is required.", 400);
        if (organizationScopeContext.MembershipId == membershipId)
            return Failure<TenantMembershipDto>("Administrators cannot modify their own membership.", 409);
        if (string.IsNullOrWhiteSpace(input.Role) || input.Role.Length > 50)
            return Failure<TenantMembershipDto>("A valid tenant role code is required.", 400);

        var membership = await db.TenantMemberships.Include(value => value.Scopes)
            .SingleOrDefaultAsync(value => value.Id == membershipId, cancellationToken);
        if (membership is null)
            return Failure<TenantMembershipDto>("Membership not found.", 404);
        if (!CanManageMembershipScopes(ToScopes(membership)))
            return Failure<TenantMembershipDto>(
                "The membership is outside your assigned organization scope.",
                404);

        // Scope-only edits may retain an existing role whose full grants exceed
        // this administrator's delegated set. A role change is always validated
        // against the actor's current permission set.
        if (!string.Equals(membership.Role, input.Role, StringComparison.Ordinal))
        {
            var roleValidation = await ValidateRoleAssignmentAsync(input.Role, tenantId, cancellationToken);
            if (roleValidation.Error is not null)
                return Failure<TenantMembershipDto>(roleValidation.Error, roleValidation.StatusCode);
        }

        var resolution = await ResolveScopesAsync(input.Scopes, cancellationToken);
        if (resolution.Error is not null)
            return Failure<TenantMembershipDto>(resolution.Error, resolution.StatusCode);
        if (!CanAssignScopes(resolution.Scopes))
            return Failure<TenantMembershipDto>(
                "The requested organization scope exceeds your assigned scope.",
                403);

        var removesLastAdmin =
            IsTenantWideAdministrator(membership) &&
            (!string.Equals(membership.Role, input.Role, StringComparison.Ordinal) ||
             !resolution.Scopes.Any(scope => scope.ScopeType == OrganizationScopeTypes.Tenant));

        if (membership.IsActive && removesLastAdmin &&
            !await HasAnotherTenantWideAdministratorAsync(
                membership.Id, tenantId, cancellationToken))
        {
            return Failure<TenantMembershipDto>(
                "The last active tenant-wide HospitalAdmin cannot be demoted or have its tenant scope removed.",
                409);
        }

        membership.Role = input.Role!;
        var currentScopes = ToScopes(membership);
        var scopesChanged = !currentScopes.ToHashSet().SetEquals(resolution.Scopes);

        if (scopesChanged)
        {
            var previousScopes = membership.Scopes.ToArray();
            db.TenantMembershipScopes.RemoveRange(previousScopes);

            // The request audit middleware owns the enclosing transaction. Persist
            // the deletes before severing the now-deleted navigation and inserting
            // replacements. Clearing a required relationship before SaveChanges can
            // make EF treat the membership FK as severed instead of as a row delete.
            await db.SaveChangesAsync(cancellationToken);

            // Do not clear the required Membership.Scopes navigation after deleting
            // the old rows. EF can interpret that as a severed required relationship
            // and schedule a stale UPDATE for the already-deleted child row.
            AddScopeRows(membership, resolution.Scopes);
        }

        await db.SaveChangesAsync(cancellationToken);

        return TenantMembershipResult<TenantMembershipDto>.Success(
            ToDto(membership, resolution.Scopes));
    }

    public async Task<TenantMembershipResult<TenantMembershipDto>> SetActiveAsync(
        Guid membershipId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(out var tenantId))
            return Failure<TenantMembershipDto>("Tenant context is required.", 400);
        if (!IsTenantAdministrator())
            return Failure<TenantMembershipDto>(
                "Only a HospitalAdmin membership can manage tenant memberships.",
                403);
        if (membershipId == Guid.Empty)
            return Failure<TenantMembershipDto>("A non-empty membership identifier is required.", 400);
        if (organizationScopeContext.MembershipId == membershipId)
            return Failure<TenantMembershipDto>(
                "Administrators cannot change their own membership lifecycle.",
                409);

        var membership = await db.TenantMemberships.Include(value => value.Scopes)
            .SingleOrDefaultAsync(value => value.Id == membershipId, cancellationToken);
        if (membership is null)
            return Failure<TenantMembershipDto>("Membership not found.", 404);
        if (!CanManageMembershipScopes(ToScopes(membership)))
            return Failure<TenantMembershipDto>(
                "The membership is outside your assigned organization scope.",
                404);
        if (membership.IsActive == isActive)
            return TenantMembershipResult<TenantMembershipDto>.Success(ToDto(membership));

        if (!isActive && IsTenantWideAdministrator(membership) &&
            !await HasAnotherTenantWideAdministratorAsync(
                membership.Id, tenantId, cancellationToken))
        {
            return Failure<TenantMembershipDto>(
                "The last active tenant-wide HospitalAdmin cannot be deactivated.",
                409);
        }

        membership.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);
        return TenantMembershipResult<TenantMembershipDto>.Success(ToDto(membership));
    }

    private async Task<ScopeResolution> ResolveScopesAsync(
        IReadOnlyList<TenantMembershipScopeInput>? inputs,
        CancellationToken cancellationToken)
    {
        if (inputs is null || inputs.Count == 0 || inputs.Count > 25)
        {
            return ScopeResolution.Failure(
                "At least one and no more than 25 organization scopes are required.",
                400);
        }

        var scopes = new List<OrganizationScope>();
        foreach (var input in inputs)
        {
            if (!OrganizationScopeTypes.IsKnown(input.ScopeType))
                return ScopeResolution.Failure("An organization scope type is not supported.", 400);

            switch (input.ScopeType)
            {
                case OrganizationScopeTypes.Tenant:
                    if (input.HospitalId.HasValue || input.DepartmentId.HasValue)
                        return ScopeResolution.Failure(
                            "Tenant scope must not include hospital or department identifiers.",
                            400);
                    scopes.Add(new OrganizationScope(OrganizationScopeTypes.Tenant));
                    break;

                case OrganizationScopeTypes.Hospital:
                    if (!input.HospitalId.HasValue ||
                        input.HospitalId == Guid.Empty ||
                        input.DepartmentId.HasValue)
                    {
                        return ScopeResolution.Failure(
                            "Hospital scope requires a hospital ID and no department ID.",
                            400);
                    }
                    if (!await db.Hospitals.AnyAsync(
                            value => value.Id == input.HospitalId.Value,
                            cancellationToken))
                    {
                        return ScopeResolution.Failure(
                            "The selected hospital does not exist in this tenant.",
                            404);
                    }
                    scopes.Add(new OrganizationScope(
                        OrganizationScopeTypes.Hospital,
                        input.HospitalId));
                    break;

                case OrganizationScopeTypes.Department:
                    if (!input.DepartmentId.HasValue || input.DepartmentId == Guid.Empty)
                        return ScopeResolution.Failure("Department scope requires a department ID.", 400);

                    var department = await db.Departments.AsNoTracking()
                        .Where(value => value.Id == input.DepartmentId.Value)
                        .Select(value => new { value.Id, value.HospitalId })
                        .SingleOrDefaultAsync(cancellationToken);

                    if (department is null)
                        return ScopeResolution.Failure(
                            "The selected department does not exist in this tenant.",
                            404);
                    if (input.HospitalId.HasValue && input.HospitalId.Value != department.HospitalId)
                    {
                        return ScopeResolution.Failure(
                            "The department does not belong to the supplied hospital.",
                            400);
                    }

                    scopes.Add(new OrganizationScope(
                        OrganizationScopeTypes.Department,
                        department.HospitalId,
                        department.Id));
                    break;
            }
        }

        if (scopes.Distinct().Count() != scopes.Count)
            return ScopeResolution.Failure("Duplicate organization scopes are not allowed.", 400);
        if (scopes.Any(value => value.ScopeType == OrganizationScopeTypes.Tenant) && scopes.Count != 1)
            return ScopeResolution.Failure(
                "Tenant-wide scope cannot be combined with hospital or department scopes.",
                400);

        var hospitalScopes = scopes
            .Where(value => value.ScopeType == OrganizationScopeTypes.Hospital)
            .Select(value => value.HospitalId)
            .ToHashSet();

        if (scopes.Any(value =>
                value.ScopeType == OrganizationScopeTypes.Department &&
                hospitalScopes.Contains(value.HospitalId)))
        {
            return ScopeResolution.Failure(
                "A department scope is redundant when its hospital scope is already assigned.",
                400);
        }

        return ScopeResolution.Success(scopes);
    }

    private bool CanAssignScopes(IReadOnlyList<OrganizationScope> scopes) =>
        scopes.All(scope => scope.ScopeType switch
        {
            OrganizationScopeTypes.Tenant => organizationScopeContext.IsTenantWide,
            OrganizationScopeTypes.Hospital =>
                scope.HospitalId.HasValue &&
                organizationScopeContext.CanManageHospital(scope.HospitalId.Value),
            OrganizationScopeTypes.Department =>
                scope.HospitalId.HasValue &&
                scope.DepartmentId.HasValue &&
                organizationScopeContext.CanManageDepartment(
                    scope.HospitalId.Value,
                    scope.DepartmentId.Value),
            _ => false
        });

    private bool CanManageMembershipScopes(IReadOnlyList<OrganizationScope> scopes) =>
        scopes.Count > 0 && CanAssignScopes(scopes);

    private bool IsTenantAdministrator() =>
        organizationScopeContext.MembershipId.HasValue &&
        permissionContext.HasPermission(TenantPermissions.MembershipManage);

    private async Task<RoleAssignmentValidation> ValidateRoleAssignmentAsync(
        string? roleCode,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(roleCode) || roleCode.Length > 50)
            return RoleAssignmentValidation.Failure("A valid tenant role code is required.", 400);

        if (!await LockRoleForShareAsync(tenantId, roleCode, cancellationToken))
            return RoleAssignmentValidation.Failure("The requested tenant role is not active or does not exist.", 400);

        var role = await db.TenantRoles
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.TenantId == tenantId &&
                         value.Code == roleCode &&
                         value.IsActive,
                cancellationToken);
        if (role is null)
            return RoleAssignmentValidation.Failure("The requested tenant role is not active or does not exist.", 400);

        var permissions = await db.TenantRolePermissions
            .AsNoTracking()
            .Where(value => value.TenantId == tenantId && value.RoleId == role.Id)
            .Select(value => value.PermissionCode)
            .ToArrayAsync(cancellationToken);

        if (permissions.Length == 0 ||
            permissions.Any(permission =>
            {
                var definition = TenantPermissionCatalog.Find(permission);
                return definition is null ||
                       (!definition.IsAssignable && !(role.IsSystem && definition.IsSystemOnly));
            }))
            return RoleAssignmentValidation.Failure(
                "The requested tenant role contains no valid grants or includes a gated permission.",
                409);

        if (role.IsSystem)
        {
            if (!permissionContext.HasPermission(TenantPermissions.MembershipRoleAssignSystem))
                return RoleAssignmentValidation.Failure(
                    "Only administrators with explicit system-role assignment authority may assign a system role.",
                    403);
        }
        else if (permissions.Any(permission => !permissionContext.HasPermission(permission)))
        {
            return RoleAssignmentValidation.Failure(
                "You cannot assign a custom role that grants permissions beyond your own delegated authority.",
                403);
        }

        return RoleAssignmentValidation.Success();
    }

    private bool TryGetTenantId(out Guid tenantId)
    {
        tenantId = tenantContext.TenantId.GetValueOrDefault();
        return tenantId != Guid.Empty;
    }

    private static bool ValidateIdentity(
        string? issuerInput,
        string? subjectInput,
        string configuredIssuer,
        out string? issuer,
        out string? subject,
        out string? error)
    {
        issuer = issuerInput?.Trim();
        subject = subjectInput?.Trim();
        error = null;

        if (string.IsNullOrWhiteSpace(configuredIssuer) ||
            !string.Equals(issuer, configuredIssuer.Trim(), StringComparison.Ordinal))
        {
            error = "The identity issuer must exactly match the configured OIDC authority.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 255)
        {
            error = "The OIDC subject is required and must not exceed 255 characters.";
            return false;
        }

        return true;
    }

    private async Task<bool> LockTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction;
        if (transaction is null)
            throw new InvalidOperationException(
                "Membership provisioning requires the audited request transaction.");

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT \"Id\" FROM \"tenants\" WHERE \"Id\" = @tenantId FOR UPDATE";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenantId";
        parameter.Value = tenantId;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }

    private async Task<bool> HasAnotherTenantWideAdministratorAsync(
        Guid currentMembershipId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (!await LockTenantAsync(tenantId, cancellationToken))
            return false;

        return await db.TenantMemberships.Include(value => value.Scopes)
            .AnyAsync(value =>
                value.Id != currentMembershipId &&
                value.IsActive &&
                value.Role == TenantRoles.HospitalAdmin &&
                value.Scopes.Any(scope => scope.ScopeType == OrganizationScopeTypes.Tenant),
                cancellationToken);
    }

    private static bool IsTenantWideAdministrator(TenantMembership membership) =>
        membership.Role == TenantRoles.HospitalAdmin &&
        membership.Scopes.Any(scope => scope.ScopeType == OrganizationScopeTypes.Tenant);

    private static void AddScopes(
        TenantMembership membership,
        IReadOnlyList<OrganizationScope> scopes)
    {
        foreach (var scope in scopes)
        {
            membership.Scopes.Add(new TenantMembershipScope
            {
                TenantId = membership.TenantId,
                MembershipId = membership.Id,
                ScopeType = scope.ScopeType,
                HospitalId = scope.HospitalId,
                DepartmentId = scope.DepartmentId
            });
        }
    }

    private void AddScopeRows(
        TenantMembership membership,
        IReadOnlyList<OrganizationScope> scopes)
    {
        foreach (var scope in scopes)
        {
            db.TenantMembershipScopes.Add(new TenantMembershipScope
            {
                TenantId = membership.TenantId,
                MembershipId = membership.Id,
                ScopeType = scope.ScopeType,
                HospitalId = scope.HospitalId,
                DepartmentId = scope.DepartmentId
            });
        }
    }

    private static IReadOnlyList<OrganizationScope> ToScopes(TenantMembership membership) =>
        membership.Scopes.Select(scope =>
            new OrganizationScope(
                scope.ScopeType,
                scope.HospitalId,
                scope.DepartmentId)).ToArray();

    private static TenantMembershipDto ToDto(TenantMembership membership) =>
        ToDto(membership, ToScopes(membership));

    private static TenantMembershipDto ToDto(
        TenantMembership membership,
        IReadOnlyList<OrganizationScope> scopes) =>
        new(
            membership.Id,
            membership.Issuer,
            membership.Subject,
            membership.Role,
            membership.IsActive,
            membership.CreatedAtUtc,
            membership.UpdatedAtUtc,
            scopes);

    private static TenantMembershipResult<T> Failure<T>(string error, int statusCode) =>
        TenantMembershipResult<T>.Failure(error, statusCode);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        };

    private async Task<bool> LockRoleForShareAsync(
        Guid tenantId,
        string roleCode,
        CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction;
        if (transaction is null)
            throw new InvalidOperationException("Role assignment requires the audited request transaction.");

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            SELECT "Id" FROM "tenant_roles"
            WHERE "TenantId" = @tenantId AND "Code" = @roleCode AND "IsActive" = TRUE
            FOR SHARE
            """;

        var tenantParameter = command.CreateParameter();
        tenantParameter.ParameterName = "tenantId";
        tenantParameter.Value = tenantId;
        command.Parameters.Add(tenantParameter);

        var roleParameter = command.CreateParameter();
        roleParameter.ParameterName = "roleCode";
        roleParameter.Value = roleCode;
        command.Parameters.Add(roleParameter);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }

    private sealed record RoleAssignmentValidation(string? Error, int StatusCode)
    {
        public static RoleAssignmentValidation Success() => new(null, 200);
        public static RoleAssignmentValidation Failure(string error, int statusCode) => new(error, statusCode);
    }

    private sealed record ScopeResolution(
        IReadOnlyList<OrganizationScope> Scopes,
        string? Error,
        int StatusCode)
    {
        public static ScopeResolution Success(IReadOnlyList<OrganizationScope> scopes) =>
            new(scopes, null, 200);

        public static ScopeResolution Failure(string error, int statusCode) =>
            new(Array.Empty<OrganizationScope>(), error, statusCode);
    }
}
