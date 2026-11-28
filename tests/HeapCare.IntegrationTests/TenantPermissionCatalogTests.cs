using HeapCare.Application.Appointments;
using HeapCare.Application.Auditing;
using HeapCare.Application.Billing;
using HeapCare.Application.Clinical;
using HeapCare.Application.Inpatient;
using HeapCare.Application.Patients;
using HeapCare.Application.Staff;
using HeapCare.Application.Tenancy;
using Xunit;

namespace HeapCare.IntegrationTests;

public sealed class TenantPermissionCatalogTests
{
    [Fact]
    public void PermissionCatalog_ContainsUniqueStableCodesAndDescriptiveMetadata()
    {
        var definitions = TenantPermissionCatalog.Definitions;

        Assert.NotEmpty(definitions);
        Assert.Equal(
            definitions.Count,
            definitions.Select(permission => permission.Code).Distinct(StringComparer.Ordinal).Count());

        Assert.All(definitions, permission =>
        {
            Assert.False(string.IsNullOrWhiteSpace(permission.Code));
            Assert.False(string.IsNullOrWhiteSpace(permission.Module));
            Assert.False(string.IsNullOrWhiteSpace(permission.Resource));
            Assert.False(string.IsNullOrWhiteSpace(permission.Action));
            Assert.False(string.IsNullOrWhiteSpace(permission.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(permission.Description));
        });
    }

    [Fact]
    public void SystemRoleCatalog_PreservesExactlyTheFiveExistingTenantRolesAndGrantSets()
    {
        var expectedRoles = new[]
        {
            TenantRoles.HospitalAdmin,
            TenantRoles.FacilityManager,
            TenantRoles.FacilityViewer,
            TenantRoles.PatientRegistrar,
            TenantRoles.ClinicalUser
        };

        Assert.Equal(
            expectedRoles.OrderBy(role => role, StringComparer.Ordinal),
            TenantPermissionCatalog.SystemRoles
                .Select(role => role.Code)
                .OrderBy(role => role, StringComparer.Ordinal));

        AssertGrants(
            TenantRoles.HospitalAdmin,
            TenantPermissions.FacilityRead,
            TenantPermissions.FacilityWrite,
            TenantPermissions.FacilityDelete,
            TenantPermissions.MembershipManage,
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
            BillingPermissions.ReportRead);
        AssertGrants(
            TenantRoles.FacilityManager,
            TenantPermissions.FacilityRead,
            TenantPermissions.FacilityWrite);
        AssertGrants(TenantRoles.FacilityViewer, TenantPermissions.FacilityRead);
        AssertGrants(
            TenantRoles.PatientRegistrar,
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
            BillingPermissions.ReportRead);
        AssertGrants(
            TenantRoles.ClinicalUser,
            PatientPermissions.IdentityRead,
            TenantPermissions.FacilityRead,
            QueuePermissions.Read,
            OpdEncounterPermissions.Read,
            OpdEncounterPermissions.Manage,
            OpdEncounterPermissions.Sign,
            OpdEncounterPermissions.Amend);

        Assert.Empty(TenantPermissionCatalog.GetDefaultPermissions("UnknownRole"));
        Assert.Empty(TenantPermissionCatalog.GetDefaultPermissions(null));
    }

    private static void AssertGrants(string roleCode, params string[] expectedPermissions)
    {
        var actualPermissions = TenantPermissionCatalog.GetDefaultPermissions(roleCode);
        Assert.Equal(
            expectedPermissions.OrderBy(permission => permission, StringComparer.Ordinal),
            actualPermissions.OrderBy(permission => permission, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryBaselineRoleGrant_ResolvesToAnAssignableCatalogEntry()
    {
        foreach (var role in TenantPermissionCatalog.SystemRoles)
        {
            Assert.NotEmpty(role.PermissionCodes);
            Assert.Equal(
                role.PermissionCodes.Count,
                role.PermissionCodes.Distinct(StringComparer.Ordinal).Count());

            foreach (var permissionCode in role.PermissionCodes)
            {
                Assert.NotNull(TenantPermissionCatalog.Find(permissionCode));
                Assert.True(TenantPermissionCatalog.IsAssignable(permissionCode));
            }
        }
    }

    [Fact]
    public void IpdPermissions_AreRegisteredForPolicyMetadataButCannotBeAssignedToRolesYet()
    {
        var ipdPermissions = new[]
        {
            InpatientPermissions.Read,
            InpatientPermissions.Admit,
            InpatientPermissions.BedAssign,
            InpatientPermissions.BedTransfer,
            InpatientPermissions.CancelAdmission,
            InpatientPermissions.Discharge
        };

        foreach (var permissionCode in ipdPermissions)
        {
            Assert.NotNull(TenantPermissionCatalog.Find(permissionCode));
            Assert.False(TenantPermissionCatalog.IsAssignable(permissionCode));
            Assert.DoesNotContain(
                TenantPermissionCatalog.SystemRoles.SelectMany(role => role.PermissionCodes),
                grantedPermission => grantedPermission == permissionCode);
        }
    }
}
