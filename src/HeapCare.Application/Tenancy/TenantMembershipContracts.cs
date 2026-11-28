namespace HeapCare.Application.Tenancy;

public static class TenantRoles
{
    public const string HospitalAdmin = "HospitalAdmin";
    public const string FacilityManager = "FacilityManager";
    public const string FacilityViewer = "FacilityViewer";
    public const string PatientRegistrar = "PatientRegistrar";
    public const string ClinicalUser = "ClinicalUser";
    public static bool IsKnown(string? role) => role is HospitalAdmin or FacilityManager or FacilityViewer or PatientRegistrar or ClinicalUser;
}

public static class TenantPermissions
{
    public const string FacilityRead = "facility.read";
    public const string FacilityWrite = "facility.write";
    public const string FacilityDelete = "facility.delete";
    public const string MembershipManage = "membership.manage";
    public const string MembershipRoleAssignSystem = "membership.role.assign.system";
}

public static class OrganizationScopeTypes
{
    public const string Tenant = "Tenant";
    public const string Hospital = "Hospital";
    public const string Department = "Department";
    public static bool IsKnown(string? scopeType) => scopeType is Tenant or Hospital or Department;
}

public sealed record OrganizationScope(string ScopeType, Guid? HospitalId = null, Guid? DepartmentId = null);
public sealed record TenantMembershipAuthorization(Guid MembershipId, string Role, IReadOnlyList<OrganizationScope> Scopes, IReadOnlyList<string>? Permissions = null);

public sealed record UserTenantContextDto(
    Guid TenantId,
    string TenantName,
    string TenantSlug,
    string Role,
    IReadOnlyList<OrganizationScope> Scopes,
    IReadOnlyList<string>? Permissions = null);

public interface ITenantMembershipReader
{
    Task<TenantMembershipAuthorization?> FindActiveMembershipAsync(Guid tenantId, string issuer, string subject, CancellationToken cancellationToken);
    Task<TenantMembershipPage<UserTenantContextDto>> ListActiveTenantContextsAsync(
        string issuer, string subject, TenantMembershipPageRequest page, CancellationToken cancellationToken);
}

public interface IOrganizationScopeContext
{
    Guid? MembershipId { get; }
    string? Role { get; }
    IReadOnlyList<OrganizationScope> Scopes { get; }
    IReadOnlyCollection<Guid> HospitalIds { get; }
    IReadOnlyCollection<Guid> DepartmentIds { get; }
    bool IsTenantWide { get; }
    void SetMembership(Guid membershipId, string role, IReadOnlyList<OrganizationScope> scopes);
    bool CanAccessHospital(Guid hospitalId);
    bool CanManageHospital(Guid hospitalId);
    bool CanAccessDepartment(Guid hospitalId, Guid departmentId);
    bool CanManageDepartment(Guid hospitalId, Guid departmentId);
}

public sealed class OrganizationScopeContext : IOrganizationScopeContext
{
    private IReadOnlyList<OrganizationScope> _scopes = Array.Empty<OrganizationScope>();
    public Guid? MembershipId { get; private set; }
    public string? Role { get; private set; }
    public IReadOnlyList<OrganizationScope> Scopes => _scopes;
    public IReadOnlyCollection<Guid> HospitalIds => _scopes
        .Where(scope => scope.ScopeType == OrganizationScopeTypes.Hospital)
        .Select(scope => scope.HospitalId!.Value).Distinct().ToArray();
    public IReadOnlyCollection<Guid> DepartmentIds => _scopes
        .Where(scope => scope.ScopeType == OrganizationScopeTypes.Department)
        .Select(scope => scope.DepartmentId!.Value).Distinct().ToArray();
    public bool IsTenantWide => _scopes.Any(scope => scope.ScopeType == OrganizationScopeTypes.Tenant);

    public void SetMembership(Guid membershipId, string role, IReadOnlyList<OrganizationScope> scopes)
    {
        if (membershipId == Guid.Empty) throw new ArgumentException("Membership ID cannot be empty.", nameof(membershipId));
        if (string.IsNullOrWhiteSpace(role)) throw new ArgumentException("A membership role is required.", nameof(role));
        if (scopes.Count == 0) throw new ArgumentException("At least one organization scope is required.", nameof(scopes));
        if (MembershipId.HasValue) throw new InvalidOperationException("Organization membership context cannot change during a request.");
        MembershipId = membershipId;
        Role = role;
        _scopes = scopes.ToArray();
    }

    public bool CanAccessHospital(Guid hospitalId) =>
        IsTenantWide || HospitalIds.Contains(hospitalId) ||
        _scopes.Any(scope => scope.ScopeType == OrganizationScopeTypes.Department && scope.HospitalId == hospitalId);

    public bool CanManageHospital(Guid hospitalId) => IsTenantWide || HospitalIds.Contains(hospitalId);

    public bool CanAccessDepartment(Guid hospitalId, Guid departmentId) =>
        IsTenantWide || HospitalIds.Contains(hospitalId) ||
        _scopes.Any(scope => scope.ScopeType == OrganizationScopeTypes.Department &&
                             scope.HospitalId == hospitalId && scope.DepartmentId == departmentId);

    public bool CanManageDepartment(Guid hospitalId, Guid departmentId) =>
        CanAccessDepartment(hospitalId, departmentId);
}

public sealed record TenantMembershipScopeInput(string? ScopeType, Guid? HospitalId = null, Guid? DepartmentId = null);
public sealed record TenantMembershipInput(string? Issuer, string? Subject, string? Role, IReadOnlyList<TenantMembershipScopeInput>? Scopes);
public sealed record TenantMembershipUpdateInput(string? Role, IReadOnlyList<TenantMembershipScopeInput>? Scopes);

public sealed record TenantMembershipDto(
    Guid Id,
    string Issuer,
    string Subject,
    string Role,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<OrganizationScope> Scopes);

public sealed record TenantMembershipPageRequest(int Page = 1, int PageSize = 50)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
    public int SafePage => Page < 1 ? 1 : Math.Min(Page, int.MaxValue / MaxPageSize);
    public int SafePageSize => PageSize < 1 ? DefaultPageSize : Math.Min(PageSize, MaxPageSize);
    public int Skip => (SafePage - 1) * SafePageSize;
}

public sealed record TenantMembershipPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling((double)TotalCount / PageSize);
}

public sealed record TenantMembershipResult<T>(T? Value, string? Error, int StatusCode)
{
    public bool IsSuccess => Error is null;
    public static TenantMembershipResult<T> Success(T value) => new(value, null, 200);
    public static TenantMembershipResult<T> Failure(string error, int statusCode) => new(default, error, statusCode);
}

public interface ITenantMembershipAdministrationService
{
    Task<TenantMembershipPage<TenantMembershipDto>> ListAsync(TenantMembershipPageRequest page, CancellationToken cancellationToken);
    Task<TenantMembershipResult<TenantMembershipDto>> CreateInitialAdministratorAsync(Guid tenantId, string? issuer, string? subject, string configuredIssuer, CancellationToken cancellationToken);
    Task<TenantMembershipResult<TenantMembershipDto>> CreateAsync(TenantMembershipInput input, string configuredIssuer, CancellationToken cancellationToken);
    Task<TenantMembershipResult<TenantMembershipDto>> UpdateAsync(Guid membershipId, TenantMembershipUpdateInput input, CancellationToken cancellationToken);
    Task<TenantMembershipResult<TenantMembershipDto>> SetActiveAsync(Guid membershipId, bool isActive, CancellationToken cancellationToken);
}
