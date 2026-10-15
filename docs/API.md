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
Check-in/check-out arrive in Sprint 7, refunds in Sprint 10. Reservations are never deleted; an enquiry is cancelled.

**Related changes.** `POST /blocks` returns 409 `BLOCK_OVERLAPS_RESERVATION` ("Room 103 is booked on R00002 …") when a
booking holds the room. `GET /guests/{id}/stays` lists real reservations with their `reference`; merging guests moves
their reservations to the surviving profile.

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
