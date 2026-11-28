using HeapCare.Application.Appointments;
using HeapCare.Application.Auditing;
using HeapCare.Application.Billing;
using HeapCare.Application.Clinical;
using HeapCare.Application.Inpatient;
using HeapCare.Application.Patients;
using HeapCare.Application.Staff;

namespace HeapCare.Application.Tenancy;

/// <summary>
/// Classifies a permission by the operational risk of granting it.
/// This metadata does not replace resource-scope or clinical-privilege checks.
/// </summary>
public enum TenantPermissionRisk
{
    Standard,
    Sensitive,
    Critical
}

/// <summary>
/// Describes one permission known to the HeapCare API.
/// Permission codes are stable identifiers; display metadata may evolve independently.
/// </summary>
public sealed record TenantPermissionDefinition(
    string Code,
    string Module,
    string Resource,
    string Action,
    string DisplayName,
    string Description,
    TenantPermissionRisk Risk,
    bool IsAssignable = true,
    bool IsSystemOnly = false);

/// <summary>
/// Describes a system-managed baseline role retained for compatibility with existing memberships.
/// These definitions are the migration baseline for the future persisted role-permission model.
/// </summary>
public sealed record TenantSystemRoleDefinition(
    string Code,
    string DisplayName,
    string Description,
    IReadOnlyList<string> PermissionCodes);

