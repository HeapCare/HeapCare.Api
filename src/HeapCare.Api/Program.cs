using System.Net;
using HeapCare.Api;
using HeapCare.Api.Endpoints;
using HeapCare.Api.Middleware;
using HeapCare.Api.Security;
using HeapCare.Application;
using HeapCare.Infrastructure;
using HeapCare.Infrastructure.Persistence;
using HeapCare.Observability.Extensions;
using HeapCare.Observability.AspNetCore.Extensions;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Host.UseHeapCareObservability();
builder.Services.AddObservability(builder.Configuration);
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddNpgsql());
builder.Services.AddObservabilityHealthChecks();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddApplication();
DatabaseConnectionSecurity.Validate(builder.Configuration, builder.Environment);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHeapCareSecurity(builder.Configuration, builder.Environment);
builder.Services.AddHeapCareRateLimiting(builder.Configuration);
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    // Do not opt the entire parent domain into HSTS until every subdomain
    // and the preload commitment have been reviewed for the selected host.
    options.IncludeSubDomains = false;
    options.Preload = false;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.RequireHeaderSymmetry = true;

    // Do not trust arbitrary X-Forwarded-* headers. Operators must explicitly
    // list the IP addresses of their ingress/load-balancer proxies.
    var trustedProxies = builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
    foreach (var proxy in trustedProxies)
    {
        if (!IPAddress.TryParse(proxy, out var address))
        {
            throw new InvalidOperationException(
                $"ReverseProxy:KnownProxies contains an invalid IP address: '{proxy}'.");
        }

        options.KnownProxies.Add(address);
    }
});

var allowedOrigins = CorsOriginConfiguration.GetAllowedOrigins(builder.Configuration, builder.Environment);
var metricsAllowedNetworks = MetricsEndpointNetworkConfiguration.GetAllowedNetworks(builder.Configuration, builder.Environment);
if (allowedOrigins.Length > 0)
{
    builder.Services.AddCors(options =>
        options.AddPolicy("HeapCareWeb", policy =>
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));
}

var app = builder.Build();

if (app.Environment.IsDevelopment() &&
    builder.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<HeapCareDbContext>();
    await database.Database.MigrateAsync();
}

// Forwarded headers must be applied before HSTS, authentication, rate limiting,
// and any code that consumes the remote IP or request scheme.
app.UseForwardedHeaders();
if (metricsAllowedNetworks.Length > 0)
{
    app.UseMiddleware<MetricsEndpointAccessMiddleware>(metricsAllowedNetworks);
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRouting();
if (allowedOrigins.Length > 0) app.UseCors("HeapCareWeb");
app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<AuditEventMiddleware>();
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.MapPrometheusScrapingEndpoint();
app.MapGet("/", () => Results.Ok(new { service = "HeapCare.Api", status = "ready" }));
app.MapHealthEndpoints();
app.MapPlatformEndpoints();
app.MapAuditEndpoints();
app.MapPlatformBootstrapEndpoints(app.Environment);
app.MapPlatformTenantEndpoints();
app.MapTenantMembershipEndpoints();
app.MapTenantRoleEndpoints();
app.MapUserContextEndpoints();
app.MapHospitalEndpoints();
app.MapDepartmentEndpoints();
app.MapWardEndpoints();
app.MapRoomEndpoints();
app.MapBedEndpoints();
app.MapPatientEndpoints();
app.MapStaffEndpoints();
app.MapPatientIdentifierEndpoints();
app.MapAppointmentEndpoints();
app.MapOpdEncounterEndpoints();
app.MapBillingEndpoints();
app.MapInpatientEndpoints();
app.Run();

public partial class Program;
