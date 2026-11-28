using HeapCare.Application.Tenancy;
using HeapCare.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace HeapCare.IntegrationTests;

[Collection("PostgreSQL integration")]
public sealed class TenantRolePersistenceTests(FacilityApiFactory factory)
{
    [Fact]
    public void PersistedRoleModel_UsesTenantCompositeKeysAndConstrainedPermissionAudit()
    {
        factory.ResetDatabase();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HeapCareDbContext>();
        var model = db.GetService<IDesignTimeModel>().Model;

        var role = model.FindEntityType(typeof(TenantRole))!;
        Assert.Contains(role.GetKeys(), key =>
            key.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(TenantOwnedEntity.TenantId), nameof(TenantRole.Code)]));
        Assert.Contains(role.GetKeys(), key =>
            key.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(TenantOwnedEntity.TenantId), nameof(TenantOwnedEntity.Id)]));

        var membership = model.FindEntityType(typeof(TenantMembership))!;
        var membershipRoleFk = Assert.Single(
            membership.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType == role);
        Assert.Equal(DeleteBehavior.Restrict, membershipRoleFk.DeleteBehavior);
        Assert.Equal(
            new[] { nameof(TenantOwnedEntity.TenantId), nameof(TenantMembership.Role) },
            membershipRoleFk.Properties.Select(property => property.Name));
        Assert.Equal(
            new[] { nameof(TenantOwnedEntity.TenantId), nameof(TenantRole.Code) },
            membershipRoleFk.PrincipalKey.Properties.Select(property => property.Name));

        var rolePermission = model.FindEntityType(typeof(TenantRolePermission))!;
        var uniqueGrantIndex = Assert.Single(rolePermission.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(TenantOwnedEntity.TenantId),
                    nameof(TenantRolePermission.RoleId),
                    nameof(TenantRolePermission.PermissionCode)
                ]));
        Assert.True(uniqueGrantIndex.IsUnique);

        var roleAudit = model.FindEntityType(typeof(TenantRoleChangeAudit))!;
        Assert.Equal("jsonb",
            roleAudit.FindProperty(nameof(TenantRoleChangeAudit.PreviousPermissionCodesJson))!.GetColumnType());
        Assert.Equal("jsonb",
            roleAudit.FindProperty(nameof(TenantRoleChangeAudit.NewPermissionCodesJson))!.GetColumnType());
        Assert.Contains(
            roleAudit.GetCheckConstraints(),
            constraint => constraint.Name == "CK_tenant_role_change_audits_action");
    }

    [Fact]
    public async Task TenantRoleChangeAudit_IsAppendOnlyInApplicationAndPostgreSql()
    {
        factory.ResetDatabase();
        var tenantId = Guid.NewGuid();
        await factory.SeedTenantMembershipAsync(
            tenantId,
            TenantRoles.HospitalAdmin,
            subject: "role-audit-actor");

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<HeapCareDbContext>();

        var role = await db.TenantRoles.SingleAsync(value => value.Code == TenantRoles.HospitalAdmin);
        var audit = new TenantRoleChangeAudit
        {
            TenantId = tenantId,
            RoleId = role.Id,
            Action = "DetailsUpdated",
            ActorIssuer = TestJwtTokenFactory.Issuer,
            ActorSubject = "role-audit-actor",
            Reason = "Verify append-only role configuration history.",
            PreviousPermissionCodesJson = "[]",
            NewPermissionCodesJson = "[]"
        };
        db.TenantRoleChangeAudits.Add(audit);
        await db.SaveChangesAsync();

        audit.Reason = "Attempt to rewrite immutable permission history.";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var persisted = await db.TenantRoleChangeAudits
            .AsNoTracking()
            .SingleAsync(value => value.Id == audit.Id);
        Assert.Equal("Verify append-only role configuration history.", persisted.Reason);

        var databaseError = await Assert.ThrowsAsync<PostgresException>(
            () => db.Database.ExecuteSqlRawAsync(
                """UPDATE "tenant_role_change_audits" SET "Reason" = 'direct SQL mutation' WHERE "Id" = {0}""",
                audit.Id));
        Assert.Contains("append-only", databaseError.Message, StringComparison.OrdinalIgnoreCase);
    }
}
