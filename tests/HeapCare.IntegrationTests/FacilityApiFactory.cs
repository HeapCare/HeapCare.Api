using HeapCare.Application.Tenancy;
using HeapCare.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace HeapCare.IntegrationTests;

public sealed class FacilityApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__HeapCareTests")
        ?? Environment.GetEnvironmentVariable("ConnectionStrings__CompileCareTests")
        ?? "Host=localhost;Port=5432;Database=compilecare_test;Username=postgres;Password=postgres";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Authority"] = TestJwtTokenFactory.Issuer,
                ["Authentication:Audience"] = TestJwtTokenFactory.Audience,
                ["Authentication:RequireHttpsMetadata"] = "true"
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<HeapCareDbContext>>();
            services.AddDbContext<HeapCareDbContext>(
                options => options.UseNpgsql(_connectionString));

            // Keep the real JwtBearer handler and issuer-stamping event. Only replace
            // discovery metadata/signing keys for deterministic integration tests.
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    var discoveryConfiguration = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration
                    {
                        Issuer = TestJwtTokenFactory.Issuer
                    };
                    discoveryConfiguration.SigningKeys.Add(TestJwtTokenFactory.ValidationKey);
                    options.ConfigurationManager =
                        new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(
                            discoveryConfiguration);
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        RequireSignedTokens = true,
                        RequireExpirationTime = true,
                        ValidateIssuer = true,
                        ValidIssuer = TestJwtTokenFactory.Issuer,
                        ValidateAudience = true,
                        ValidAudience = TestJwtTokenFactory.Audience,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = TestJwtTokenFactory.ValidationKey,
                        NameClaimType = "sub",
                        RoleClaimType = "role",
                        ClockSkew = TimeSpan.FromSeconds(5)
                    };
                });
        });
    }

    public async Task SeedTenantMembershipAsync(
        Guid tenantId,
        string role,
        bool isActive = true,
        IReadOnlyList<OrganizationScope>? scopes = null,
        string? subject = null)
    {
        using var scope = Services.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(tenantId);

        var db = scope.ServiceProvider.GetRequiredService<HeapCareDbContext>();
        var tenantExists = await db.Tenants.AnyAsync(value => value.Id == tenantId);
        if (!tenantExists)
        {
            db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Test tenant",
                Slug = $"test-{tenantId:N}",
                Status = TenantStatuses.Active
            });
            await db.SaveChangesAsync();
        }

        await EnsureSystemRolesSeededAsync(db, tenantId);

        var membership = new TenantMembership
        {
            TenantId = tenantId,
            Issuer = TestJwtTokenFactory.Issuer,
            Subject = subject ?? TestJwtTokenFactory.Subject,
            Role = role,
            IsActive = isActive
        };
        foreach (var organizationScope in scopes ?? [new OrganizationScope(OrganizationScopeTypes.Tenant)])
        {
            membership.Scopes.Add(new TenantMembershipScope
            {
                TenantId = tenantId,
                MembershipId = membership.Id,
                ScopeType = organizationScope.ScopeType,
                HospitalId = organizationScope.HospitalId,
                DepartmentId = organizationScope.DepartmentId
            });
        }
        db.TenantMemberships.Add(membership);

        await db.SaveChangesAsync();
    }

    public async Task SeedSystemRolesAsync(Guid tenantId)
    {
        using var scope = Services.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<HeapCareDbContext>();
        if (!await db.Tenants.AnyAsync(value => value.Id == tenantId))
            throw new InvalidOperationException("The test tenant must exist before system roles are seeded.");

        await EnsureSystemRolesSeededAsync(db, tenantId);
    }

    private static async Task EnsureSystemRolesSeededAsync(HeapCareDbContext db, Guid tenantId)
    {
        var systemRoles = await db.TenantRoles
            .Where(value => value.TenantId == tenantId && value.IsSystem)
            .ToListAsync();

        foreach (var definition in TenantPermissionCatalog.SystemRoles)
        {
            var existingRole = systemRoles.SingleOrDefault(value => value.Code == definition.Code);
            if (existingRole is null)
            {
                var systemRole = new TenantRole
                {
                    TenantId = tenantId,
                    Code = definition.Code,
                    Name = definition.DisplayName,
                    Description = definition.Description,
                    IsSystem = true,
                    IsActive = true,
                    PermissionsRevision = 0
                };
                db.TenantRoles.Add(systemRole);
                foreach (var permissionCode in definition.PermissionCodes)
                {
                    db.TenantRolePermissions.Add(new TenantRolePermission
                    {
                        TenantId = tenantId,
                        RoleId = systemRole.Id,
                        PermissionCode = permissionCode
                    });
                }
                continue;
            }

            if (!existingRole.IsActive)
                throw new InvalidOperationException($"Test system role '{definition.Code}' must be active.");

            var existingGrants = await db.TenantRolePermissions
                .Where(value => value.TenantId == tenantId && value.RoleId == existingRole.Id)
                .Select(value => value.PermissionCode)
                .ToArrayAsync();
            if (!existingGrants.ToHashSet(StringComparer.Ordinal).SetEquals(definition.PermissionCodes))
                throw new InvalidOperationException($"Test system role '{definition.Code}' grants do not match the canonical catalog.");
        }

        if (systemRoles.Any(value =>
            !TenantPermissionCatalog.SystemRoles.Any(definition => definition.Code == value.Code)))
            throw new InvalidOperationException("The test tenant contains an unknown system role.");

        await db.SaveChangesAsync();
    }

    public async Task SeedPlatformOperatorAsync(bool isActive = true)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HeapCareDbContext>();
        db.PlatformOperators.Add(new PlatformOperator
        {
            Issuer = TestJwtTokenFactory.Issuer,
            Subject = TestJwtTokenFactory.Subject,
            IsActive = isActive
        });
        await db.SaveChangesAsync();
    }

    public void ResetDatabase()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HeapCareDbContext>();
        var databaseName = db.Database.GetDbConnection().Database;

        if (!databaseName.EndsWith("_test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to reset database '{databaseName}'. Integration tests require a dedicated database ending in '_test'.");
        }

        db.Database.EnsureDeleted();
        db.Database.Migrate();
    }
}
