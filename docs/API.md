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
  "message": "Email address is not valid.",
  "details": { "Email": ["Email address is not valid."] },
  "correlationId": "f67946a16a98461489607b078275c656"
}
```

| HTTP | `code` | When |
|---|---|---|
| 400 | `VALIDATION_FAILED` | FluentValidation failed. `message` lists the reasons (up to 3), `details` = field → messages. Never a generic "fix these fields" |
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

## Built endpoints (Sprint 1 — foundation)

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

## Built endpoints (Sprint 2 — accommodation)

All under `/api/v1/properties/{propertyId}`, scope **prop**. A property in another organisation is 404.
Dates are the property's local dates (`YYYY-MM-DD`). Ranges are `[startDate, endDate)`: the end date is not included,
like a check-out date. Who holds which permission by default: `accommodation.read` — Owner, GM, Manager, Receptionist,
housekeeping and maintenance roles; `accommodation.manage` — Owner, GM; `unit_block.manage` — Owner, GM, Manager.

### Room types — `/accommodation-types`

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /amenities` | Built-in + organisation amenities | `accommodation.read` | — | `AmenityDto[] {id, code, name, category}` | 403, 404 | — |
| `GET /accommodation-types` | Types with capacity | `accommodation.read` | — | `AccommodationTypeDto[]` | 403, 404 | — |
| `GET /accommodation-types/{id}` | One type | `accommodation.read` | — | `AccommodationTypeDto` | 403, 404 | — |
| `POST /accommodation-types` | Create | `accommodation.manage` | `{ code, name, description?, inventoryMode: UNIT\|BED, baseOccupancy, maxOccupancy, sizeSqm?, sortOrder, amenityIds? }` | `201 AccommodationTypeDto` | 400, 403, 409 `TYPE_CODE_TAKEN`, 422 `UNKNOWN_AMENITY` | INSERT type + amenity links |
| `PUT /accommodation-types/{id}` | Update (code is fixed) | `accommodation.manage` | as create without `code`, plus `isActive, version` | `AccommodationTypeDto` | 400, 403, 404, 409 `CONCURRENCY_CONFLICT`, 422 `TYPE_HAS_BEDS` (mode change while beds exist), `TYPE_HAS_ACTIVE_UNITS` (deactivate with active rooms), `UNKNOWN_AMENITY` | UPDATE (old/new) |

`AccommodationTypeDto = { id, code, name, description, inventoryMode, baseOccupancy, maxOccupancy, sizeSqm, sortOrder, isActive,
amenityIds[], unitCount, sellableItems, maxGuests, version }`. `sellableItems` = active rooms (UNIT) or active beds in
active rooms (BED); `maxGuests` = what the whole type can sleep.
Validation: code `^[A-Za-z0-9_]{2,30}$` (stored upper-case); name ≤ 100; 1 ≤ base ≤ max ≤ 50; **BED types must have
base = max = 1**; size > 0.

### Rooms and beds

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /rooms` | All rooms with their beds | `accommodation.read` | — | `RoomDto[]` | 403, 404 | — |
| `GET /rooms/{id}` | One room | `accommodation.read` | — | `RoomDto` | 403, 404 | — |
| `POST /rooms` | Create | `accommodation.manage` | `{ code, name, accommodationTypeId, floor?, notes?, sortOrder }` | `201 RoomDto` | 400, 403, 409 `ROOM_CODE_TAKEN`, 422 `UNKNOWN_TYPE`/`TYPE_INACTIVE` | INSERT |
| `PUT /rooms/{id}` | Update; deactivate with `isActive=false` (rooms are never deleted) | `accommodation.manage` | `{ name, accommodationTypeId, floor?, notes?, sortOrder, isActive, version }` | `RoomDto` | 400, 403, 404, 409, 422 `UNIT_HAS_BEDS` (moving a dorm with beds to a per-room type), `UNKNOWN_TYPE`, `TYPE_INACTIVE` | UPDATE |
| `GET /rooms/{id}/beds` | Beds of a dormitory | `accommodation.read` | — | `BedDto[]` | 403, 404 | — |
| `POST /rooms/{id}/beds` | Add a bed | `accommodation.manage` | `{ code, name, bedType: SINGLE\|BUNK_LOWER\|BUNK_UPPER\|DOUBLE, sortOrder }` | `201 BedDto` | 400, 403, 404, 409 `BED_CODE_TAKEN`, 422 `BEDS_NOT_ALLOWED` (room is sold per room) | INSERT |
| `PUT /beds/{id}` | Update; deactivate a broken bed | `accommodation.manage` | `{ name, bedType, sortOrder, isActive, version }` | `BedDto` | 400, 403, 404, 409 | UPDATE |
| `GET /rooms/status?date=` | Status board for one night (default: property's today) | `accommodation.read` | — | `RoomStatusBoardDto { date, rooms: [{ unitId, code, name, typeName, inventoryMode, status, activeBeds, blockedBeds, block?, beds[] }] }` | 403, 404 | — |

Room `status`: `AVAILABLE`, `BLOCKED` (whole room or dorm), `PARTLY_BLOCKED` (some dorm beds), `INACTIVE`.
Occupancy is added in Sprint 5 and housekeeping states in Sprint 13.

### Blocks — `/blocks`

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /blocks?from&to&unitId&includeReleased` | Active blocks (default: ending after today) | `accommodation.read` | — | `UnitBlockDto[]` | 403, 404 | — |
| `POST /blocks` | Take a room, a whole dorm or one dorm bed out of sale | `unit_block.manage` | `{ unitId, bedId?, blockType: MAINTENANCE\|OUT_OF_ORDER\|OWNER_USE\|HOLD\|OTHER, startDate, endDate, reason }` | `201 UnitBlockDto` | 400, 403, 404, **409 `BLOCK_OVERLAP`** (or `OVERLAP` from the database guard), 422 `BLOCK_IN_PAST`, `UNKNOWN_ROOM`, `ROOM_INACTIVE`, `UNKNOWN_BED`, `BED_INACTIVE`, `BEDS_NOT_ALLOWED` | INSERT with the reason |
| `POST /blocks/{id}/release` | End a block early | `unit_block.manage` | `{ reason }` | `UnitBlockDto` | 400, 403, 404, 422 `BLOCK_NOT_ACTIVE` | UPDATE with the reason |

`UnitBlockDto = { id, unitId, unitCode, unitName, bedId, bedCode, blockType, startDate, endDate, nights, reason, status,
createdAt, createdByName, releasedAt, releaseReason }`. Validation: end > start; ≤ 366 nights; end after the property's
today; reason ≥ 3 characters (trimmed) and ≤ 500. Blocks are never deleted (a trigger refuses DELETE).

Overlap rules: two active blocks on the same room can't overlap; two on the same bed can't overlap; a whole-dorm block
can't overlap any bed block in that dorm. The API checks first under a row lock and returns `BLOCK_OVERLAP` with the
existing block's dates and reason. PostgreSQL `EXCLUDE` constraints and a trigger are the final guard.

## Built endpoints (Sprint 3 — guests)

Under `/api/v1/guests`. Guests belong to the **organisation** and are shared by all its properties, so a grant for
**any** property is enough (scope **any**). Exceptions: anonymise and the retention report need `guest.delete`
**organisation-wide**. Another organisation's guest is 404. Default holders: `guest.read` — Owner, GM, Manager,
Receptionist, Restaurant Manager, Waiter, Activity Manager; `guest.create` / `guest.update` — Owner, GM, Manager,
Receptionist; `guest.delete` — Owner, GM; `guest_document.read` — Owner, GM, Manager, Receptionist.