/// <summary>
/// Canonical permission metadata and the exact baseline grants for the five currently supported
/// tenant roles. This is deliberately not a user-editable role store: tenant-custom roles and
/// persisted role-permission administration are introduced in a later RBAC increment.
/// </summary>
public static class TenantPermissionCatalog
{
    private static readonly IReadOnlyList<TenantPermissionDefinition> PermissionDefinitions =
        Array.AsReadOnly(new[]
        {
            new TenantPermissionDefinition(
                TenantPermissions.FacilityRead, "Facilities", "facility", "read",
                "View facility structure", "Read hospitals, departments, wards, rooms and beds.",
                TenantPermissionRisk.Standard),
            new TenantPermissionDefinition(
                TenantPermissions.FacilityWrite, "Facilities", "facility", "write",
                "Create and update facilities", "Create and update the facility hierarchy; deletion is a separate permission.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                TenantPermissions.FacilityDelete, "Facilities", "facility", "delete",
                "Delete facilities", "Delete eligible facility records subject to relationship and lifecycle rules.",
                TenantPermissionRisk.Critical),
            new TenantPermissionDefinition(
                TenantPermissions.MembershipManage, "Administration", "membership", "manage",
                "Manage tenant memberships", "Create, update, deactivate and reactivate user memberships and their organization scopes.",
                TenantPermissionRisk.Critical),
            new TenantPermissionDefinition(
                AuditPermissions.Read, "Administration", "audit", "read",
                "View audit events", "Query and export the audit events visible to the current tenant-wide scope.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                TenantPermissions.MembershipRoleAssignSystem, "Administration", "membership.role", "assign-system",
                "Assign system roles", "Assign one of the five platform-approved system roles to an existing tenant member. This capability cannot be delegated through a custom role.",
                TenantPermissionRisk.Critical, IsAssignable: false, IsSystemOnly: true),

            new TenantPermissionDefinition(
                PatientPermissions.Read, "Patients", "patient", "read",
                "Search and view patient records", "Search and read patient records within the assigned organization scope.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                PatientPermissions.IdentityRead, "Patients", "patient.identity", "read",
                "View patient identity", "View the minimum patient identity details required by an authorized workflow.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                PatientPermissions.Register, "Patients", "patient", "register",
                "Register patients", "Register a patient and create a hospital-specific registration.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                PatientDuplicatePermissions.Review, "Patients", "patient.duplicate", "review",
                "Review possible duplicates", "Review duplicate-candidate matches before completing an eligible registration workflow.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                PatientIdentifierPermissions.Read, "Patient identifiers", "patient.identifier", "read",
                "View patient identifiers", "Read patient identifiers that are visible in the assigned organization scope.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                PatientIdentifierPermissions.Assign, "Patient identifiers", "patient.identifier", "assign",
                "Assign patient identifiers", "Assign an identifier under the hospital's active identifier policy.",
                TenantPermissionRisk.Critical),
            new TenantPermissionDefinition(
                PatientIdentifierPermissions.PolicyRead, "Patient identifiers", "patient.identifier.policy", "read",
                "View identifier policy", "Read hospital patient-identifier policy configuration.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                PatientIdentifierPermissions.PolicyManage, "Patient identifiers", "patient.identifier.policy", "manage",
                "Manage identifier policy", "Change hospital patient-identifier policy configuration.",
                TenantPermissionRisk.Critical),

            new TenantPermissionDefinition(
                StaffPermissions.Read, "Staff", "staff", "read",
                "View staff", "Read staff profiles and assignments within the assigned organization scope.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                StaffPermissions.Manage, "Staff", "staff", "manage",
                "Manage staff", "Create, update, deactivate and reactivate staff profiles and assignments.",
                TenantPermissionRisk.Critical),

            new TenantPermissionDefinition(
                AppointmentPermissions.AvailabilityRead, "Appointments", "availability", "read",
                "View provider availability", "Read provider availability and bookable slots.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                AppointmentPermissions.AvailabilityManage, "Appointments", "availability", "manage",
                "Manage provider availability", "Publish and update provider availability sessions.",
                TenantPermissionRisk.Critical),
            new TenantPermissionDefinition(
                AppointmentPermissions.AppointmentRead, "Appointments", "appointment", "read",
                "View appointments", "Read appointments visible within the assigned organization scope.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                AppointmentPermissions.AppointmentManage, "Appointments", "appointment", "manage",
                "Manage appointments", "Book, reschedule, cancel and check in appointments where supported by the workflow.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                QueuePermissions.Read, "OPD queue", "queue", "read",
                "View OPD queue", "Read queue entries visible within the assigned organization scope.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                QueuePermissions.Manage, "OPD queue", "queue", "manage",
                "Manage OPD queue", "Perform supported queue transitions and walk-in operations.",
                TenantPermissionRisk.Sensitive),

            new TenantPermissionDefinition(
                OpdEncounterPermissions.Read, "Clinical", "encounter", "read",
                "Read OPD encounters", "Read clinical encounters visible within the assigned organization scope.",
                TenantPermissionRisk.Critical),
            new TenantPermissionDefinition(
                OpdEncounterPermissions.Manage, "Clinical", "encounter", "manage",
                "Manage OPD encounters", "Create and update encounter drafts subject to clinical lifecycle rules.",
                TenantPermissionRisk.Critical),
            new TenantPermissionDefinition(
                OpdEncounterPermissions.Sign, "Clinical", "encounter", "sign",
                "Sign clinical encounters", "Sign an encounter using the authenticated and authorized clinical actor.",
                TenantPermissionRisk.Critical),
            new TenantPermissionDefinition(
                OpdEncounterPermissions.Amend, "Clinical", "encounter", "amend",
                "Amend signed encounters", "Append an attributed amendment under the signed-record amendment workflow.",
                TenantPermissionRisk.Critical),

            new TenantPermissionDefinition(
                BillingPermissions.CatalogueManage, "Billing", "billing.catalogue", "manage",
                "Manage billing catalogue", "Create and update billing service catalogue items.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                BillingPermissions.InvoiceRead, "Billing", "invoice", "read",
                "View invoices", "Read invoices visible within the assigned organization scope.",
                TenantPermissionRisk.Sensitive),
            new TenantPermissionDefinition(
                BillingPermissions.InvoiceManage, "Billing", "invoice", "manage",
                "Create invoices", "Create invoices under supported billing workflows.",
                TenantPermissionRisk.Critical),
            new TenantPermissionDefinition(
                BillingPermissions.PaymentManage, "Billing", "payment", "manage",
                "Record payments", "Record invoice payments under the supported settlement lifecycle.",
                TenantPermissionRisk.Critical),
            new TenantPermissionDefinition(
                BillingPermissions.ReportRead, "Billing", "billing.report", "read",
                "View billing reports", "Read billing collection reports within the authorized scope.",
                TenantPermissionRisk.Sensitive),

            // IPD identifiers are registered so every policy has canonical metadata, but they
            // remain non-assignable until the separate IPD role/workflow approval is complete.
            new TenantPermissionDefinition(
                InpatientPermissions.Read, "IPD", "inpatient.admission", "read",
                "View inpatient admissions", "Read inpatient admissions and placement history within the assigned hospital and department scope.",
                TenantPermissionRisk.Critical, IsAssignable: false),
            new TenantPermissionDefinition(
                InpatientPermissions.Admit, "IPD", "inpatient.admission", "admit",
                "Admit patients", "Create an inpatient admission using an existing registration at the same hospital.",
                TenantPermissionRisk.Critical, IsAssignable: false),
            new TenantPermissionDefinition(
                InpatientPermissions.BedAssign, "IPD", "inpatient.bed", "assign",
                "Assign inpatient beds", "Assign an eligible bed to an active inpatient admission.",
                TenantPermissionRisk.Critical, IsAssignable: false),
            new TenantPermissionDefinition(
                InpatientPermissions.BedTransfer, "IPD", "inpatient.bed", "transfer",
                "Transfer inpatient beds", "Transfer an active placement to another eligible bed in the same hospital.",
                TenantPermissionRisk.Critical, IsAssignable: false),
            new TenantPermissionDefinition(
                InpatientPermissions.CancelAdmission, "IPD", "inpatient.admission", "cancel",
                "Cancel inpatient admissions", "Cancel an eligible inpatient admission with validated attribution and reason.",
                TenantPermissionRisk.Critical, IsAssignable: false),
            new TenantPermissionDefinition(
                InpatientPermissions.Discharge, "IPD", "inpatient.admission", "discharge",
                "Discharge inpatients", "Discharge an eligible inpatient admission with validated attribution.",
                TenantPermissionRisk.Critical, IsAssignable: false)
        });

