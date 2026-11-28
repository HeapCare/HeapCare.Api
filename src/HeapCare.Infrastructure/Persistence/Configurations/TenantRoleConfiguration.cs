using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HeapCare.Infrastructure.Persistence.Configurations;

public sealed class TenantRoleConfiguration : IEntityTypeConfiguration<TenantRole>
{
    public void Configure(EntityTypeBuilder<TenantRole> builder)
    {
        builder.ToTable("tenant_roles", table =>
            table.HasCheckConstraint(
                "CK_tenant_roles_permissions_revision",
                "\"PermissionsRevision\" >= 0"));
        builder.Property(x => x.Code).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500).IsRequired();
        builder.Property(x => x.PermissionsRevision).HasDefaultValue(0).IsRequired();
        builder.HasAlternateKey(x => new { x.TenantId, x.Id });
        builder.HasAlternateKey(x => new { x.TenantId, x.Code });
        builder.HasIndex(x => new { x.TenantId, x.IsActive, x.IsSystem });
    }
}

public sealed class TenantRolePermissionConfiguration : IEntityTypeConfiguration<TenantRolePermission>
{
    public void Configure(EntityTypeBuilder<TenantRolePermission> builder)
    {
        builder.ToTable("tenant_role_permissions");
        builder.Property(x => x.PermissionCode).HasMaxLength(128).IsRequired();
        builder.HasIndex(x => new { x.TenantId, x.RoleId, x.PermissionCode }).IsUnique();
        builder.HasOne<TenantRole>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.RoleId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TenantRoleChangeAuditConfiguration : IEntityTypeConfiguration<TenantRoleChangeAudit>
{
    public void Configure(EntityTypeBuilder<TenantRoleChangeAudit> builder)
    {
        builder.ToTable("tenant_role_change_audits", table =>
            table.HasCheckConstraint(
                "CK_tenant_role_change_audits_action",
                "\"Action\" IN ('Created', 'PermissionsUpdated', 'DetailsUpdated', 'Deactivated', 'Reactivated')"));

        builder.Property(x => x.Action).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ActorIssuer).HasMaxLength(255).IsRequired();
        builder.Property(x => x.ActorSubject).HasMaxLength(255).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        builder.Property(x => x.PreviousName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.NewName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.PreviousDescription).HasMaxLength(500).IsRequired();
        builder.Property(x => x.NewDescription).HasMaxLength(500).IsRequired();
        builder.Property(x => x.PreviousPermissionCodesJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.NewPermissionCodesJson).HasColumnType("jsonb").IsRequired();
        builder.HasOne<TenantRole>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.RoleId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.TenantId, x.RoleId, x.CreatedAtUtc });
        builder.HasIndex(x => new { x.TenantId, x.ActorIssuer, x.ActorSubject, x.CreatedAtUtc });
    }
}
