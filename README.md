# HeapCare API

> **HeapCare migration status:** .NET solution, project/folder names, source namespaces, EF context/migration namespaces, package references and API-facing documentation are migrated and merged to main. The GitHub organization and repository slug are now HeapCare. The existing PostgreSQL database/role, EF migration IDs/history and legacy connection-string key remain compatibility contracts; `ConnectionStrings:HeapCare` is preferred and `ConnectionStrings:CompileCare` remains a fallback. OIDC realm/client/audience identifiers are also preserved until a separately tested identity migration.

Tenant-aware modular backend for the HeapCare healthcare platform.

## Architecture

The first delivery is a **modular monolith** with explicit capability boundaries. The target architecture is distributed, but modules will be extracted into independently deployable services only when justified by scaling, deployment, ownership, integration, or resilience needs.

## Solution layout

- `HeapCare.Api`: ASP.NET Core host, HTTP middleware and endpoints.
- `HeapCare.Application`: application contracts and use cases.
- `HeapCare.Infrastructure`: provider-specific adapters.
- `Modules/`: business capabilities added incrementally.
- `tests/`: unit and integration tests.

## Foundation

- .NET 10 / ASP.NET Core
- EF Core 10
- PostgreSQL via Npgsql
- Versioned platform endpoint
- OpenAPI in Development
- Shared `HeapCare.Observability` library for structured logging, OpenTelemetry traces/metrics and health checks
- Liveness/readiness endpoints provided by the shared observability package
- Correlation ID propagation through `X-Correlation-ID`
- Request-scoped tenant context through `X-Tenant-ID`
- Problem Details exception handling
- Dockerfile and GitHub Actions CI

## Database configuration

Set `ConnectionStrings__HeapCare` through environment variables or a secret store. Do not commit credentials.

Example local connection string:

```
Host=localhost;Database=compilecare;Username=compilecare;Password=replace-me
```

Outside Development and the isolated `Testing` host, the API requires `ConnectionStrings:HeapCare` (the legacy `ConnectionStrings:CompileCare` key remains a compatibility fallback) and rejects startup unless PostgreSQL uses `SSL Mode=VerifyFull` with `Trust Server Certificate=false`. This verifies both the certificate chain and server hostname. If the selected provider uses a private CA, mount its CA certificate securely and configure Npgsql's `Root Certificate` connection-string option. Do not disable certificate validation to work around a provider certificate problem. Development may use the local plaintext PostgreSQL service.

Example production connection-string shape (replace host/database/user and supply the password from a secret manager):

```text
Host=postgres.example.com;Database=compilecare;Username=compilecare_app;Password=<secret>;SSL Mode=VerifyFull
```

Infrastructure registers EF Core/Npgsql and database readiness only when the connection string is configured. Migrations are created with the EF Core CLI from the Infrastructure project.

```bash
dotnet ef migrations add <MigrationName> --project src/HeapCare.Infrastructure --startup-project src/HeapCare.Api --output-dir Persistence/Migrations
dotnet ef database update --project src/HeapCare.Infrastructure --startup-project src/HeapCare.Api

dotnet ef migrations script --idempotent --project src/HeapCare.Infrastructure --startup-project src/HeapCare.Api --output artifacts/heapcare-migrations.sql
```

The design-time factory supports `ConnectionStrings__HeapCare`; its local fallback is tooling-only. Never use it in production. Production deployments must review and apply a generated migration script through the release process after a verified backup. **The API does not run migrations automatically at startup.** Keep migration files and the model snapshot in source control; never edit a migration already applied to a shared environment. Integration tests recreate their dedicated `_test` database and apply migrations rather than using `EnsureCreated`.

## Authentication and tenant authorization

Configure an OIDC identity provider using its HTTPS issuer/discovery endpoint and the API's registered audience. Do not commit credentials or signing keys.

Set the following environment variables in each deployed environment:

```text
Authentication__Authority=https://identity.example.com/
Authentication__Audience=compilecare-api
Authentication__RequireHttpsMetadata=true
Cors__AllowedOrigins__0=https://workspace.example.com
```

In non-Development environments, every configured `Cors:AllowedOrigins` entry must be an explicit HTTPS origin (scheme, host and optional port only). Wildcards, credentials, paths, query strings, fragments and HTTP origins are rejected at startup. Omit the list for a same-origin Web/API deployment. Development may use explicit HTTP(S) loopback origins only (`localhost`, `*.localhost`, `127.0.0.1` or another loopback IP); remote hosts, wildcards, paths and credentials are rejected even in Development.

The API's `/metrics` endpoint is intended for internal Prometheus scraping only. Outside Development and Testing, startup requires `Observability:Metrics:AllowedNetworks` with one or more explicit CIDRs (for example, `Observability__Metrics__AllowedNetworks__0=10.20.0.0/16`). Requests to `/metrics` from outside those networks receive 404. Configure the CIDR values to match the actual monitoring source network after the hosting topology is selected; do not use a broad public range. Forwarded client addresses are considered only from configured trusted proxies. Development and isolated Testing hosts do not require this setting.

