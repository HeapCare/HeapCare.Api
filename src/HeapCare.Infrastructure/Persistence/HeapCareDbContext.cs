using HeapCare.Application.Appointments;
using HeapCare.Application.Billing;
using HeapCare.Application.Clinical;
using HeapCare.Application.Patients;
using HeapCare.Application.Staff;
using HeapCare.Application.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HeapCare.Infrastructure.Persistence;

public sealed class HeapCareDbContext(
    DbContextOptions<HeapCareDbContext> options,
    ITenantContext tenantContext) : DbContext(options)
{
    public Guid? CurrentTenantId => tenantContext.TenantId;
    public DbSet<Hospital> Hospitals => Set<Hospital>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Ward> Wards => Set<Ward>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Bed> Beds => Set<Bed>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<PlatformOperator> PlatformOperators => Set<PlatformOperator>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<TenantMembershipScope> TenantMembershipScopes => Set<TenantMembershipScope>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<HospitalPatientRegistration> HospitalPatientRegistrations => Set<HospitalPatientRegistration>();
    public DbSet<PatientIdentifier> PatientIdentifiers => Set<PatientIdentifier>();
    public DbSet<HospitalPatientIdentifierPolicy> HospitalPatientIdentifierPolicies => Set<HospitalPatientIdentifierPolicy>();
    public DbSet<StaffProfile> StaffProfiles => Set<StaffProfile>();
    public DbSet<StaffAssignment> StaffAssignments => Set<StaffAssignment>();
    public DbSet<ProviderAvailabilitySession> ProviderAvailabilitySessions => Set<ProviderAvailabilitySession>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<QueueEntry> QueueEntries => Set<QueueEntry>();
    public DbSet<OpdEncounter> OpdEncounters => Set<OpdEncounter>();
    public DbSet<OpdEncounterAmendment> OpdEncounterAmendments => Set<OpdEncounterAmendment>();
    public DbSet<BillingServiceItem> BillingServiceItems => Set<BillingServiceItem>();
    public DbSet<BillingInvoice> BillingInvoices => Set<BillingInvoice>();
    public DbSet<BillingInvoiceLine> BillingInvoiceLines => Set<BillingInvoiceLine>();
    public DbSet<BillingPayment> BillingPayments => Set<BillingPayment>();
    public DbSet<InpatientAdmission> InpatientAdmissions => Set<InpatientAdmission>();
    public DbSet<InpatientBedPlacement> InpatientBedPlacements => Set<InpatientBedPlacement>();
    public DbSet<TenantRole> TenantRoles => Set<TenantRole>();
    public DbSet<TenantRolePermission> TenantRolePermissions => Set<TenantRolePermission>();
    public DbSet<TenantRoleChangeAudit> TenantRoleChangeAudits => Set<TenantRoleChangeAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(HeapCareDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(TenantOwnedEntity).IsAssignableFrom(entityType.ClrType) && !entityType.ClrType.IsAbstract)
                modelBuilder.Entity(entityType.ClrType).Property(nameof(TenantOwnedEntity.Version)).IsConcurrencyToken();
            if (typeof(IPlatformEntity).IsAssignableFrom(entityType.ClrType))
                modelBuilder.Entity(entityType.ClrType).Property(nameof(IPlatformEntity.Version)).IsConcurrencyToken();
        }

        modelBuilder.Entity<Hospital>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Department>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Ward>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Room>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Bed>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<TenantRole>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<TenantRolePermission>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<TenantRoleChangeAudit>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<TenantMembership>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<TenantMembershipScope>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AuditEvent>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Patient>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<HospitalPatientRegistration>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PatientIdentifier>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<HospitalPatientIdentifierPolicy>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<StaffProfile>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<StaffAssignment>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProviderAvailabilitySession>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Appointment>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<QueueEntry>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<OpdEncounter>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<OpdEncounterAmendment>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<BillingServiceItem>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<BillingInvoice>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<BillingInvoiceLine>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<BillingPayment>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InpatientAdmission>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InpatientBedPlacement>().HasOne<Tenant>().WithMany()
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

        TenantModelConfiguration.ApplyTenantFilters(modelBuilder, this);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditAndTenantRules();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyAuditAndTenantRules();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyAuditAndTenantRules()
    {
        foreach (var entry in ChangeTracker.Entries<Patient>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.NormalizedGivenName =
                PatientNameNormalizer.Normalize(entry.Entity.GivenName) ?? string.Empty;
            entry.Entity.NormalizedMiddleName = PatientNameNormalizer.Normalize(entry.Entity.MiddleName);
            entry.Entity.NormalizedFamilyName = PatientNameNormalizer.Normalize(entry.Entity.FamilyName);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<AuditEvent>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.TenantId.HasValue &&
                    (CurrentTenantId is null || entry.Entity.TenantId != CurrentTenantId))
                    throw new InvalidOperationException("An audit event cannot claim an unvalidated tenant context.");
                entry.Entity.TenantId = CurrentTenantId;
                if (entry.Entity.Id == Guid.Empty ||
                    string.IsNullOrWhiteSpace(entry.Entity.Action) ||
                    string.IsNullOrWhiteSpace(entry.Entity.ResourceType) ||
                    string.IsNullOrWhiteSpace(entry.Entity.Outcome) ||
                    string.IsNullOrWhiteSpace(entry.Entity.CorrelationId))
                    throw new InvalidOperationException("An audit event requires its identifier, action, resource type, outcome and correlation ID.");
                if (entry.Entity.OccurredAtUtc == default)
                    entry.Entity.OccurredAtUtc = now;
            }
            else if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Audit events are append-only and cannot be modified or deleted.");
        }

        foreach (var entry in ChangeTracker.Entries<IPlatformEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.Id == Guid.Empty)
                    throw new InvalidOperationException("A platform entity requires a non-empty identifier.");
                entry.Entity.CreatedAtUtc = now;
                entry.Entity.UpdatedAtUtc = now;
                entry.Entity.Version = Guid.NewGuid();
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Property(x => x.Id).IsModified = false;
                entry.Property(x => x.CreatedAtUtc).IsModified = false;
                entry.Entity.UpdatedAtUtc = now;
                entry.Entity.Version = Guid.NewGuid();
            }
            else if (entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Platform records cannot be physically deleted.");
        }

        foreach (var entry in ChangeTracker.Entries<TenantRole>())
        {
            if (entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Tenant roles are retained and must be deactivated instead of deleted.");
            if (entry.State == EntityState.Modified &&
                (entry.Property(nameof(TenantRole.IsSystem)).OriginalValue is true || entry.Entity.IsSystem))
                throw new InvalidOperationException("System tenant roles are immutable and cannot be converted to custom roles.");
        }

        foreach (var entry in ChangeTracker.Entries<TenantRoleChangeAudit>())
        {
            if (entry.State == EntityState.Added &&
                (string.IsNullOrWhiteSpace(entry.Entity.Action) ||
                 string.IsNullOrWhiteSpace(entry.Entity.ActorIssuer) ||
                 string.IsNullOrWhiteSpace(entry.Entity.ActorSubject) ||
                 string.IsNullOrWhiteSpace(entry.Entity.Reason)))
                throw new InvalidOperationException("A tenant role change audit requires an action, actor and reason.");
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Tenant role change history is append-only.");
        }

        foreach (var entry in ChangeTracker.Entries<InpatientAdmission>())
        {
            if (entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Inpatient admissions are retained and cannot be physically deleted.");
        }

        foreach (var entry in ChangeTracker.Entries<InpatientBedPlacement>())
        {
            if (entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Inpatient bed-placement history is retained and cannot be physically deleted.");
        }

        foreach (var entry in ChangeTracker.Entries<BillingInvoiceLine>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Issued invoice lines are immutable and cannot be modified or deleted.");
        }

        foreach (var entry in ChangeTracker.Entries<BillingPayment>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Billing payments are append-only and cannot be modified or deleted.");
        }

        foreach (var entry in ChangeTracker.Entries<BillingInvoice>())
        {
            if (entry.State != EntityState.Modified) continue;

            var sequence = entry.Property(nameof(BillingInvoice.PaymentSequence));
            var status = entry.Property(nameof(BillingInvoice.Status));
            if (entry.Properties.Any(property => property.IsModified &&
                property.Metadata.Name != nameof(BillingInvoice.PaymentSequence) &&
                property.Metadata.Name != nameof(BillingInvoice.Status)))
                throw new InvalidOperationException("Issued invoice details are immutable after issue.");

            if (status.IsModified != sequence.IsModified)
                throw new InvalidOperationException("Invoice settlement status and payment sequence must change together.");

            if (sequence.IsModified && sequence.CurrentValue is int currentSequence &&
                sequence.OriginalValue is int originalSequence && currentSequence != originalSequence + 1)
                throw new InvalidOperationException("Invoice payment sequence must advance exactly once per payment.");

            if (status.IsModified)
            {
                var previousStatus = status.OriginalValue as string;
                var currentStatus = status.CurrentValue as string;
                var validTransition =
                    previousStatus == InvoiceStatuses.Issued &&
                        (currentStatus == InvoiceStatuses.PartiallyPaid || currentStatus == InvoiceStatuses.Paid) ||
                    previousStatus == InvoiceStatuses.PartiallyPaid && currentStatus == InvoiceStatuses.Paid;
                if (!validTransition)
                    throw new InvalidOperationException("Invoice settlement status transition is invalid.");
            }
        }

        foreach (var entry in ChangeTracker.Entries<PatientIdentifier>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Patient identifiers are append-only and cannot be edited or deleted.");
        }

        foreach (var entry in ChangeTracker.Entries<TenantOwnedEntity>())
        {
            if (entry.State is EntityState.Added)
            {
                if (CurrentTenantId is null || CurrentTenantId == Guid.Empty)
                    throw new InvalidOperationException("A validated tenant context is required to persist tenant-owned data.");
                if (entry.Entity.TenantId != Guid.Empty && entry.Entity.TenantId != CurrentTenantId)
                    throw new InvalidOperationException("The entity tenant does not match the current tenant context.");
                entry.Entity.TenantId = CurrentTenantId.Value;
                entry.Entity.CreatedAtUtc = now;
                entry.Entity.UpdatedAtUtc = now;
                entry.Entity.Version = Guid.NewGuid();
            }
            else if (entry.State is EntityState.Modified)
            {
                if (CurrentTenantId is null || entry.Entity.TenantId != CurrentTenantId)
                    throw new InvalidOperationException("Tenant context mismatch while updating tenant-owned data.");
                entry.Property(x => x.TenantId).IsModified = false;
                entry.Property(x => x.CreatedAtUtc).IsModified = false;
                entry.Entity.UpdatedAtUtc = now;
                entry.Entity.Version = Guid.NewGuid();
            }
            else if (entry.State is EntityState.Deleted)
            {
                if (CurrentTenantId is null || entry.Entity.TenantId != CurrentTenantId)
                    throw new InvalidOperationException("Tenant context mismatch while deleting tenant-owned data.");
            }
        }
    }
}
