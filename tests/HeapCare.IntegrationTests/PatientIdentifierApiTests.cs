using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HeapCare.Api.Middleware;
using HeapCare.Application.Patients;
using HeapCare.Application.Tenancy;
using HeapCare.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HeapCare.IntegrationTests;

[Collection("PostgreSQL integration")]
public sealed class PatientIdentifierApiTests(FacilityApiFactory factory)
{
    [Fact]
    public async Task HospitalAdmin_ConfiguresPolicy_RegistrarAssignsAndLooksUpUniqueIdempotentMrns()
    {
        factory.ResetDatabase();

        var tenantId = Guid.NewGuid();
        const string adminSubject = "mrn-policy-admin";
        const string registrarSubject = "mrn-patient-registrar";
        await SeedMembershipAsync(tenantId, adminSubject, TenantRoles.HospitalAdmin);
        await SeedMembershipAsync(tenantId, registrarSubject, TenantRoles.PatientRegistrar);
        var hospitalId = await SeedHospitalAsync(tenantId, "MRN Test Hospital");

        using var admin = CreateClient(tenantId, adminSubject);
        using var registrar = CreateClient(tenantId, registrarSubject);

        // Reception users need to load the hospital selector before they can
        // perform hospital-scoped patient and appointment workflows.
        var registrarHospitals = await registrar.GetAsync("/api/v1/hospitals?page=1&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, registrarHospitals.StatusCode);

        var policyResponse = await admin.PostAsJsonAsync(
            $"/api/v1/hospitals/{hospitalId:D}/patient-identifier-policy",
            new PatientIdentifierPolicyInput(" akh ", 6));
        Assert.Equal(HttpStatusCode.Created, policyResponse.StatusCode);
        var policy = await policyResponse.Content.ReadFromJsonAsync<PatientIdentifierPolicyDto>();
        Assert.NotNull(policy);
        Assert.Equal("AKH", policy!.Prefix);
        Assert.Equal(6, policy.NumberLength);

        var duplicatePolicyResponse = await admin.PostAsJsonAsync(
            $"/api/v1/hospitals/{hospitalId:D}/patient-identifier-policy",
            new PatientIdentifierPolicyInput("OTHER", 5));
        Assert.Equal(HttpStatusCode.Conflict, duplicatePolicyResponse.StatusCode);

        var firstPatient = await RegisterPatientAsync(registrar, hospitalId, "Asha");
        var firstAssignments = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            registrar.PostAsync(
                $"/api/v1/hospitals/{hospitalId:D}/patients/{firstPatient.Id:D}/identifiers/mrn",
                content: null)));
        var firstIdentifiers = new List<PatientIdentifierDto>();
        foreach (var response in firstAssignments)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var identifier = await response.Content.ReadFromJsonAsync<PatientIdentifierDto>();
            Assert.NotNull(identifier);
            firstIdentifiers.Add(identifier!);
        }

        Assert.Single(firstIdentifiers.Select(x => x.Id).Distinct());
        Assert.Single(firstIdentifiers.Select(x => x.Value).Distinct());
        Assert.Equal("AKH000001", firstIdentifiers[0].Value);

        var secondPatient = await RegisterPatientAsync(registrar, hospitalId, "Rohan");
        var secondAssignment = await registrar.PostAsync(
            $"/api/v1/hospitals/{hospitalId:D}/patients/{secondPatient.Id:D}/identifiers/mrn",
            content: null);
        Assert.Equal(HttpStatusCode.OK, secondAssignment.StatusCode);
        var secondIdentifier = await secondAssignment.Content.ReadFromJsonAsync<PatientIdentifierDto>();
        Assert.NotNull(secondIdentifier);
        Assert.Equal("AKH000002", secondIdentifier!.Value);
        Assert.NotEqual(firstIdentifiers[0].Id, secondIdentifier.Id);

        var lookup = await registrar.GetFromJsonAsync<PatientIdentifierLookupDto>(
            $"/api/v1/hospitals/{hospitalId:D}/patients/by-mrn/{secondIdentifier.Value.ToLowerInvariant()}");
        Assert.NotNull(lookup);
        Assert.Equal(secondPatient.Id, lookup!.Patient.Id);
        Assert.Equal(secondIdentifier.Id, lookup.Identifier.Id);

        var identifiers = await registrar.GetFromJsonAsync<List<PatientIdentifierDto>>(
            $"/api/v1/hospitals/{hospitalId:D}/patients/{firstPatient.Id:D}/identifiers");
        Assert.NotNull(identifiers);
        Assert.Single(identifiers!);
        Assert.Equal(firstIdentifiers[0].Id, identifiers[0].Id);

        var registrarCannotManagePolicy = await registrar.PostAsJsonAsync(
            $"/api/v1/hospitals/{hospitalId:D}/patient-identifier-policy",
            new PatientIdentifierPolicyInput("DENIED", 6));
        Assert.Equal(HttpStatusCode.Forbidden, registrarCannotManagePolicy.StatusCode);

        var adminCanAssignExistingMrn = await admin.PostAsync(
            $"/api/v1/hospitals/{hospitalId:D}/patients/{firstPatient.Id:D}/identifiers/mrn",
            content: null);
        Assert.Equal(HttpStatusCode.OK, adminCanAssignExistingMrn.StatusCode);
        var adminAssignedIdentifier =
            await adminCanAssignExistingMrn.Content.ReadFromJsonAsync<PatientIdentifierDto>();
        Assert.NotNull(adminAssignedIdentifier);
        Assert.Equal(firstIdentifiers[0].Id, adminAssignedIdentifier!.Id);
        Assert.Equal(firstIdentifiers[0].Value, adminAssignedIdentifier.Value);

        // HospitalAdmin must be able to read and manage appointments. Use a
        // non-existent identifier to verify authorization without creating data.
        var missingAppointmentId = Guid.NewGuid();
        var adminCanReadAppointment = await admin.GetAsync(
            $"/api/v1/hospitals/{hospitalId:D}/appointments/{missingAppointmentId:D}");
        Assert.Equal(HttpStatusCode.NotFound, adminCanReadAppointment.StatusCode);

        var adminCanManageAppointment = await admin.PostAsync(
            $"/api/v1/hospitals/{hospitalId:D}/appointments/{missingAppointmentId:D}/cancel",
            content: null);
        Assert.Equal(HttpStatusCode.NotFound, adminCanManageAppointment.StatusCode);
    }

    [Fact]
    public async Task HospitalScopedPolicyAdmin_CannotConfigureAnotherHospital()
    {
        factory.ResetDatabase();

        var tenantId = Guid.NewGuid();
        const string subject = "hospital-scoped-mrn-admin";
        var (authorizedHospitalId, otherHospitalId) = await SeedTenantAndTwoHospitalsAsync(tenantId);
        await SeedMembershipAsync(
            tenantId,
            subject,
            TenantRoles.HospitalAdmin,
            [new OrganizationScope(OrganizationScopeTypes.Hospital, authorizedHospitalId)]);

        using var client = CreateClient(tenantId, subject);
        var denied = await client.PostAsJsonAsync(
            $"/api/v1/hospitals/{otherHospitalId:D}/patient-identifier-policy",
            new PatientIdentifierPolicyInput("OTHER", 6));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        var allowed = await client.PostAsJsonAsync(
            $"/api/v1/hospitals/{authorizedHospitalId:D}/patient-identifier-policy",
            new PatientIdentifierPolicyInput("LOCAL", 5));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task HospitalAdmin_CannotReadOrAssignAnotherTenantsMrn_AndDenialsAreAudited()
    {
        factory.ResetDatabase();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        const string tenantAAdminSubject = "mrn-tenant-a-admin";
        const string tenantARegistrarSubject = "mrn-tenant-a-registrar";
        const string tenantBAdminSubject = "mrn-tenant-b-admin";

        await SeedMembershipAsync(tenantA, tenantAAdminSubject, TenantRoles.HospitalAdmin);
        await SeedMembershipAsync(tenantA, tenantARegistrarSubject, TenantRoles.PatientRegistrar);
        await SeedMembershipAsync(tenantB, tenantBAdminSubject, TenantRoles.HospitalAdmin);

        var hospitalA = await SeedHospitalAsync(tenantA, "Tenant A MRN Hospital");
        var hospitalB = await SeedHospitalAsync(tenantB, "Tenant B MRN Hospital");

        using var adminA = CreateClient(tenantA, tenantAAdminSubject);
        using var registrarA = CreateClient(tenantA, tenantARegistrarSubject);
        using var adminB = CreateClient(tenantB, tenantBAdminSubject);

        var policy = await adminA.PostAsJsonAsync(
            $"/api/v1/hospitals/{hospitalA:D}/patient-identifier-policy",
            new PatientIdentifierPolicyInput("TEN", 6));
        Assert.Equal(HttpStatusCode.Created, policy.StatusCode);

        var patientA = await RegisterPatientAsync(registrarA, hospitalA, "Asha");
        var assignment = await adminA.PostAsync(
            $"/api/v1/hospitals/{hospitalA:D}/patients/{patientA.Id:D}/identifiers/mrn",
            content: null);
        Assert.Equal(HttpStatusCode.OK, assignment.StatusCode);
        var identifierA = await assignment.Content.ReadFromJsonAsync<PatientIdentifierDto>();
        Assert.NotNull(identifierA);
        Assert.Equal("TEN000001", identifierA!.Value);

        // The tenant-B audit watermark excludes any earlier fixture or tenant setup events.
        using var auditBaselineScope = factory.Services.CreateScope();
        auditBaselineScope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantB);
        var auditBaselineDb = auditBaselineScope.ServiceProvider.GetRequiredService<HeapCareDbContext>();
        var existingAuditIds = await auditBaselineDb.AuditEvents.AsNoTracking()
            .Where(audit => audit.ActorSubject == tenantBAdminSubject)
            .Select(audit => audit.Id)
            .ToListAsync();

        var attempts = new[]
        {
            await adminB.GetAsync(
                $"/api/v1/hospitals/{hospitalA:D}/patients/{patientA.Id:D}/identifiers"),
            await adminB.PostAsync(
                $"/api/v1/hospitals/{hospitalA:D}/patients/{patientA.Id:D}/identifiers/mrn",
                content: null),
            await adminB.GetAsync(
                $"/api/v1/hospitals/{hospitalA:D}/patient-identifier-policy"),
            await adminB.GetAsync(
                $"/api/v1/hospitals/{hospitalB:D}/patients/by-mrn/{identifierA.Value}")
        };

        Assert.All(attempts, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        var deniedBodies = await Task.WhenAll(attempts.Select(response => response.Content.ReadAsStringAsync()));
        Assert.All(deniedBodies, body =>
        {
            Assert.DoesNotContain(identifierA.Value, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Asha", body, StringComparison.Ordinal);
        });

        // The rejected cross-tenant assignment must not alter tenant A's patient identifier.
        var tenantAIdentifiers = await adminA.GetFromJsonAsync<List<PatientIdentifierDto>>(
            $"/api/v1/hospitals/{hospitalA:D}/patients/{patientA.Id:D}/identifiers");
        Assert.NotNull(tenantAIdentifiers);
        var retainedIdentifier = Assert.Single(tenantAIdentifiers!);
        Assert.Equal(identifierA.Id, retainedIdentifier.Id);
        Assert.Equal(identifierA.Value, retainedIdentifier.Value);

        using var auditScope = factory.Services.CreateScope();
        auditScope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantB);
        var auditDb = auditScope.ServiceProvider.GetRequiredService<HeapCareDbContext>();
        var auditEvents = await auditDb.AuditEvents.AsNoTracking()
            .Where(audit => audit.ActorSubject == tenantBAdminSubject &&
                            !existingAuditIds.Contains(audit.Id))
            .OrderBy(audit => audit.OccurredAtUtc)
            .ToListAsync();

        Assert.Equal(attempts.Length, auditEvents.Count);
        Assert.Equal(
            new[]
            {
                "GetHospitalPatientIdentifiers",
                "AssignHospitalMedicalRecordNumber",
                "GetHospitalPatientIdentifierPolicy",
                "FindHospitalPatientByMrn"
            },
            auditEvents.Select(audit => audit.Action).ToArray());
        Assert.All(auditEvents, audit =>
        {
            Assert.Equal(tenantB, audit.TenantId);
            Assert.Equal(tenantB, audit.RequestedTenantId);
            Assert.Equal(TestJwtTokenFactory.Issuer, audit.ActorIssuer);
            Assert.Equal(tenantBAdminSubject, audit.ActorSubject);
            Assert.Equal((int)HttpStatusCode.NotFound, audit.StatusCode);
            Assert.Equal("Rejected", audit.Outcome);
        });
    }

    private async Task<PatientDto> RegisterPatientAsync(HttpClient client, Guid hospitalId, string givenName)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/hospitals/{hospitalId:D}/patients",
            new PatientRegistrationInput(givenName, null, "Test", null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var patient = await response.Content.ReadFromJsonAsync<PatientDto>();
        Assert.NotNull(patient);
        return patient!;
    }

    private async Task SeedMembershipAsync(
        Guid tenantId,
        string subject,
        string role,
        IReadOnlyList<OrganizationScope>? scopes = null)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<HeapCareDbContext>();

        if (!await db.Tenants.AnyAsync(x => x.Id == tenantId))
        {
            db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "MRN integration test tenant",
                Slug = $"mrn-{tenantId:N}",
                Status = TenantStatuses.Active
            });
            await db.SaveChangesAsync();
        }

        await factory.SeedSystemRolesAsync(tenantId);

        var membership = new TenantMembership
        {
            TenantId = tenantId,
            Issuer = TestJwtTokenFactory.Issuer,
            Subject = subject,
            Role = role,
            IsActive = true
        };
        foreach (var item in scopes ?? [new OrganizationScope(OrganizationScopeTypes.Tenant)])
        {
            membership.Scopes.Add(new TenantMembershipScope
            {
                TenantId = tenantId,
                MembershipId = membership.Id,
                ScopeType = item.ScopeType,
                HospitalId = item.HospitalId,
                DepartmentId = item.DepartmentId
            });
        }

        db.TenantMemberships.Add(membership);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedHospitalAsync(Guid tenantId, string name)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<HeapCareDbContext>();
        var hospital = new Hospital { Name = name, Code = Guid.NewGuid().ToString("N")[..8] };
        db.Hospitals.Add(hospital);
        await db.SaveChangesAsync();
        return hospital.Id;
    }

    private async Task<(Guid First, Guid Second)> SeedTenantAndTwoHospitalsAsync(Guid tenantId)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<HeapCareDbContext>();
        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "MRN scope test tenant",
            Slug = $"mrn-scope-{tenantId:N}",
            Status = TenantStatuses.Active
        });
        await db.SaveChangesAsync();

        var first = new Hospital { Name = "Authorized MRN Hospital", Code = Guid.NewGuid().ToString("N")[..8] };
        var second = new Hospital { Name = "Other MRN Hospital", Code = Guid.NewGuid().ToString("N")[..8] };
        db.Hospitals.AddRange(first, second);
        await db.SaveChangesAsync();
        return (first.Id, second.Id);
    }

    private HttpClient CreateClient(Guid tenantId, string subject)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokenFactory.CreateToken(subject: subject));
        client.DefaultRequestHeaders.Add(TenantContextMiddleware.HeaderName, tenantId.ToString("D"));
        return client;
    }
}
