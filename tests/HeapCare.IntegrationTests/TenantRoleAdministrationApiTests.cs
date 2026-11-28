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
        Assert.Equal(2, roleAudits.Count);
        Assert.Equal("Created", roleAudits[0].Action);
        Assert.Equal("PermissionsUpdated", roleAudits[1].Action);
        Assert.Equal("rbac-tenant-admin", roleAudits[1].ActorSubject);
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
