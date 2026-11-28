using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HeapCare.Api.Middleware;
using HeapCare.Application.Tenancy;
using HeapCare.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HeapCare.IntegrationTests;

[Collection("PostgreSQL integration")]
public sealed class TenantRoleAdministrationApiTests(FacilityApiFactory factory)
{
    [Fact]
    public async Task CustomRolePermissions_ArePersistedAuditedAndAppliedOnTheNextRequest()
    {
        factory.ResetDatabase();
        var tenantId = Guid.NewGuid();
        await factory.SeedTenantMembershipAsync(
            tenantId,
            TenantRoles.HospitalAdmin,
            subject: "rbac-tenant-admin");

        using var admin = CreateClient("rbac-tenant-admin", tenantId);
        var createRole = await admin.PostAsJsonAsync(
            "/api/v1/tenant/roles",
            new
            {
                name = "Facility Read Only",
                description = "Can read the facility hierarchy but cannot create or delete it.",
                permissionCodes = new[] { TenantPermissions.FacilityRead },
                reason = "Create a restricted role for facility inspection."
            });

        Assert.Equal(HttpStatusCode.Created, createRole.StatusCode);
        var createdRole = await createRole.Content.ReadFromJsonAsync<TenantRoleResponse>();
        Assert.NotNull(createdRole);
        Assert.StartsWith("custom_", createdRole!.Code, StringComparison.Ordinal);
        Assert.False(createdRole.IsSystem);
        Assert.Single(createdRole.PermissionCodes);

        var createMembership = await admin.PostAsJsonAsync(
            "/api/v1/tenant/memberships",
            new
            {
                issuer = TestJwtTokenFactory.Issuer,
                subject = "rbac-custom-role-user",
                role = createdRole.Code,
                scopes = new[]
                {
                    new { scopeType = OrganizationScopeTypes.Tenant }
                }
            });
        Assert.Equal(HttpStatusCode.Created, createMembership.StatusCode);
        var membership = await createMembership.Content.ReadFromJsonAsync<TenantMembershipResponse>();
        Assert.NotNull(membership);

        using var member = CreateClient("rbac-custom-role-user", tenantId);
        var initialRead = await member.GetAsync("/api/v1/hospitals");
        var initialCreate = await member.PostAsJsonAsync(
            "/api/v1/hospitals",
            new { name = "Must not be created", code = "RBAC-READ-ONLY" });

        Assert.Equal(HttpStatusCode.OK, initialRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, initialCreate.StatusCode);

        var updateRole = await admin.PutAsJsonAsync(
            $"/api/v1/tenant/roles/{createdRole.Id:D}",
            new
            {
                name = "Facility Creator",
                description = "Can create or update facility structure but cannot read facility listings.",
                permissionCodes = new[] { TenantPermissions.FacilityWrite },
                reason = "Change the delegated capability to facility maintenance."
            });
        Assert.Equal(HttpStatusCode.OK, updateRole.StatusCode);

        var updatedRead = await member.GetAsync("/api/v1/hospitals");
        var updatedCreate = await member.PostAsJsonAsync(
            "/api/v1/hospitals",
            new { name = "Created with persisted role", code = "RBAC-CUSTOM-WRITE" });

        Assert.Equal(HttpStatusCode.Forbidden, updatedRead.StatusCode);
        Assert.Equal(HttpStatusCode.Created, updatedCreate.StatusCode);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<HeapCareDbContext>();

        var persistedRole = await db.TenantRoles.AsNoTracking()
            .SingleAsync(value => value.Id == createdRole.Id);
        Assert.Equal("Facility Creator", persistedRole.Name);
        Assert.Equal(2, persistedRole.PermissionsRevision);

        var currentGrants = await db.TenantRolePermissions.AsNoTracking()
            .Where(value => value.RoleId == createdRole.Id)
            .Select(value => value.PermissionCode)
            .ToArrayAsync();
        Assert.Equal(new[] { TenantPermissions.FacilityWrite }, currentGrants);

        var roleAudits = await db.TenantRoleChangeAudits.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(value => value.TenantId == tenantId && value.RoleId == createdRole.Id)
            .OrderBy(value => value.CreatedAtUtc)
            .ToListAsync();
        Assert.Equal(
            new[] { "Created", "PermissionsUpdated" },
            roleAudits.Select(value => value.Action));
        Assert.Equal("rbac-tenant-admin", roleAudits[1].ActorSubject);
        Assert.Equal(string.Empty, roleAudits[0].PreviousName);
        Assert.Equal("Facility Read Only", roleAudits[0].NewName);
        Assert.Equal(string.Empty, roleAudits[0].PreviousDescription);
        Assert.Equal("Can read the facility hierarchy but cannot create or delete it.", roleAudits[0].NewDescription);
        Assert.Equal("Facility Read Only", roleAudits[1].PreviousName);
        Assert.Equal("Facility Creator", roleAudits[1].NewName);
        Assert.Equal("Can read the facility hierarchy but cannot create or delete it.", roleAudits[1].PreviousDescription);
        Assert.Equal("Can create or update facility structure but cannot read facility listings.", roleAudits[1].NewDescription);
        Assert.Equal("Create a restricted role for facility inspection.", roleAudits[0].Reason);
        Assert.Equal("Change the delegated capability to facility maintenance.", roleAudits[1].Reason);

        using var previous = JsonDocument.Parse(roleAudits[1].PreviousPermissionCodesJson);
        using var current = JsonDocument.Parse(roleAudits[1].NewPermissionCodesJson);
        Assert.Equal(
            TenantPermissions.FacilityRead,
            Assert.Single(previous.RootElement.EnumerateArray()).GetString());
        Assert.Equal(
            TenantPermissions.FacilityWrite,
            Assert.Single(current.RootElement.EnumerateArray()).GetString());

        var persistedMembership = await db.TenantMemberships.AsNoTracking()
            .SingleAsync(value => value.Id == membership!.Id);
        Assert.Equal(createdRole.Code, persistedMembership.Role);
    }

