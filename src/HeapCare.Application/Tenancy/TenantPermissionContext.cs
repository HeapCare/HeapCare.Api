namespace HeapCare.Application.Tenancy;

/// <summary>
/// Holds permissions derived from the persisted tenant role for one request.
/// It is never populated from caller-supplied OIDC role or permission claims.
/// </summary>
public interface ITenantPermissionContext
{
    IReadOnlySet<string> Permissions { get; }
    bool HasPermission(string permissionCode);
    void SetPermissions(IEnumerable<string> permissionCodes);
}

public sealed class TenantPermissionContext : ITenantPermissionContext
{
    private IReadOnlySet<string> _permissions = new HashSet<string>(StringComparer.Ordinal);
    private bool _initialized;

    public IReadOnlySet<string> Permissions => _permissions;

    public bool HasPermission(string permissionCode) =>
        !string.IsNullOrWhiteSpace(permissionCode) && _permissions.Contains(permissionCode);

    public void SetPermissions(IEnumerable<string> permissionCodes)
    {
        ArgumentNullException.ThrowIfNull(permissionCodes);
        if (_initialized)
            throw new InvalidOperationException("Effective tenant permissions cannot change during a request.");

        var permissions = permissionCodes
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        _permissions = permissions;
        _initialized = true;
    }
}
