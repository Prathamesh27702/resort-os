# API

REST over HTTPS, JSON, versioned under **`/api/v1`** (brief §62). The live spec is served by the API in Development
at `/openapi/v1.json`, with Swagger UI at `/swagger`. A committed snapshot is in [api/openapi-v1.json](api/openapi-v1.json).

- **Built endpoints** are fully specified below (§63 format).
- **Planned endpoints**, by sprint: [api/endpoints-by-sprint.md](api/endpoints-by-sprint.md).
- **External APIs** each module depends on: [see the table at the end](#external-apis-by-module). Keys for those are
  **never in this repo**; the owner issues them privately. Variable names are in `.env.example`.

## Conventions

| Topic | Rule |
|---|---|
| Base path | `/api/v1/<group>`; property-specific resources live under `/api/v1/properties/{propertyId}/…` |
| Groups (§62) | auth, users, roles, properties, rooms, beds, guests, reservations, folios, payments, invoices, housekeeping, maintenance, menu, orders, kitchen, inventory, suppliers, purchases, employees, attendance, timesheets, leave, roster, activities, transport, equipment, expenses, budgets, reports, integrations, webhooks |
| Auth | `Authorization: Bearer <accessToken>` (15 min). Refresh via HttpOnly cookie on `/api/v1/auth/refresh` with header `X-Resort-Client: web` |
| Permissions | Each endpoint requires one permission code. Under `/properties/{propertyId}/…` the grant must be org-wide or for that property |
| Ids | UUID strings |
| Time | ISO-8601 UTC instants (`2026-10-06T08:30:00Z`); business dates as `YYYY-MM-DD` in property local time |
| Money | JSON numbers with 2 decimals, currency from the property (INR) |
| Concurrency | Mutable resources return `version`; send it back on PUT. Stale → `409 CONCURRENCY_CONFLICT` |
| Idempotency | Money/booking POSTs require `Idempotency-Key: <uuid>`; retries with the same key return the original response (Sprint 5+) |
| Reason | Send `X-Change-Reason: <url-encoded text>` on sensitive changes; stored in the audit log. Required for refunds, cancellations, reassignments, adjustments (enforced per endpoint) |
| Tracing | Optional `X-Correlation-Id` (8–64 chars `[A-Za-z0-9-]`); every response returns `X-Correlation-Id` and `X-Request-Id` |
| Paging | `?page=1&pageSize=50` (max 200) → `{ items, page, pageSize, total }` |
| Rate limits | Sign-in & password change: 10/min/IP. Everything else: 600/min per user (or IP) |

### Error format (brief §66)

Every non-2xx response:

```json
{
  "code": "VALIDATION_FAILED",
  "message": "Some fields need fixing.",
  "details": { "Email": ["Email address is not valid."] },
  "correlationId": "f67946a16a98461489607b078275c656"
}
```

| HTTP | `code` | When |
|---|---|---|
| 400 | `VALIDATION_FAILED` | FluentValidation failed (`details` = field → messages) |
| 400 | `MISSING_CLIENT_HEADER`, `BAD_REQUEST` | Malformed request |
| 401 | `UNAUTHORIZED`, `INVALID_CREDENTIALS`, `ACCOUNT_LOCKED`, `INVALID_REFRESH_TOKEN` | Not signed in / bad credentials |
| 403 | `FORBIDDEN`, `PASSWORD_CHANGE_REQUIRED` | Missing permission / temporary password |
| 404 | `NOT_FOUND` | Missing **or in another tenant** (never reveals which) |
| 409 | `CONCURRENCY_CONFLICT`, `DUPLICATE`, `OVERLAP`, `REFERENCE_CONFLICT`, business conflicts (`EMAIL_TAKEN`, `PROPERTY_CODE_TAKEN`, later `ROOM_UNAVAILABLE`) | State conflict |
| 422 | Business rule codes (`SELF_ROLE_CHANGE`, `LAST_OWNER`, `CONSTRAINT_VIOLATION`, …) | Valid request, rule says no |
| 429 | `RATE_LIMITED` | Too many requests |
| 500 | `INTERNAL_ERROR` | Bug. Details only in server logs, found by `correlationId` |

Stack traces, SQL, exception types and infrastructure details are never returned.

## How to add an endpoint

Example: *"GET a room"* in Sprint 2.

1. **Permission.** Use an existing code (`accommodation.read`) or add one in a migration + `Resort.Application/Common/Permissions.cs`.
2. **Contract.** Add the request/response records to `Resort.Contracts` (`RoomDto`, `CreateRoomRequest`).
3. **Domain/entity.** Add the entity in `Resort.Domain/Accommodation`, map it in `AppDbContext` (table name; snake_case is automatic).
4. **Migration.** `database/migrations/V00N__rooms.sql`.
5. **Validator.** `Resort.Application/Validation` — one `AbstractValidator<CreateRoomRequest>`.
6. **Service.** `Resort.Application/Accommodation/RoomService.cs`: all business rules, resource-level checks via
   `CallerAccess`, `ConcurrencyGuard.ExpectVersion` for updates. Register it in `Application/DependencyInjection.cs`.
7. **Endpoint.** `Resort.Api/Endpoints/AccommodationEndpoints.cs`: a thin mapping only.

   ```csharp
   var rooms = app.MapGroup("/api/v1/properties/{propertyId:guid}/rooms").WithTags("Rooms");
   rooms.MapGet("/{id:guid}", (Guid propertyId, Guid id, RoomService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
        .RequirePermission(Perm.AccommodationRead);
   rooms.MapPost("", (Guid propertyId, CreateRoomRequest r, RoomService s, CancellationToken ct) => s.CreateAsync(propertyId, r, ct))
        .Validate<CreateRoomRequest>()
        .RequirePermission(Perm.AccommodationManage);
   ```
   Call `app.MapAccommodationEndpoints()` in `Program.cs`.
8. **Audit.** Mark the entity `IAudited`; the interceptor records it. For actions that need a reason, check
   `ICurrentUser.ChangeReason` in the service and throw `BusinessRuleAppException("REASON_REQUIRED", …)` if empty.
9. **Tests.** Integration tests in `Resort.Tests/Integration`: success, 403 for a role without the permission, 404
   across tenants, validation 400, and any conflict/concurrency case.
10. **Docs.** Add the endpoint to the spec table below and regenerate `api/openapi-v1.json` (see the end of this file).
11. **Frontend.** Add a typed method or service in `frontend/src/app/features/<module>/`; never call `HttpClient` from templates.

## Built endpoints (Sprint 1)

Auth column: **anon** = no token, **auth** = any signed-in user, otherwise the permission code. *Scope*: **org** =
needs an org-wide grant; **prop** = org-wide or for `{propertyId}`; **any** = at least one grant, results filtered.

### Auth — `/api/v1/auth`

| Method & URL | Purpose | Auth | Request | Response | Errors | Idempotent | Audit |
|---|---|---|---|---|---|---|---|
| `POST /login` | Sign in | anon (10/min/IP) | `{ email, password }` | `200 AuthResponse` + `Set-Cookie: __Secure-resort_rt` (HttpOnly, Secure, SameSite=Strict, Path=/api/v1/auth) | 400, 401 `INVALID_CREDENTIALS`/`ACCOUNT_LOCKED`, 429 | n/a | `login_events` row (success/failure reason); user lockout counters |
| `POST /refresh` | Rotate refresh token, new access token | cookie + `X-Resort-Client` header | — | `200 AuthResponse` + new cookie | 400 `MISSING_CLIENT_HEADER`, 401 `INVALID_REFRESH_TOKEN` | **No** — reuse of a rotated token revokes the whole session family | `REFRESH_TOKEN_REUSE` on theft detection |
| `POST /logout` | Revoke the session family | cookie + `X-Resort-Client` | — | `204` | 400 | yes | token revocation |
| `GET /me` | Current user, organisation, effective grants | auth | — | `200 MeResponse` | 401 | yes | — |
| `POST /change-password` | Change own password; signs out everywhere | auth (10/min/IP) | `{ currentPassword, newPassword }` (≥10 chars, letters+digits, differs) | `204` | 400, 422 `CURRENT_PASSWORD_INCORRECT` | no | user UPDATE (hash masked) |

`AuthResponse = { accessToken, accessTokenExpiresAt, me: MeResponse }`
`MeResponse = { userId, email, fullName, mustChangePassword, organization: {id, code, name}, grants: [{ propertyId|null, roles[], permissions[] }] }`

### Organisation — `/api/v1/organization`

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET` | Caller's organisation | `organization.read` (org) | — | `OrganizationDto {id, code, name, legalName, baseCurrency, countryCode, version}` | 403 | — |
| `PUT` | Rename | `organization.update` (org) | `{ name, legalName?, version }` | `OrganizationDto` | 400, 403, 409 | UPDATE |

### Properties — `/api/v1/properties`

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET` | Properties the caller can see | `property.read` (any, filtered) | — | `PropertyDto[]` | 403 | — |
| `POST` | Create property | `property.create` (org) | `CreatePropertyRequest` | `201 PropertyDto` | 400, 403, 409 `PROPERTY_CODE_TAKEN` | INSERT |
| `GET /{propertyId}` | One property | `property.read` (prop) | — | `PropertyDto` | 403, 404 | — |
| `PUT /{propertyId}` | Update | `property.update` (prop) | `UpdatePropertyRequest` incl. `version` | `PropertyDto` | 400, 403, 404, 409 `CONCURRENCY_CONFLICT` | UPDATE (old/new) |
| `GET /{propertyId}/settings` | Settings | `property.read` (prop) | — | `[{ key, value(json), updatedAt }]` | 403, 404 | — |
| `PUT /{propertyId}/settings/{key}` | Upsert setting | `property_settings.update` (prop) | `{ value: <any JSON> }`; key `^[a-z][a-z0-9_.]{1,99}$` | `PropertySettingDto` | 400, 403, 404 | INSERT/UPDATE |

`PropertyDto` fields: `id, code, name, legalName, propertyType (RESORT|HOTEL|HOMESTAY|HOSTEL|VILLA|OTHER), timezone (IANA),
currency, addressLine1/2, city, district, stateName, gstStateCode (2 digits), postalCode, countryCode, phone, email,
checkInTime, checkOutTime, latitude, longitude, isActive, localDate (property-local today), version`.
Validation: name ≤ 200; code `^[A-Za-z0-9_]{2,30}$` (create only, immutable); timezone must be IANA; currency ISO-4217; GST state code `^\d{2}$`; lat/lng ranges.

### Users — `/api/v1/users`

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET` | All users in the organisation | `user.read` (org) | — | `UserDto[]` | 403 | — |
| `GET /{id}` | One user | `user.read` (org) | — | `UserDto` | 403, 404 | — |
| `POST` | Create with temporary password | `user.create` (org) | `{ email, fullName, phone?, temporaryPassword, roles: [{roleCode, propertyId?}] }` | `201 UserDto` | 400, 403 (escalation), 409 `EMAIL_TAKEN`, 422 `UNKNOWN_ROLE`/`UNKNOWN_PROPERTY` | INSERT user + role grants |
| `PUT /{id}` | Name/phone | `user.update` (org) | `{ fullName, phone?, version }` | `UserDto` | 400, 403, 404, 409 | UPDATE |
| `PUT /{id}/roles` | Replace role grants | `user.update` (org) | `{ roles: [...] }` | `UserDto` | 403 (escalation), 422 `SELF_ROLE_CHANGE`/`LAST_OWNER` | INSERT/DELETE UserRole (+ reason) |
| `POST /{id}/deactivate` | Disable + revoke sessions | `user.deactivate` (org) | — | `UserDto` | 403, 422 `SELF_DEACTIVATE`/`LAST_OWNER` | UPDATE (+ reason) |
| `POST /{id}/activate` | Re-enable | `user.deactivate` (org) | — | `UserDto` | 403 | UPDATE (+ reason) |
| `POST /{id}/reset-password` | New temporary password, revoke sessions | `user.update` (org) | `{ temporaryPassword }` | `204` | 400, 403 | UPDATE (+ reason) |

Escalation guard: the caller must hold every permission of each role they grant **or remove**, in that scope.

### Roles & audit

| Method & URL | Purpose | Permission | Response |
|---|---|---|---|
| `GET /api/v1/roles` | Roles with permission codes | `role.read` (any) | `RoleDto[] {id, code, name, description, isSystem, permissions[]}` |
| `GET /api/v1/permissions` | Permission catalogue | `role.read` (any) | `PermissionDto[] {code, module, description, isSensitive}` |
| `GET /api/v1/audit-logs?entityType&entityId&userId&from&to&page&pageSize` | Audit trail (newest first) | `audit.read` (org) | `Paged<AuditLogDto {id, occurredAt, userId, userName, propertyId, action, entityType, entityId, changes, reason, device, ipAddress, correlationId, requestId}>` |

### Health (no auth, not rate-limited)

`GET /health/live` (process up) · `GET /health/ready` (database reachable).

## External APIs by module

| Module | External API | Purpose | Sprint | Config keys (`.env`) |
|---|---|---|---|---|
| Payments | **Razorpay** Orders, Payments, Refunds APIs + webhooks | Online payment, refunds, authoritative capture via webhook | 11 | `Razorpay__KeyId`, `Razorpay__KeySecret`, `Razorpay__WebhookSecret` |
| Notifications | **WhatsApp Business Platform** (Meta Cloud API) + status webhooks | Confirmations, reminders, invoices | 24 | `WhatsApp__*` |
| Notifications | Transactional **email** provider (SES / SendGrid / Postmark — owner to choose) | Email copies of confirmations/invoices | 24 | `Email__*` |
| Accounting | **Zoho Books** API (India DC) | Customers, items, taxes, invoices, credit notes, payments, expenses | 25 | `ZohoBooks__*` |
| GST | e-Invoice IRP (via Zoho or a GSP — not called directly) | IRN, ack no/date, signed QR | 25 | via provider |
| OTA | **Channel manager** API + webhooks (provider decided in Sprint 0) | Reservation import/modify/cancel, inventory and rate push | 26 | `ChannelManager__*` |
| Payroll | Indian payroll provider (CSV/API, decided in Sprint 0) | Attendance/leave export | 19 | — |
| Files | Blob storage (Azure Blob / S3-compatible) | ID scans, photos, receipts, PDFs | 3+ | `Storage__*` |

Each sits behind an interface in `Resort.Application` with its implementation in `Resort.Infrastructure`, and a fake
server for tests. See [INTEGRATIONS.md](INTEGRATIONS.md).

## Regenerating the OpenAPI snapshot

```bash
cd backend/Resort.Api && dotnet run &
curl -s http://localhost:5180/openapi/v1.json | python3 -m json.tool > ../../docs/api/openapi-v1.json
```

Commit it in the same PR as the endpoint change.
