using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HeapCare.Infrastructure.Persistence.Configurations;

public sealed class TenantMembershipConfiguration : IEntityTypeConfiguration<TenantMembership>
{
    public void Configure(EntityTypeBuilder<TenantMembership> builder)
    {
        builder.ToTable("tenant_memberships");
        builder.Property(x => x.Issuer).HasMaxLength(255).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(255).IsRequired();
        builder.Property(x => x.Role).HasMaxLength(50).IsRequired();
        builder.HasAlternateKey(x => new { x.TenantId, x.Id });
        builder.HasOne<TenantRole>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.Role })
            .HasPrincipalKey(x => new { x.TenantId, x.Code })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.TenantId, x.Issuer, x.Subject }).IsUnique();
    }
}
