using HeapCare.Application.Tenancy;

namespace HeapCare.Api.Security;

internal static class TenantRolePermissions
{
    public static IReadOnlyList<string> GetPermissions(string role) =>
        TenantPermissionCatalog.GetDefaultPermissions(role);
}