The API validates JWT signature, issuer, audience, lifetime, expiry and subject. The validated issuer and subject (`sub`) are matched against an active HeapCare tenant membership. Facility endpoints require exactly one `X-Tenant-ID` GUID; this header only selects the requested tenant and never grants access by itself.

Tenant roles are stored in the HeapCare database. Current facility permissions are:
- `HospitalAdmin`: read, create/update and delete facility records.
- `FacilityManager`: read and create/update facility records.
- `FacilityViewer`: read facility records only.

The complete source-derived grant snapshot for all five persisted tenant roles, including patient registration, identifiers, staff, appointments, OPD, billing and audit capabilities, is maintained in the [persisted role-to-permission matrix](https://github.com/HeapCare/HeapCare.Doc/blob/main/security/persisted-role-permission-matrix.md). PlatformOperator is a separate global identity, not a tenant membership role. The matrix describes current implementation grants and does not replace product/clinical approval or endpoint/resource-scope verification.

Token-supplied role or permission claims do not grant HeapCare access. The source-derived [API authorization policy inventory](https://github.com/HeapCare/HeapCare.Doc/blob/main/security/authorization-policy-inventory.md) documents current policy-to-endpoint assignments and the required role/scope regression review; it is an inventory, not a grant matrix. Tenant registry endpoints are available only to persisted platform operators at `/api/v1/platform/tenants`. New tenants start in `Provisioning`; activation, suspension and closure are explicit lifecycle transitions. The first platform operator must be bootstrapped out-of-band by an authorised database administrator using the exact validated OIDC issuer and subject. Platform operators can provision exactly one initial tenant-wide HospitalAdmin while a tenant is in Provisioning. Tenant HospitalAdmins can create, update, deactivate and reactivate memberships inside their assigned organization scope. New memberships require an explicit tenant, hospital or department scope.

### Platform tenant registry

Platform-operator-only routes:

- `GET /api/v1/platform/tenants?page=1&pageSize=50`
- `GET /api/v1/platform/tenants/{id}`
- `POST /api/v1/platform/tenants` with `{ "name": "...", "slug": "..." }`
- `POST /api/v1/platform/tenants/{id}/activate`
- `POST /api/v1/platform/tenants/{id}/suspend`
- `POST /api/v1/platform/tenants/{id}/close`

Creation produces a `Provisioning` tenant. Only active tenants can be used for tenant-scoped requests. Slugs are unique, lowercase, and limited to letters/digits separated by single hyphens. Closing is terminal; tenant records are not physically deleted.

The initial platform operator is bootstrapped by a restricted database administrator after verifying the operator's exact issuer and subject with the identity provider. Example SQL (replace both placeholders with verified values; do not use an email address as the subject):

```sql
INSERT INTO platform_operators
    ("Id", "Issuer", "Subject", "IsActive", "CreatedAtUtc", "UpdatedAtUtc", "Version")
VALUES
    (gen_random_uuid(), '<validated-oidc-issuer>', '<validated-oidc-subject>',
     TRUE, now(), now(), gen_random_uuid());
```

No public platform-operator enrolment endpoint exists. Protect and audit the database bootstrap procedure. The separate tenant membership bootstrap and administration routes are described below.

### Tenant membership and organization scopes

The first platform operator provisions the initial tenant-wide HospitalAdmin:

- `POST /api/v1/platform/tenants/{tenantId}/initial-administrator`
- Body: `{ "issuer": "<configured OIDC authority>", "subject": "<validated OIDC sub>" }`

The route is platform-operator-only, idempotent for the same initial identity and available only while the tenant is `Provisioning`. Concurrent bootstrap attempts are serialized on the tenant record; only one initial membership is created.

Tenant HospitalAdmins manage memberships within their assigned organization scope:

- `GET /api/v1/tenant/memberships?page=1&pageSize=50`
- `POST /api/v1/tenant/memberships`
- `PUT /api/v1/tenant/memberships/{membershipId}`
- `POST /api/v1/tenant/memberships/{membershipId}/deactivate`
- `POST /api/v1/tenant/memberships/{membershipId}/reactivate`

A membership requires an issuer matching the configured OIDC authority, a subject (`sub`), one supported HeapCare role, and one or more explicit scopes. Scope shapes are:

- Tenant: `{ "scopeType": "Tenant" }`
- Hospital: `{ "scopeType": "Hospital", "hospitalId": "<guid>" }`
- Department: `{ "scopeType": "Department", "departmentId": "<guid>" }`

Department scope is resolved to its owning hospital by the API. Cross-tenant and cross-hospital department assignments are rejected. A tenant-wide scope cannot be combined with narrower scopes. Existing memberships are backfilled to tenant-wide scope to preserve their pre-existing access. Membership records are deactivated/reactivated rather than physically deleted.

Only HospitalAdmin receives `membership.manage`; the service additionally checks that the acting administrator can manage every scope assigned to the target member. Administrators cannot modify or deactivate their own membership, and the last active tenant-wide HospitalAdmin cannot be demoted, de-scoped or deactivated.

The integration suite uses synthetic, signed HS256 JWTs and a local static OIDC signing configuration. Its test key is not used by any deployed environment.

## Observability configuration

The API consumes stable shared packages from the HeapCare GitHub Packages NuGet feed. CI uses `GITHUB_TOKEN` with `packages: read`; grant the API repository read access in each package's Settings → Manage Actions access.

Set:
- `OpenTelemetry__ServiceName=HeapCare.Api`
- `OpenTelemetry__OtlpEndpoint=http://localhost:4318`
- `ElasticConfiguration__Uri` (optional)

Do not log patient data, credentials, tokens, or other sensitive healthcare information.

## Run

Requires the .NET 10 SDK and access to the HeapCare GitHub Packages feed.

```bash
dotnet restore HeapCare.Api.slnx
dotnet build HeapCare.Api.slnx
dotnet test HeapCare.Api.slnx
dotnet run --project src/HeapCare.Api
```

The integration suite exercises the facility API against PostgreSQL. CI provisions this service automatically. For local runs, provide `ConnectionStrings__HeapCareTests` pointing to a dedicated test database whose name ends in `_test` (the default is `compilecare_test`). The integration test host deletes and recreates that database, and refuses to reset a database without the required suffix. Do not point it at a development, staging, or production database.

Platform endpoints: `/`, `/health/live`, `/health/ready`, `/metrics`, `/api/v1/platform/context`, and (Development only) `/openapi/v1.json`.

## Facility hierarchy API

The facility module exposes CRUD for the hierarchy **Hospital → Department → Ward → Room → Bed**:

- Hospitals: `/api/v1/hospitals`
- Departments: `/api/v1/hospitals/{hospitalId}/departments`
- Wards: `/api/v1/departments/{departmentId}/wards`
- Rooms: `/api/v1/wards/{wardId}/rooms`
- Beds: `/api/v1/rooms/{roomId}/beds`

Each collection supports bounded `page` and `pageSize` pagination (default 50, maximum 100). See [the facility module documentation](docs/modules/facilities/README.md) for route behavior, resource constraints, and response metadata.

## Staff and provider management

The first staff-management increment provides tenant-owned workforce profiles and effective-dated hospital/department assignments:

- `POST /api/v1/staff`
- `GET /api/v1/staff?page=1&pageSize=50&query=...`
- `GET /api/v1/staff/{staffId}`
- `PUT /api/v1/staff/{staffId}`
- `POST /api/v1/staff/{staffId}/assignments`
- `POST /api/v1/staff/{staffId}/assignments/{assignmentId}/end`
- `POST /api/v1/staff/{staffId}/deactivate` and `/reactivate`

Only HospitalAdmin receives the new staff read/manage permissions in this first increment. Every request still requires validated OIDC identity, active tenant membership, tenant context and organization-scope validation. Profiles and assignments are tenant-owned with composite tenant-aware facility/profile foreign keys. Provider designation is descriptive metadata only—not credential or clinical-privilege verification. Login provisioning, membership linking, payroll, attendance, credentialing and sensitive employment identifiers are deferred.

See [staff module API documentation](docs/modules/staff/README.md) and [documentation PR #21](https://github.com/HeapCare/HeapCare.Doc/pull/21) for the product workflow and design boundaries.

## Inpatient (IPD) API

The initial inpatient ADT API supports admission creation, admission listing/detail, first-bed assignment and same-Hospital bed transfer. The routes remain protected by dedicated IPD permissions that are not currently granted to existing tenant roles. Cancellation and discharge routes are not implemented. See the [inpatient module API contract](docs/modules/inpatient/README.md) for request/response shapes, resource-scope rules, audit behavior and current access gates.

## Tenant isolation

The tenant header is request context only. It is **not authentication** and does not prove that a caller may access a tenant. Facility and platform-context API routes now require a validated OIDC/JWT principal and an active HeapCare membership matching the token issuer, subject and selected tenant. Persistence requires the validated tenant context for tenant-owned writes and applies tenant query filters. Background jobs and message consumers must establish an equivalent trusted tenant context before accessing tenant-owned data. The platform context endpoint is diagnostic only.

## Pull request workflow

Use a short-lived branch from `main`; open a focused PR; review the diff and passing CI; squash merge; delete the merged head branch and verify cleanup. No direct feature commits to `main`.

## Trusted ingress and HSTS

The API trusts forwarded client IP/scheme headers only from the explicit `ReverseProxy:KnownProxies` IP allowlist, with a one-hop limit and symmetric-header validation. Configure the exact ingress/load-balancer source IPs for the deployment; never trust arbitrary forwarded headers from the public internet.

For non-Development requests that are HTTPS after trusted forwarded-header processing, the API emits HSTS with a one-year `max-age`. `includeSubDomains` and `preload` remain disabled until the organization has verified HTTPS across every affected subdomain and approved the preload commitment. The API does not terminate TLS: configure certificates, HTTPS redirects and edge HSTS at the selected ingress as well.