    private static readonly IReadOnlyList<TenantSystemRoleDefinition> SystemRoleDefinitions =
        Array.AsReadOnly(new[]
        {
            new TenantSystemRoleDefinition(
                TenantRoles.HospitalAdmin,
                "Hospital Administrator",
                "Current broad tenant/hospital administration role. Effective access remains constrained by membership organization scopes.",
                PermissionCodes(
                    TenantPermissions.FacilityRead,
                    TenantPermissions.FacilityWrite,
                    TenantPermissions.FacilityDelete,
                    TenantPermissions.MembershipManage,
                    TenantPermissions.MembershipRoleAssignSystem,
                    AuditPermissions.Read,
                    PatientPermissions.Read,
                    PatientPermissions.IdentityRead,
                    PatientPermissions.Register,
                    PatientDuplicatePermissions.Review,
                    PatientIdentifierPermissions.Read,
                    PatientIdentifierPermissions.Assign,
                    PatientIdentifierPermissions.PolicyRead,
                    PatientIdentifierPermissions.PolicyManage,
                    StaffPermissions.Read,
                    StaffPermissions.Manage,
                    AppointmentPermissions.AvailabilityRead,
                    AppointmentPermissions.AvailabilityManage,
                    AppointmentPermissions.AppointmentRead,
                    AppointmentPermissions.AppointmentManage,
                    QueuePermissions.Read,
                    QueuePermissions.Manage,
                    BillingPermissions.CatalogueManage,
                    BillingPermissions.InvoiceRead,
                    BillingPermissions.InvoiceManage,
                    BillingPermissions.PaymentManage,
                    BillingPermissions.ReportRead)),
            new TenantSystemRoleDefinition(
                TenantRoles.FacilityManager,
                "Facility Manager",
                "Can read and create/update facility structure; cannot delete facilities or administer memberships.",
                PermissionCodes(TenantPermissions.FacilityRead, TenantPermissions.FacilityWrite)),
            new TenantSystemRoleDefinition(
                TenantRoles.FacilityViewer,
                "Facility Viewer",
                "Read-only access to facility structure.",
                PermissionCodes(TenantPermissions.FacilityRead)),
            new TenantSystemRoleDefinition(
                TenantRoles.PatientRegistrar,
                "Patient Registrar",
                "Current reception/registration baseline, including appointment, queue and cashier operations already present in the API.",
                PermissionCodes(
                    TenantPermissions.FacilityRead,
                    PatientPermissions.Read,
                    PatientPermissions.IdentityRead,
                    PatientPermissions.Register,
                    PatientDuplicatePermissions.Review,
                    PatientIdentifierPermissions.Read,
                    PatientIdentifierPermissions.Assign,
                    AppointmentPermissions.AvailabilityRead,
                    AppointmentPermissions.AppointmentRead,
                    AppointmentPermissions.AppointmentManage,
                    QueuePermissions.Read,
                    QueuePermissions.Manage,
                    BillingPermissions.InvoiceRead,
                    BillingPermissions.InvoiceManage,
                    BillingPermissions.PaymentManage,
                    BillingPermissions.ReportRead)),
            new TenantSystemRoleDefinition(
                TenantRoles.ClinicalUser,
                "Clinical User",
                "Current OPD clinical baseline. Professional registration, specialty, department assignment and clinical privileges remain separate controls.",
                PermissionCodes(
                    PatientPermissions.IdentityRead,
                    TenantPermissions.FacilityRead,
                    QueuePermissions.Read,
                    OpdEncounterPermissions.Read,
                    OpdEncounterPermissions.Manage,
                    OpdEncounterPermissions.Sign,
                    OpdEncounterPermissions.Amend))
        });

    private static readonly IReadOnlyDictionary<string, TenantPermissionDefinition> PermissionLookup =
        PermissionDefinitions.ToDictionary(permission => permission.Code, StringComparer.Ordinal);

    public static IReadOnlyList<TenantPermissionDefinition> Definitions => PermissionDefinitions;

    public static IReadOnlyList<TenantSystemRoleDefinition> SystemRoles => SystemRoleDefinitions;

    public static IReadOnlyList<string> GetDefaultPermissions(string? roleCode)
    {
        if (string.IsNullOrWhiteSpace(roleCode))
            return Array.Empty<string>();

        return SystemRoleDefinitions
            .FirstOrDefault(role => string.Equals(role.Code, roleCode, StringComparison.Ordinal))
            ?.PermissionCodes ?? Array.Empty<string>();
    }

    public static TenantPermissionDefinition? Find(string? permissionCode)
    {
        if (string.IsNullOrWhiteSpace(permissionCode))
            return null;

        return PermissionLookup.GetValueOrDefault(permissionCode);
    }

    public static bool IsAssignable(string? permissionCode) =>
        Find(permissionCode) is { IsAssignable: true };

    private static IReadOnlyList<string> PermissionCodes(params string[] values) =>
        Array.AsReadOnly(values);
}
