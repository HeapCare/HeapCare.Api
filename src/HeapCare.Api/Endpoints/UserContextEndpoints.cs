using HeapCare.Api.Security;
using HeapCare.Application.Tenancy;

namespace HeapCare.Api;

public static class UserContextEndpoints
{
    public static IEndpointRouteBuilder MapUserContextEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/me")
            .WithTags("User context")
            .WithMetadata(UserScopedEndpointMetadata.Instance)
            .RequireAuthorization();

        group.MapGet("/tenant-contexts", async (
                HttpContext context,
                ITenantMembershipReader membershipReader,
                int page = 1,
                int pageSize = TenantMembershipPageRequest.DefaultPageSize,
                CancellationToken cancellationToken = default) =>
            {
                var issuer = context.User
                    .FindFirst(AuthorizationPolicies.ValidatedIssuerClaim)?.Value;
                var subject = context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
                {
                    return Results.Problem(
                        title: "Identity is not provisioned",
                        detail: "The authenticated identity does not contain the validated issuer and subject required to resolve tenant memberships.",
                        statusCode: StatusCodes.Status403Forbidden);
                }

                var result = await membershipReader.ListActiveTenantContextsAsync(
                    issuer,
                    subject,
                    new TenantMembershipPageRequest(page, pageSize),
                    cancellationToken);

                var items = result.Items.Select(item => new CurrentUserTenantContextDto(
                    item.TenantId,
                    item.TenantName,
                    item.TenantSlug,
                    item.Role,
                    item.Scopes,
                    item.Permissions ?? Array.Empty<string>())).ToArray();

                return Results.Ok(new TenantMembershipPage<CurrentUserTenantContextDto>(
                    items, result.Page, result.PageSize, result.TotalCount));
            })
            .WithName("ListCurrentUserTenantContexts");

        return endpoints;
    }
}

public sealed record CurrentUserTenantContextDto(
    Guid TenantId,
    string TenantName,
    string TenantSlug,
    string Role,
    IReadOnlyList<OrganizationScope> Scopes,
    IReadOnlyList<string> Permissions);