    [Fact]
    public async Task TenantRoleAdministration_RejectsIpdGrantsAndHospitalScopedRoleChanges()
    {
        factory.ResetDatabase();
        var tenantId = Guid.NewGuid();
        await factory.SeedTenantMembershipAsync(
            tenantId,
            TenantRoles.HospitalAdmin,
            subject: "rbac-security-admin");

        using var admin = CreateClient("rbac-security-admin", tenantId);
        var ipdRole = await admin.PostAsJsonAsync(
            "/api/v1/tenant/roles",
            new
            {
                name = "Unapproved IPD Role",
                description = "This role must not receive an unapproved inpatient permission.",
                permissionCodes = new[] { "inpatient.admit" },
                reason = "Attempt to test the IPD permission gate."
            });
        Assert.Equal(HttpStatusCode.Forbidden, ipdRole.StatusCode);

        var hospitalResponse = await admin.PostAsJsonAsync(
            "/api/v1/hospitals",
            new { name = "Scope Boundary Hospital", code = "RBAC-SCOPE" });
        Assert.Equal(HttpStatusCode.Created, hospitalResponse.StatusCode);
        var hospital = await hospitalResponse.Content.ReadFromJsonAsync<HospitalResponse>();
        Assert.NotNull(hospital);

        await factory.SeedTenantMembershipAsync(
            tenantId,
            TenantRoles.HospitalAdmin,
            scopes: [new OrganizationScope(OrganizationScopeTypes.Hospital, hospital!.Id)],
            subject: "rbac-hospital-admin");

        using var hospitalAdmin = CreateClient("rbac-hospital-admin", tenantId);
        var createRole = await hospitalAdmin.PostAsJsonAsync(
            "/api/v1/tenant/roles",
            new
            {
                name = "Hospital Scoped Role",
                description = "A hospital-scoped administrator must not change tenant-wide role definitions.",
                permissionCodes = new[] { TenantPermissions.FacilityRead },
                reason = "This change must be denied outside tenant scope."
            });

        Assert.Equal(HttpStatusCode.Forbidden, createRole.StatusCode);
    }

