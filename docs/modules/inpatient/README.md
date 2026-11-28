# Inpatient (IPD) API contract

**Status:** Initial ADT and bed-placement API implemented; positive access remains gated.  
**Runtime scope:** Admission creation, admission list/detail, first bed assignment and same-Hospital bed transfer.  
**Not implemented:** Admission cancellation and discharge endpoints/services.

This document describes the current API implementation. It does not approve hospital policy, clinical privileges or role grants. See the [IPD workflow approval worksheet](https://github.com/HeapCare/HeapCare.Doc/blob/main/product/workflows/ipd-hospital-policy-decision-worksheet.md) and [ADR-018](https://github.com/HeapCare/HeapCare.Doc/blob/main/architecture/decisions/ADR-018-inpatient-ADT-bed-management-boundaries.md) for the decision boundary.

## Security and access status

All routes below are tenant-scoped, require authentication and carry a dedicated IPD authorization policy. Requests must include:

- A valid Bearer access token issued by the configured OIDC authority.
- Exactly one valid `X-Tenant-ID` header selecting the active tenant.

The tenant header is context only; it does not grant membership, permission or resource access. The API resolves authorization from the validated issuer/subject and the active persisted tenant membership. Caller-supplied role or permission claims cannot elevate access.

The dedicated permission identifiers are:

| API policy | Permission | Operation |
|---|---|---|
| `InpatientRead` | `inpatient.read` | List and read admissions |
| `InpatientAdmit` | `inpatient.admit` | Create an admission |
| `InpatientBedAssign` | `inpatient.bed.assign` | Assign the first bed |
| `InpatientBedTransfer` | `inpatient.bed.transfer` | Transfer an active placement |
| `InpatientAdmissionCancel` | `inpatient.admission.cancel` | Reserved for a future cancellation operation |
| `InpatientDischarge` | `inpatient.admission.discharge` | Reserved for a future discharge operation |

**Current grant state:** none of these permissions is granted to HospitalAdmin, FacilityManager, FacilityViewer, PatientRegistrar or ClinicalUser. PlatformOperator is a separate global identity and receives no implicit tenant clinical access. Consequently, existing roles receive HTTP 403 from the implemented IPD routes until a hospital-approved role/operation/scope matrix is accepted and implemented through a separate reviewed change.

Do not reuse OPD, patient-registration, facility or billing permissions to open IPD.

## Endpoints

Base path: `/api/v1/hospitals/{hospitalId}/ipd`

| Method | Route | Policy | Current behavior |
|---|---|---|---|
| POST | `/admissions` | `InpatientAdmit` | Create an inpatient episode |
| GET | `/admissions` | `InpatientRead` | List admissions with paging and optional status |
| GET | `/admissions/{admissionId}` | `InpatientRead` | Read an admission and visible placement history |
| POST | `/admissions/{admissionId}/bed-placement` | `InpatientBedAssign` | Assign the first bed |
| POST | `/admissions/{admissionId}/bed-transfer` | `InpatientBedTransfer` | Transfer to another bed in the same Hospital |

There are currently no cancellation or discharge routes. The cancellation/discharge permission constants are policy placeholders only; they are not evidence that those workflows exist.

### Create an admission

`POST /admissions`

Request:

```json
{
  "patientId": "00000000-0000-0000-0000-000000000001"
}
```

The patient must already have a `HospitalPatientRegistration` for the exact Hospital in the selected Tenant. A Patient master record by itself is insufficient. A department-only membership cannot create an admission; the service requires tenant-wide or selected-Hospital scope.

A successful request returns `201 Created` with the admission representation and a Location header. The current technical operation creates an Active admission without a bed placement. This behavior is not a decision that hospitals must permit admission-before-bed; that sequencing remains pending approval.

The database uniqueness constraint is the final safeguard against two active admissions for the same patient at the same Hospital. A concurrent duplicate request returns a conflict.

### List admissions

`GET /admissions?page=1&pageSize=25&status=Active`

Query parameters:

| Parameter | Default | Boundary |
|---|---:|---|
| `page` | 1 | Values below 1 are normalized to 1 |
| `pageSize` | 25 | Values below 1 normalize to 25; maximum 100 |
| `status` | omitted | `Active`, `Cancelled` or `Discharged` (case-insensitive) |

The response is an `InpatientAdmissionPage` with `items`, `page`, `pageSize`, `totalCount` and `totalPages`. Ordering is newest admission first with admission ID as a stable tie-breaker.

A tenant-wide or selected-Hospital scope can list admissions at that Hospital. A department-only scope sees only admissions with a current placement in one of its assigned departments. Unplaced admissions are not exposed to department-only members. Placement history is limited to the caller's visible departments.

### Read an admission

`GET /admissions/{admissionId}`

The response contains the admission and only the placement history visible to the caller's organization scope. A department-only member can read an admission only when its current placement is in an assigned department; an admission without a current placement is not visible through a department-only scope. Historical movements outside the caller's assigned departments are omitted.

Requests for a missing or out-of-scope admission return a non-disclosing `404 Not Found`.

### Assign the first bed

`POST /admissions/{admissionId}/bed-placement`

Request:

```json
{
  "departmentId": "00000000-0000-0000-0000-000000000010",
  "wardId": "00000000-0000-0000-0000-000000000020",
  "roomId": "00000000-0000-0000-0000-000000000030",
  "bedId": "00000000-0000-0000-0000-000000000040"
}
```

The service resolves and validates the complete Department → Ward → Room → Bed chain under the requested Tenant and Hospital. The admission must be Active; the destination bed must exist, be active and be manageable within the caller's department scope. An admission with an active placement must use the transfer operation instead.

The database constraints enforce at most one active placement per admission and at most one active occupant per bed. Duplicate and concurrent occupancy attempts fail with a conflict rather than creating a second occupant.

### Transfer a bed

`POST /admissions/{admissionId}/bed-transfer`

Request:

```json
{
  "destination": {
    "departmentId": "00000000-0000-0000-0000-000000000010",
    "wardId": "00000000-0000-0000-0000-000000000020",
    "roomId": "00000000-0000-0000-0000-000000000030",
    "bedId": "00000000-0000-0000-0000-000000000041"
  },
  "reason": "Transfer to another eligible bed"
}
```

The transfer reason is required and limited to 1–250 characters. The admission must be Active and it must have a current active bed placement. The current and destination departments must both be within the caller's manageable scope. The destination must resolve within the same Hospital, be active and differ from the current bed.

The prior placement is closed and the new placement is opened in one database save/transaction. If a concurrency or destination-occupancy constraint is hit, the operation returns a conflict and no partial movement should persist. Cross-Hospital transfer is not supported.

## Response contract

The admission representation contains:

- `id`, `hospitalId`, `patientId`, `status`, `admittedAtUtc`
- `cancelledAtUtc` and `dischargedAtUtc` (nullable lifecycle fields)
- `currentPlacement` (nullable)
- `placementHistory` (visible placement records)

Each placement representation contains `id`, `admissionId`, `hospitalId`, `location`, `startedAtUtc`, `endedAtUtc` and `endReason`. The location contains `departmentId`, `wardId`, `roomId` and `bedId`.

Admission status values currently represented by the persistence contract are `Active`, `Cancelled` and `Discharged`. No cancellation or discharge transition is exposed by the current API.

## HTTP outcomes

The routes use RFC 9457 Problem Details for errors. Important outcomes include:

- `400 Bad Request`: invalid request values or an unsupported list status.
- `401 Unauthorized`: unauthenticated request or invalid actor context at the service boundary.
- `403 Forbidden`: authenticated principal lacks the dedicated IPD permission.
- `404 Not Found`: missing or out-of-scope Hospital, admission, patient registration or bed hierarchy resource.
- `409 Conflict`: duplicate active admission, existing placement, inactive bed, invalid lifecycle transition or concurrent occupancy/transfer conflict.
- `503 Service Unavailable`: the request transaction cannot be committed because metadata-only audit persistence fails.

Exact conflict details are implementation safeguards, not a substitute for approved hospital workflow rules.

## Tenant, Hospital and department boundaries

- A Tenant represents the customer/hospital organization and may operate multiple physical Hospitals.
- The initial IPD release uses an existing Hospital registration and stays within one Hospital for admission and bed movement.
- Composite tenant/facility relationships constrain admission and placement references.
- A sibling Hospital under the same Tenant is not interchangeable with the requested Hospital.
- Department-only visibility is based on the current placement, with history filtered to visible departments.
- The proposed FacilitySite model in ADR-019 is not implemented. No branch/clinic sharing or cross-Hospital clinical visibility is implied.

## Audit and transaction behavior

IPD routes are included in the metadata-only `AuditEventMiddleware` pipeline. Audit records capture trusted actor issuer/subject, Tenant, action, resource type and resource identifier, HTTP method, route template, outcome/status and correlation ID. Request/response bodies, query strings and patient payloads are not stored in audit records.

Business writes and the corresponding audit event share the request transaction. If audit persistence fails, the transaction is rolled back and the client receives `503 Service Unavailable`; successful admission creation, bed assignment and transfer cannot be acknowledged without the audit record committing.

## Explicitly out of scope

The initial ADT and bed-management increment does not implement medication ordering/administration, nursing notes, observations/vitals, clinical orders, clinical discharge summaries, pharmacy, diagnostics, patient portal, payer/ABDM integration, automatic billing/payment/MRN changes, cross-Hospital transfers, bed reservations or housekeeping/readiness workflows.

Cancellation, discharge, mandatory admission data/reason codes, role grants and bed reservation/readiness rules require hospital operations, clinical and security approval before implementation is enabled.
