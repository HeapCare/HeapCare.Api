using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HeapCare.Infrastructure.Persistence.Migrations;

[DbContext(typeof(HeapCareDbContext))]
[Migration("20261128130000_AddPersistedTenantRolesAndPermissions")]
public partial class AddPersistedTenantRolesAndPermissions : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        HeapCareDbContextModelSnapshot.BuildModelForMigration(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "tenant_roles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                PermissionsRevision = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_roles", x => x.Id);
                table.UniqueConstraint("AK_tenant_roles_TenantId_Id", x => new { x.TenantId, x.Id });
                table.UniqueConstraint("AK_tenant_roles_TenantId_Code", x => new { x.TenantId, x.Code });
                table.ForeignKey(
                    name: "FK_tenant_roles_tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "tenant_role_permissions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                PermissionCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_role_permissions", x => x.Id);
                table.ForeignKey(
                    name: "FK_tenant_role_permissions_tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_tenant_role_permissions_tenant_roles_TenantId_RoleId",
                    columns: x => new { x.TenantId, x.RoleId },
                    principalTable: "tenant_roles",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "tenant_role_change_audits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ActorIssuer = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                ActorSubject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                PreviousPermissionCodesJson = table.Column<string>(type: "jsonb", nullable: false),
                NewPermissionCodesJson = table.Column<string>(type: "jsonb", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_role_change_audits", x => x.Id);
                table.CheckConstraint(
                    "CK_tenant_role_change_audits_action",
                    "\"Action\" IN ('Created', 'PermissionsUpdated', 'DetailsUpdated', 'Deactivated', 'Reactivated')");
                table.ForeignKey(
                    name: "FK_tenant_role_change_audits_tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_tenant_role_change_audits_tenant_roles_TenantId_RoleId",
                    columns: x => new { x.TenantId, x.RoleId },
                    principalTable: "tenant_roles",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_tenant_roles_TenantId_IsActive_IsSystem",
            table: "tenant_roles",
            columns: new[] { "TenantId", "IsActive", "IsSystem" });

        migrationBuilder.CreateIndex(
            name: "IX_tenant_role_permissions_TenantId_RoleId_PermissionCode",
            table: "tenant_role_permissions",
            columns: new[] { "TenantId", "RoleId", "PermissionCode" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_tenant_role_change_audits_TenantId_RoleId_CreatedAtUtc",
            table: "tenant_role_change_audits",
            columns: new[] { "TenantId", "RoleId", "CreatedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_tenant_role_change_audits_TenantId_ActorIssuer_ActorSubject_CreatedAtUtc",
            table: "tenant_role_change_audits",
            columns: new[] { "TenantId", "ActorIssuer", "ActorSubject", "CreatedAtUtc" });

        // Backfill the five exact system-role definitions for every tenant,
        // including tenants still in Provisioning. Membership.Role is retained
        // as the stable role code and receives a composite tenant-safe FK below.
        migrationBuilder.Sql("""
            INSERT INTO "tenant_roles"
                ("Id", "TenantId", "Code", "Name", "Description", "IsSystem", "IsActive",
                 "PermissionsRevision", "CreatedAtUtc", "UpdatedAtUtc", "Version")
            SELECT
                gen_random_uuid(), t."Id", r."Code", r."Name", r."Description", TRUE, TRUE,
                0, NOW(), NOW(), gen_random_uuid()
            FROM "tenants" AS t
            CROSS JOIN (VALUES
                ('HospitalAdmin', 'Hospital Administrator',
                 'Current broad tenant and hospital administration role. Effective access is constrained by membership organization scopes.'),
                ('FacilityManager', 'Facility Manager',
                 'Can read and create or update facility structure; cannot delete facilities or administer memberships.'),
                ('FacilityViewer', 'Facility Viewer',
                 'Read-only access to facility structure.'),
                ('PatientRegistrar', 'Patient Registrar',
                 'Reception and registration baseline, including appointment, queue and cashier operations.'),
                ('ClinicalUser', 'Clinical User',
                 'OPD clinical baseline. Professional registration and clinical privileges remain separate controls.')
            ) AS r("Code", "Name", "Description")
            ON CONFLICT ("TenantId", "Code") DO NOTHING;
            """);

        migrationBuilder.Sql("""
            INSERT INTO "tenant_role_permissions"
                ("Id", "TenantId", "RoleId", "PermissionCode",
                 "CreatedAtUtc", "UpdatedAtUtc", "Version")
            SELECT
                gen_random_uuid(), r."TenantId", r."Id", g."PermissionCode",
                NOW(), NOW(), gen_random_uuid()
            FROM "tenant_roles" AS r
            JOIN (VALUES
                ('HospitalAdmin', 'facility.read'),
                ('HospitalAdmin', 'facility.write'),
                ('HospitalAdmin', 'facility.delete'),
                ('HospitalAdmin', 'membership.manage'),
                ('HospitalAdmin', 'audit.read'),
                ('HospitalAdmin', 'patient.read'),
                ('HospitalAdmin', 'patient.identity.read'),
                ('HospitalAdmin', 'patient.register'),
                ('HospitalAdmin', 'patient.duplicate.review'),
                ('HospitalAdmin', 'patient.identifier.read'),
                ('HospitalAdmin', 'patient.identifier.assign'),
                ('HospitalAdmin', 'patient.identifier.policy.read'),
                ('HospitalAdmin', 'patient.identifier.policy.manage'),
                ('HospitalAdmin', 'staff.read'),
                ('HospitalAdmin', 'staff.manage'),
                ('HospitalAdmin', 'availability.read'),
                ('HospitalAdmin', 'availability.manage'),
                ('HospitalAdmin', 'appointment.read'),
                ('HospitalAdmin', 'appointment.manage'),
                ('HospitalAdmin', 'queue.read'),
                ('HospitalAdmin', 'queue.manage'),
                ('HospitalAdmin', 'billing.catalogue.manage'),
                ('HospitalAdmin', 'billing.invoice.read'),
                ('HospitalAdmin', 'billing.invoice.manage'),
                ('HospitalAdmin', 'billing.payment.manage'),
                ('HospitalAdmin', 'billing.report.read'),

                ('FacilityManager', 'facility.read'),
                ('FacilityManager', 'facility.write'),

                ('FacilityViewer', 'facility.read'),

                ('PatientRegistrar', 'facility.read'),
                ('PatientRegistrar', 'patient.read'),
                ('PatientRegistrar', 'patient.identity.read'),
                ('PatientRegistrar', 'patient.register'),
                ('PatientRegistrar', 'patient.duplicate.review'),
                ('PatientRegistrar', 'patient.identifier.read'),
                ('PatientRegistrar', 'patient.identifier.assign'),
                ('PatientRegistrar', 'availability.read'),
                ('PatientRegistrar', 'appointment.read'),
                ('PatientRegistrar', 'appointment.manage'),
                ('PatientRegistrar', 'queue.read'),
                ('PatientRegistrar', 'queue.manage'),
                ('PatientRegistrar', 'billing.invoice.read'),
                ('PatientRegistrar', 'billing.invoice.manage'),
                ('PatientRegistrar', 'billing.payment.manage'),
                ('PatientRegistrar', 'billing.report.read'),

                ('ClinicalUser', 'patient.identity.read'),
                ('ClinicalUser', 'facility.read'),
                ('ClinicalUser', 'queue.read'),
                ('ClinicalUser', 'encounter.read'),
                ('ClinicalUser', 'encounter.manage'),
                ('ClinicalUser', 'encounter.sign'),
                ('ClinicalUser', 'encounter.amend')
            ) AS g("RoleCode", "PermissionCode")
                ON g."RoleCode" = r."Code"
            WHERE r."IsSystem" = TRUE
            ON CONFLICT ("TenantId", "RoleId", "PermissionCode") DO NOTHING;
            """);

        migrationBuilder.AddForeignKey(
            name: "FK_tenant_memberships_tenant_roles_TenantId_Role",
            table: "tenant_memberships",
            columns: new[] { "TenantId", "Role" },
            principalTable: "tenant_roles",
            principalColumns: new[] { "TenantId", "Code" },
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION "heapcare_prevent_tenant_role_change_audit_mutation"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'tenant_role_change_audits is append-only';
                RETURN NULL;
            END;
            $$;

            CREATE TRIGGER "TR_tenant_role_change_audits_no_update_delete"
            BEFORE UPDATE OR DELETE ON "tenant_role_change_audits"
            FOR EACH ROW
            EXECUTE FUNCTION "heapcare_prevent_tenant_role_change_audit_mutation"();

            CREATE TRIGGER "TR_tenant_role_change_audits_no_truncate"
            BEFORE TRUNCATE ON "tenant_role_change_audits"
            FOR EACH STATEMENT
            EXECUTE FUNCTION "heapcare_prevent_tenant_role_change_audit_mutation"();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_tenant_memberships_tenant_roles_TenantId_Role",
            table: "tenant_memberships");

        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS "TR_tenant_role_change_audits_no_update_delete" ON "tenant_role_change_audits";
            DROP TRIGGER IF EXISTS "TR_tenant_role_change_audits_no_truncate" ON "tenant_role_change_audits";
            DROP FUNCTION IF EXISTS "heapcare_prevent_tenant_role_change_audit_mutation"();
            """);

        migrationBuilder.DropTable(name: "tenant_role_change_audits");
        migrationBuilder.DropTable(name: "tenant_role_permissions");
        migrationBuilder.DropTable(name: "tenant_roles");
    }
}