    [Fact]
    public async Task DelegatedMembershipAdministrator_CannotAssignPermissionsBeyondTheirOwnGrantSet()
    {
        factory.ResetDatabase();
        var tenantId = Guid.NewGuid();
        await factory.SeedTenantMembershipAsync(
            tenantId,
            TenantRoles.HospitalAdmin,
            subject: "rbac-root-admin");

        using var rootAdmin = CreateClient("rbac-root-admin", tenantId);
        var createDelegatedRole = await rootAdmin.PostAsJsonAsync(
            "/api/v1/tenant/roles",
            new
            {
                name = "Membership Delegator",
                description = "May manage memberships but cannot grant operational permissions.",
                permissionCodes = new[] { TenantPermissions.MembershipManage },
                reason = "Create a least-privilege membership administrator."
            });
        Assert.Equal(HttpStatusCode.Created, createDelegatedRole.StatusCode);
        var delegatedRole = await createDelegatedRole.Content.ReadFromJsonAsync<TenantRoleResponse>();
        Assert.NotNull(delegatedRole);

        var createDelegatedMembership = await rootAdmin.PostAsJsonAsync(
            "/api/v1/tenant/memberships",
            new
            {
                issuer = TestJwtTokenFactory.Issuer,
                subject = "rbac-delegated-admin",
                role = delegatedRole!.Code,
                scopes = new[] { new { scopeType = OrganizationScopeTypes.Tenant } }
            });
        Assert.Equal(HttpStatusCode.Created, createDelegatedMembership.StatusCode);

        using var delegatedAdmin = CreateClient("rbac-delegated-admin", tenantId);
        var availablePermissions = await delegatedAdmin.GetFromJsonAsync<PermissionDefinitionResponse[]>(
            "/api/v1/tenant/roles/permissions");
        Assert.NotNull(availablePermissions);
        var delegatedPermission = Assert.Single(availablePermissions!);
        Assert.Equal(TenantPermissions.MembershipManage, delegatedPermission.Code);

        var escalationAttempt = await delegatedAdmin.PostAsJsonAsync(
            "/api/v1/tenant/memberships",
            new
            {
                issuer = TestJwtTokenFactory.Issuer,
                subject = "rbac-escalation-target",
                role = TenantRoles.FacilityManager,
                scopes = new[] { new { scopeType = OrganizationScopeTypes.Tenant } }
            });

        Assert.Equal(HttpStatusCode.Forbidden, escalationAttempt.StatusCode);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<HeapCareDbContext>();
        Assert.False(await db.TenantMemberships.AnyAsync(
            value => value.Subject == "rbac-escalation-target"));
    }

    [Fact]
    public async Task SystemRolesCannotBeChanged_AndCustomRoleCannotBeDeactivatedWhileAssigned()
    {
        factory.ResetDatabase();
        var tenantId = Guid.NewGuid();
        await factory.SeedTenantMembershipAsync(
            tenantId,
            TenantRoles.HospitalAdmin,
            subject: "rbac-lifecycle-admin");

        using var admin = CreateClient("rbac-lifecycle-admin", tenantId);
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<HeapCareDbContext>();

        var systemRole = await db.TenantRoles.AsNoTracking()
            .SingleAsync(value => value.TenantId == tenantId && value.Code == TenantRoles.HospitalAdmin);
        var systemUpdate = await admin.PutAsJsonAsync(
            $"/api/v1/tenant/roles/{systemRole.Id:D}",
            new
            {
                name = "Overwritten System Administrator",
                description = "System roles must remain immutable.",
                permissionCodes = new[] { TenantPermissions.FacilityRead },
                reason = "Verify the system role immutability boundary."
            });
        Assert.Equal(HttpStatusCode.Conflict, systemUpdate.StatusCode);

        var createRole = await admin.PostAsJsonAsync(
            "/api/v1/tenant/roles",
            new
            {
                name = "Temporary Facility Viewer",
                description = "Temporary custom role for lifecycle verification.",
                permissionCodes = new[] { TenantPermissions.FacilityRead },
                reason = "Create a temporary role for assignment lifecycle testing."
            });
        Assert.Equal(HttpStatusCode.Created, createRole.StatusCode);
        var role = await createRole.Content.ReadFromJsonAsync<TenantRoleResponse>();
        Assert.NotNull(role);

        var createMembership = await admin.PostAsJsonAsync(
            "/api/v1/tenant/memberships",
            new
            {
                issuer = TestJwtTokenFactory.Issuer,
                subject = "rbac-temporary-user",
                role = role!.Code,
                scopes = new[] { new { scopeType = OrganizationScopeTypes.Tenant } }
            });
        Assert.Equal(HttpStatusCode.Created, createMembership.StatusCode);
        var membership = await createMembership.Content.ReadFromJsonAsync<TenantMembershipResponse>();
        Assert.NotNull(membership);

        var deniedDeactivation = await admin.PostAsJsonAsync(
            $"/api/v1/tenant/roles/{role.Id:D}/deactivate",
            new { reason = "Deactivate this role while a member is still active." });
        Assert.Equal(HttpStatusCode.Conflict, deniedDeactivation.StatusCode);

        var deactivateMembership = await admin.PostAsync(
            $"/api/v1/tenant/memberships/{membership!.Id:D}/deactivate",
            content: null);
        Assert.Equal(HttpStatusCode.OK, deactivateMembership.StatusCode);

        var deactivateRole = await admin.PostAsJsonAsync(
            $"/api/v1/tenant/roles/{role.Id:D}/deactivate",
            new { reason = "Retire this custom role after its membership was deactivated." });
        Assert.Equal(HttpStatusCode.OK, deactivateRole.StatusCode);
    }