### Profiles

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /guests?q&includeInactive&page&pageSize` | List / search. `q` that looks like a phone (≥ 4 digits) matches any part of the number, typed in any format; otherwise name, email or company contains `q` | `guest.read` (any) | — | `PagedResult<GuestListItemDto>` | 403 | — |
| `GET /guests/possible-duplicates?firstName&lastName&phone&email&excludeId` | Profiles that may be the same person, with reasons | `guest.read` (any) | — | `PossibleDuplicateDto[] {id, fullName, phone, email, nationality, reasons[]}` (`Same phone`, `Same email`, `Same name`, `Same ID document`) | 403 | — |
| `POST /guests` | Create | `guest.create` (any) | `CreateGuestRequest` (below) | `201 GuestDto` | 400, 403, **409 `POSSIBLE_DUPLICATE`** with the matches in `details` (send again with `ignoreDuplicates: true` to create anyway) | INSERT (personal fields masked) |
| `GET /guests/{id}` | One profile | `guest.read` (any) | — | `GuestDto` (`documentCount` is `null` without `guest_document.read`) | 403, 404 | — |
| `PUT /guests/{id}` | Update; address and preferences are replaced as a set | `guest.update` (any) | as create, plus `version`, without `ignoreDuplicates` | `GuestDto` | 400, 403, 404, 409 `CONCURRENCY_CONFLICT`, 422 `GUEST_MERGED` / `GUEST_ANONYMISED` | UPDATE (personal fields masked) |
| `GET /guests/{id}/stays` | Stays of the guest | `guest.read` (any) | — | `GuestStayDto[]` (empty until reservations, Sprint 5) | 403, 404 | — |
| `POST /guests/{id}/consents` | Record a consent answer (history is kept) | `guest.update` (any) | `{ purpose: MARKETING_WHATSAPP\|MARKETING_EMAIL\|DATA_PROCESSING, granted, source: FRONT_DESK\|PHONE\|WEBSITE\|OTA\|EMAIL }` | `GuestDto` | 400, 403, 404, 422 | INSERT |
| `POST /guests/merge` | Fold a duplicate into a survivor | `guest.update` (any) | `{ survivorId, survivorVersion, duplicateId, duplicateVersion, reason }` | `GuestDto` (survivor) | 400, 403, 404, 409 `CONCURRENCY_CONFLICT`, 422 `SAME_GUEST`, `GUEST_MERGED`, `GUEST_ANONYMISED` | UPDATEs with the reason |
| `POST /guests/{id}/anonymise` | Right to be forgotten: remove personal data, purge documents and scans; keep the row | `guest.delete` (org-wide) | `{ reason }` (3–500) | `GuestDto` (`status: ANONYMISED`) | 400, 403, 404, 422 `ALREADY_ANONYMISED`, `GUEST_MERGED` | UPDATE/DELETE rows with the reason |
| `GET /guests/retention/dry-run` | ID documents the retention job **would** purge (nothing is deleted) | `guest.delete` (org-wide) | — | `RetentionReportDto { retentionDays, generatedAt, documentCount, items[] }` | 403 | — |

`CreateGuestRequest = { firstName, lastName?, phone?, email?, dateOfBirth?, nationality?, companyName?, gstin?, notes?,
isVip, address?: { line1?, line2?, city?, state?, stateCode?, postalCode?, countryCode }, preferences?: [{ category:
ROOM|FOOD|DIETARY|ACTIVITY|OTHER, value }], ignoreDuplicates? }`.

`GuestDto = { id, firstName, lastName, fullName, phone, email, dateOfBirth, nationality, companyName, gstin, notes, isVip,
status: ACTIVE|MERGED|ANONYMISED, mergedIntoId, anonymisedAt, address, preferences[], consents[] (latest per purpose:
{purpose, granted, source, recordedAt, recordedByName}), documentCount, createdAt, updatedAt, version }`.

Validation: first name 1–100; **phone** is normalised to E.164: Indian numbers may be typed locally (`98200 12345`,
`098200 12345`, `91 98200 12345`), others need `+` or `00` and the country code; anything else is a 400 with a field
message. Email valid, stored lower-case. Date of birth 1900–today. Nationality and countries: 2-letter ISO codes.
GSTIN `^[0-9]{2}[A-Z0-9]{13}$`. Indian PIN 6 digits. Notes ≤ 2000. Up to 30 preferences of 1–500 characters.

### ID documents (sensitive) — `guest_document.read` for every call

| Method & URL | Purpose | Request | Response | Errors | Audit |
|---|---|---|---|---|---|
| `GET /guests/{id}/documents` | List, numbers masked | — | `GuestDocumentDto[] { id, documentType, maskedNumber ("•••• 4567"), issuingCountry, expiresOn, hasFile, fileName, contentType, sizeBytes, status, createdAt, createdByName }` | 403, 404 | — |
| `POST /guests/{id}/documents` | Add a document. **multipart/form-data**: `documentType` (PASSPORT\|AADHAAR\|DRIVING_LICENCE\|VOTER_ID\|PAN\|VISA\|OTHER), `number?`, `issuingCountry?`, `expiresOn?` (YYYY-MM-DD), `details?` (JSON object, e.g. `{"visaNumber":"…"}`), `file?` (JPEG, PNG or PDF ≤ 5 MB) | form | `201 GuestDocumentDto` | 400, 403, 404, 422 `DOCUMENT_EMPTY` (no number and no file), `FILE_TYPE` (checked from the bytes, not the name), `FILE_SIZE`, `GUEST_NOT_ACTIVE` | INSERT (number, ciphertext and file name masked) |
| `GET /guests/{id}/documents/{docId}` | Open one: decrypted `number` and `details` | — | `GuestDocumentDetailDto` | 403, 404, 422 `DOCUMENT_PURGED` | **`SENSITIVE_READ`** (who, when, IP, device) |
| `GET /guests/{id}/documents/{docId}/file` | The decrypted scan | — | the file (`Content-Type` image/jpeg, image/png or application/pdf) | 403, 404, 422 `DOCUMENT_PURGED` | **`SENSITIVE_READ`** |

The number and details are encrypted with AES-256-GCM before they reach the database, and the scan before it reaches
file storage ([SECURITY.md → Field encryption](SECURITY.md#field-encryption-and-key-rotation)). **Aadhaar:** only the last 4
digits are stored (masked Aadhaar); `number` in the detail is `null`. Every guest audit entry names the changed fields
but shows `***` instead of personal values, so anonymising really removes the data.

## Built endpoints (Sprint 4 — rate plans and packages)

All under `/api/v1/properties/{propertyId}`, scope **prop**. A property in another organisation is 404. Default holders:
`rate.read` — Owner, GM, Manager, Receptionist; `rate.manage` — Owner, GM. Money is a JSON number with at most 2
decimals (rupees and paise), never a float in storage (`numeric(12,2)`). Prices are **before tax** (GST: Sprint 9/12).
Dates are the property's local dates; a range `[from, to)` excludes `to`, like a check-out date.

### Rate plans — `/rate-plans`

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /rate-plans` | All plans | `rate.read` | — | `RatePlanDto[]` | 403, 404 | — |
| `GET /rate-plans/{id}` | One plan | `rate.read` | — | `RatePlanDto` | 403, 404 | — |
| `POST /rate-plans` | Create. The property's first plan becomes the default; `isDefault: true` moves the default here | `rate.manage` | `{ code, name, description?, mealPlan: EP\|CP\|MAP\|AP, isDefault, sortOrder }` | `201 RatePlanDto` | 400, 403, 409 `RATE_PLAN_CODE_TAKEN` | INSERT (+ UPDATE of the old default) |
| `PUT /rate-plans/{id}` | Update (code is fixed); deactivate with `isActive=false` | `rate.manage` | as create without `code`, plus `isActive, version` | `RatePlanDto` | 400 (`IsDefault`: the default can't be switched off — make another plan the default; `IsActive`: the default must stay active), 403, 404, 409 `CONCURRENCY_CONFLICT` | UPDATE (old/new) |

`RatePlanDto = { id, code, name, description, mealPlan, isDefault, sortOrder, isActive, version }`. Meal plans: EP room
only, CP + breakfast, MAP + breakfast and one meal, AP all meals. Plans are never deleted (a trigger refuses DELETE).

### Rate grid — `/rate-plans/{id}/prices`

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /rate-plans/{id}/prices?from&to` | The plan's prices for nights in `[from, to)` (≤ 366 nights), every room type | `rate.read` | — | `RatePriceDto[] { accommodationTypeId, date, price, extraAdult, extraChild, minStay, updatedAt }` | 400 (missing/reversed dates), 403, 404 | — |
| `PUT /rate-plans/{id}/prices` | **Bulk edit**: the same values on every chosen night for every chosen room type. Creates missing cells, changes different ones, leaves equal ones alone | `rate.manage` | `{ accommodationTypeIds[], from, to, daysOfWeek?: ["Friday","Saturday"…] (empty = every night), price, extraAdult, extraChild, minStay }` | `{ created, changed, unchanged }` | 400 with the reason under the field (below), 403, 404, 409 `DUPLICATE` if two people set the same new nights at the same moment | **one row per changed night, old and new values** |

`price` is per room per night for per-room types, and **per bed per night** for dormitory (BED) types.
`extraAdult` / `extraChild` are per guest per night above the type's standard occupancy (rooms only). `minStay` applies to
stays **arriving** on that night. Validation (each reason is returned under its field): at least one room type, ≤ 50;
`to` > `from`, ≤ 366 nights; money 0 … ₹1,00,00,000 in whole paise; min stay 1–30; extra prices must be 0 for a dormitory
type ("… is sold per bed, so extra adult and child prices don't apply to it"); the chosen days of the week must occur in
the range ("None of the nights from … to … falls on Sunday.").

### Quote — `/rates/quote`

| Method & URL | Purpose | Permission | Response | Errors |
|---|---|---|---|---|
| `GET /rates/quote?type\|room&ratePlan&from&to&adults&children` | Price one room (or the beds for a group in a dormitory) for every night of a stay | `rate.read` | `RateQuoteDto` | 400 under the field (`From`, `To`, `Adults`, `Room`, `Type`, `RatePlan`: missing, reversed, > 30 nights, unknown room/type/plan), 403, 404, **422** `RATE_NOT_SET` (`details.dates` lists every night without a price), `TOO_MANY_GUESTS`, `MIN_STAY_NOT_MET`, `NO_DEFAULT_RATE_PLAN`, `RATE_PLAN_INACTIVE`, `TYPE_INACTIVE`, `ROOM_INACTIVE` |

`type`, `room` and `ratePlan` take an id or a code (`room=103`). Without `ratePlan` the property's default plan is used.
`RateQuoteDto = { ratePlanId, ratePlanCode, ratePlanName, mealPlan, accommodationTypeId, accommodationTypeName,
inventoryMode, roomCode, from, to, nights, adults, children, units, breakdown: [{ date, units, unitPrice, baseAmount,
extraAdults, extraAdultRate, extraAdultAmount, extraChildren, extraChildRate, extraChildAmount, total }], total, currency }`.

Pricing rules (pure code in `Resort.Domain/Rates/QuoteEngine.cs`, unit-tested): rooms — the night's price covers the
standard guests; each adult above them pays the extra-adult rate; children first fill standard places the adults leave
free, then each pays the extra-child rate; adults + children may not exceed the type's maximum. Dormitories — every
guest takes a bed at the per-bed price, no extras. Messages name the plan, type and nights, e.g. *"Best available rate has
no price for Standard room on Fri 16 Oct 2026. Set the price in Settings → Rates."*

### Packages — `/packages`

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /packages` | All packages with their split | `rate.read` | — | `PackageDto[]` | 403, 404 | — |
| `GET /packages/{id}` | One package | `rate.read` | — | `PackageDto` | 403, 404 | — |
| `POST /packages` | Create | `rate.manage` | `{ code, name, description?, accommodationTypeId, nights, adults, children, price, sortOrder, components: [{ category: ROOM\|FOOD\|TRANSPORT\|ACTIVITY, description, sharePercent }] }` | `201 PackageDto` | 400 under the field (below), 403, 409 `PACKAGE_CODE_TAKEN` | INSERT package + components |
| `PUT /packages/{id}` | Update (code is fixed); components are replaced as a set; deactivate with `isActive=false` | `rate.manage` | as create without `code`, plus `isActive, version` | `PackageDto` | 400, 403, 404, 409 `CONCURRENCY_CONFLICT` | UPDATE (old/new), DELETE/INSERT of components |

`PackageDto = { id, code, name, description, accommodationTypeId, accommodationTypeName, nights, adults, children, price,
sortOrder, isActive, components: [{ id, category, description, sharePercent, amount, sortOrder }], revenueByCategory:
{ ROOM: …, FOOD: … }, version }`.

**Revenue split.** The manager gives each component a share in percent; the API computes each `amount` with the
largest-remainder rule ([DATABASE.md → Money](DATABASE.md#money-and-rounding)), so the amounts **always add up to exactly
the price**. Validation: 1–20 components; shares > 0, ≤ 100, 2 decimals, **adding up to exactly 100** ("The shares add up
to 95.5%; they must add up to exactly 100%." under `Components`); **a ROOM component is required**; each component's
reason under `Components[i].Description` etc.; price > 0; 1–30 nights; adults + children within the room type's maximum
("Standard room sleeps at most 2 guests; this package includes 3." under `Adults`).

### Reservation sources

| Method & URL | Purpose | Permission | Response |
|---|---|---|---|
| `GET /reservation-sources` | Where bookings come from (walk-in, phone, WhatsApp, website, OTAs, travel agent, corporate) | `rate.read` | `ReservationSourceDto[] { id, code, name, channel: DIRECT\|OTA\|TRAVEL_AGENT\|CORPORATE\|OTHER, commissionPercent }` |

Built-in sources are shared by every organisation; every reservation names one.

## Built endpoints (Sprint 5 — availability and reservations)

All under `/api/v1/properties/{propertyId}`, scope **prop**. A property in another organisation is 404. Default holders:
`reservation.read` — Owner, GM, Manager, Receptionist, Housekeeping supervisor, Restaurant and Activity managers;
`reservation.create`, `.update`, `.cancel` — Owner, GM, Manager, Receptionist. Dates are property-local; a stay is
`[checkIn, checkOut)` (the guest leaves on `checkOut`), 1–180 nights. Money as in Sprint 4 (before tax, 2 decimals).
How booking stays race-free: [architecture/booking-algorithm.md](architecture/booking-algorithm.md).

### Availability — `/availability`

| Method & URL | Purpose | Permission | Response | Errors |
|---|---|---|---|---|
| `GET /availability?from&to&adults&children` | Per active room type: rooms (or dorm beds) free for **every** night, whether the guests fit, the default plan's price, and the free rooms | `reservation.read` | `AvailabilityDto` | 400 under the field: `From` (missing, before today), `To` (missing, not after `from`, > 180 nights), `Adults` (< 1), `Children` |

`AvailabilityDto = { from, to, nights, adults, children, currency, types: [{ accommodationTypeId, code, name, inventoryMode,
baseOccupancy, maxOccupancy, total, free, fits, fitProblem, ratePlanId, ratePlanName, price, priceProblem, freeRooms: [{ id,
code, name, floor }] }] }`. A snapshot without locks: booking checks again. `fitProblem` says why the guests can't be booked
here ("Fully booked or blocked for these nights.", "Only 2 beds are free; 3 guests need 3."); `priceProblem` why there is
no price (nights without a price, minimum stay).

### Reservations — `/reservations`

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /reservations?q&status&from&to&page&pageSize` | List, soonest arrival first. Default: stays that haven't ended (`checkOut ≥ today`). `q` = reference, guest name, phone (≥ 4 digits) or email; `status` = comma list | `reservation.read` | — | `PagedResult<ReservationListItemDto>` | 400 `Status` (unknown status), 403, 404 | — |
| `GET /reservations/{id}` | One booking with rooms, nights, guests, notes, history | `reservation.read` | — | `ReservationDto` | 403, 404 | — |
| `POST /reservations` | **Create.** Header **`Idempotency-Key`** (8–100 chars) required | `reservation.create` | `CreateReservationRequest` | `201 ReservationDto` (replay: same body + header `Idempotent-Replayed: true`) | 400 under the field (below), 403, 404, **409 `ROOM_UNAVAILABLE`** (says which room/type and what holds it), 409 `IDEMPOTENCY_IN_PROGRESS`, 422 `IDEMPOTENCY_KEY_REUSED`, `RATE_NOT_SET`, `MIN_STAY_NOT_MET`, `RATE_PLAN_INACTIVE` | INSERT reservation, rooms, guests, nights, history |
| `PUT /reservations/{id}` | Change dates, rooms or guest counts, requests. Re-checks availability (own rooms kept when free) and re-prices | `reservation.update` | `UpdateReservationRequest` | `ReservationDto` | 400 under the field, 403, 404, 409 `ROOM_UNAVAILABLE`, 409 `CONCURRENCY_CONFLICT`, 422 `RESERVATION_NOT_CHANGEABLE` (checked-in or finished), `RATE_PLAN_INACTIVE` | UPDATE; old rooms superseded, new ones inserted |
| `POST /reservations/{id}/confirm` | INQUIRY/PENDING → CONFIRMED (an enquiry's rooms are checked again) | `reservation.update` | `{ reason? }` | `ReservationDto` | 403, 404, 409 `ROOM_UNAVAILABLE`, 422 `INVALID_STATUS_TRANSITION` | UPDATE + history |
| `POST /reservations/{id}/cancel` | → CANCELLED; frees the rooms at once | `reservation.cancel` | `{ reason }` (≥ 3 chars) | `ReservationDto` | 400 `Reason`, 403, 404, 422 `INVALID_STATUS_TRANSITION` | UPDATE + history with reason |
| `POST /reservations/{id}/no-show` | CONFIRMED → NO_SHOW, from the arrival day | `reservation.cancel` | `{ reason }` | `ReservationDto` | 400 `Reason`, 403, 404, 422 `INVALID_STATUS_TRANSITION`, `NO_SHOW_TOO_EARLY` | UPDATE + history with reason |
| `POST /reservations/{id}/notes` | Add a note | `reservation.update` | `{ note }` (1–2000) | `201 ReservationDto` | 400 `Note`, 403, 404 | INSERT |

`CreateReservationRequest = { checkIn, checkOut, rooms: [{ accommodationTypeId, unitId?, adults, children }], guestIds: [primary,
…others], sourceId, ratePlanId? (default plan), status?: CONFIRMED (default) | PENDING | INQUIRY, specialRequests?, note? }`.
A room line books one room of the type (`unitId` picks one, else the first free); a dormitory line books **one bed per
guest**. `UpdateReservationRequest = { checkIn, checkOut, rooms, specialRequests, version }`.

`ReservationDto = { id, reference (R00001), status, checkIn, checkOut, nights, adults, children, sourceId, sourceName, ratePlanId,
ratePlanName, mealPlan, totalAmount, currency, specialRequests, guests: [{ guestId, fullName, phone, email, isPrimary }],
units: [{ id, accommodationTypeId, accommodationTypeName, inventoryMode, unitId, unitCode, unitName, bedId, bedCode, startDate,
endDate, adults, children, holdsInventory, current, total }], breakdown: [{ reservationUnitId, date, units, unitPrice,
baseAmount, extraAdults, extraAdultRate, extraAdultAmount, extraChildren, extraChildRate, extraChildAmount, total }], notes:
[{ id, note, createdAt, createdByName }], history: [{ fromStatus, toStatus, reason, changedAt, changedByName }],
nextStatuses: [...], createdAt, createdByName, version }`.

**Field errors** (400, each under its field): `Idempotency-Key` (missing, wrong length); `CheckIn` (before today);
`CheckOut` (not after check-in, > 180 nights); `Rooms` (none, > 20); `Rooms[i].AccommodationTypeId` (unknown, inactive);
`Rooms[i].UnitId` (unknown, another type); `Rooms[i].Adults` ("Deluxe room sleeps at most 3 guests; this room has 4. Add
another room for the others."); `GuestIds` (none, unknown, merged or anonymised profile, repeated); `SourceId`;
`RatePlanId`; `Status` (only CONFIRMED, PENDING or INQUIRY at creation); `SpecialRequests`, `Note` (≤ 2000).

**Statuses.** PENDING, CONFIRMED and CHECKED_IN hold rooms; INQUIRY holds nothing. Allowed moves are in the table
`reservation_status_transitions`; anything else is 422 `INVALID_STATUS_TRANSITION` with `details: { from, to, allowed }`.
Check-in/check-out arrived in Sprint 7 (below), refunds in Sprint 10. Reservations are never deleted; an enquiry is cancelled.

**Related changes.** `POST /blocks` returns 409 `BLOCK_OVERLAPS_RESERVATION` ("Room 103 is booked on R00002 …") when a
booking holds the room. `GET /guests/{id}/stays` lists real reservations with their `reference`; merging guests moves
their reservations to the surviving profile.

## Built endpoints (Sprint 6 — reservation calendar)

Same scope and permissions as Sprint 5 (no new permission): reading the calendar needs `reservation.read`, moving a
guest needs `reservation.update`. How a move stays race-free: [system-design.md → 4.16](architecture/system-design.md#416-moving-a-guest-to-another-room).

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /calendar?from&to` | Rooms (and dorm beds) as rows, the bookings and active blocks in `[from, to)`, at most 62 days. Bookings: current rows of PENDING, CONFIRMED, CHECKED_IN and CHECKED_OUT reservations (enquiries hold nothing and are not drawn). Real dates are returned; the client clips to the window | `reservation.read` | — | `CalendarDto` | 400 `From` (missing), `To` (missing, not after `from`, more than 62 days: "The calendar shows at most 62 days at a time; this window is 63."), 403, 404 | — |
| `POST /reservations/{id}/reassign` | Move one room (or dorm bed) of a booking to another for the same nights. The price stays as booked; a note "Moved from Room 101 to Room 102 (…). Reason: …" is added | `reservation.update` | `ReassignRoomRequest` | `ReservationDto` (new `version`; the old room row is `current: false`) | 400 under the field (below), 403, 404, **409 `ROOM_UNAVAILABLE`** ("Room 104 isn't free for the nights of 15 Oct – 16 Oct 2026: it is blocked (AC repair). Choose another room."), 409 `CONCURRENCY_CONFLICT` (the booking changed since it was shown), 422 `RESERVATION_NOT_MOVABLE` (checked in, cancelled, finished) | UPDATE old room row + INSERT new one, both with the reason; UPDATE reservation |

`CalendarDto = { from, to, today, rooms: [{ id, code, name, accommodationTypeId, accommodationTypeName, inventoryMode,
maxOccupancy, floor, isActive, beds: [{ id, code, name, isActive }] }], bookings: [{ reservationUnitId, reservationId,
reference, status, unitId, bedId, startDate, endDate, guestName, adults, children, sourceName, paymentStatus, totalAmount,
version }], blocks: [{ id, unitId, bedId, blockType, startDate, endDate, reason }] }`. `paymentStatus` is `NONE` until
payments are recorded (Sprint 10), then `PARTIAL` or `PAID` (paid net of refunds against the booking's bills).

`ReassignRoomRequest = { reservationUnitId, unitId, bedId?, reason, version }`. **Field errors** (400, each under its
field): `Reason` (missing: "Say why the guest is moving (at least 3 characters), e.g. AC not working in 102.", < 3, > 500);
`ReservationUnitId` (missing, or no longer part of the booking); `UnitId` (missing, unknown, inactive, the guest is already
there, a dormitory for a room booking or a private room for a bed guest, too small: "Room 103 (Standard room) sleeps at
most 2; this room has 3 guests. Choose a bigger room."); `BedId` (missing for a dormitory, not in that dormitory,
inactive, given for a private room). A whole room line moves for all its nights; checked-in guests move with the front
desk (Sprint 7).

## Built endpoints (Sprint 7 — front desk)

All under `/api/v1/properties/{propertyId}`. **"Today" is the property's business date** (its timezone, from an injected
clock), never the server's. Lists need `reservation.read`; check-in, room assignment and walk-ins need
`frontdesk.checkin`; check-out `frontdesk.checkout`; marking a room `housekeeping.update`. A walk-in also needs
`reservation.create` (and `guest.create` for a new guest). How check-in stays race-free:
[system-design.md → 4.17](architecture/system-design.md#417-the-front-desk).

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /front-desk/arrivals?date` | Bookings arriving on `date` (default today); on today also earlier arrivals that haven't come yet (`late: true`). PENDING, CONFIRMED, CHECKED_IN and CHECKED_OUT | `reservation.read` | — | `FrontDeskListDto` | 403, 404 | — |
| `GET /front-desk/departures?date` | Bookings leaving on `date`; on today also guests still in after their departure day (`overdue: true`) | `reservation.read` | — | `FrontDeskListDto` | 403, 404 | — |
| `GET /front-desk/in-house` | Every CHECKED_IN booking | `reservation.read` | — | `FrontDeskListDto` | 403, 404 | — |
| `GET /front-desk/dashboard` | Counts for today (arrivals, departures, overdue, in house, rooms ready / not ready / occupied / blocked) and the rooms board: every room and dorm bed with occupancy (`VACANT`, `OCCUPIED`, `ARRIVING`, `BLOCKED`, `INACTIVE`) and cleaning state | `reservation.read` | — | `FrontDeskDashboardDto` | 403, 404 | — |
| `GET /reservations/{id}/room-options?reservationUnitId` | Every room (or bed) the row could move to, with `free`, `fits`, `housekeeping` and the `problem` in words | `reservation.read` | — | `RoomOptionDto[]` | 400 `ReservationUnitId`, 403, 404 | — |
| `POST /reservations/{id}/assign-room` | Before arrival: the room for the whole stay (price unchanged). In house: a move **from today**; the nights already slept stay on the first room, which becomes DIRTY (`ROOM_MOVE`). Reason required in house | `frontdesk.checkin` | `AssignRoomRequest` | `ReservationDto` | 400 under the field, 403, 404, 409 `ROOM_UNAVAILABLE`, `ROOM_OCCUPIED`, `HOUSEKEEPING_NOT_READY` (target not ready and no override), `CONCURRENCY_CONFLICT`; 422 `RESERVATION_NOT_MOVABLE` | as Sprint 6 reassign, plus housekeeping rows |
| `POST /reservations/{id}/check-in` | Locks the booking, then its rooms; checks the date, that the previous guest has left, and that the room is READY. PENDING is confirmed on the way in. Sets `checkedInAt` | `frontdesk.checkin` | `CheckInRequest` | `ReservationDto` | 403, 404; **409** `ALREADY_CHECKED_IN` ("R00001 is already checked in (at 14:05 on Sat 17 Oct 2026 by Rita). Reload to see it."), `ROOM_OCCUPIED` ("Room 105 is still occupied: R00007 (Asha Rao) was due out … and hasn't checked out. Check them out first, or assign this guest another room."), `HOUSEKEEPING_NOT_READY` ("Room 101 is dirty, not ready. Ask housekeeping to mark it ready, assign a ready room, or check in anyway with a reason."), `CONCURRENCY_CONFLICT`; **422** `CHECK_IN_DATE` ("R00002 arrives on Sun 18 Oct 2026; it is Sat 17 Oct 2026 at the property. Change the arrival date …"), `INVALID_STATUS_TRANSITION`, `OVERRIDE_NOT_ALLOWED`; 400 `OverrideReason` | UPDATE reservation, status history (with the override reason), note when overridden |
| `POST /reservations/{id}/check-out` | Refused while the folio balance is above zero unless the property allows it. Leaving before the booked day needs a reason; the nights from the departure day are released and the total re-priced. The rooms being left become DIRTY (`CHECK_OUT` events). Sets `checkedOutAt` | `frontdesk.checkout` | `CheckOutRequest` | `ReservationDto` | 400 `Reason` ("The guest is booked until …. Say why they are leaving early …"), 403, 404, 409 `ALREADY_CHECKED_OUT`, `CONCURRENCY_CONFLICT`; 422 `BALANCE_DUE` ("R00007 still owes ₹2,000. Take the payment before check-out, or ask a manager …"), `INVALID_STATUS_TRANSITION` | UPDATE reservation, status history, note when early, housekeeping rows |
| `POST /walk-ins` | Guest (existing `guestId` or `newGuest`) + booking from today (source Walk-in, ready rooms first) + check-in, **one transaction**: if any step fails nothing is saved. `Idempotency-Key` required; a repeat returns the same stay with `Idempotent-Replayed: true` | `frontdesk.checkin` (+ `reservation.create`, `guest.create`) | `WalkInRequest` | 201 `ReservationDto` | 400 under the field (`GuestId`: "Choose a returning guest, or enter the new guest's details (one of the two).", `NewGuest.*` as Create guest, `CheckOut`: "Check-out must be after today (… at the property).", `Rooms`, …), 409 `POSSIBLE_DUPLICATE` (with the matches; resend with `newGuest.ignoreDuplicates: true`), `ROOM_UNAVAILABLE`, `HOUSEKEEPING_NOT_READY`; 422 `IDEMPOTENCY_KEY_REUSED` | as guest create + booking + check-in |
| `PUT /rooms/{unitId}/housekeeping` | Set a room's (or a dorm bed's, with `bedId`) cleaning state by hand, e.g. *ready*. The full housekeeping workflow comes in Sprint 13 | `housekeeping.update` | `SetHousekeepingRequest` | `FrontDeskBoardRoomDto` | 400 `Status` (not one of the six), `BedId`, 403, 404 | UPDATE/INSERT state + history row (`MANUAL`) |

`FrontDeskListDto = { date, today, items: FrontDeskStayDto[] }`. `FrontDeskStayDto = { reservationId, reference, status,
guestId, guestName, guestPhone, isVip, checkIn, checkOut, nights, adults, children, rooms: [{ reservationUnitId, unitId,
unitCode, unitName, bedId, bedCode, typeName, startDate, endDate, housekeeping, occupiedBy }], sourceName, totalAmount,
balance, currency, specialRequests, checkedInAt, checkedOutAt, late, overdue, checkInProblem, version }`. `checkInProblem`
says in words why a check-in would be refused right now (e.g. "Room 101 is dirty, not ready."); `balance` is 0 until
folios arrive (Sprint 8).

`AssignRoomRequest = { reservationUnitId, unitId, bedId?, reason?, version, overrideHousekeeping? }` ·
`CheckInRequest = { version?, overrideHousekeeping?, overrideReason? }` · `CheckOutRequest = { version?, reason? }` ·
`WalkInRequest = { guestId? | newGuest?, checkOut, rooms: [{ accommodationTypeId, unitId?, adults, children }],
ratePlanId?, specialRequests?, note?, overrideHousekeeping?, overrideReason? }` ·
`SetHousekeepingRequest = { bedId?, status: DIRTY | CLEANING | CLEAN | INSPECTION | READY | OUT_OF_ORDER, reason? }`.
`version` is optional on check-in and check-out (the front desk acts on what it sees); when sent, an older version is
409 `CONCURRENCY_CONFLICT`.

**Property settings** (`PUT /settings/{key}`, JSON `true`/`false`): `frontdesk.allow_checkin_when_not_ready` (default
`true`: reception may check into a room that isn't ready, with a reason) and `frontdesk.allow_checkout_with_balance`
(default `false`).

## Built endpoints (Sprint 8 — folio)

All under `/api/v1/properties/{propertyId}`. A folio is the bill of one stay; every confirmed booking has a main folio
("Main", F00001 …), opened when it is confirmed. **Lines are never edited or deleted**: a mistake is corrected with a
reversal line, and moving a line to another folio is a reversal here plus a copy there. Reading needs `folio.read`,
posting and moving lines `folio.post_charge`, reversing `folio.adjust` (owner, general manager and manager). Totals are
summed from the lines; GST is added from Sprint 9 and payments from Sprint 10 (below). How it stays consistent:
[system-design.md → 4.18](architecture/system-design.md#418-folios-the-bill-of-a-stay).

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /folios?q&show&page&pageSize` | Folios with their booking, total and balance. `show`: `in-house` (default), `open` (in house and arriving), `all`; `q` searches the folio number, booking reference, guest name or phone | `folio.read` | — | `PagedResult<FolioListItemDto>` | 400 `Show` ("'x' isn't a folio filter. Use in-house, open or all."), 403, 404 | — |
| `GET /reservations/{reservationId}/folios` | The folios of one booking, main first (the Bill buttons on the booking page and the front desk) | `folio.read` | — | `FolioSummaryDto[]` | 403, 404 | — |
| `GET /folios/{id}` | One bill: every line in order, sums by category, charges, reversals, tax, total, payments, balance, the stay's other folios, and whether it takes charges | `folio.read` | — | `FolioDto` | 403, 404 | — |
| `POST /folios/{id}/charges` | Post a charge, or sell a package (one line per component, adding up to the package price). `Idempotency-Key` required; a repeat returns the same bill with `Idempotent-Replayed: true` | `folio.post_charge` | `PostChargeRequest` | 201 `FolioDto` | 400 under the field (below), 403, 404; **422 `FOLIO_CLOSED`** ("R00005 is cancelled, so nothing can be charged to F00004."), `IDEMPOTENCY_KEY_REUSED` | INSERT folio item(s) |
| `POST /folios/{id}/charges/{itemId}/reverse` | Cancel a charge with a reversal line (same quantity and price, negated) and the reason. The charge stays on the bill, marked reversed | `folio.adjust` | `ReverseChargeRequest` | `FolioDto` | 400 `Reason` ("Say why the charge is reversed (at least 3 characters), e.g. Posted to the wrong guest."), 403, 404; **409 `ALREADY_REVERSED`** ("\"Airport transfer\" was already reversed at 14:05 on Sun 18 Oct 2026 by Rita (reason: …). Reload to see it." or "… was moved to F00007 at …. Open F00007 to change it."); 422 `NOT_REVERSIBLE` (a reversal line), `FOLIO_CLOSED` | INSERT reversal, with the reason |
| `POST /folios/{id}/split` | Move charges to another folio of the same stay, or to a new one (`newFolioName`, optional `billToGuestId`). Each line is reversed here ("Moved to F00007 (…)") and posted there; the stay's total doesn't change | `folio.post_charge` | `SplitFolioRequest` | `SplitFolioResultDto` | 400 `ItemIds` ("Choose at least one line to move.", not on this folio, a reversal, already reversed or moved), `TargetFolioId` ("Choose the folio to move the lines to, or name a new one (one of the two).", the same folio, another booking's), `NewFolioName`, `BillToGuestId`; 403, 404; 422 `FOLIO_CLOSED` | INSERT folio (when new), reversals and copies |

`PostChargeRequest = { category?, description?, quantity = 1, unitPrice = 0, serviceDate?, packageId? }`. **Field
errors** (400, each under its field), for a charge: `Category` ("Choose what the charge is for: Room, Food, Beverage,
Activity, Transport, Equipment, Laundry, Other."), `Description` ("Say what the charge is for, e.g. Dinner at the beach
shack.", > 200), `Quantity` (> 0, at most 9,999, 3 decimals), `UnitPrice` (as every price: not negative, 2 decimals; and
quantity × price at most ₹1,00,00,000); for a package: `PackageId` (unknown, switched off), `Quantity` ("A package is
sold whole: enter how many, 1 to 50."); for both: `ServiceDate` (default today at the property; "A charge can't be dated
after today (…). Post it on the day of the service.", or more than 30 days before arrival).
`ReverseChargeRequest = { reason }` · `SplitFolioRequest = { itemIds, targetFolioId? | newFolioName?, billToGuestId? }`.

`FolioDto = { id, number, name, isPrimary, reservationId, reference, reservationStatus, guestId, guestName,
billToGuestId, billToName, checkIn, checkOut, rooms, currency, items: FolioItemDto[], categories: [{ category, amount }],
charges, reversals, tax, total, payments, balance, folios: [{ id, number, name, isPrimary, total, balance }], canPost,
cantPostReason }`. `FolioItemDto = { id, itemType: CHARGE | REVERSAL, category, description, serviceDate, quantity,
unitPrice, amount, taxAmount, total, source: MANUAL | ROOM_NIGHT | PACKAGE, packageId, packageName, groupId,
reversesItemId, reversedByItemId, reason, reversalReason, movedFrom, movedTo, postedAt, postedByName, live }`. `live` is a
charge that still counts (not reversed or moved). A booking takes charges while CONFIRMED, CHECKED_IN or CHECKED_OUT
(late bar bills after check-out); otherwise `canPost` is false and `cantPostReason` says why.

**Room nights** are posted by the system (`source: ROOM_NIGHT`, one line per night with all the booking's rooms, at the
booked prices): at check-in for the whole stay (property setting `folio.room_charge_posting` = `"AT_CHECK_IN"`, the
default) or one night at a time (`"NIGHTLY"`, by a job that runs every 15 minutes). A night is never charged twice. At
check-out any night still missing is posted, and when a guest leaves early the released nights are reversed ("Left early
on …: reason") before the balance is checked. From this sprint the front desk's `balance` (Sprint 7) is the main folio's
balance.

## Built endpoints (Sprint 9 — GST and the tax engine)

No GST rate, threshold or SAC code is in the code: they are **rules with dates** in the database, so an accountant adds
next month's rate here and bills use it from its first day, with no deployment. One engine (`TaxCalculator`) taxes every
charge; the folio stores the rule id and the rate it used with each line, so a later rule never changes a posted
charge. Reading needs `tax.read` (owner, general manager, manager, accountant); changing categories and rules needs
`tax.manage` **organisation-wide** (they apply to every property); a registration needs `tax.manage` for its property.
Nothing here is deleted. How it works: [GST.md](GST.md) (worked examples) and
[system-design.md → 4.19](architecture/system-design.md#419-gst-rules-as-data-the-tax-engine).

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /properties/{propertyId}/gst-registrations` | The GSTINs the property bills under, newest first; `current` = it covers today | `tax.read` | — | `GstRegistrationDto[]` | 403, 404 | — |
| `POST /properties/{propertyId}/gst-registrations` | Add a registration from a date. A registration that runs on past that day is ended the day before. A property with no GST state takes the GSTIN's | `tax.manage` | `CreateGstRegistrationRequest` | 201 `GstRegistrationDto` | 400 under the field (below), 403, 404 | INSERT registration (and UPDATE of the one it ends) |
| `GET /tax/categories` | What is taxed: code, name, SAC/HSN, the bill charge category it covers, how many rules, and the rule that applies today | `tax.read` (any scope) | — | `TaxCategoryDto[]` | 403 | — |
| `POST /tax/categories` | Add a category | `tax.manage` (org-wide) | `CreateTaxCategoryRequest` | 201 `TaxCategoryDto` | 400 `Code` ("The code SPA is already used by Spa. Choose another code."), `Name`, `HsnSac` ("Enter the SAC or HSN code: 4, 6 or 8 digits, e.g. 996311 for accommodation."), `ChargeCategory` ("Food charges are already taxed under Restaurant service. A charge category can belong to one tax category only."), 403 | INSERT category |
| `GET /tax/rules?categoryId` | Every rule (of one category): dates, type, rates, source, notes, `state` PAST / CURRENT / FUTURE (by today in India), who verified it | `tax.read` (any scope) | — | `TaxRuleDto[]` | 403 | — |
| `POST /tax/rules` | Add a rule from `effectiveFrom` (today in India or later). The category's rule that runs on past that day is ended the day before (`closedRule` in the result); any other overlap is refused | `tax.manage` (org-wide) | `CreateTaxRuleRequest` | 201 `CreateTaxRuleResultDto` | 400 under the field (below), 403 | INSERT rule and rates (and UPDATE of the rule it ends) |
| `POST /tax/rules/{id}/verify` | Record that the caller checked the rule against its source (seeded rules start unverified). Verifying again changes nothing | `tax.manage` (org-wide) | — | `TaxRuleDto` | 403, 404 | UPDATE rule |
| `POST /tax/calculate` | Preview the GST on a charge on a day, with the same engine the bills use. Changes nothing | `tax.read` for the property | `TaxCalculationRequest` | `TaxCalculationDto` | 400 `PropertyId`, `TaxCategoryId`, `ChargeCategory`, `Amount`, `UnitValue`, `PlaceOfSupply`; 403 | — |

`CreateGstRegistrationRequest = { gstin, legalName, tradeName?, registrationType = REGULAR | COMPOSITION, effectiveFrom,
effectiveTo? }`. **Field errors**: `Gstin` says exactly what is wrong ("A GSTIN has 15 characters; this one has 14.",
"99 isn't a GST state code.", "Characters 3 to 12 are the PAN …", "Character 14 of a GSTIN is always Z.", "The last
character (check digit) should be 9 for this GSTIN; check it for a typing mistake.", "This GSTIN is registered in
Maharashtra (27), but Main Resort is in Goa (30). A property bills under the GSTIN of its own state."), `LegalName`,
`RegistrationType`, `EffectiveFrom` (required; "Main Resort is already registered under 30AABCT1234C1Z9 from Sat 1 Jul
2017 with no end date, which overlaps. Start this registration after …"), `EffectiveTo` (before the start).

`CreateTaxRuleRequest = { taxCategoryId, effectiveFrom, effectiveTo?, ruleType: FLAT | SLAB, slabs: [{ upTo?, rate,
inputTaxCredit }], source, notes? }`. A FLAT rule has one rate with `upTo` null; a SLAB rule two or more, exactly one
with `upTo` null (everything above the other limits). A value **exactly at a limit is in that slab** (₹7,500.00 is "up to
₹7,500"). **Field errors**: `EffectiveFrom` ("A new rule can start today (Sun 18 Oct 2026) or later. Charges already
posted keep the GST they were posted with …", "Restaurant service already has a rule from Sun 1 Nov 2026 with no end
date, which overlaps. Start the new rule after …"), `EffectiveTo`, `RuleType`, `Source` ("Say where the rule comes from,
e.g. Notification 05/2025-Central Tax (Rate), so it can be checked later."), `Slabs` ("A slab rule needs at least two
rates …", "A flat rule has one rate for any value …", "Exactly one rate must have no upper limit …", "Two rates have the
same upper limit …"), `Slabs[i].Rate` (0–100; "The rate is split evenly into CGST and SGST, so it must be a multiple of
0.02% (e.g. 5, 18 or 0.1)."), `Slabs[i].UpTo`.

`TaxCalculationRequest = { propertyId, taxCategoryId? | chargeCategory?, date? (default today at the property), amount,
unitValue? (the value of one unit for one night, which picks the slab; default amount), placeOfSupply? (a state code;
default the property's state) }` → `TaxCalculationDto = { date, amount, unitValue, taxed, noTaxReason, gstin,
supplierState, placeOfSupply, intraState, category, rule, slab, components: [{ code, rate, taxableValue, amount }], tax,
total }`. When `taxed` is false, `noTaxReason` says why: "Transport charges carry no GST: no tax category covers them.
…", "Beach Annex has no GST registration on …, so it charges no GST. …", "… is under the composition scheme on … : it
charges guests no GST.", "There is no GST rule for Equipment rental on …. Ask your accountant to add one in Settings →
GST."

**On the bill (Sprint 8 endpoints).** Every new charge is taxed when it is posted: `taxAmount` is the line's GST and
`total = amount + taxAmount`; `FolioItemDto.taxes = [{ code: CGST | SGST | IGST, rate, taxableValue, amount, taxRuleId }]`
(CGST and SGST each at half the rate). A room night is taxed room by room at the slab of each room's price for that
night; a package component is taxed in its own category, the room part at its value per night. A reversal or a moved
line carries the same taxes as its charge (negated for a reversal). Posting a charge in a taxed category on a day no
rule covers, at a registered property, is refused: **422 `TAX_RULE_MISSING`** ("There is no GST rule for Equipment
rental on Sun 18 Oct 2026. Ask your accountant to add one in Settings → GST. Until then equipment charges dated Sun 18
Oct 2026 can't be posted.").

## Built endpoints (Sprint 10 — payments, refunds, cash drawer)

All under `/api/v1/properties/{propertyId}`. Money taken at the property: cash, UPI, card (on the property's own
terminal) and bank transfer. A desk payment is `CAPTURED` when it is recorded (the money is in hand); the gateway states
are for Razorpay (Sprint 11). **Nothing is edited or deleted**: a refund and a void are new records with their reasons.
**No card data, ever**: there is no field for a card number, and anything that looks like one (13–19 digits in a row
passing the Luhn check, spaces and dashes ignored) is refused under its field. Taking and refunding money need an
`Idempotency-Key`, so a double click or a retry can't move money twice; a repeat returns the first result with
`Idempotent-Replayed: true`. Reading needs `payment.read`, taking money and putting an advance on a bill
`payment.create`, refunds `payment.refund`, voids `payment.void`, the drawer `cash_drawer.operate`, approving a short or
over drawer `cash_drawer.approve_variance` (role defaults in V002: receptionists take money and run the drawer; managers
also refund and approve variances; the accountant refunds and approves variances; voids are for the owner and the general
manager). How the locking works:
[system-design.md → 4.20](architecture/system-design.md#420-refunds-that-cant-overspend-and-a-cash-drawer-that-balances).

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /payments?q&method&status&from&to&reservationId&page&pageSize` | Payments, newest first, with totals by method (received, refunded, net; voided payments left out). `from`/`to` are days at the property; `q` searches the payment number, booking reference, payment reference or guest name | `payment.read` | — | `PaymentListDto` | 400 `Method` ("'CHEQUE' isn't a payment method. Use CASH, UPI, CARD, BANK_TRANSFER."), `Status`, 403, 404 | — |
| `GET /payments/{id}` | One payment: its bills, what is on account, refunds, the booking's folios and whether it can still be voided (and why not) | `payment.read` | — | `PaymentDto` | 403, 404 | — |
| `POST /payments` | Take money. With `folioId` the whole amount goes on that bill (at most what it owes); with only `reservationId` it stays **on account** for the booking (an advance). Cash goes into the open drawer shift | `payment.create` | `CreatePaymentRequest` | 201 `PaymentDto` | 400 under the field (below), `Idempotency-Key` missing; 403, 404; 422 `BOOKING_CLOSED` / `FOLIO_CLOSED` ("R00005 is cancelled, so no payment can be taken for it."), `IDEMPOTENCY_KEY_REUSED` | INSERT payment (+ allocation, + drawer movement) |
| `POST /payments/{id}/allocate` | Put money that is on account onto bills of the same booking | `payment.create` | `AllocatePaymentRequest` | `PaymentDto` | 400 `Allocations` ("P00002 has ₹2,000 on account; ₹2,500 can't be allocated. Allocate at most ₹2,000.", "F00004 owes ₹1,200; ₹1,500 would put it in credit. Allocate at most ₹1,200.", another booking's folio), 403, 404; 422 `PAYMENT_NOT_CAPTURED`, `FOLIO_CLOSED` | INSERT allocations |
| `POST /payments/{id}/refund` | Give money back, with a reason. It comes off what is on account first, then the bills it paid (or the one named). The payment becomes `PARTIALLY_REFUNDED` or `REFUNDED` | `payment.refund` | `RefundPaymentRequest` | `PaymentDto` | 400 under the field (below), `Idempotency-Key` missing; 403, 404; 422 `PAYMENT_NOT_CAPTURED` ("P00003 was voided (Entered twice), so nothing of it can be refunded."), `IDEMPOTENCY_KEY_REUSED` | INSERT refund(s) (+ drawer movement), UPDATE payment status, with the reason |
| `POST /payments/{id}/void` | Cancel a payment entered by mistake, with a reason. It becomes `CANCELLED` and counts for nothing; voided cash leaves the drawer | `payment.void` | `VoidPaymentRequest` | `PaymentDto` | 400 `Reason`, 403, 404; **409 `ALREADY_VOIDED`** ("P00003 was already voided at 14:05 on Tue 20 Oct 2026 by Rita (reason: …). Reload to see it."); 422 `NOT_VOIDABLE` ("Part of P00003 has been refunded, so it can't be voided. Refund the rest instead.", or its cash shift is closed) | UPDATE payment (+ drawer movement), with the reason |
| `GET /cash-drawer?drawerId` | The drawer: the open shift with what it should hold now, shifts waiting for approval, the last closed ones | `payment.read` | — | `CashDrawerDto` | 400 `DrawerId`, 403, 404 | — |
| `GET /cash-drawer/sessions/{id}` | One shift with its ledger (every movement) | `payment.read` | — | `CashDrawerSessionDto` | 403, 404 | — |
| `POST /cash-drawer/open` | Open a shift with the cash counted into the drawer (the float) | `cash_drawer.operate` | `OpenCashDrawerRequest` | 201 `CashDrawerSessionDto` | 400 `OpeningFloat`, `Note`; 403, 404; **409 `DRAWER_ALREADY_OPEN`** ("The Front desk drawer is already open since 08:00 on Tue 20 Oct 2026 (Rekha). …") | INSERT session + opening float movement |
| `POST /cash-drawer/close` | Close the open shift with the cash counted now. Counted = expected → `CLOSED`; short or over → `VARIANCE_PENDING`, which needs a note | `cash_drawer.operate` | `CloseCashDrawerRequest` | `CashDrawerSessionDto` | 400 `CountedAmount`, `Note` ("The drawer should hold ₹5,800 but ₹5,750 was counted: ₹50 short. Count again, or say why (at least 3 characters) …"); 403, 404; 422 `DRAWER_NOT_OPEN` | UPDATE session, with the note |
| `POST /cash-drawer/sessions/{id}/approve-variance` | Approve a short or over shift, with a note; it becomes `CLOSED`. The person who closed it may approve it if they hold the permission | `cash_drawer.approve_variance` | `ApproveVarianceRequest` | `CashDrawerSessionDto` | 400 `Note` ("Say why the variance is approved (at least 3 characters), e.g. …"); 403, 404; **409 `NO_VARIANCE_PENDING`** (it balanced, or "This shift's variance was already approved at … by …. Reload to see it.") | UPDATE session, with the note |

`CreatePaymentRequest = { method, amount, folioId?, reservationId?, paymentReference?, note? }`. **Field errors** (400,
each under its field): `Method` ("Choose how the money was paid: Cash, UPI, Card, Bank transfer."; cash with no open shift:
"The Front desk cash drawer isn't open, so a payment in cash can't be recorded. Open it on the front desk (Cash drawer →
Open drawer) with the cash in it, or choose another method."), `Amount` (more than ₹0, 2 decimals, at most ₹1,00,00,000;
on a bill: "F00006 owes ₹9,500, so it can take at most that. Take ₹9,500, or record the rest on account for the booking."),
`ReservationId` ("Choose the folio this pays, or the booking to put the money on account for."), `FolioId` (another
booking's bill), `PaymentReference` (≤ 100; for a card: "Don't type the card number: card details are never stored. Enter
the approval code from the terminal slip (up to 12 digits), or leave it empty."; otherwise "This looks like a card number,
and card details are never stored. …"), `Note` (≤ 500, no card numbers).
`AllocatePaymentRequest = { allocations: [{ folioId, amount }] }` (1–20 lines, each folio once).

`RefundPaymentRequest = { amount, reason, method? (default the payment's), folioId?, reference? }`. **Field errors**:
`Amount` ("P00003 was ₹1,000 and ₹200 of it is already refunded, so at most ₹800 can be refunded.", "P00003 has ₹1,000 on
F00006, so at most that can be refunded from it."), `Reason` ("Say why the money is refunded (at least 3 characters), e.g.
Guest cancelled the airport transfer."), `Method` (a cash refund needs an open shift, and "The drawer should hold ₹300,
less than ₹500. Refund by another method, or add cash to the drawer first."), `FolioId` ("P00003 isn't on that folio. …"),
`Reference` (as above). `VoidPaymentRequest = { reason }` · `OpenCashDrawerRequest = { openingFloat, note?, drawerId? }` ·
`CloseCashDrawerRequest = { countedAmount, note?, drawerId? }` · `ApproveVarianceRequest = { note }`.

`PaymentDto = { id, number, reservationId, reference, guestName, method, status, amount, allocated, refunded, onAccount,
refundable, currency, paymentReference, note, cashDrawerSessionId, receivedAt, receivedByName, voidedAt, voidedByName,
voidReason, canVoid, cantVoidReason, allocations: [{ id, folioId, folioNumber, folioName, amount, refunded, allocatedAt,
allocatedByName }], refunds: RefundDto[], folios: [{ id, number, name, isPrimary, balance }] }`.
`RefundDto = { id, paymentId, paymentNumber, folioId, folioNumber, amount, method, status, reason, reference, refundedAt,
refundedByName }`. `PaymentListDto = { items: [{ id, number, reservationId, reference, guestName, method, status, amount,
refunded, onAccount, paymentReference, receivedAt, receivedByName, folios }], page, pageSize, total, byMethod: [{ method,
count, received, refunded, net }], received, refundedTotal, net }`.
`CashDrawerDto = { id, name, current: CashDrawerSessionDto?, pendingApproval: CashDrawerSessionDto[], recent:
CashDrawerSessionDto[] }`. `CashDrawerSessionDto = { id, cashDrawerId, drawerName, status: OPEN | VARIANCE_PENDING |
CLOSED, openedAt, openedByName, openingFloat, openNote, cashIn, cashOut, expected, payments, closedAt, closedByName,
counted, variance (counted − expected; negative = short), closeNote, approvedAt, approvedByName, approvalNote, movements?:
[{ id, movementType: OPENING_FLOAT | PAYMENT | REFUND | VOID | PAY_IN | PAY_OUT, amount (+ in, − out), description,
paymentId, paymentNumber, reference, movedAt, byName }] }`.

**On the bill and the booking (earlier endpoints).** `FolioDto.payments` is what the bill has been paid (its allocations
from payments that count, less refunds taken off it), `balance = total − payments`, and `FolioDto.paymentLines = [{
paymentId, paymentNumber, refundId, kind: PAYMENT | REFUND, method, amount (refunds negative), at, byName, detail, voided
}]`. The front desk's balance and check-out use the **booking**: all its bills less every payment that counts, net of
refunds, so money on account counts too. The calendar's `paymentStatus` is `NONE`, `PARTIAL` or `PAID` from the same
numbers.

## Built endpoints (Sprint 11 — Razorpay)

Guests pay online with UPI, cards, net banking or wallets through Razorpay. Staff send a **payment link** for a booking
and an amount: a Razorpay order plus a private page in the app (`/pay/<token>`, no sign-in; the token is 32 random bytes)
that opens Razorpay Checkout. **Razorpay's signed webhook is the only thing that records a payment**: what the guest's
browser says only changes what their page shows. The payment (method `ONLINE`, reference = Razorpay's `pay_…` id, at most
one per Razorpay payment) goes on the booking's main bill up to what it owes (the rest on account), the link becomes
`PAID`, and a `PENDING` booking is confirmed, all in one transaction. Staff need `payment.create` to send or cancel a link
and `payment.read` to see links; there are no new permissions. Keys: `Razorpay__KeyId`, `Razorpay__KeySecret`,
`Razorpay__WebhookSecret` (`.env`, test mode outside production). With no keys outside production the API uses a built-in
fake Razorpay (links say so: `fake: true`); production never uses it (`Razorpay__Mode=Fake` stops it starting, and with no keys sending a link says the keys are missing). How it works:
[RAZORPAY.md](RAZORPAY.md) and [system-design.md → 4.21](architecture/system-design.md#421-razorpay-the-webhook-is-the-only-writer).

**Staff** (under `/api/v1/properties/{propertyId}`):

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `POST /payments/razorpay/orders` | Send a payment link: creates the Razorpay order (amount in paise, receipt = our link id) and the guest's page. Amount: ₹1 or more, at most what the booking owes less what open links already ask for | `payment.create` + `Idempotency-Key` | `CreatePaymentLinkRequest` | 201 `PaymentLinkDto` | 400 `Amount` ("The amount must be at least ₹1, Razorpay's smallest payment."; "R00003 still owes ₹8,400 and open links already ask for ₹2,000, so a new link can be for at most ₹6,400. Cancel an open link to ask for more."; paid in full), `ReservationId`, `Note` (≤ 500, no card numbers), `Idempotency-Key` missing; 403, 404; 422 `BOOKING_CLOSED` ("R00006 is cancelled, so no payment link can be sent for it."), **`GATEWAY_UNAVAILABLE`** (Razorpay's reason, e.g. "Razorpay didn't answer within 10 seconds, so it couldn't create the payment order (tried 3 times). No link was created; try again in a minute."), `IDEMPOTENCY_KEY_REUSED` | INSERT payment_gateway_orders |
| `GET /payments/razorpay/orders?reservationId` | A booking's links, newest first, with what it owes (`due`, `paid`, `owed`, `open`) and whether a new link can be sent (`canCreate`, `cantCreateReason`) | `payment.read` | — | `BookingPaymentLinksDto` | 400 `ReservationId`; 403 | — |
| `GET /payments/razorpay/orders/{id}` | One link with every webhook Razorpay sent for it (`events`: what each did, or why it changed nothing) | `payment.read` | — | `PaymentLinkDto` | 403, 404 | — |
| `POST /payments/razorpay/orders/{id}/cancel` | Stop a link being offered, with a reason. If the guest pays it anyway, the money is still recorded | `payment.create` | `{ reason }` | `PaymentLinkDto` | 400 `Reason` ("Say why the link is cancelled (at least 3 characters), e.g. Guest will pay at the desk."); 403, 404; **409 `LINK_ALREADY_PAID`** ("The guest already paid this link (P00004, ₹2,000), so it can't be cancelled. Refund the payment instead."), **`LINK_ALREADY_CANCELLED`** | UPDATE the link, with the reason |

**Razorpay and the guest** (no sign-in):

| Method & URL | Purpose | Request | Response | Errors |
|---|---|---|---|---|
| `POST /api/v1/webhooks/razorpay` | Razorpay's events (`payment.authorized`, `payment.captured`, `payment.failed`, `order.paid`, `refund.processed`, `refund.failed`). Checks `X-Razorpay-Signature` (HMAC-SHA256 of the **raw** body with the webhook secret), claims `X-Razorpay-Event-Id` once (a repeat gets 200 and changes nothing), fetches the payment from Razorpay and uses its status, then applies it in one transaction. Out-of-order events never move a payment backwards | raw JSON, ≤ 256 kB | 200 `{ status: processed \| ignored, message }` | **401 `BAD_SIGNATURE`** (nothing stored); 400 (not JSON, no event name, no event id header); 413; **500 `WEBHOOK_FAILED`** (our error: stored as `FAILED` with the reason, Razorpay retries and the retry takes it over) |
| `GET /api/v1/pay/{token}` | The guest's page: property, booking, dates, amount, status (`AWAITING_PAYMENT`, `CONFIRMING`, `PAID`, `CANCELLED`, `CLOSED`) and a message in words; while payable, what Checkout needs (`keyId`, `gatewayOrderId`) | — | `PayPageDto` | 404 ("This payment link isn't valid. Check you opened the whole link, or ask the resort for a new one.") |
| `POST /api/v1/pay/{token}/callback` | What Checkout hands the browser after paying. The signature (HMAC of `order_id\|payment_id` with the key secret) is checked, and the page shows "confirming" until the webhook arrives. **Records nothing** | `{ razorpayPaymentId, razorpayOrderId, razorpaySignature }` | `PayPageDto` | 400 `RazorpaySignature`, 404 |
| `POST /api/v1/pay/{token}/fake-checkout` | Fake Razorpay only (no keys, never production): pays (`succeed: true`) or declines the link as a test and delivers the fake's signed webhooks through the same code | `{ succeed, method? (upi \| card) }` | `PayPageDto` | 404 (real Razorpay in use), 422 `NOT_PAYABLE` |

`CreatePaymentLinkRequest = { reservationId, amount, note? }`. `PaymentLinkDto = { id, reservationId, reference,
guestName, gatewayOrderId, amount, currency, status: CREATED | PAID | CANCELLED, payPath ("/pay/<token>"), note,
attempts (declined tries), lastError, callbackAt, paymentId, paymentNumber, paymentStatus, paidAt, createdAt,
createdByName, cancelledAt, cancelledByName, cancelReason, events?: [{ id, eventId, eventType, status: PROCESSED |
IGNORED | FAILED, outcome, attempts, receivedAt }] }`. `BookingPaymentLinksDto = { reservationId, reference, due, paid,
owed, open, canCreate, cantCreateReason, fake, links }`. `PayPageDto = { propertyName, bookingReference, guestName (first
name), checkIn, checkOut, amount, currency, status, message, keyId, gatewayOrderId, fake, lastError, paymentNumber, paidAt }`.

**Changes to Sprint 10 endpoints.** Payment method `ONLINE` ("Online (Razorpay)") appears in lists, filters and totals,
but `POST /payments` refuses it under `Method` ("Online payments are recorded by Razorpay when the guest pays. Send the
guest a payment link from the booking instead."). `PaymentDto` adds `gatewayMethod` (upi, card, netbanking, wallet) and
`paymentLinkId`. An online payment can't be voided ("…was paid online through Razorpay, so the money really came in and it
can't be voided. Refund it instead."). A refund of an online payment with `method: ONLINE` (the default for it) goes back
through Razorpay: the refund is `PENDING` until `refund.processed` (immediately `PROCESSED` when Razorpay says so at once);
`refund.failed` makes it `FAILED` and the money counts on the payment again. 422 `GATEWAY_REFUND_FAILED` (Razorpay said
no; nothing was refunded) or `GATEWAY_REFUND_UNCLEAR` (no answer: check Razorpay's dashboard before trying again; a refund
POST is never retried automatically). `method: ONLINE` for a desk payment is refused under `Method`. Online refunds can
also be paid back at the desk (cash, UPI …), as before.

## Built endpoints (Sprint 12 — GST invoices, credit and debit notes, receipts)

All under `/api/v1/properties/{propertyId}`. A folio becomes a **GST tax invoice** (or a **bill of supply** when the
property is under the composition scheme) with a gap-free number from the property's series for the financial year
(April–March): `BEACH/2627/00001` for invoices, `BEACH/C2627/0001` credit notes, `BEACH/D2627/0001` debit notes,
`BEACH/R2627/00001` receipts (at most 16 characters, letters, digits, `/` and `-`, as GST rule 46(b) asks; the prefix is
up to 5 letters and digits of the property code). The invoice **copies** each folio charge's taxes as they were posted
(Sprint 9: rule, rate, taxable value); it never recalculates. **Place of supply** for a hotel stay is the property's state
(IGST Act s.12(3)), so even a company from another state is charged CGST + SGST. With a valid customer GSTIN the invoice is
**B2B**, otherwise **B2C**. **Nothing issued is ever edited or deleted** (the database refuses it): an invoice is
corrected with a **credit note** (`CANCEL` the whole invoice, so its charges can be invoiced again, e.g. in a company's
name; or `REDUCE` chosen lines or part of them, which also puts a `CREDIT` line on the folio), and charges posted after
it go on a **debit note**. Check-out issues the invoice by itself (setting `invoice.issue_at_checkout`, default on) plus a
receipt for each payment on the booking (nothing is issued when the property has no GST registration); if a folio can't be
invoiced (a wrong GSTIN on the guest's profile …) the check-out still goes through and a note on the booking says why. Issuing needs `invoice.create` and an
`Idempotency-Key`; reading `invoice.read`; credit and debit notes `credit_note.create` and an `Idempotency-Key`;
cancelling an invoice also `invoice.cancel` (role defaults in V002: receptionists and managers read and issue; the
accountant, general manager and owner also issue notes and cancel). How the numbering lock works:
[system-design.md → 4.22](architecture/system-design.md#422-gst-invoices-gap-free-numbers-and-corrections-only-by-note).

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /folios/{id}/invoice-preview` | What issuing the folio's invoice would give: the customer it defaults to (the bill-to guest, else the booking's guest; their company and GSTIN make it B2B), the lines and totals, the folio's invoice if it has one, and why it can't be issued now (`cantIssueReason`) | `invoice.read` | — | `InvoicePreviewDto` | 403, 404 | — |
| `POST /folios/{id}/invoice` | Issue the folio's invoice. An empty `customerName` bills the guest on file; with `customerName` the customer is exactly what is sent | `invoice.create` + `Idempotency-Key` | `IssueInvoiceRequest` | 201 `InvoiceDto` | 400 under the field (below), `Idempotency-Key` missing; 403, 404; **409 `ALREADY_INVOICED`** ("F00012 already has invoice BEACH/2627/00007. Charges posted after it go on a debit note: open BEACH/2627/00007 and issue one."); 422 `FOLIO_CLOSED` ("R00005 is cancelled, so F00009 can't be invoiced."), `NOTHING_TO_INVOICE` ("F00012 has no charges yet, so there is nothing to invoice."), `NO_GST_REGISTRATION` ("Palm Grove Beach Resort has no GST registration on Fri 23 Oct 2026, so it can't issue a GST invoice. Add its GSTIN in Settings → GST."), `TAXED_UNDER_COMPOSITION`, `IDEMPOTENCY_KEY_REUSED` | INSERT invoice (+ items, taxes), series +1 |
| `GET /invoices?q&from&to&reservationId&status&page&pageSize` | Invoices and bills of supply, newest first. `q`: invoice number, booking reference, folio number, GSTIN or customer name; `status`: `ISSUED`, `PARTLY_CREDITED`, `CREDITED`, `CANCELLED` (worked out from the credit notes) | `invoice.read` | — | `PagedResult<InvoiceListItemDto>` | 400 `To` (before `From`), `Status` ("'X' isn't an invoice status. Use issued, partly-credited, credited or cancelled."), 403 | — |
| `GET /invoices/{id}` | One invoice as printed, with its credit notes, debit notes, receipts, what each line has had credited and can still be, and the charges posted to the folio after it | `invoice.read` | — | `InvoiceDto` | 403, 404 | — |
| `GET /invoices/{id}/pdf` | The invoice as an A4 PDF (`TAX INVOICE` or `BILL OF SUPPLY`, a `CANCELLED` mark once cancelled), named `BEACH-2627-00001.pdf` | `invoice.read` | — | `application/pdf` | 403, 404 | — |
| `POST /invoices/{id}/credit-notes` | Issue a credit note with a reason. `CANCEL`: everything still on the invoice and its debit notes (needs `invoice.cancel` too; `lines` empty). `REDUCE`: the chosen lines (an invoice line or a debit note line), each in full or by a taxable amount; GST comes off at each line's own rate and the folio gets a `CREDIT` line for each | `credit_note.create` + `Idempotency-Key` | `CreateCreditNoteRequest` | 201 `InvoiceDto` | 400 `Type` ("Choose what the credit note does: CANCEL (the whole invoice, to issue it again) or REDUCE (some lines or part of them)."), `Reason`, `Lines` ("Choose at least one line to credit."; "Cancelling credits the whole invoice, so don't choose lines. …"), `Lines[i].Amount` ("\"Dinner for two\" has ₹2,000.00 left to credit (before GST); ₹2,500.00 is more than that."; credited in full), `Lines[i].LineId` (not on this invoice, chosen twice); 403, 404; **409 `INVOICE_CANCELLED`** ("BEACH/2627/00007 was already cancelled by credit note BEACH/C2627/0002 on Fri 23 Oct 2026. Reload to see it."); 422 `NOTHING_TO_CREDIT`, `IDEMPOTENCY_KEY_REUSED` | INSERT credit note (+ items, taxes, folio CREDIT lines), series +1, with the reason |
| `POST /invoices/{id}/debit-notes` | Bill the charges posted to the folio after the invoice (all, or `folioItemIds`) on a debit note of the invoice, with a reason | `credit_note.create` + `Idempotency-Key` | `CreateDebitNoteRequest` | 201 `InvoiceDto` | 400 `Reason`, `FolioItemIds` ("2 of the chosen charges aren't on F00012 or are already billed. …"); 403, 404; 422 `NOTHING_TO_BILL` ("Every charge on F00012 is already on BEACH/2627/00007 or its debit notes, …"), `INVOICE_CANCELLED` ("… Issue a new invoice for F00012 instead."), `TAXED_UNDER_COMPOSITION` | INSERT debit note (+ items, taxes), series +1, with the reason |
| `GET /credit-notes/{id}/pdf` · `GET /debit-notes/{id}/pdf` | A note as an A4 PDF, naming the invoice it adjusts and the reason | `invoice.read` | — | `application/pdf` | 403, 404 | — |
| `POST /payments/{id}/receipt` | The payment's receipt: issued the first time, the same one after that | `invoice.create` | — | `ReceiptDto` | 403, 404; 422 `NO_RECEIPT_FOR_PAYMENT` ("P00003 was voided (Entered twice), so it gets no receipt."; a pending online payment: "the money hasn't arrived …") | INSERT receipt, series +1 |
| `GET /receipts/{id}/pdf` | A receipt as an A4 PDF (a `VOID` mark if the payment was voided after it was issued) | `invoice.read` | — | `application/pdf` | 403, 404 | — |

`IssueInvoiceRequest = { customerName?, customerGstin?, customerAddress?, customerStateCode?, notes? }`. **Field errors**
(400, each under its field): `CustomerGstin` (format, state code, PAN, "Character 14 of a GSTIN is always Z.", "The last
character (check digit) should be V for this GSTIN; check it for a typing mistake."; the guest's GSTIN on file is wrong:
"The GSTIN on Ravi Kumar's profile (27AAPFU0939F1ZX) isn't valid: … Correct it, or clear it to issue a B2C invoice."),
`CustomerName` ("Enter the customer's name (the company's legal name for a B2B invoice) with the GSTIN."; ≤ 200),
`CustomerStateCode` ("99 isn't a GST state code. Choose the state from the list."; "The GSTIN 27… is registered in
Maharashtra (27), not Karnataka (29). A B2B customer's state is its GSTIN's state."), `CustomerAddress` (≤ 300), `Notes`
(≤ 500). `CreateCreditNoteRequest = { type: CANCEL | REDUCE, reason, lines?: [{ lineId, amount? (taxable value; empty =
all that is left) }] }` · `CreateDebitNoteRequest = { reason, folioItemIds? }`. Reasons: 3–500 characters ("Say why the
credit note is issued (at least 3 characters), e.g. Guest was charged for a dinner they didn't have.").

`InvoiceDto = { id, number, documentType: INVOICE | BILL_OF_SUPPLY, title, financialYear, invoiceDate, folioId,
folioNumber, reservationId, reference, supplier: DocumentPartyDto, customer: DocumentPartyDto, isB2B, placeOfSupply,
placeOfSupplyName, items: DocumentLineDto[], taxSummary: [{ hsnSac, code, rate, taxableValue, amount }], taxableValue,
cgst, sgst, igst, cess, taxTotal, total, amountInWords ("Rupees … Only", lakh and crore), currency, notes, issuedAt,
issuedByName, status, billed (invoice + debit notes), credited, outstanding, creditNotes: NoteDto[], debitNotes:
NoteDto[], receipts: ReceiptDto[], eInvoice (Sprint 25), unbilledCharges, unbilledAmount }`.
`DocumentPartyDto = { name, tradeName, gstin, address, stateCode, stateName }`. `DocumentLineDto = { id, lineNo,
folioItemId, category, description, hsnSac, serviceDate, quantity, unitPrice, grossAmount, discount (credited off the
charge before it was billed), taxableValue, taxAmount, total, taxes: [{ code, rate, taxableValue, amount }], credited,
creditable, debitNoteNumber }` (debit note lines follow the invoice's own). `NoteDto = { id, kind: CREDIT_NOTE |
DEBIT_NOTE, number, noteType: CANCEL | REDUCE | null, noteDate, reason, taxableValue, taxTotal, total, issuedAt,
issuedByName, lines: [{ lineNo, description, hsnSac, taxableValue, taxAmount, total, taxes }] }`. `ReceiptDto = { id,
number, receiptDate, paymentId, paymentNumber, reservationId, reference, receivedFrom, amount, method, paymentReference,
receivedAt, paymentStatus, voided, issuedAt, issuedByName }`. `InvoiceListItemDto = { id, number, documentType,
invoiceDate, folioId, folioNumber, reservationId, reference, customerName, customerGstin, taxableValue, taxTotal, total,
credited, status, creditNotes, debitNotes, currency }`. `InvoicePreviewDto = { folioId, folioNumber, reservationId,
reference, documentType, supplier, billToGuestId, customerName, customerGstin, customerAddress, customerStateCode,
gstinProblem, lines, taxableValue, taxTotal, total, invoiceId, invoiceNumber, cantIssueReason }`.

**Changes to earlier endpoints.** A folio charge on an invoice (or debit note) that stands, or one a credit note reduced,
can't be reversed or moved: 422 `INVOICED` ("\"Dinner for two\" is on invoice BEACH/2627/00007, so it can't be reversed or
moved. Issue a credit note on BEACH/2627/00007 instead."; under `ItemIds` when moving lines). A `CREDIT` line can't be
reversed (`NOT_REVERSIBLE`). `FolioItemDto` adds `invoiceNumber` and `adjustable` (it can still be reversed or moved), and
`itemType` can be `CREDIT` (`reason` = the credit note's reason); `FolioDto.reversals` sums reversal and credit lines.
`POST /reservations/{id}/check-out` issues the invoice and the receipts in the same transaction (see above).

## Built endpoints (Sprint 13 — housekeeping and maintenance)

All under `/api/v1/properties/{propertyId}`. A room's (or dorm bed's) cleaning state is Sprint 7's `unit_housekeeping_status`:
`DIRTY` → `CLEANING` → `CLEAN` (cleaned, waiting for inspection) → `INSPECTION` (being inspected) → `READY`, or `OUT_OF_ORDER`.
Every change that makes a room `DIRTY` (check-out, room move, a resolved repair, front desk by hand) creates its **cleaning
task in the same transaction**; the task points at that state change (unique), so the same check-out retried or sent three
times at once makes one task, and a room or bed has at most one open task. A task is `OPEN` → `IN_PROGRESS` → `CLEANED`
(waiting for inspection) → `DONE`, or `CANCELLED` (the room went out of order). With property setting
`housekeeping.inspection_required` off (default on), Done makes the room `READY` straight away. Steps take the task's
`version` (refused with 409 when someone else changed it). Housekeepers (`housekeeping.update`) start and finish; supervisors
(`housekeeping.assign`, `housekeeping.inspect`) assign, make tasks by hand, inspect and edit checklists; receptionists and
managers read. The board shows **no guest names**. **Maintenance tickets** (`M00001` …) say where (a room, a dorm bed, a
piece of equipment or a place in words), the kind and how urgent. **Out of order** puts a Sprint 2 `OUT_OF_ORDER` block on
the room from tonight until the expected date (at most 90 days), so it can't be sold or checked into; **resolving** the
ticket releases the block and makes the room `DIRTY` with a "Clean after repair" task, so it is cleaned before it is sold.
Anyone with `maintenance.create` reports problems and sees the ones they reported; `maintenance.read` sees all;
`maintenance.update` works and resolves; `maintenance.assign` assigns and closes. How the task is made exactly once:
[system-design.md → 4.23](architecture/system-design.md#423-housekeeping-one-task-per-check-out-and-out-of-order-rooms).

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /housekeeping/board` | Today's board (the property's date): every room and dorm bed with its cleaning state, open task, repair and whether a guest is in, leaving or arriving today. Rush rooms (not ready, guest arriving today) first | `housekeeping.read` | — | `HousekeepingBoardDto` | 403 | — |
| `GET /housekeeping/staff` | Who can be given rooms (housekeeping staff at the property) with their open tasks | `housekeeping.assign` | — | `StaffOptionDto[]` | 403 | — |
| `POST /housekeeping/tasks` | A task by hand (stay-over, deep clean); the room becomes `DIRTY` | `housekeeping.assign` | `CreateTaskRequest` | 201 `HousekeepingTaskDto` | 400 `UnitId` ("That room doesn't exist …", "Room 105 is not in use; …"), `BedId` ("Dorm A is a dormitory; choose the bed."), `TaskType`, `AssignTo` (not housekeeping staff here); 403; **409 `TASK_EXISTS`** ("Room 101 already has an open task (departure clean, room dirty). Finish or inspect that one instead."); 422 `ROOM_OUT_OF_ORDER` | INSERT task, room state change |
| `GET /housekeeping/tasks/{id}` | One task with its checklist and what the caller may do (`nextActions`) | `housekeeping.read` | — | `HousekeepingTaskDto` | 403, 404 | — |
| `POST /housekeeping/tasks/{id}/assign` | Give it to someone (`userId` null: nobody) | `housekeeping.assign` | `{ userId, version }` | `HousekeepingTaskDto` | 400 `UserId`; 403, 404; 409 version, `TASK_CLOSED` | UPDATE task |
| `POST /housekeeping/tasks/{id}/start` | Start cleaning (tap 1); an unassigned task becomes the caller's. The room becomes `CLEANING` | `housekeeping.update` | `{ version }` | `HousekeepingTaskDto` | 403, 404; **409 `TASK_ALREADY_STARTED`** ("Room 101 is already being cleaned by Hema (started 10:42)."), `TASK_ASSIGNED_TO_SOMEONE_ELSE` ("Room 101 is assigned to Asha. Ask your supervisor to give it to you."), `TASK_NOT_STARTABLE`, version | UPDATE task, room state change |
| `POST /housekeeping/tasks/{id}/finish` | Done (tap 2). `checked` null: every line done; otherwise the ticked line ids, which must include every required line. The room becomes `CLEAN` (or `READY` without inspection) | `housekeeping.update` | `{ checked?, note?, version }` | `HousekeepingTaskDto` | 400 `Checked` ("Tick every required line before finishing: Bathroom cleaned, Bed linen changed."); 403, 404; 409 `TASK_NOT_STARTED` ("Cleaning of Room 101 hasn't been started. Tap Start first."), `TASK_ALREADY_FINISHED`, `TASK_ASSIGNED_TO_SOMEONE_ELSE` | UPDATE task, room state change |
| `POST /housekeeping/tasks/{id}/inspect` | `PASS`: the room is `READY`. `FAIL` (note required): back to `DIRTY`, the task `OPEN` again for the same housekeeper with the note. `START`: the room shows `INSPECTION` | `housekeeping.inspect` | `{ result, note?, version }` | `HousekeepingTaskDto` | 400 `Result`, `Note` ("Say what needs redoing (at least 3 characters), e.g. Bathroom mirror smeared."); 403, 404; 409 `TASK_NOT_FINISHED` ("Room 101 is being cleaned; it can be inspected once the cleaning is finished."), `ALREADY_READY` | UPDATE task, room state change |
| `GET /housekeeping/checklists` · `POST` · `PUT /{id}` | Checklist templates: one active per kind of cleaning and room type (none = every room type). A change applies to tasks made afterwards | read: `housekeeping.read`; save: `housekeeping.assign` | `SaveChecklistTemplateRequest` | `ChecklistTemplateDto[]` / 201 / 200 | 400 `Name`, `TaskType` (another active one for the same kind and room type), `Items` ("Add at least one line to the checklist."; ≤ 40), `Items[i].Label`, `AccommodationTypeId`; 403, 404 | INSERT/UPDATE template (+ lines) |
| `GET /maintenance/tickets?status&mine&unitId` | Tickets, unresolved and most urgent first. `status`: `OPEN` (default: anything unresolved), `ALL` or one status; `mine`: assigned to or reported by the caller. Without `maintenance.read`, only the caller's reports | `maintenance.create` | — | `MaintenanceTicketSummaryDto[]` | 400 `Status`; 403 | — |
| `POST /maintenance/tickets` | Report a problem. `Idempotency-Key` optional (a retried report is saved once). Out of order: blocks the room from tonight | `maintenance.create` | `CreateTicketRequest` | 201 `MaintenanceTicketDto` | 400 `Title` ("Say what is wrong in 3–120 characters, e.g. AC not cooling."), `Location` ("Say where the problem is: choose a room or equipment, or type the place (e.g. Pool deck)."), `Category`, `Priority`, `UnitId`, `BedId`, `AssetId`, `ExpectedReadyOn` ("Say when the room should be usable again …", today or earlier, more than 90 days); 403; **409** `BLOCK_OVERLAPS_RESERVATION` ("Room 104 can't be taken out of order from tonight: Room 104 is booked on R00012 for the night of 25 Oct 2026. Move that booking to another room first, or report the problem without taking the room out of order."), `BLOCK_OVERLAP` | INSERT ticket, comment, block |
| `GET /maintenance/tickets/{id}` | One ticket with its timeline, photos and `nextActions` | `maintenance.create` (own reports) / `maintenance.read` | — | `MaintenanceTicketDto` | 403, 404 | — |
| `POST /maintenance/tickets/{id}/assign` | Give it to maintenance staff (`userId` null: nobody) | `maintenance.assign` | `{ userId, note?, version }` | `MaintenanceTicketDto` | 400 `UserId`; 403, 404; 409 version; 422 `TICKET_CLOSED`, `INVALID_STATUS_TRANSITION` ("M00003 is resolved; …") | UPDATE ticket, STATUS comment |
| `POST /maintenance/tickets/{id}/progress` | Work on it: `IN_PROGRESS` (default) or `ON_HOLD`, with a note for the timeline | `maintenance.update` | `{ status?, note, version }` | `MaintenanceTicketDto` | 400 `Status`, `Note`; 403, 404; 409 version; 422 as assign | UPDATE ticket, comment |
| `POST /maintenance/tickets/{id}/resolve` | Fixed: releases the out-of-order block and sends the room to cleaning (`AFTER_REPAIR` task) | `maintenance.update` | `{ resolution, version }` | `MaintenanceTicketDto` | 400 `Resolution`; 403, 404; 409 version; 422 `INVALID_STATUS_TRANSITION` ("M00003 is already resolved.") | UPDATE ticket, block released, room state change, task |
| `POST /maintenance/tickets/{id}/close` | Checked and closed | `maintenance.assign` | `{ note?, version }` | `MaintenanceTicketDto` | 403, 404; 422 `INVALID_STATUS_TRANSITION` ("M00003 is in progress; resolve it before closing it."), `TICKET_CLOSED` | UPDATE ticket, comment |
| `POST /maintenance/tickets/{id}/comments` | A note on the timeline | `maintenance.create` | `{ body }` | `MaintenanceTicketDto` | 400 `Body`; 403, 404; 422 `TICKET_CLOSED` | INSERT comment |
| `POST /maintenance/tickets/{id}/photos` | One photo as `multipart/form-data` field `file`, shrunk on the phone first. Checked by its bytes (JPEG, PNG or WebP), at most 2 MB, 6 per ticket; stored encrypted in the file store | `maintenance.create` | multipart | 201 `MaintenancePhotoDto` | 400 `File` ("That file isn't a photo. Send a JPEG, PNG or WebP picture."; "The photo is 3.4 MB; at most 2 MB. …"); 403, 404; 413 over 3 MB; 422 `TOO_MANY_PHOTOS`, `TICKET_CLOSED` | INSERT attachment |
| `GET /maintenance/tickets/{id}/photos/{photoId}` | The photo, decrypted | `maintenance.create` (own reports) / `maintenance.read` | — | image | 403, 404 | — |
| `GET /maintenance/staff` | Maintenance staff with their open tickets | `maintenance.assign` | — | `StaffOptionDto[]` | 403 | — |
| `GET /maintenance/assets` · `POST` · `PUT /{id}` | Equipment (code like `AC-101`, kind, room or place) with its open tickets | read: `maintenance.create`; save: `maintenance.assign` | `SaveAssetRequest` | `MaintenanceAssetDto[]` / 201 / 200 | 400 `Code` ("AC-101 is already used by another piece of equipment here. …"; capitals, digits, `-`, `_`), `Name`, `Category`, `UnitId`; 403, 404 | INSERT/UPDATE asset |

`HousekeepingBoardDto = { today, inspectionRequired, summary: { dirty, cleaning, toInspect, ready, outOfOrder, mine, rush },
rooms: HousekeepingRoomDto[] }`. `HousekeepingRoomDto = { unitId, unitCode, unitName, bedId, bedCode, typeName, place,
status, occupied, departureToday, arrivalToday, rush, task: HousekeepingTaskDto | null, repair: { ticketId, number, title,
outOfOrder, expectedReadyOn } | null }`. `HousekeepingTaskDto = { id, unitId, bedId, place, taskType: DEPARTURE | STAYOVER |
DEEP_CLEAN | AFTER_REPAIR, status, source: CHECK_OUT | ROOM_MOVE | MAINTENANCE | MANUAL, reservationReference, assignedTo,
assignedToName, startedAt, startedByName, finishedAt, finishedByName, inspectedAt, inspectedByName, inspectionNote,
failedInspections, note, checklist: [{ id, label, required, done }], businessDate, createdAt, nextActions, version }`.
`CreateTaskRequest = { unitId, bedId?, taskType, note?, assignTo? }`. `SaveChecklistTemplateRequest = { name, taskType,
accommodationTypeId?, isActive, items: [{ label, isRequired }] }`. `CreateTicketRequest = { title, description?, category: AC |
PLUMBING | ELECTRICAL | FURNITURE | APPLIANCE | POOL | CIVIL | OTHER, priority: LOW | NORMAL | HIGH | URGENT, unitId?, bedId?,
assetId?, location?, outOfOrder, expectedReadyOn? }`. `MaintenanceTicketDto = { id, number, title, description, category,
priority, status: OPEN | ASSIGNED | IN_PROGRESS | ON_HOLD | RESOLVED | CLOSED, place, unitId, unitName, bedId, bedCode,
assetId, assetName, location, outOfOrder, expectedReadyOn, blockId, blockActive, reportedBy, reportedByName, assignedTo,
assignedToName, assignedAt, startedAt, resolvedAt, resolvedByName, resolution, closedAt, closedByName, createdAt, comments:
[{ id, kind: NOTE | STATUS, body, fromStatus, toStatus, createdAt, createdByName }], photos: [{ id, fileName, contentType,
sizeBytes, createdAt, createdByName }], nextActions, version }`.

**Changes to earlier endpoints.** `POST /reservations/{id}/check-out`, `POST /reservations/{id}/assign-room` (a room move after check-in) and
`PUT /rooms/{unitId}/housekeeping` (front desk) now lock the room and create, reset or close its cleaning task in the same
transaction; a room move's old room gets a task. `GET /rooms/status` adds `housekeeping` (today only; a dormitory shows its
least ready bed) and `openRepairs` to each room, and `housekeeping` to each bed. Unit history sources add `HOUSEKEEPING` and
`MAINTENANCE`.

## Built endpoints (Sprint 14 — menu, restaurant POS and KOT)

All under `/api/v1/properties/{propertyId}`. Prices are **before GST**. An **order** (`O00001` …) is dine-in at a table (at
most one open order per table), room service to an in-house room, or takeaway. Its lines start `NEW` (not sent; the waiter
can still change or remove them); **send** puts them on a **kitchen ticket** (`K00001` …), after which a line can only be
**voided** (`pos_order.void`, with a reason). A ticket is `NEW` → `PREPARING` → `READY` (kitchen, `kot.update`) → `SERVED`
(waiter), or `CANCELLED` when everything on it is voided. Once every line is sent and every ticket served, the bill is
**paid** at the restaurant (`payment.create`; cash goes into the open cash drawer) or **charged to the room**
(`folio.post_charge`: one FOOD line, source `RESTAURANT`, on the in-house guest's main folio with its GST). A settled or
voided order is final. **Every change to an order sends its `version`**: if someone else changed the order first, 409
`CONCURRENCY_CONFLICT` ("Someone else changed O00002 a moment ago (another waiter, or the kitchen). Reload it to see their
change, then make yours again."). GST is worked out by the tax engine (category FOOD, the order's business date) on the
subtotal less any discount (`pos.discount`, with a reason). Each order lists the steps the caller may take
(`nextActions`: `add`, `edit`, `send`, `serve`, `discount`, `pay`, `charge`, `void`). How it works:
[system-design.md → 4.24](architecture/system-design.md#424-restaurant-orders-two-waiters-one-order-and-the-room-charge).

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /menu?all` | The menu by section, dishes with portions and choices. `all=true`: also switched-off sections and dishes (the editor) | `menu.read` | — | `MenuDto` | 403 | — |
| `POST /menu/categories` · `PUT /menu/categories/{id}` | A menu section | `menu.create` / `menu.update` | `SaveMenuCategoryRequest` | 201 / 200 `MenuCategoryDto` | 400 `Name` ("The menu already has a section called Starters."), `SortOrder`; 403, 404 | INSERT/UPDATE |
| `POST /menu/items` · `PUT /menu/items/{id}` | A dish with all its portions and groups of choices; ones sent with an `id` are kept, new ones added, ones left out switched off. `version` optional on PUT | `menu.create` / `menu.update` | `SaveMenuItemRequest` | 201 / 200 `MenuItemDto` | 400 `Code` ("Another dish already has the code PANEER-TIKKA. Codes are unique on the menu."), `Name`, `CategoryId`, `FoodType`, `Price`, `Variants` ("Two portions have the same name."), `Variants[i].Name`, `ModifierGroups[i].MinSelect` / `MaxSelect` / `Modifiers`; 403, 404; 409 version | INSERT/UPDATE dish (+ portions, choices) |
| `POST /menu/items/{id}/availability` | Sold out (`false`) or back on (`true`); dishes already ordered are not affected | `menu.update` | `{ isAvailable }` | `MenuItemDto` | 403, 404 | UPDATE dish ("Bebinca sold out") |
| `GET /menu/tables` · `POST` · `PUT /menu/tables/{id}` | Restaurant tables | read `menu.read`; save `menu.create` / `menu.update` | `SaveRestaurantTableRequest` | `RestaurantTableDto[]` / 201 / 200 | 400 `Code` ("There is already a table T4."), `Seats`, `Area`, `IsActive` ("Table T2 has open order O00001. Settle or move it before switching the table off."); 403, 404 | INSERT/UPDATE |
| `GET /orders/floor` | Every active table, free or with its open order (lines not sent, cooking, ready), plus open room-service and takeaway orders | `pos_order.read` | — | `FloorDto` | 403 | — |
| `GET /orders?date&status` | One day's orders (default today), newest first, with the settled and open totals. `status`: `ALL` (default), `OPEN`, `SETTLED`, `VOIDED` | `pos_order.read` | — | `OrderListDto` | 400 `Status`; 403 | — |
| `GET /orders/rooms` | Rooms with a guest checked in, to deliver to or charge to | `pos_order.read` | — | `InHouseRoomDto[]` | 403 | — |
| `POST /orders` | Open an order, optionally with its first dishes. `Idempotency-Key` optional | `pos_order.create` | `CreateOrderRequest` | 201 `OrderDto` | 400 `OrderType`, `TableId` ("Choose the table."), `UnitId` ("Nobody is checked in to Room 103, so it can't order room service. Choose an occupied room."), `Covers`, `Items[i].MenuItemId` ("Masala Chai is sold out for now. The kitchen switches it back on when it has more."), `Items[i].VariantId` ("Choose the portion of Paneer Tikka: Half or Full."), `Items[i].ModifierIds` ("Choose the spice level for Paneer Tikka."), `Items[i].Quantity` ("The quantity must be 1–99."); 403; **409 `TABLE_HAS_ORDER`** ("Table T2 already has open order O00001 (opened by Wasim). Add the dishes to that order, or settle it first.") | INSERT order, lines |
| `GET /orders/{id}` | One order with its lines, tickets, money, taxes and `nextActions` | `pos_order.read` | — | `OrderDto` | 403, 404 | — |
| `PUT /orders/{id}` | Move to another table, guests at the table, note | `pos_order.update` | `UpdateOrderRequest` | `OrderDto` | 400; 403, 404; 409 version, `TABLE_HAS_ORDER`; 422 `ORDER_SETTLED`, `ORDER_VOIDED` | UPDATE order |
| `POST /orders/{id}/items` | Add dishes (`NEW` lines) | `pos_order.update` | `AddOrderItemsRequest` | `OrderDto` | 400 as `POST /orders`; 403, 404; **409 version**; 422 `ORDER_SETTLED` ("O00002 is already settled, so it can't be changed. Open a new order for anything more."), `ORDER_VOIDED` | INSERT lines |
| `PUT /orders/{id}/items/{itemId}` · `DELETE …?version=` | Change the quantity or note of a line not sent yet, or remove it | `pos_order.update` | `UpdateOrderItemRequest` | `OrderDto` | 400 `Quantity`; 403, 404; 409 version; 422 `ALREADY_SENT` ("Butter Naan has gone to the kitchen, so it can't be changed. A manager can void it, with a reason.") | UPDATE / DELETE line |
| `POST /orders/{id}/items/{itemId}/void` | Void a line already sent; struck through on its ticket (a ticket with nothing left is cancelled) | `pos_order.void` | `VoidRequest` | `OrderDto` | 400 `Reason` ("Say why it is voided (at least 3 characters), e.g. Guest changed their mind."); 403, 404; 409 version; 422 `NOT_SENT` (remove it instead), `ALREADY_VOIDED` | UPDATE line, ticket line (reason) |
| `POST /orders/{id}/send` | Send every `NEW` line to the kitchen as one ticket | `pos_order.update` | `SendToKitchenRequest` | `OrderDto` | 403, 404; 409 version; 422 `NOTHING_TO_SEND` | INSERT ticket |
| `POST /orders/{id}/kots/{kotId}/serve` | The waiter served a ready ticket (does not change the order's version) | `pos_order.update` | — | `OrderDto` | 403, 404; 422 `INVALID_STATUS_TRANSITION` ("K00002 is still being prepared. Serve it when the kitchen marks it ready.") | UPDATE ticket |
| `POST /orders/{id}/discount` | A % or ₹ off the food before GST (`kind` null: remove it); never more than the subtotal | `pos.discount` | `DiscountRequest` | `OrderDto` | 400 `Kind`, `Value` ("A percentage discount can be at most 100%."), `Reason`; 403, 404; 409 version | UPDATE order (reason) |
| `POST /orders/{id}/void` | Void the whole order: lines not sent removed, sent lines voided on their tickets; nothing billed | `pos_order.void` | `VoidRequest` | `OrderDto` | 400 `Reason`; 403, 404; 409 version; 422 `ORDER_SETTLED` | UPDATE order, lines, tickets (reason) |
| `POST /orders/{id}/pay` | Paid at the restaurant: `CASH` (into the open cash drawer as `POS_SALE`), `UPI`, `CARD`, `BANK_TRANSFER`. `Idempotency-Key` optional | `payment.create` | `PayOrderRequest` | `OrderDto` | 400 `Method` ("Restaurant bills are paid at the restaurant: choose cash, UPI, card or bank transfer."; "The Front desk cash drawer isn't open, so …"), `Reference` (no card numbers); 403, 404; 409 version; 422 `ORDER_NOT_SENT` ("1 line on O00002 hasn't gone to the kitchen (Butter Naan). Send it or remove it first."), `ORDER_IN_KITCHEN` ("K00002 is ready. Mark it served first."), `ORDER_EMPTY`, `TAX_RULE_MISSING` | UPDATE order, drawer movement |
| `POST /orders/{id}/charge-to-room` | One FOOD line (source `RESTAURANT`) on the guest's main folio for the amount after discount, with the same GST as the order. `reservationId` defaults to a room-service order's booking. `Idempotency-Key` optional | `folio.post_charge` | `ChargeToRoomRequest` | `OrderDto` | 400 `ReservationId` ("Choose the room to charge it to."; "R00008 has checked out, so nothing more can go on the room's bill. Take the payment at the restaurant instead."); 403, 404; 409 version; 422 as pay | UPDATE order, INSERT folio line (+ taxes) |
| `GET /kitchen/kots` | Tickets still to cook or serve (`NEW`, `PREPARING`, `READY`), oldest first, with their lines (voided ones struck through). The display polls it every 10 s | `kot.read` | — | `KitchenDto` | 403 | — |
| `POST /kitchen/kots/{id}/start` · `/ready` | Start cooking; ready to serve (from `NEW` straight to ready is allowed) | `kot.update` | `{ version? }` | `KotDto` | 403, 404; 409 version ("Someone else changed K00002 a moment ago. …"); 422 `INVALID_STATUS_TRANSITION` ("K00002 is already ready.") | UPDATE ticket |

`MenuDto = { categories: [{ id, name, sortOrder, isActive, items: MenuItemDto[] }] }`. `MenuItemDto = { id, categoryId, code,
name, description, foodType: VEG | NON_VEG | EGG, price, isAvailable, isActive, sortOrder, variants: [{ id, name, price }],
modifierGroups: [{ id, name, minSelect, maxSelect, modifiers: [{ id, name, priceDelta }] }], version }`.
`SaveMenuItemRequest = { categoryId, code, name, description?, foodType, price, isActive, sortOrder, variants: [{ id?, name,
price }], modifierGroups: [{ id?, name, minSelect, maxSelect, modifiers: [{ id?, name, priceDelta }] }], version? }`.
`CreateOrderRequest = { orderType: DINE_IN | ROOM_SERVICE | TAKEAWAY, tableId?, unitId?, covers?, note?, items?:
OrderLineRequest[] }`; `OrderLineRequest = { menuItemId, variantId?, modifierIds?, quantity, note? }`.
`OrderDto = { id, number, orderType, status: OPEN | SETTLED | VOIDED, where, tableId, tableCode, unitId, unitName,
reservationId, reservationReference, guestName, covers, note, businessDate, items: [{ id, menuItemId, name, variantName,
foodType, modifiers, quantity, unitPrice, amount, note, status: NEW | SENT | VOIDED, kotId, kotNumber, kitchenStatus,
voidReason, voidedByName }], kots: [{ id, number, status, sentAt, readyAt, servedAt, lines }], subtotal, discountKind,
discountValue, discountAmount, discountReason, discountedByName, net, taxes: DocumentTaxDto[], taxAmount, total, taxNote,
settlement: PAID | ROOM_CHARGE | null, paymentMethod, paymentReference, folioId, folioNumber, settledAt, settledByName,
voidedAt, voidedByName, voidReason, openedByName, createdAt, nextActions, version }`. `FloorDto = { today, tables: [{ id,
code, seats, area, order: OrderSummaryDto | null }], otherOrders: OrderSummaryDto[] }`. `KitchenDto = { now, tickets: [{ id,
number, orderId, orderNumber, where, status, sentAt, sentByName, startedAt, readyAt, note, items: [{ id, name, quantity,
modifiers, note, foodType, voided, voidReason }], version }] }`.

**Changes to earlier endpoints.** A folio line can now have source `RESTAURANT` (the room charge; it shows on the bill,
is paid at check-out and invoiced with the stay like any FOOD charge). The cash drawer's movements add `POS_SALE` (cash
paid at the restaurant, with the order).

## Built endpoints (Sprint 15 — inventory and recipe costing)

All under `/api/v1/properties/{propertyId}/inventory`. Stock is counted per **item** (in one unit: g, kg, ml, l, pcs or
dozen) and **store** (location); one store is the **kitchen**. Stock changes only by adding a row to the **ledger**
(`stock_movements`, append-only): an opening quantity, stock **received** (at a cost, which moves the item's moving-average
unit cost), **wastage** / expired / damaged, a **correction**, a **transfer** between stores, a physical **count**'s
difference, or a **served dish** using its recipe (`CONSUMPTION`, automatic when the waiter serves a kitchen ticket —
Sprint 14's `POST /orders/{id}/kots/{kotId}/serve`). On hand per item and store is kept by a database trigger as the sum of
the ledger, so two changes at the same moment both count. Every change a person makes needs a **reason** and is audited
(brief §58); stock out can't take a store below zero (a served dish can: the restaurant never stops serving because a count
is behind, and the screen asks for a count). A **recipe** is what one portion of a dish uses, in any unit of each item's
kind; a portion (Half / Full) can have its own, which wins over the dish's. **Food cost %** = recipe cost ÷ price × 100;
**contribution** = price − cost; **margin %** = contribution ÷ price × 100 (menu prices before GST). How it works:
[system-design.md → 4.25](architecture/system-design.md#425-the-stock-ledger-recipes-and-food-cost).

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /inventory` | Units, groups, stores, and every item with its on hand per store, value, `low` (at or below the reorder level) and `negative` | `inventory.read` | — | `InventoryDto` | 403 | — |
| `GET /inventory/movements?itemId&type&from&to&take` | The ledger, newest first (default 100, at most 500), with each row's source (`SA00004`, `SC00002`) and note ("O00012 · K00009 · 3× Paneer Tikka (Full)") | `inventory.read` | — | `StockMovementListDto` | 403 | — |
| `POST /inventory/categories` · `PUT /inventory/categories/{id}` | A stock group | `inventory.manage` | `SaveInventoryCategoryRequest` | 201 / 200 `InventoryCategoryDto` | 400 `Name` ("There is already a stock group called Dairy."), `SortOrder`; 403, 404 | INSERT/UPDATE |
| `POST /inventory/locations` · `PUT /inventory/locations/{id}` | A store. Marking one the kitchen takes the mark off the old one | `inventory.manage` | `SaveStockLocationRequest` | 201 / 200 `StockLocationDto` | 400 `Code` ("Another store already has the code KITCHEN."), `Name`, `IsActive` ("Kitchen still has stock. Move it to another store (a transfer) or count it to zero before switching the store off."; "The kitchen store can't be switched off …"); 403, 404 | INSERT/UPDATE |
| `POST /inventory/items` · `PUT /inventory/items/{id}` | A stock item; a new one may come with its opening stock (into the kitchen unless another store is given; a property with no store gets a Kitchen). `version` optional on PUT | `inventory.manage` | `SaveInventoryItemRequest` | 201 / 200 `InventoryItemDto` | 400 `Code` ("Another stock item already has the code PANEER. …"), `Name`, `CategoryId`, `UnitId` ("Paneer already has stock movements in kg, so its unit can't change. Add a new item for the new unit."), `UnitCost`, `ReorderLevel`, `OpeningQuantity`, `IsActive` ("Paneer is used in a recipe. Take it out of the recipes first, then switch it off."); 403, 404; 409 ("Someone else changed Paneer a moment ago. …") | INSERT/UPDATE item (+ OPENING movement) |
| `POST /inventory/adjustments` | Record a change of stock with its reason: `RECEIVED`, `WASTAGE`, `EXPIRED`, `DAMAGED`, `CORRECTION` (signed) or `TRANSFER` (to `toLocationId`). Numbered `SA00001` … | `inventory.adjust` | `CreateStockAdjustmentRequest` | 201 `StockAdjustmentDto` | 400 `Reason` ("Say why, in 3–300 characters (e.g. Spoilt in the fridge, Supplier delivery bill 4411). Every stock change needs a reason."), `Quantity` ("Kitchen has only 0.5 kg of Onion and capsicum, so 1.5 kg can't be taken out."), `Kind`, `ItemId`, `LocationId`, `ToLocationId` ("Choose the store the stock goes to."), `UnitCost`; 403 | INSERT adjustment, movement(s) ("SA00004 expired: 0.5 kg Paneer in Kitchen — Past its date"); UPDATE item cost when received |
| `POST /inventory/counts` | A physical count of one store: each line's difference from the system is posted as a `COUNT` movement. Numbered `SC00001` … per line | `inventory.adjust` | `CreateStockCountRequest` | 201 `StockCountResultDto` | 400 `LocationId`, `Lines` ("An item is counted twice. …"), `Lines[i].CountedQuantity`, `Lines[i].Reason` ("Paneer: the system has 5 kg and you counted 4.8 kg. Say why they differ (e.g. spillage, not recorded)."); 403 | INSERT counts, movements ("Stock count of Kitchen: 2 items counted, 1 changed") |
| `GET /inventory/recipes` | Every dish on the menu (each portion of a dish with portions) with the recipe it is made by, its cost, food cost %, contribution and margin %; and the units | `recipe.read` | — | `RecipeListDto` | 403 | — |
| `PUT /inventory/recipes` | Save the recipe of a dish (`variantId` null) or of one portion; an empty list of ingredients takes it off. Returns the dish's costed rows | `recipe.manage` | `SaveRecipeRequest` | `CostedDishDto[]` | 400 `Ingredients` ("Add at least one ingredient to the recipe of Paneer Tikka."), `Ingredients[i].ItemId` ("Paneer is in the recipe twice. Put the whole quantity on one line."), `Ingredients[i].UnitId` ("Paneer is kept in kg, so its quantity must be in kg or g."), `Ingredients[i].Quantity`, `MenuItemId`, `VariantId`; 403; 409 ("Someone else changed the recipe of Paneer Tikka (Half) a moment ago. …") | INSERT/UPDATE recipe (+ ingredients) |
| `GET /inventory/food-cost?from&to` | The food-cost report (property dates; default this month to today): every costed dish, what was served in the period (sold, sales before GST and discount, the value of the stock it used), the three highest and lowest margins, dishes without a recipe, and stock written off | `recipe.read` | — | `FoodCostReportDto` | 400 `from` ("The start date must be on or before the end date."), `to` (a period of at most a year); 403 | — |

`InventoryDto = { units: [{ id, code, name, dimension: MASS | VOLUME | COUNT, factor }], categories: [{ id, name, sortOrder,
isActive }], locations: [{ id, code, name, isKitchen, sortOrder, isActive }], items: InventoryItemDto[], value, lowCount }`.
`InventoryItemDto = { id, categoryId, categoryName, code, name, unitId, unit, dimension, unitCost, reorderLevel, isActive,
onHand, value, low, negative, levels: [{ locationId, locationCode, onHand }], version }`.
`SaveInventoryItemRequest = { categoryId, code, name, unitId, unitCost, reorderLevel?, isActive, openingQuantity?,
openingLocationId?, version? }`. `CreateStockAdjustmentRequest = { kind, itemId, locationId, toLocationId?, quantity (in the
item's unit; positive, signed for a correction), unitCost? (received only; empty = the item's cost), reason }`.
`StockAdjustmentDto = { id, number, kind, itemId, itemName, unit, locationId, locationName, toLocationId, toLocationName,
quantity (as applied to locationId), unitCost, value, reason, onHandAfter, byName, createdAt }`.
`CreateStockCountRequest = { locationId, lines: [{ itemId, countedQuantity, reason? }] }`.
`StockMovementDto = { id, at, itemId, itemName, unit, locationCode, type: OPENING | RECEIVED | CONSUMPTION | WASTAGE |
ADJUSTMENT | COUNT | TRANSFER_OUT | TRANSFER_IN, quantity, unitCost, value, onHandAfter, source, note, byName }`.
`SaveRecipeRequest = { menuItemId, variantId?, note?, ingredients: [{ itemId, quantity, unitId }], version? }`.
`CostedDishDto = { menuItemId, variantId, name, category, foodType, price, ownRecipe, recipe: { id, menuItemId, variantId, note,
ingredients: [{ id, itemId, itemCode, itemName, quantity, unitId, unit, itemQuantity, itemUnit, cost }], cost, version } |
null, cost, foodCostPercent, contribution, marginPercent }`. `FoodCostReportDto = { from, to, lines: [{ menuItemId,
variantId, name, category, price, cost, foodCostPercent, contribution, marginPercent, sold, revenue, costOfSales,
actualFoodCostPercent }], highest, lowest, noRecipe: string[], sold, revenue, costOfSales, foodCostPercent, wastage }`.

**Changes to earlier endpoints.** `POST /orders/{id}/kots/{kotId}/serve` (Sprint 14) now also takes each served dish's
recipe out of the kitchen store, in the same transaction (one `CONSUMPTION` movement per ingredient, valued at the item's
cost then). A line voided after it was served keeps its stock used (it was cooked). Add-ons (modifiers) use no stock yet.

## Built endpoints (Sprint 16 — suppliers and purchasing)

All under `/api/v1/properties/{propertyId}/purchasing`. The cycle: a **purchase request** (`PR00001`, what a store needs)
→ a **purchase order** (`PO00001`, to one supplier, raised from a request or on its own) → **approval** → **goods received
notes** (`GRN00001`, one per delivery; a part delivery leaves the rest *to come*) → the **supplier's bill** (`SI00001`,
recorded against what came in) → **payments** (`SP00001`). An order whose total (with GST) is over the property's approval
limit (`purchasing.approval_limit`, default ₹10,000) waits for someone with `purchase.approve` who did not raise or submit
it; within the limit it is approved as soon as it is submitted. Each goods received line adds a `RECEIVED` row to the stock
ledger (Sprint 15) at the order's price before GST, which moves the item's average cost. A supplier's state comes from the
first two digits of their GSTIN (checked, check digit included); a bill is CGST + SGST when the supplier is in the
property's GST state, else IGST; an unregistered supplier's bill carries no GST. Input tax counts as claimable only when the
property has a regular GST registration and the supplier a GSTIN. How it works:
[system-design.md → 4.26](architecture/system-design.md#426-purchasing-approval-goods-received-and-supplier-bills).

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /suppliers` | Suppliers with GSTIN, state, payment terms, what is owed and overdue | `supplier.read` | — | `SupplierDto[]` | 403 | — |
| `POST /suppliers` · `PUT /suppliers/{id}` | A supplier. `version` on PUT | `supplier.manage` | `SaveSupplierRequest` | 201 / 200 `SupplierDto` | 400 `Gstin` ("The last character (check digit) should be S for this GSTIN; check it for a typing mistake."; "Goa Dairy Co-operative already has the GSTIN …"), `StateCode` (differs from the GSTIN's), `Code` ("Another supplier already has the code FRESH-VEG."), `Name`, `Phone`, `Email`, `PaymentTermsDays`; 403, 404; 409 | INSERT/UPDATE |
| `GET /requests?status` | Purchase requests, newest first, with their order once raised | `purchase.read` | — | `PurchaseRequestDto[]` | 403 | — |
| `POST /requests` | Ask for stock for a store | `purchase.create` | `CreatePurchaseRequestRequest` | 201 `PurchaseRequestDto` | 400 `LocationId`, `NeededBy` (a past day), `Items` ("Add at least one item."), `Items[i].ItemId` ("Lime is on two lines. …"), `Items[i].Quantity`; 403 | INSERT |
| `POST /requests/{id}/cancel` | Cancel an open request, with the reason | `purchase.create` | `PurchaseReasonRequest` | `PurchaseRequestDto` | 400 `Reason`; 403, 404; 409 (already ordered or cancelled; someone else changed it) | UPDATE |
| `GET /orders?status&supplierId` | Orders, newest first, with the approval limit and how many wait for approval | `purchase.read` | — | `PurchaseOrderListDto` | 403 | — |
| `GET /orders/{id}` | One order with its lines (ordered, received, to come, billed), deliveries and bills | `purchase.read` | — | `PurchaseOrderDto` | 403, 404 | — |
| `POST /orders` · `PUT /orders/{id}` | Create (optionally from a request, which becomes ORDERED) or edit a draft. Lines: quantity, price before GST, GST rate (0, 0.25, 3, 5, 12, 18, 28, 40) | `purchase.create` | `SavePurchaseOrderRequest` | 201 / 200 `PurchaseOrderDto` | 400 `SupplierId` ("Mapusa Fresh Vegetables is switched off. …"), `LocationId`, `RequestId` (already ordered), `ExpectedDate`, `Lines[i].GstRate` ("Anjuna Fishermen has no GSTIN, so … 0 %"), `Lines[i].ItemId`, `Lines[i].Quantity`, `Lines[i].UnitPrice`; 403, 404; 409 (not a draft; someone else changed it) | INSERT/UPDATE |
| `POST /orders/{id}/submit` | Submit a draft: approved at once when within the limit, else waits for approval | `purchase.create` | `PurchaseOrderStepRequest` | `PurchaseOrderDto` | 403, 404; 409 ("PO00002 is waiting for approval; only a draft can be submitted.") | UPDATE |
| `POST /orders/{id}/approve` | Approve an order waiting for approval | `purchase.approve` | `PurchaseOrderStepRequest` | `PurchaseOrderDto` | 403, 404; 409 `SELF_APPROVAL` ("You raised PO00002, so you can't approve it yourself. …"), `CONCURRENCY_CONFLICT` | UPDATE |
| `POST /orders/{id}/reject` | Send it back to draft with the reason | `purchase.approve` | `PurchaseReasonRequest` | `PurchaseOrderDto` | 400 `Reason`; 403, 404; 409 | UPDATE |
| `POST /orders/{id}/cancel` | Cancel an order nothing has come in on, with the reason (its request opens again) | `purchase.create` | `PurchaseReasonRequest` | `PurchaseOrderDto` | 400 `Reason`; 403, 404; 409 (goods have come in: close it instead) | UPDATE |
| `POST /orders/{id}/close` | Close a part-received order: the rest is not coming | `purchase.create` | `PurchaseReasonRequest` | `PurchaseOrderDto` | 400 `Reason`; 403, 404; 409 | UPDATE |
| `POST /orders/{id}/receipts` | A delivery: per line how much came in (never more than is still to come), into which store, the challan number. Adds the stock | `grn.create` | `ReceiveGoodsRequest` | 201 `PurchaseOrderDto` | 400 `Lines[i].Quantity` ("Only 60 kg of Mixed vegetables is still to come on PO00001 (100 kg of 160 kg already in). …"), `Lines` (a line twice), `ReceivedOn` (future; before the order), `LocationId`; 403, 404; 409 (not approved; someone else received at the same moment) | INSERT GRN, lines, movements ("GRN00001 · PO00001 · Mapusa Fresh Vegetables") |
| `POST /orders/{id}/invoices` | The supplier's bill against what came in and is not billed yet; a price that differs from the order is flagged | `supplier_invoice.manage` | `RecordSupplierInvoiceRequest` | 201 `SupplierInvoiceDto` | 400 `SupplierNumber` (already recorded for this supplier), `InvoiceDate`, `DueDate`, `Lines[i].Quantity` ("Only 15 kg of Onion and capsicum has come in and not been billed on PO00001. …"), `Lines[i].GstRate`; 403, 404; 409 | INSERT bill, lines |
| `GET /invoices?status&supplierId&from&to` | Supplier bills (status `OPEN` = not fully paid) with what is still to pay, overdue and input GST | `purchase.read` | — | `SupplierInvoiceListDto` | 400 `to`; 403 | — |
| `POST /invoices/{id}/payments` | Pay a bill (all or part). Cash comes out of the open cash drawer as a pay-out. Send an `Idempotency-Key` header: the same key returns the first payment | `supplier_invoice.manage` | `PaySupplierRequest` | 201 `SupplierInvoiceDto` | 400 `Reference` ("Enter the UTR (bank transfer or UPI) or the cheque number, …"), `Amount` ("SI00001 has ₹1,400 left to pay, so ₹2,000 is too much."; more than the drawer holds), `Method` (no open drawer), `PaidOn`; 403, 404; 409 `INVOICE_PAID`, `CONCURRENCY_CONFLICT` | INSERT payment (+ drawer pay-out), UPDATE bill |

`SupplierDto = { id, code, name, gstin, stateCode, stateName, contactName, phone, email, address, paymentTermsDays, note,
isActive, outstanding, overdue, version }`. `SaveSupplierRequest = { code, name, gstin?, stateCode? (needed only without a
GSTIN), contactName?, phone?, email?, address?, paymentTermsDays, note?, isActive, version? }`.
`CreatePurchaseRequestRequest = { locationId, neededBy?, note?, items: [{ itemId, quantity, note? }] }`.
`PurchaseReasonRequest = { reason, version }`. `PurchaseOrderStepRequest = { note?, version }`.
`SavePurchaseOrderRequest = { supplierId, locationId, requestId?, expectedDate?, note?, version?, lines: [{ itemId, quantity,
unitPrice, gstRate }] }`. `PurchaseOrderDto = { id, number, supplierId, supplierName, supplierGstin, supplierState, requestId,
requestNumber, locationId, locationName, orderDate, expectedDate, status: DRAFT | PENDING_APPROVAL | APPROVED |
PARTLY_RECEIVED | RECEIVED | CLOSED | CANCELLED, intraState, note, subtotal, taxTotal, total, approvalLimit, needsApproval,
submittedAt, submittedBy, submittedByName, approvedAt, approvedByName, approvalNote, rejectedReason, endReason, lines: [{ id,
itemId, itemCode, itemName, unit, quantity, unitPrice, gstRate, taxableValue, tax, total, received, remaining, billed, toBill }],
receipts: GrnDto[], invoices: SupplierInvoiceDto[], createdByName, createdAt, version }`.
`ReceiveGoodsRequest = { receivedOn?, locationId?, challanNumber?, note?, lines: [{ orderItemId, quantity }] }`.
`RecordSupplierInvoiceRequest = { supplierNumber, invoiceDate, dueDate? (default: the supplier's payment terms), note?, lines:
[{ orderItemId, quantity, unitPrice, gstRate }] }`. `SupplierInvoiceDto = { id, number, supplierId, supplierName,
supplierGstin, orderId, orderNumber, supplierNumber, invoiceDate, dueDate, intraState, itcEligible, taxableValue, cgst, sgst,
igst, inputTax, total, amountPaid, balance, status: UNPAID | PART_PAID | PAID, overdue, note, lines: [{ orderItemId,
itemName, unit, quantity, unitPrice, orderPrice, priceDiffers, gstRate, taxableValue, cgst, sgst, igst, total }], payments:
[{ id, number, paidOn, amount, method: CASH | BANK_TRANSFER | UPI | CHEQUE, reference, note, byName, createdAt }], byName, createdAt,
version }`. `GrnDto = { id, number, receivedOn, locationName, challanNumber, note, value, lines: [{ orderItemId, itemName, unit,
quantity, unitCost, value }], byName, createdAt }`.
`PaySupplierRequest = { amount, paidOn?, method, reference? (needed except for cash), note?, version }`.

**Changes to earlier endpoints.** `GET /inventory/movements` (Sprint 15) shows a goods received row's source as its GRN
number and its note as "PO00001 · Mapusa Fresh Vegetables". `GET /cash-drawer` and `GET /cash-drawer/sessions/{id}` (Sprint 10) list a cash supplier payment
as a `PAY_OUT` movement.

## Built endpoints (Sprint 17 — employees and roster)

All under `/api/v1/properties/{propertyId}`. **Employees** (`E00001` …) belong to a department and may have a job title, a
weekly day off (ISO day 1 Monday … 7 Sunday) and, optionally, a linked staff account (one employee per account). An
employee profile **never carries pay**: pay lives in its own table (`employee_compensation`, append-only) behind its own
endpoints, readable only with `employee_salary.read` (owner and HR; a general manager and a manager can't). Documents
(PAN, bank account, passport …) need `employee_document.read`: numbers are stored encrypted and listed masked, opening one
writes a `SENSITIVE_READ` audit row, and for Aadhaar only the last 4 digits are ever kept. The **roster** is one week
(Monday–Sunday) of shifts, each from a **shift template** (department, times, break, how many people it needs every day)
or with its own times. The database refuses two scheduled shifts of one person that overlap (`EXCLUDE USING gist` on the
shift's period) unless the manager allowed it with a reason; the API answers `409 SHIFT_CLASH` naming the other shift.
**Shortages** are template × day with fewer people than it needs. How it works:
[system-design.md → 4.27](architecture/system-design.md#427-roster-clash-detection-and-shortages).

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /departments` | Departments with their job titles and how many active people work in each | `employee.read` | — | `DepartmentDto[]` | 403 | — |
| `POST /departments` · `PUT /departments/{id}` | A department. `version` on PUT | `employee.update` | `SaveDepartmentRequest` | 201 / 200 `DepartmentDto` | 400 `Code` ("Another department already has the code FRONT."), `Name`, `IsActive` ("2 active employees work in Kitchen. …"); 403, 404; 409 | INSERT/UPDATE |
| `POST /designations` · `PUT /designations/{id}` | A job title in a department | `employee.update` | `SaveDesignationRequest` | 201 / 200 `DepartmentDto` | 400 `DepartmentId`, `Name` ("Front office already has the job title Receptionist."); 403, 404; 409 | INSERT/UPDATE |
| `GET /employees?q&departmentId&status` | Employees (status `ACTIVE` by default, `LEFT` or `ALL`); `q` searches name, code, phone, email | `employee.read` | — | `EmployeeDto[]` | 403 | — |
| `GET /employees/accounts` | Staff accounts that can be linked, with whom each is linked to now | `employee.update` | — | `LinkableAccountDto[]` | 403 | — |
| `GET /employees/{id}` | One profile (no pay) | `employee.read` | — | `EmployeeDto` | 403, 404 | — |
| `POST /employees` · `PUT /employees/{id}` | Add (the code is given) or edit an employee | `employee.create` / `employee.update` | `SaveEmployeeRequest` | 201 / 200 `EmployeeDto` | 400 `FirstName`, `Phone`, `Email`, `DepartmentId` (switched off), `DesignationId` ("Housekeeper is a job title in another department. Choose one in Front office."), `JoinedOn` (more than 90 days ahead), `DateOfBirth` (under 18), `UserId` ("The account asha@… is already linked to Asha Naik (E00001). An account belongs to one employee."), `WeeklyOff`; 403, 404; 409 | INSERT/UPDATE |
| `POST /employees/{id}/leave` | Mark them as left: the last working day and the reason | `employee.update` | `EmployeeLeftRequest` | `EmployeeDto` | 400 `LeftOn` ("Asha Naik is on the roster after Fri 6 Nov: Sat 7 Nov, Sun 8 Nov. Remove those shifts from the roster first."; before joining), `Reason`; 403, 404; 409 | UPDATE |
| `GET /employees/{id}/shifts` | Their shifts from today on | `roster.read` | — | `RosterShiftDto[]` | 403, 404 | — |
| `GET /employees/{id}/compensation` | Today's pay and every entry, newest first | `employee_salary.read` | — | `EmployeeCompensationDto` | 403 (a manager), 404 | — |
| `POST /employees/{id}/compensation` | Record pay from a date (a correction: record again with the same date; the newest wins) | `employee_salary.read` + `employee.update` | `RecordCompensationRequest` | 201 `EmployeeCompensationDto` | 400 `EffectiveFrom` ("Asha Naik joined on Mon 2 Jun 2025; pay can start on that day or later."), `PayBasis`, `Basic`, `Allowances`; 403, 404 | INSERT (amounts shown as `***`) |
| `GET /employees/{id}/documents` | Their documents, numbers masked | `employee_document.read` | — | `EmployeeDocumentDto[]` | 403, 404 | — |
| `POST /employees/{id}/documents` | Add a document: `multipart/form-data` with `documentType`, `number?`, `expiresOn?`, `note?`, `file?` (JPEG, PNG or PDF, at most 5 MB) | `employee_document.read` + `employee.update` | form | 201 `EmployeeDocumentDto` | 400 `Number` ("A PAN is 10 characters: 5 letters, 4 digits, 1 letter, e.g. ABCPK1234F."; Aadhaar: 12 digits or the last 4), `File`, `ExpiresOn`, `DocumentType`; 403, 404 | INSERT (number hidden) |
| `GET /employees/{id}/documents/{docId}` · `…/file` | Open one: the number in full (Aadhaar: last 4 only), or the scan | `employee_document.read` | — | `EmployeeDocumentDetailDto` / the file | 403, 404 | `SENSITIVE_READ` |
| `POST /employees/{id}/documents/{docId}/remove` | Remove it with the reason: the number and the scan are wiped, the entry stays | `employee_document.read` + `employee.update` | `RemoveEmployeeDocumentRequest` | `EmployeeDocumentDto` | 400 `Reason`; 403, 404; 422 (already removed) | UPDATE |
| `GET /roster?week` | One week (Monday given or this week): departments, people, templates, shifts, shortages, warnings | `roster.read` | — | `RosterWeekDto` | 403 | — |
| `GET /roster/mine?from` | The caller's own shifts, from their linked employee | `roster.self` | — | `MyRosterDto` | 403, 404 (account not linked) | — |
| `GET /roster/templates` · `POST` · `PUT /roster/templates/{id}` | Shift templates. An end time at or before the start ends the next day | `roster.read` / `roster.manage` | `SaveShiftTemplateRequest` | `ShiftTemplateDto[]` / 201 / 200 `ShiftTemplateDto` | 400 `Code`, `DepartmentId`, `EndTime` (longer than 16 hours), `BreakMinutes`, `MinStaff`; 403, 404; 409 | INSERT/UPDATE |
| `POST /roster/shifts` · `PUT /roster/shifts/{id}` | Put a person on a shift, or change it | `roster.manage` | `SaveRosterShiftRequest` | 201 / 200 `RosterShiftDto` | 400 `WorkDate` (more than a month ago; over 180 days ahead; before they joined), `EmployeeId` (left), `TemplateId` (switched off), `StartTime`, `EndTime`, `BreakMinutes`, `OverlapReason` ("Say why Asha works two shifts at once, e.g. a handover …"); 403, 404; 409 `SHIFT_CLASH` ("Asha Naik is already on Front office · Morning 07:00–15:00 on Tue 3 Nov. One person can't work two shifts at once: …", `details.shiftId`), `CONCURRENCY_CONFLICT` | INSERT/UPDATE |
| `POST /roster/shifts/{id}/cancel` | Take a shift off the roster, with the reason | `roster.manage` | `CancelRosterShiftRequest` | 204 | 400 `Reason`; 403, 404; 409; 422 (already removed) | UPDATE |
| `POST /roster/copy-week` | Copy one week into another day for day; clashes, days off the job and switched-off templates are skipped and listed | `roster.manage` | `CopyRosterWeekRequest` | `CopyRosterWeekResultDto` | 400 `ToWeek` (same week; passed; over 180 days), `FromWeek` (nobody on it); 403 | INSERT per shift |

`EmployeeDto = { id, code, firstName, lastName, fullName, phone, email, dateOfBirth, departmentId, departmentName,
designationId, designationName, employmentType: FULL_TIME | PART_TIME | CONTRACT | SEASONAL, joinedOn, weeklyOff, userId,
userEmail, emergencyContactName, emergencyContactPhone, address, note, status: ACTIVE | LEFT, leftOn, leavingReason, version }`.
`SaveEmployeeRequest` is the same without the code, names of linked things and status. `EmployeeLeftRequest = { leftOn,
reason, version }`. `RecordCompensationRequest = { effectiveFrom, payBasis: MONTHLY | DAILY, basic, allowances, pfApplicable,
esiApplicable, note? }`. `EmployeeCompensationDto = { employeeId, employeeName, current: CompensationDto?, history:
CompensationDto[] }`, `CompensationDto = { id, effectiveFrom, payBasis, basic, allowances, gross, pfApplicable, esiApplicable,
note, isCurrent, replaced, byName, createdAt }`. `EmployeeDocumentDto = { id, documentType: AADHAAR | PAN | BANK_ACCOUNT |
PASSPORT | DRIVING_LICENCE | OFFER_LETTER | OTHER, maskedNumber, expiresOn, expired, note, hasFile, fileName, contentType,
sizeBytes, status: ACTIVE | REMOVED, removedReason, createdAt, byName }`.
`ShiftTemplateDto = { id, departmentId, departmentName, code, name, startTime, endTime, crossesMidnight, breakMinutes,
paidMinutes, minStaff, isActive, version }`. `SaveRosterShiftRequest = { employeeId, workDate, templateId?, departmentId?,
startTime?, endTime?, breakMinutes?, allowOverlap, overlapReason?, note?, version? }` (with a template its times are used
unless given). `RosterShiftDto = { id, employeeId, employeeCode, employeeName, departmentId, departmentName, templateId,
templateName, workDate, startTime, endTime, startsAt, endsAt, breakMinutes, paidMinutes, overlapAllowed, overlapReason, note,
version }`. `RosterWeekDto = { weekStart, weekEnd, today, departments, employees: [{ id, code, name, departmentId,
designationName, weeklyOff, joinedOn, leftOn, paidMinutes, shifts }], templates, shifts, shortages: [{ date, templateId,
templateName, departmentId, departmentName, startTime, endTime, needed, rostered, missing }], warnings: [{ kind: DAY_OFF |
LONG_WEEK | OVERLAP_ALLOWED, date, employeeId, shiftId, message }] }`. `CopyRosterWeekRequest = { fromWeek, toWeek }` (Mondays),
`CopyRosterWeekResultDto = { copied, skipped: string[], week: RosterWeekDto }`.

## Built endpoints (Sprint 18 — attendance and timesheets)

All under `/api/v1/properties/{propertyId}`. A person **clocks in and out on their phone**: the browser sends its position
and the **server** measures the distance to each active **geofence** (a circle: centre and a radius of 20–2000 m). The
phone's own accuracy counts up to 50 m; a reading worse than 500 m is refused under `AccuracyM`. Outside every area the
answer is `422 OUTSIDE_GEOFENCE` with how far away and which area (`details: { distanceM, radiusM, geofence }`). With no
geofence set up, location isn't checked; breaks never check it. A manager (`attendance.update`) can **record** a clock
event for someone, with a reason (e.g. no signal); it shows as a manager entry (`source: MANAGER`, `locationStatus:
OVERRIDE`). Nobody records their own. Each person has one **timesheet** per week (Monday–Sunday), made when first needed,
with one line per day worked out in the property's time zone: worked = out − in − breaks, late and left early against
the rostered shift, overtime beyond the shift's paid minutes (beyond 8 h without a shift), a night shift on the day it
starts, and a clock-in with no clock-out after 16 hours shown as `MISSING_OUT`. States: `DRAFT` → `SUBMITTED` →
`APPROVED` or `REJECTED` (with a reason; the person fixes and submits again) → `LOCKED` for payroll. **Corrections** set a
day's times with a reason and keep the old ones; a corrected day is never recalculated from clock events; nothing changes
once `LOCKED`. Nobody approves, rejects, locks or corrects their own timesheet. How it works:
[system-design.md → 4.28](architecture/system-design.md#428-attendance-geofence-and-timesheet-maths).

| Method & URL | Purpose | Permission | Request | Response | Errors | Audit |
|---|---|---|---|---|---|---|
| `GET /attendance/me` | The caller's attendance now: clocked in or not, today's and the next shift, the day's entries and hours, this week's timesheet | `attendance.self` | — | `MyAttendanceDto` | 403, 404 (account not linked to an employee) | — |
| `POST /attendance/me/clock` | Clock in, start or end a break, clock out | `attendance.self` | `ClockRequest` | `MyAttendanceDto` | 400 `Action`, `Latitude` ("We need your phone's location to check you are at the resort. …"), `AccuracyM` ("Your phone only knows where you are to within 1.2 km. …"); 403, 404; 409 `ATTENDANCE_STATE` ("You clocked in at 08:03 and haven't clocked out yet."), `TIMESHEET_CLOSED` (the week is approved or locked); 422 `OUTSIDE_GEOFENCE` ("You are 1.1 km from Main gate; clocking in counts within 300 m of it. Do it at the resort, or ask your manager to record it with a reason."), `NOT_EMPLOYED` | INSERT |
| `GET /attendance?date` | One day's board (today by default): everyone rostered or who clocked in, their state, in and out, late, manager entries; counts | `attendance.read` | — | `AttendanceDayDto` | 403 | — |
| `POST /attendance/events` | A manager records a clock event for someone, now or at a time up to 7 days back, with the reason | `attendance.update` | `ManagerClockRequest` | `AttendanceDayDto` | 400 `EmployeeId`, `Action`, `Reason` ("Say why you are recording this for them, 3–300 characters …"), `Time` (in the future; before their latest entry), `Date` (more than 7 days back; before they joined); 403, 404; 409 `ATTENDANCE_STATE`, `TIMESHEET_CLOSED`; 422 `OWN_ATTENDANCE` | INSERT |
| `GET /attendance/geofences` | The areas where clocking in counts | `attendance.read` | — | `GeofenceDto[]` | 403 | — |
| `POST /attendance/geofences` · `PUT /attendance/geofences/{id}` | An area: name, centre, radius, on or off. `version` on PUT | `attendance.update` | `SaveGeofenceRequest` | 201 / 200 `GeofenceDto` | 400 `Name` (2–60; "There is already an area called Main gate."), `Latitude`, `Longitude`, `RadiusM` ("The radius is 20–2000 metres."); 403, 404; 409 | INSERT/UPDATE |
| `GET /timesheets?week` | One week's timesheets for everyone (Monday given or this week) with counts per status | `attendance.read` | — | `TimesheetWeekDto` | 403 | — |
| `GET /timesheets/mine?week` | The caller's own timesheet for the week, made if missing | `timesheet.self` | — | `TimesheetDto` | 403, 404 (account not linked) | — |
| `GET /timesheets/{id}` | One timesheet: days, entries, corrections | `attendance.read` | — | `TimesheetDto` | 403, 404 | — |
| `POST /timesheets/{id}/submit` | Submit one's own week, with an optional note | `timesheet.self` | `SubmitTimesheetRequest` | `TimesheetDto` | 400 `Note`; 403 (not yours), 404; 409; 422 `INVALID_STATUS_TRANSITION`, `TIMESHEET_EMPTY`, `STILL_CLOCKED_IN` ("You are still clocked in since 08:03. Clock out first, then submit.") | UPDATE |
| `POST /timesheets/{id}/approve` | Approve a submitted week | `timesheet.approve` | `TimesheetVersionRequest` | `TimesheetDto` | 403, 404; 409; 422 `OWN_TIMESHEET`, `INVALID_STATUS_TRANSITION`, `TIMESHEET_OPEN_DAY` ("Tue 20 Oct has a clock-in at 14:01 with no clock-out. Correct that day first.") | UPDATE |
| `POST /timesheets/{id}/reject` | Send it back with what needs fixing | `timesheet.approve` | `RejectTimesheetRequest` | `TimesheetDto` | 400 `Reason`; 403, 404; 409; 422 `OWN_TIMESHEET`, `INVALID_STATUS_TRANSITION` | UPDATE |
| `POST /timesheets/{id}/lock` | Lock an approved week for payroll | `timesheet.approve` | `TimesheetVersionRequest` | `TimesheetDto` | 403, 404; 409; 422 `OWN_TIMESHEET`, `INVALID_STATUS_TRANSITION` | UPDATE |
| `POST /timesheets/{id}/corrections` | Set one day's in, out and break with the reason; the old times are kept | `attendance.update` (+ `timesheet.approve` once approved) | `CorrectTimesheetLineRequest` | `TimesheetDto` | 400 `WorkDate` (outside the week; before joining), `ClockIn`/`ClockOut` ("Give both the clock-in and the clock-out time, or leave both empty …"; in the future; over 16 hours), `BreakMinutes` (0–180; not shorter than the time worked), `Reason`; 403 (approved, without `timesheet.approve`), 404; 409 `TIMESHEET_LOCKED`, `CONCURRENCY_CONFLICT`; 422 `OWN_TIMESHEET` | INSERT correction + UPDATE |
| `POST /timesheets/approve-week` | Approve every submitted timesheet of a week; any that can't be approved are skipped and listed with the reason | `timesheet.approve` | `ApproveTimesheetWeekRequest` | `ApproveTimesheetWeekResultDto` | 400 `WeekStart`; 403 | UPDATE per timesheet |

`ClockRequest = { action: CLOCK_IN | BREAK_START | BREAK_END | CLOCK_OUT, latitude?, longitude?, accuracyM? }`.
`ManagerClockRequest = { employeeId, action, date?, time?, reason }` (local date and time at the property; default now).
`AttendanceEventDto = { id, eventType, occurredAt, time, workDate, locationStatus: INSIDE | NO_GEOFENCE | OVERRIDE,
distanceM, geofenceName, source: DEVICE | MANAGER, reason, recordedBy }`. `MyAttendanceDto = { employeeId, employeeName,
departmentName, state: OFF | ON_SHIFT | ON_BREAK, since, today, workDate, todayShift, nextShift, events, workedMinutes,
breakMinutes, missedClockOut, geofenceRequired, week: TimesheetSummaryDto? }`. `AttendanceDayDto = { date, today, onShift,
notIn, late, rows: [{ employeeId, employeeCode, employeeName, departmentId, departmentName, shift, state: NOT_YET | NOT_IN |
ON_SHIFT | ON_BREAK | DONE | MISSING_OUT, clockIn, clockOut, workedMinutes, lateMinutes, overridden, events }], geofences }`.
`GeofenceDto = { id, name, latitude, longitude, radiusM, isActive, version }`; `SaveGeofenceRequest` is the same without
the id. `TimesheetSummaryDto = { id, employeeId, employeeCode, employeeName, departmentName, weekStart, weekEnd, status:
DRAFT | SUBMITTED | APPROVED | REJECTED | LOCKED, workedMinutes, breakMinutes, overtimeMinutes, lateMinutes, earlyMinutes,
daysWorked, openDays, corrections, submittedAt, decidedAt, decidedBy, rejectionReason, lockedAt, actions: (submit | approve
| reject | lock | correct)[], version }`. `TimesheetDto = { sheet, note, submittedBy, lines: [{ workDate, shiftName,
shiftStart, shiftEnd, scheduledMinutes, clockIn, clockOut, clockOutNextDay, clockInAt, clockOutAt, breakMinutes,
workedMinutes, overtimeMinutes, lateMinutes, earlyMinutes, status: WORKED | ABSENT | ON_SHIFT | MISSING_OUT, overridden,
corrected, events }], corrections: [{ id, workDate, oldClockIn, oldClockOut, oldBreakMinutes, oldWorkedMinutes, newClockIn,
newClockOut, newBreakMinutes, newWorkedMinutes, reason, timesheetStatus, createdAt, by }] }`. `TimesheetWeekDto = { weekStart,
weekEnd, today, timesheets, counts }`. `SubmitTimesheetRequest = { note?, version? }`, `RejectTimesheetRequest = { reason,
version? }`, `TimesheetVersionRequest = { version? }`, `ApproveTimesheetWeekRequest = { weekStart }`,
`ApproveTimesheetWeekResultDto = { approved, skipped: string[], week }`. `CorrectTimesheetLineRequest = { workDate, clockIn?,
clockOut?, breakMinutes, reason, version? }` (a clock-out at or before the clock-in is the next day; both empty: not worked).

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
