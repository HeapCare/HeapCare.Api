namespace HeapCare.Application.Tenancy;

public sealed record TenantRoleDto(
    Guid Id,
    string Code,
    string Name,
    string Description,
    bool IsSystem,
    bool IsActive,
    IReadOnlyList<string> PermissionCodes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record TenantRoleCreateInput(
    string? Name,
    string? Description,
    IReadOnlyList<string>? PermissionCodes,
    string? Reason);

public sealed record TenantRoleUpdateInput(
    string? Name,
    string? Description,
    IReadOnlyList<string>? PermissionCodes,
    string? Reason);

public sealed record TenantRoleAdministrationResult<T>(T? Value, string? Error, int StatusCode)
{
    public bool IsSuccess => Error is null;
    public static TenantRoleAdministrationResult<T> Success(T value) => new(value, null, 200);
    public static TenantRoleAdministrationResult<T> Created(T value) => new(value, null, 201);
    public static TenantRoleAdministrationResult<T> Failure(string error, int statusCode) =>
        new(default, error, statusCode);
}

public interface ITenantRoleAdministrationService
{
    Task<IReadOnlyList<TenantPermissionDefinition>> ListAssignablePermissionsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantRoleDto>> ListAsync(CancellationToken cancellationToken);
    Task<TenantRoleAdministrationResult<TenantRoleDto>> CreateAsync(
        TenantRoleCreateInput input,
        string actorIssuer,
        string actorSubject,
        CancellationToken cancellationToken);
    Task<TenantRoleAdministrationResult<TenantRoleDto>> UpdateAsync(
        Guid roleId,
        TenantRoleUpdateInput input,
        string actorIssuer,
        string actorSubject,
        CancellationToken cancellationToken);
    Task<TenantRoleAdministrationResult<TenantRoleDto>> SetActiveAsync(
        Guid roleId,
        bool isActive,
        string? reason,
        string actorIssuer,
        string actorSubject,
        CancellationToken cancellationToken);
}