    [Fact]
    public async Task MembershipCannotBeReactivatedWhileItsCustomRoleIsInactive()
    {
        factory.ResetDatabase();
        var tenantId = Guid.NewGuid();
        await factory.SeedTenantMembershipAsync(
            tenantId,
            TenantRoles.HospitalAdmin,
            subject: "rbac-reactivation-admin");

        using var admin = CreateClient("rbac-reactivation-admin", tenantId);
        var createRole = await admin.PostAsJsonAsync(
            "/api/v1/tenant/roles",
            new
            {
                name = "Temporary Reactivation Role",
                description = "Used to verify membership and role lifecycle ordering.",
                permissionCodes = new[] { TenantPermissions.FacilityRead },
                reason = "Create a role for the reactivation lifecycle test."
            });
        Assert.Equal(HttpStatusCode.Created, createRole.StatusCode);
        var role = await createRole.Content.ReadFromJsonAsync<TenantRoleResponse>();
        Assert.NotNull(role);

        var createMembership = await admin.PostAsJsonAsync(
            "/api/v1/tenant/memberships",
            new
            {
                issuer = TestJwtTokenFactory.Issuer,
                subject = "rbac-reactivation-target",
                role = role!.Code,
                scopes = new[] { new { scopeType = OrganizationScopeTypes.Tenant } }
            });
        Assert.Equal(HttpStatusCode.Created, createMembership.StatusCode);
        var membership = await createMembership.Content.ReadFromJsonAsync<TenantMembershipResponse>();
        Assert.NotNull(membership);

        var deactivateMembership = await admin.PostAsync(
            $"/api/v1/tenant/memberships/{membership!.Id:D}/deactivate",
            content: null);
        Assert.Equal(HttpStatusCode.OK, deactivateMembership.StatusCode);

        var deactivateRole = await admin.PostAsJsonAsync(
            $"/api/v1/tenant/roles/{role.Id:D}/deactivate",
            new { reason = "Retire the role while no active membership uses it." });
        Assert.Equal(HttpStatusCode.OK, deactivateRole.StatusCode);

        var blockedReactivation = await admin.PostAsync(
            $"/api/v1/tenant/memberships/{membership.Id:D}/reactivate",
            content: null);
        Assert.Equal(HttpStatusCode.BadRequest, blockedReactivation.StatusCode);

        var reactivateRole = await admin.PostAsJsonAsync(
            $"/api/v1/tenant/roles/{role.Id:D}/reactivate",
            new { reason = "Restore the role before restoring its membership." });
        Assert.Equal(HttpStatusCode.OK, reactivateRole.StatusCode);

        var reactivateMembership = await admin.PostAsync(
            $"/api/v1/tenant/memberships/{membership.Id:D}/reactivate",
            content: null);
        Assert.Equal(HttpStatusCode.OK, reactivateMembership.StatusCode);

        using var member = CreateClient("rbac-reactivation-target", tenantId);
        var authorizedRead = await member.GetAsync("/api/v1/hospitals");
        Assert.Equal(HttpStatusCode.OK, authorizedRead.StatusCode);
    }


