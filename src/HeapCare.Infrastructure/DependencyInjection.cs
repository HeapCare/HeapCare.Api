using HeapCare.Application.Appointments;
using HeapCare.Application.Auditing;
using HeapCare.Application.Billing;
using HeapCare.Application.Clinical;
using HeapCare.Application.Facilities;
using HeapCare.Application.Inpatient;
using HeapCare.Application.Patients;
using HeapCare.Application.Staff;
using HeapCare.Application.Tenancy;
using HeapCare.Infrastructure.Appointments;
using HeapCare.Infrastructure.Auditing;
using HeapCare.Infrastructure.Billing;
using HeapCare.Infrastructure.Clinical;
using HeapCare.Infrastructure.Facilities;
using HeapCare.Infrastructure.Inpatient;
using HeapCare.Infrastructure.Memberships;
using HeapCare.Infrastructure.Patients;
using HeapCare.Infrastructure.Staff;
using HeapCare.Infrastructure.Persistence;
using HeapCare.Infrastructure.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HeapCare.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("HeapCare") ?? configuration.GetConnectionString("CompileCare");
        services.AddDbContext<HeapCareDbContext>(options =>
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
                options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsAssembly(typeof(HeapCareDbContext).Assembly.FullName));
        });

        if (!string.IsNullOrWhiteSpace(connectionString))
            services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

        services.AddScoped<ITenantMembershipReader, TenantMembershipReader>();
        services.AddScoped<ITenantPermissionContext, TenantPermissionContext>();
        services.AddScoped<ITenantRoleAdministrationService, TenantRoleAdministrationService>();
        services.AddScoped<IPlatformOperatorReader, PlatformOperatorReader>();
        services.AddScoped<IPlatformOperatorBootstrapService, PlatformOperatorBootstrapService>();
        services.AddScoped<ITenantRegistryService, TenantRegistryService>();
        services.AddScoped<ITenantMembershipAdministrationService, TenantMembershipAdministrationService>();
        services.AddScoped<IPatientService, PatientService>();
        services.AddScoped<IPatientDuplicateDetectionService, PatientDuplicateDetectionService>();
        services.AddScoped<IPatientIdentifierService, PatientIdentifierService>();
        services.AddScoped<IStaffManagementService, StaffManagementService>();
        services.AddScoped<IAppointmentSchedulingService, AppointmentSchedulingService>();
        services.AddScoped<IAuditEventQueryService, AuditEventQueryService>();
        services.AddScoped<IAppointmentQueueService, AppointmentQueueService>();
        services.AddScoped<IOpdEncounterService, OpdEncounterService>();
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<IInpatientService, InpatientService>();
        services.AddScoped<FacilityService>();
        services.AddScoped<IHospitalService>(sp => sp.GetRequiredService<FacilityService>());
        services.AddScoped<IDepartmentService>(sp => sp.GetRequiredService<FacilityService>());
        services.AddScoped<IWardService>(sp => sp.GetRequiredService<FacilityService>());
        services.AddScoped<IRoomService>(sp => sp.GetRequiredService<FacilityService>());
        services.AddScoped<IBedService>(sp => sp.GetRequiredService<FacilityService>());
        services.AddScoped<IFacilityService>(sp => sp.GetRequiredService<FacilityService>());
        return services;
    }
}
