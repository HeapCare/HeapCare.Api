using HeapCare.Api.Security;
using HeapCare.Application.Tenancy;

namespace HeapCare.Api;

public static class TenantRoleEndpoints
{
    public static IEndpointRouteBuilder MapTenantRoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenant/roles")
            .WithTags("Tenant roles")
            .WithMetadata(TenantScopedEndpointMetadata.Instance)
            .RequireAuthorization()
            .RequireAuthorization(AuthorizationPolicies.TenantMembershipManage);

        group.MapGet("/", async (
                ITenantRoleAdministrationService service,
                CancellationToken cancellationToken) =>
                Results.Ok(await service.ListAsync(cancellationToken)))
            .WithName("ListTenantRoles");

        group.MapGet("/permissions", async (
                ITenantRoleAdministrationService service,
                CancellationToken cancellationToken) =>
                Results.Ok(await service.ListAssignablePermissionsAsync(cancellationToken)))
            .WithName("ListAssignableTenantRolePermissions");

        group.MapPost("/", async (
                HttpContext context,
                TenantRoleCreateRequest request,
                ITenantRoleAdministrationService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.CreateAsync(
                    new TenantRoleCreateInput(
                        request.Name,
                        request.Description,
                        request.PermissionCodes,
                        request.Reason),
                    context.User.FindFirst(AuthorizationPolicies.ValidatedIssuerClaim)?.Value ?? string.Empty,
                    context.User.FindFirst("sub")?.Value ?? string.Empty,
                    cancellationToken);

                if (!result.IsSuccess || result.Value is null)
                    return TenantRoleHttp.From(result);

                return Results.Created(
                    $"/api/v1/tenant/roles/{result.Value.Id:D}",
                    result.Value);
            })
            .WithName("CreateTenantRole");

        group.MapPut("/{roleId:guid}", async (
                Guid roleId,
                HttpContext context,
                TenantRoleUpdateRequest request,
                ITenantRoleAdministrationService service,
                CancellationToken cancellationToken) =>
                TenantRoleHttp.From(await service.UpdateAsync(
                    roleId,
                    new TenantRoleUpdateInput(
                        request.Name,
                        request.Description,
                        request.PermissionCodes,
                        request.Reason),
                    context.User.FindFirst(AuthorizationPolicies.ValidatedIssuerClaim)?.Value ?? string.Empty,
                    context.User.FindFirst("sub")?.Value ?? string.Empty,
                    cancellationToken)))
            .WithName("UpdateTenantRole");

        group.MapPost("/{roleId:guid}/deactivate", async (
                Guid roleId,
                HttpContext context,
                TenantRoleLifecycleRequest request,
                ITenantRoleAdministrationService service,
                CancellationToken cancellationToken) =>
                TenantRoleHttp.From(await service.SetActiveAsync(
                    roleId,
                    false,
                    request.Reason,
                    context.User.FindFirst(AuthorizationPolicies.ValidatedIssuerClaim)?.Value ?? string.Empty,
                    context.User.FindFirst("sub")?.Value ?? string.Empty,
                    cancellationToken)))
            .WithName("DeactivateTenantRole");

        group.MapPost("/{roleId:guid}/reactivate", async (
                Guid roleId,
                HttpContext context,
                TenantRoleLifecycleRequest request,
                ITenantRoleAdministrationService service,
                CancellationToken cancellationToken) =>
                TenantRoleHttp.From(await service.SetActiveAsync(
                    roleId,
                    true,
                    request.Reason,
                    context.User.FindFirst(AuthorizationPolicies.ValidatedIssuerClaim)?.Value ?? string.Empty,
                    context.User.FindFirst("sub")?.Value ?? string.Empty,
                    cancellationToken)))
            .WithName("ReactivateTenantRole");

        return endpoints;
    }
}

public sealed record TenantRoleCreateRequest(
    string? Name,
    string? Description,
    IReadOnlyList<string>? PermissionCodes,
    string? Reason);

public sealed record TenantRoleUpdateRequest(
    string? Name,
    string? Description,
    IReadOnlyList<string>? PermissionCodes,
    string? Reason);

public sealed record TenantRoleLifecycleRequest(string? Reason);

internal static class TenantRoleHttp
{
    public static IResult From(TenantRoleAdministrationResult<TenantRoleDto> result)
    {
        if (result.IsSuccess && result.Value is not null)
            return Results.Ok(result.Value);

        var title = result.StatusCode switch
        {
            StatusCodes.Status400BadRequest => "Validation failed",
            StatusCodes.Status403Forbidden => "Access denied",
            StatusCodes.Status404NotFound => "Tenant role not found",
            StatusCodes.Status409Conflict => "Tenant role conflict",
            _ => "The tenant role request could not be completed"
        };

        return Results.Problem(
            statusCode: result.StatusCode,
            title: title,
            detail: result.Error);
    }
}