    [Fact]
    public async Task MembershipReactivation_RejectsGrantsOutsideTheActorsCurrentDelegation()
    {
        factory.ResetDatabase();
        var tenantId = Guid.NewGuid();
        await factory.SeedTenantMembershipAsync(
            tenantId,
            TenantRoles.HospitalAdmin,
            subject: "rbac-reactivation-root");

        using var rootAdmin = CreateClient("rbac-reactivation-root", tenantId);
        var createDormantRole = await rootAdmin.PostAsJsonAsync(
            "/api/v1/tenant/roles",
            new
            {
                name = "Dormant Membership Manager",
                description = "Initially grants membership management only.",
                permissionCodes = new[] { TenantPermissions.MembershipManage },
                reason = "Create a role for testing reactivation delegation."
            });
        Assert.Equal(HttpStatusCode.Created, createDormantRole.StatusCode);
        var dormantRole = await createDormantRole.Content.ReadFromJsonAsync<TenantRoleResponse>();
        Assert.NotNull(dormantRole);

        var createTarget = await rootAdmin.PostAsJsonAsync(
            "/api/v1/tenant/memberships",
            new
            {
                issuer = TestJwtTokenFactory.Issuer,
                subject = "rbac-reactivation-delegation-target",
                role = dormantRole!.Code,
                scopes = new[] { new { scopeType = OrganizationScopeTypes.Tenant } }
            });
        Assert.Equal(HttpStatusCode.Created, createTarget.StatusCode);
        var target = await createTarget.Content.ReadFromJsonAsync<TenantMembershipResponse>();
        Assert.NotNull(target);

        var deactivateTarget = await rootAdmin.PostAsync(
            $"/api/v1/tenant/memberships/{target!.Id:D}/deactivate",
            content: null);
        Assert.Equal(HttpStatusCode.OK, deactivateTarget.StatusCode);

        var createDelegateRole = await rootAdmin.PostAsJsonAsync(
            "/api/v1/tenant/roles",
            new
            {
                name = "Restricted Membership Delegate",
                description = "Can manage memberships but cannot grant facility access.",
                permissionCodes = new[] { TenantPermissions.MembershipManage },
                reason = "Create a restricted administrator for the delegation test."
            });
        Assert.Equal(HttpStatusCode.Created, createDelegateRole.StatusCode);
        var delegateRole = await createDelegateRole.Content.ReadFromJsonAsync<TenantRoleResponse>();
        Assert.NotNull(delegateRole);

        var createDelegate = await rootAdmin.PostAsJsonAsync(
            "/api/v1/tenant/memberships",
            new
            {
                issuer = TestJwtTokenFactory.Issuer,
                subject = "rbac-reactivation-delegate",
                role = delegateRole!.Code,
                scopes = new[] { new { scopeType = OrganizationScopeTypes.Tenant } }
            });
        Assert.Equal(HttpStatusCode.Created, createDelegate.StatusCode);

        var expandDormantRole = await rootAdmin.PutAsJsonAsync(
            $"/api/v1/tenant/roles/{dormantRole.Id:D}",
            new
            {
                name = "Dormant Membership Manager",
                description = "Now also grants facility read access.",
                permissionCodes = new[]
                {
                    TenantPermissions.MembershipManage,
                    TenantPermissions.FacilityRead
                },
                reason = "Verify reactivation checks the current persisted grants."
            });
        Assert.Equal(HttpStatusCode.OK, expandDormantRole.StatusCode);

        using var delegatedAdmin = CreateClient("rbac-reactivation-delegate", tenantId);
        var deniedReactivation = await delegatedAdmin.PostAsync(
            $"/api/v1/tenant/memberships/{target.Id:D}/reactivate",
            content: null);
        Assert.Equal(HttpStatusCode.Forbidden, deniedReactivation.StatusCode);

        var allowedReactivation = await rootAdmin.PostAsync(
            $"/api/v1/tenant/memberships/{target.Id:D}/reactivate",
            content: null);
        Assert.Equal(HttpStatusCode.OK, allowedReactivation.StatusCode);
    }

    private HttpClient CreateClient(string subject, Guid tenantId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                TestJwtTokenFactory.CreateToken(subject: subject));
        client.DefaultRequestHeaders.Add(
            TenantContextMiddleware.HeaderName,
            tenantId.ToString("D"));
        return client;
    }

    private sealed record PermissionDefinitionResponse(
        string Code,
        string Module,
        string Resource,
        string Action,
        string DisplayName,
        string Description,
        TenantPermissionRisk Risk,
        bool IsAssignable);

    private sealed record TenantRoleResponse(
        Guid Id,
        string Code,
        string Name,
        string Description,
        bool IsSystem,
        bool IsActive,
        IReadOnlyList<string> PermissionCodes);

    private sealed record TenantMembershipResponse(Guid Id, string Role, bool IsActive);
    private sealed record HospitalResponse(Guid Id, string Name, string? Code);
}
