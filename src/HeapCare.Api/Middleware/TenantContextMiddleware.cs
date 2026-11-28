using System.Security.Claims;
using HeapCare.Api.Security;
using HeapCare.Application.Tenancy;
using Serilog.Context;

namespace HeapCare.Api.Middleware;

public sealed class TenantContextMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Tenant-ID";

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenantContext,
        IOrganizationScopeContext organizationScopeContext,
        ITenantPermissionContext permissionContext,
        ITenantMembershipReader membershipReader)
    {
        var isTenantScoped = context.GetEndpoint()?.Metadata
            .GetMetadata<TenantScopedEndpointMetadata>() is not null;
        if (!isTenantScoped || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var tenantHeaders = context.Request.Headers[HeaderName];
        if (tenantHeaders.Count != 1 ||
            !Guid.TryParse(tenantHeaders[0], out var tenantId) ||
            tenantId == Guid.Empty)
        {
            await Results.Problem(
                title: "Invalid tenant context",
                detail: $"A single, non-empty GUID must be supplied in the {HeaderName} header.",
                statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context);
            return;
        }

        var issuer = context.User.FindFirst(AuthorizationPolicies.ValidatedIssuerClaim)?.Value;
        var subject = context.User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
        {
            await Results.Problem(
                title: "Identity is not provisioned",
                detail: "The authenticated identity does not contain the required issuer and subject claims.",
                statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
            return;
        }

        var membership = await membershipReader.FindActiveMembershipAsync(
            tenantId, issuer, subject, context.RequestAborted);
        if (membership is null)
        {
            await Results.Problem(
                title: "Tenant access denied",
                detail: "The authenticated user does not have an active membership in the requested tenant.",
                statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
            return;
        }

        if (membership.Scopes.Count == 0)
        {
            await Results.Problem(
                title: "Organization scope is not provisioned",
                detail: "The tenant membership has no active organization scope. Contact the tenant administrator.",
                statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
            return;
        }

        var permissions = membership.Permissions ?? Array.Empty<string>();
        if (permissions.Count == 0)
        {
            await Results.Problem(
                title: "Tenant role is not supported",
                detail: "The user's tenant membership has no permissions assigned.",
                statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
            return;
        }

        var reservedClaimTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            AuthorizationPolicies.TenantIdClaim,
            AuthorizationPolicies.TenantRoleClaim,
            AuthorizationPolicies.PermissionClaim,
            AuthorizationPolicies.ValidatedIssuerClaim,
            "role",
            "roles",
            ClaimTypes.Role
        };
        reservedClaimTypes.UnionWith(AuthorizationPolicies.LegacyInternalClaimTypes);
        foreach (var identity in context.User.Identities)
        {
            foreach (var claim in identity.Claims.Where(claim => reservedClaimTypes.Contains(claim.Type)).ToArray())
                identity.RemoveClaim(claim);
        }

        var membershipClaims = new List<Claim>
        {
            new(AuthorizationPolicies.ValidatedIssuerClaim, issuer),
            new(AuthorizationPolicies.TenantIdClaim, tenantId.ToString("D")),
            new(AuthorizationPolicies.TenantRoleClaim, membership.Role)
        };
        membershipClaims.AddRange(permissions.Select(permission =>
            new Claim(AuthorizationPolicies.PermissionClaim, permission)));

        context.User.AddIdentity(new ClaimsIdentity(
            membershipClaims,
            authenticationType: AuthorizationPolicies.MembershipIdentityAuthenticationType));

        tenantContext.SetTenant(tenantId);
        organizationScopeContext.SetMembership(membership.MembershipId, membership.Role, membership.Scopes);
        permissionContext.SetPermissions(permissions);

        using (LogContext.PushProperty("TenantId", tenantId))
        using (LogContext.PushProperty("OrganizationScopeCount", membership.Scopes.Count))
            await next(context);
    }
}
