# How Resort OS works: architecture and system design

This is the guide for someone joining the project. Read it once from top to bottom and you will know how the whole
code base fits together, why it is built the way it is, and where to look when you change something. It is updated
at the end of every sprint (see [How this guide stays current](#how-this-guide-stays-current)).

| Part | What it answers |
|---|---|
| [1. The system in one picture](#1-the-system-in-one-picture) | What runs where, and how a click becomes a database row |
| [2. The code base map](#2-the-code-base-map) | Which folder holds what, and the rules between layers |
| [3. Built sprint by sprint](#3-built-sprint-by-sprint) | Each sprint's architecture: tables, services, screens, diagram |
| [4. System design: how the hard parts work](#4-system-design-how-the-hard-parts-work) | Passwords, sign-in, permissions, tenants, audit, encryption, no double booking, idempotency, money, time, errors, room moves, the front desk, folios, GST |
| [5. Changing the system safely](#5-changing-the-system-safely) | Adding a table, an endpoint or a screen without breaking these guarantees |
| [6. Where to read more](#6-where-to-read-more) | The detailed documents behind each part |

Status: Sprints 01–09 built and accepted (October 2026); Sprint 10 (payments, refunds, cash drawer) built and in review. Anything marked *planned* is design that a later sprint
builds; [ARCHITECTURE.md](../ARCHITECTURE.md) holds the full target design and [decisions.md](decisions.md) the
decisions behind it.

---

## 1. The system in one picture

![System architecture](../../tools/handbooks/assets/architecture.png)

Resort OS is a **modular monolith**: one ASP.NET Core API (.NET 10), one PostgreSQL 16 database and one Angular 20
staff app. There are no microservices, queues or caches to run; every rule that matters is enforced in one process
and one database transaction, which keeps the system cheap to host and easy to reason about. External services
(payments, accounting, WhatsApp, OTAs) arrive in later sprints behind adapters.

```mermaid
flowchart LR
  B["Browser<br/>Angular staff app"] -- "HTTPS JSON /api/v1<br/>Bearer access token" --> A["Resort.Api<br/>ASP.NET Core"]
  B -- "HttpOnly refresh cookie<br/>(only /auth/refresh)" --> A
  A -- "EF Core + raw SQL<br/>one transaction per request" --> D[("PostgreSQL 16<br/>constraints, triggers,<br/>exclusion constraints")]
  A -- "encrypted bytes" --> F[("File store<br/>ID scans")]
```

### What happens on one request

Take "book Room 102 for tonight". The same path applies to every write.

1. The Angular page calls `ReservationsApi.create()`. `authInterceptor` (`frontend/src/app/core/http.ts`) adds the
   15-minute access token; if the API answers 401 it refreshes the token once from the HttpOnly cookie and retries.
2. The request enters the API pipeline (`backend/Resort.Api/Program.cs`):

   ![Request pipeline](../../tools/handbooks/assets/pipeline.png)

   correlation id → request log → exception handler → security headers → CORS → JWT check (signature, expiry and the
   user's **security stamp**) → rate limiter → **permission policy** for the property in the URL → **validator**
   (FluentValidation; every error names its field and says why).
3. The endpoint (`Api/Endpoints/ReservationEndpoints.cs`) only maps HTTP to a call on an **application service**
   (`Application/Reservations/ReservationService.cs`). All business rules live there or in the **domain**.
4. The service reads and writes through `AppDbContext`. A **tenant filter** limits every query to the caller's
   organisation; the **audit interceptor** writes an `audit_logs` row for each change inside the same transaction.
5. PostgreSQL enforces the rules a bug in C# must never break: foreign keys, checks, triggers (status graph,
   never-delete) and **exclusion constraints** (no room held twice on a night).
6. Errors come back as `{code, message, details, correlationId}`; the page shows each `details` entry under its field
   (`showFieldErrors`), and anything else next to the button, always with the reason.

---

## 2. The code base map

![Layers](../../tools/handbooks/assets/layers.png)

```
backend/
  Resort.Domain          Entities and pure rules: no database, no clock, no framework. Unit-tested.
                         e.g. Rates/QuoteEngine.cs, Reservations/Availability.cs, Guests/Guests.cs (GuestRules)
  Resort.Contracts       Request/response records (what the API accepts and returns)
  Resort.Application     One service per use case group, validators, permission codes, shared helpers
                         Common/ (errors, idempotency, concurrency, protection), Identity/, Properties/,
                         Accommodation/, Guests/, Rates/, Reservations/, FrontDesk/, Folios/, Tax/, Payments/, Audit/, Validation/
  Resort.Infrastructure  AppDbContext (EF Core, snake_case), SQL migrator, audit interceptor,
                         JWT + password hashing (Identity/Security.cs), field encryption, file store, dev seeder
  Resort.Api             Program.cs (pipeline), Endpoints/*.cs (one file per module),
                         Infrastructure/ (authorisation, problems/error mapping, current user, security headers)
  Resort.Tests           Unit tests + integration tests against a real PostgreSQL (one database per test class)
database/
  migrations/V###__*.sql The schema. Applied in order at API start; never edited once applied
  seed/dev/S###__*.sql   Demo data, Development only
  queries/check-sprint-NN.sql  Read-only checks the owner runs during review
frontend/src/app/
  core/                  auth, http (interceptor, error helpers), property context, guards, dates, models
  layout/                the shell (menu, header with the property's local time)
  features/<module>/     one folder per module: *.page.ts (screens), *.api.ts (HTTP), *.forms.ts (form rules)
frontend/e2e/            Playwright browser tests, one file per sprint (the review scripts)
docs/                    Everything you are reading; sprint plan in docs/sprints/sprints.yml
tools/handbooks/         Builds the two Word handbooks and the diagrams used here
```

Rules between the layers:

- Dependencies point inwards: `Api → Infrastructure → Application → Domain / Contracts`. The domain knows nothing
  about HTTP or SQL, which is why `QuoteEngine` and `AvailabilityEngine` can be tested with plain inputs.
- Business rules live in Application or Domain, never in endpoints or Angular components. The UI repeats a rule only
  to give a faster message; the API always checks again.
- The **database owns the schema**. EF Core maps onto the SQL files; EF migrations are not used. The rules that
  protect money and inventory are also enforced by the database, so they hold for any code that writes rows.
- Every module is a folder in each layer with the same name (`Reservations` in Domain, Application, Api, frontend).

---

## 3. Built sprint by sprint

Each sprint adds one module end to end: migration → domain → service → endpoints → screens → tests → docs. The
per-sprint guides in [docs/sprints/](../sprints/README.md) list every file; this section explains the design.

### Sprint 01: Foundation (sign-in, organisations, roles, audit)

```mermaid
flowchart LR
  U[users] --> UR[user_roles] --> R[roles] --> RP[role_permissions] --> P[permissions]
  UR -. "property_id NULL = whole organisation" .-> PR[properties]
  U --> RT["refresh_tokens<br/>(hash only, family)"]
  U --> LE["login_events<br/>(append-only)"]
  O[organizations] --> PR --> PS[property_settings]
  AL["audit_logs<br/>(append-only, same transaction)"]
```

![Foundation tables](../../tools/handbooks/assets/er-foundation.png)

- Migrations V001 (foundation), V002 (17 roles, 117 permissions), V003 (request context on audit rows).
- Code: `Application/Identity/AuthService.cs`, `UserService.cs`, `Infrastructure/Identity/Security.cs`,
  `Infrastructure/Persistence/AuditInterceptor.cs`, `Api/Infrastructure/Authorization.cs`.
- Screens: sign-in, forced password change, staff accounts, roles and permissions, audit trail, dashboard.
- Design detail: [4.1 Passwords](#41-passwords), [4.2 Sign-in and tokens](#42-sign-in-tokens-and-sessions),
  [4.3 Permissions](#43-permissions-and-roles), [4.4 Tenants](#44-one-database-many-organisations),
  [4.5 Audit](#45-the-audit-trail).

### Sprint 02: Accommodation (room types, rooms, dorm beds, blocks)

![Accommodation tables](../../tools/handbooks/assets/er-accommodation.png)

- Migration V004. A room type's `inventory_mode` decides how it is sold: **UNIT** (a whole room) or **BED** (a
  dormitory, sold per bed, so each bed is its own row in `beds`).
- `unit_blocks` take a room or one bed out of sale for a date range (maintenance, owner use) with a reason. Two
  exclusion constraints stop two active blocks overlapping on the same room or bed; `BlockService` locks the room row
  first so it can explain which block is in the way.
- Room status (`/rooms/status`) shows each room's own condition for a night (available, blocked, some beds blocked,
  not in use). It does not show bookings yet; that view comes with the calendar and front desk sprints.

### Sprint 03: Guests (profiles, ID documents, duplicates, privacy)

![Guest tables](../../tools/handbooks/assets/er-guests.png)

- Migration V005. Guests belong to the organisation and are shared by all its properties.
- Phones are stored as E.164 (`+919820012345`); `GuestRules.NormalizePhone` accepts the ways people type Indian
  numbers (98200 12345, 098200 12345, +91-98200-12345). Search uses generated columns (`search_text`, phone digits)
  with trigram indexes.
- **Duplicates**: before creating a profile the API looks for the same phone, the same email, the same full name, or
  (for an existing profile) the same ID document via a keyed hash. The user picks one or says "add anyway".
- **Merge** folds a duplicate into the survivor (documents, consents, preferences, bookings) and marks it MERGED;
  **anonymise** removes personal data but keeps the row so stays and invoices still add up. Profiles are never deleted.
- ID numbers and scans are encrypted: [4.6 Encryption](#46-encrypting-id-documents).

### Sprint 04: Rates and packages

![Rate tables](../../tools/handbooks/assets/er-rates.png)

- Migration V006. `rate_plan_prices` has one row per plan × room type × night; no row means "no price", and that
  night cannot be quoted or booked.
- `Domain/Rates/QuoteEngine.cs` prices a stay night by night: the base price covers the type's standard occupancy,
  extra adults and children pay their rates, dormitories charge per bed, minimum stay is read from the arrival night.
  It is pure and unit-tested.
- Packages split their price into room, food, transport and activity with the largest-remainder rule
  ([4.9 Money](#49-money)). A deferred trigger checks at COMMIT that shares add up to 100 % and amounts to the price.

### Sprint 05: Availability and reservations

![Reservation tables](../../tools/handbooks/assets/er-reservations.png)

- Migration V007: `reservations`, `reservation_units` (each room or bed held, with its stay as a `daterange`),
  `reservation_rate_breakdowns` (the price of every night, kept for invoicing), `reservation_guests`,
  `reservation_notes`, `reservation_status_history`, `reservation_status_transitions`, `idempotency_keys`,
  `property_counters`.
- `Domain/Reservations/Availability.cs` (`AvailabilityEngine`) is pure: given the rooms, what is held and blocked, it
  finds free rooms per night, allocates rooms or one bed per dormitory guest, and explains why something does not fit.
- `ReservationService` runs the booking transaction; [4.7](#47-no-double-booking) explains it and
  [booking-algorithm.md](booking-algorithm.md) gives the exact SQL.
- Screens: Reservations list, the five-step New booking wizard, the booking page (change dates or guests, confirm,
  cancel, no-show, notes, history). Date fields across the app show "15 Oct 2026" with a calendar
  (`frontend/src/app/core/dates.ts`); the form value stays the API's ISO date.

![Booking flow](../../tools/handbooks/assets/booking-flow.png)

### Sprint 06: Reservation calendar

![Two moves into the same room](../../tools/handbooks/assets/room-move-flow.png)

- Migration V008: one index, `ix_reservation_units_calendar (property_id, start_date, end_date) WHERE superseded_at IS
  NULL`. No new table: a move reuses Sprint 05's superseded-row history.
- `Application/Reservations/CalendarService.cs` is the calendar's read model: for a window of up to 62 days it reads
  the rooms with their beds, every current reservation row that overlaps the window (PENDING, CONFIRMED, CHECKED_IN,
  CHECKED_OUT; enquiries hold nothing and stay in the list), the guests and sources those rows name, and the active
  blocks. One indexed query per layer, never one per room or per booking. `paymentStatus` is `NONE` until payments
  arrive in Sprint 10.
- `ReservationService.ReassignAsync` moves one room (or dorm bed) of a booking to another for the same nights:
  [4.16](#416-moving-a-guest-to-another-room) explains the locking.
- Screens: `/reservations/calendar` (`features/reservations/calendar.page.ts`) with Day, Week and Month views. The grid
  (`calendar-grid.ts`) is a CSS grid, one row per room or dorm bed, bookings and blocks as bars; Angular CDK drag-drop
  with the axis locked vertically, so a drag can change the room but never the dates. `calendar.model.ts` holds the
  pure logic (windows, rows, placement, "why can't it go there") and is unit-tested, including the 7 rooms × 31 days
  render under 200 ms. `move-dialog.ts` shows a booking's status, source and payment and asks for the reason. Under
  760 px wide the same window is a list by day (arrives, in house, leaves, blocked).

### Sprint 07: Front desk (arrivals, check-in, check-out, walk-ins)

![Check-in and check-out](../../tools/handbooks/assets/check-in-flow.png)

- Migration V009: `reservations.checked_in_at` / `checked_out_at`, `unit_housekeeping_status` (the cleaning state of
  each room or dorm bed; no row means READY) and `unit_housekeeping_history` (append-only; the check-out rows are the
  events Sprint 13's housekeeping module will consume). Details: [schema-sprint-07.md](../database/schema-sprint-07.md).
- `Domain/FrontDesk/FrontDesk.cs`: the housekeeping states and the pure date rules (`FrontDeskRules.CheckInDateProblem`,
  `DepartureOnCheckOut`), unit-tested.
- `Application/FrontDesk/FrontDeskService.cs`: the lists (arrivals with late arrivals, departures with overdue guests,
  in house, the dashboard and rooms board), room options, assign/move, check-in, check-out, walk-in and setting a room's
  state. It reuses `ReservationService`'s booking and room-move steps (`BookAsync`, `MoveRoomAsync`) instead of
  copying them. `IFolioBalances` answers "what does this stay still owe"; it returns 0 until folios arrive in Sprint 8.
- Two property settings: `frontdesk.allow_checkin_when_not_ready` (default on, with a reason) and
  `frontdesk.allow_checkout_with_balance` (default off).
- Screens: `/front-desk/arrivals | departures | in-house | rooms` (`features/front-desk/front-desk.page.ts`, tiles and
  one list per tab, a rooms board with *Mark ready*), `/front-desk/walk-in` (stay, guest and check in on one screen),
  and Check in / Check out / Assign room on the booking page. `desk-actions.ts` is shared by both: one click when
  nothing is in the way, otherwise a window that says why and offers *Choose another room* or *Check in anyway*.
  [4.17](#417-the-front-desk) explains the rules.

### Sprint 08: Folio (the bill of a stay)

![The bill of a stay](../../tools/handbooks/assets/folio-flow.png)

- Migration V010: `folios` (one main folio per confirmed booking, more to split a bill), `folio_items` (immutable
  lines: CHARGE or REVERSAL, eight categories, source MANUAL / ROOM_NIGHT / PACKAGE) and `folio_item_taxes` (filled
  from Sprint 9). Triggers refuse UPDATE and DELETE; CHECKs and a trigger make every amount add up and every reversal
  cancel its charge exactly. Details: [schema-sprint-08.md](../database/schema-sprint-08.md).
- `Domain/Folios/Folios.cs`: categories, sources, the posting setting and `FolioMath` (line amount, package lines,
  negation), unit-tested.
- `Application/Folios/FolioPosting.cs`: the shared steps every writer uses (open the main folio, post room nights,
  reverse, sum the balance). `FolioService.cs`: list, read, post a charge or a package, reverse, split.
  `RoomChargeRun` (in FolioPosting.cs) is the nightly room charge; `Api/Infrastructure/RoomChargeJob.cs` runs it every
  15 minutes. `FolioBalances` replaces Sprint 7's placeholder, so the front desk's balance and the check-out rule are real.
- Hooks into earlier code: `ReservationService` opens the main folio when a booking becomes CONFIRMED;
  `FrontDeskService` posts room nights at check-in and at check-out, and reverses released nights on an early departure.
- Screens: `/folios` (search, in house / arriving / all) and `/folios/:id` (balance, sums by category, every line with
  who posted it, reversed lines struck through with the reason; Post charge, Move lines, Reverse for managers),
  plus a Bill button on the booking page and on the front desk lists. [4.18](#418-folios-the-bill-of-a-stay)
  explains the rules.

### Sprint 09: GST configuration and the tax engine

![GST as data with dates](../../tools/handbooks/assets/tax-flow.png)

- Migration V011: `gst_registrations` (the GSTIN a property bills under, from/to a date), `tax_categories` (what is
  taxed, its SAC code and the folio charge category it covers), `tax_rules` (a category's rule with dates and its
  source) and `tax_rule_slabs` (rates by value). EXCLUDE constraints stop overlapping periods; triggers refuse deletes,
  freeze slabs and let a rule change only its end date and verification. `fn_seed_tax_defaults` seeds accommodation and
  restaurant GST for every organisation, unverified. Details: [schema-sprint-09.md](../database/schema-sprint-09.md).
- `Domain/Tax/Tax.cs`: the entities, `TaxMath` (slab lookup, CGST/SGST or IGST split, rounding, a line made of parts)
  and `Gstin` (format and check digit with a reason in words), unit-tested.
- `Application/Tax/TaxCalculator.cs`: **the one tax engine**. Given a property, a charge category, a day and the
  line's parts, it finds the registration, category and rule of that day and returns the tax rows, or why there is no
  GST. `TaxService.cs`: registrations, categories, rules (auto-closing the running one), Verify and the preview.
- Hooks into earlier code: `FolioPosting.ChargeAsync` asks the engine for every new charge (room nights, manual
  charges, package parts) and stores `folio_item_taxes`; `FolioService` returns each line's taxes. Reversals and
  splits already copied tax rows (Sprint 8).
- Screens: `/settings/gst` (registration, categories with their rules, Add rule, Verify, Try a charge); the bill shows
  *amount + GST* under each line. [4.19](#419-gst-rules-as-data-the-tax-engine) explains the rules; worked examples in
  [GST.md](../GST.md#worked-examples).

### Sprint 10: Payments, refunds and the cash drawer

![Refunds and the cash drawer](../../tools/handbooks/assets/payment-flow.png)

- Migration V012: `payments` (P00001 numbers, CASH / UPI / CARD / BANK_TRANSFER, the status graph in
  `payment_status_transitions`), `payment_allocations` (which bill a payment settles), `refunds` (with a reason),
  `cash_drawers`, `cash_drawer_sessions` (one shift, OPEN / VARIANCE_PENDING / CLOSED) and `cash_drawer_movements` (the
  drawer's ledger). Triggers refuse deletes, let a payment change only its status and void, and check every allocation
  and refund against its payment under a row lock (`fn_payment_money_check`). No column can hold card data. Details:
  [schema-sprint-10.md](../database/schema-sprint-10.md).
- `Domain/Payments/Payments.cs`: the entities, the status graph and `PaymentMath` (what can be refunded, what is on
  account, the status after a refund, the variance, the card-number check with Luhn), unit-tested.
- `Application/Payments/PaymentService.cs`: list, read, take (on a bill or on account), allocate, refund, void.
  `CashDrawerService.cs`: open, close, approve a variance, and the helpers the payment service uses to put cash in and
  out of the open shift. `PaymentLedger.cs`: the one place that sums what bills and bookings have been paid.
- Hooks into earlier code: `FolioService` and `FolioBalances` (front desk, check-out) subtract real payments;
  `CalendarService` shows paid / part paid; `FolioDto` lists the bill's payment lines.
- Screens: `/payments` (totals by method, filters, search, the drawer panel) and `/payments/:id` (Refund, Void, Put on
  a bill); **Take payment** and the payments list on the bill (`/folios/:id`); the cash drawer panel on the front desk.
  [4.20](#420-refunds-that-cant-overspend-and-a-cash-drawer-that-balances) explains the rules.

---

## 4. System design: how the hard parts work

### 4.1 Passwords

Code: `PasswordHasherService` in `backend/Resort.Infrastructure/Identity/Security.cs`; policy in
`Application/Validation/Validators.cs`.

- Passwords are never stored. We store a **PBKDF2** hash in the ASP.NET Core Identity v3 format: HMAC-SHA512,
  100,000 iterations and a random salt for every hash. The salt and iteration count are inside the hash string, so
  the cost can be raised later without a migration.
- On sign-in `Verify` returns *success*, *failed* or *success, rehash needed*. In the last case the service stores a
  fresh hash with today's settings, so old hashes upgrade themselves as people sign in.
- **No user enumeration**: when the email does not exist, `SimulateVerify` runs the same hash against a dummy, so a
  wrong email takes as long as a wrong password and the error text is the same.
- **Lockout**: 5 wrong passwords lock the account for 15 minutes (`AuthOptions`, configurable). Every attempt is
  written to the append-only `login_events`. Sign-in endpoints are also rate-limited to 10 a minute per IP.
- The policy is at least 10 characters with letters and numbers (`Validators.Password`). The first owner account,
  new staff and reset passwords get `must_change_password`; the app forces the change before anything else.

### 4.2 Sign-in, tokens and sessions

Code: `AuthService` (`Application/Identity/AuthService.cs`), `TokenService` (`Infrastructure/Identity/Security.cs`),
`OnTokenValidated` in `Api/Program.cs`, `frontend/src/app/core/auth.service.ts` and `http.ts`.

```mermaid
sequenceDiagram
  participant B as Browser
  participant A as API
  participant D as Database
  B->>A: POST /auth/login (email, password)
  A->>D: user by email · verify PBKDF2 hash · reset failed count
  A->>D: insert refresh_tokens (SHA-256 of the token, family id)
  A-->>B: access token (JWT, 15 min) in JSON + refresh token in HttpOnly cookie (14 days)
  B->>A: API calls with Authorization: Bearer
  Note over A: checks signature, expiry and the security stamp
  B->>A: POST /auth/refresh (cookie) when the access token expires
  A->>D: lock the token row FOR UPDATE · revoke it as ROTATED · issue the next one in the same family
  A-->>B: new access token + new cookie
```

- The **access token** is a JWT signed with HMAC-SHA256 (key from `Jwt__SigningKey`, at least 32 bytes, never in
  git). It lives 15 minutes and is kept **in memory** by the app, never in localStorage, so a script injected into
  the page cannot read a long-lived secret.
- The **refresh token** is 32 random bytes in an HttpOnly, Secure cookie. The database stores only its SHA-256 hash.
  Each refresh **rotates** it. If a token that was already rotated is presented again, someone copied it: the whole
  token family is revoked and a `RefreshTokenReuse` audit row is written. The row is locked `FOR UPDATE` so two tabs
  refreshing at once do not look like theft.
- Every token carries the user's **security stamp**. Changing or resetting a password, or deactivating a user,
  rotates the stamp, so their existing access tokens stop working at once. Role changes clear the user's cached
  access, so new permissions apply on the next request (the cache otherwise lives at most 30 seconds).
- Signing out revokes the family; changing the password revokes every refresh token of that user.

### 4.3 Permissions and roles

Code: `Application/Common/Permissions.cs`, `Api/Infrastructure/Authorization.cs`, `Application/Common/CallerAccess.cs`;
matrix in [security/permission-matrix.md](../security/permission-matrix.md).

- Code checks **permission codes**, never role names: `reservation.create`, `rate.manage`, `guest.read`, … Roles are
  rows that bundle permissions, so the owner can change what a role may do without a code change.
- A grant is **scoped**: `user_roles.property_id` NULL means the whole organisation, otherwise one property. A route
  with `{propertyId}` needs the permission org-wide or for that property.
- Endpoint policies do the coarse check; services do the fine one (e.g. a guest is visible only to someone with guest.read for at least one property). An **escalation guard**
  stops anyone granting a role with permissions they do not hold themselves.
- The Angular guards and hidden buttons are only convenience. The API checks every call.

### 4.4 One database, many organisations

Code: `AppDbContext` (`Infrastructure/Persistence/AppDbContext.cs`).

- Every business table has `organization_id`, and property-level tables also `property_id`. Composite foreign keys
  such as `(unit_id, property_id)` make it impossible to attach a row to another property's room.
- EF Core **global query filters** add `organization_id = <caller's org>` to every query. For an anonymous caller
  the org is NULL, so tenant queries return nothing unless code explicitly calls `IgnoreQueryFilters()` (only sign-in
  does, to find the user by email).
- Another organisation's id in a URL gives **404**, not 403, so callers cannot probe which ids exist.

### 4.5 The audit trail

Code: `AuditInterceptor` (`Infrastructure/Persistence/AuditInterceptor.cs`), `AuditContext`.

- The interceptor runs inside `SaveChanges`, so each change and its audit row commit or roll back **together**.
- Each row records who, when, the entity, old and new values, the reason (cancel, block, release …), IP, device,
  correlation id and request id. Personal fields (`[PersonalData]`) and secrets are recorded as changed but masked,
  so anonymising a guest really removes their data.
- `audit_logs` and `login_events` are **append-only**: a trigger refuses UPDATE and DELETE.
- Business tables are never deleted either. Reservations, plans, packages and guests have their own never-delete
  triggers; deletion is a status (cancelled, inactive, merged, anonymised).

### 4.6 Encrypting ID documents

Code: `AesGcmFieldEncryptor` (`Infrastructure/Protection/FieldEncryption.cs`), `IFieldEncryptor`
(`Application/Common/Protection.cs`), `LocalDiskFileStore`; rotation steps in [SECURITY.md](../SECURITY.md).

- ID numbers, document details and scans are encrypted by the API before they reach the database or disk, with
  **AES-256-GCM** (confidentiality and tamper detection).
- The key is `Encryption__Key` (32 random bytes, base64) from the environment, never the database or git. HKDF
  derives separate keys for encryption and for lookup hashes from it.
- Each value is an envelope: version, key id, 12-byte random nonce, ciphertext, 16-byte tag. The key id makes
  **key rotation** possible: new data uses the active key, old data stays readable with `PreviousKeys` until it is
  re-encrypted.
- The row and field (`guest_document:{id}:number`) are bound in as associated data, so a ciphertext copied to another
  row will not decrypt.
- To answer "is this passport already on another profile?" without decrypting everything, the API stores an
  **HMAC-SHA256 keyed hash** of the normalised number and compares hashes.
- Numbers are masked in the UI until someone with permission opens them, and every opening is audited.

### 4.7 No double booking

Code: `ReservationService.CreateAsync` and `InventoryLoader`, `AvailabilityEngine`, migration V007. Exact SQL and
tests: [booking-algorithm.md](booking-algorithm.md). Diagram: the booking flow above.

The goal: when two receptionists book the last room at the same moment, exactly one succeeds and the other is told
why, every time. It is solved in three layers, each enough on its own for its job:

1. **Serialise the competitors.** Inside one transaction the service runs
   `SELECT 1 FROM accommodation_units WHERE property_id = $p AND accommodation_type_id = ANY($types) ORDER BY id FOR UPDATE`.
   Two bookings that want the same room types queue on these row locks; bookings for other types do not wait. The
   fixed `ORDER BY id` means two bookings that want overlapping sets of types lock in the same order and cannot
   deadlock.
2. **Decide with fresh data.** Holding the lock, the service reads every held `reservation_units` row and active
   block that overlaps the stay and gives them to the pure `AvailabilityEngine`. Because of the lock, nothing can be
   added for these types until we commit, so "free" really means free. If it does not fit, the engine says exactly
   why ("Room 102 isn't free for the night of 14 Oct 2026: it is booked on R00007") → `409 ROOM_UNAVAILABLE`.
3. **The database guarantees it anyway.** `reservation_units.stay` is a `daterange [check_in, check_out)` and two
   exclusion constraints (`EXCLUDE USING gist (unit_id WITH =, stay WITH &&)` and the same on `bed_id`, only for rows
   that hold inventory) make overlapping rows impossible to store. Any code path that skipped the lock still fails
   with SQLSTATE 23P01, which the API maps to the same 409. Triggers do the same between bookings and blocks, in both
   directions.

Details that matter:

- Stays are **half-open**: a guest leaving on the 12th and another arriving on the 12th never clash.
- Only PENDING, CONFIRMED and CHECKED_IN **hold inventory** (`holds_inventory`). An enquiry holds nothing; cancel and
  no-show set it to false in the same transaction, so the room is bookable the moment the cancel commits.
- **Changing dates** never deletes: the old rows get `superseded_at` and stop holding, new rows are inserted and the
  stay is re-priced, under the same locks.
- Why not `SERIALIZABLE`? It is also correct, but the loser gets a serialisation failure and must retry the whole
  request blindly. The row lock makes it wait a few milliseconds and then answer with the real reason.
- Proved by tests: the last room raced **50 times** (exactly one 201 and one 409 every run), the last dorm bed with 5
  parallel requests, and a direct SQL insert of an overlapping row failing with 23P01.

### 4.8 Idempotency: a retried click never books twice

Code: `Application/Common/Idempotency.cs`, `ReservationService`, table `idempotency_keys`.

- The app sends an `Idempotency-Key` header (a new UUID per booking attempt, kept when the user retries).
- First step of the transaction: `INSERT INTO idempotency_keys … ON CONFLICT DO NOTHING`. The unique index makes a
  second request with the same key **wait** for the first. When the first commits, the second reads the stored
  response and returns it with `Idempotent-Replayed: true`.
- The request is hashed as parsed (SHA-256), so a different body with the same key is `422 IDEMPOTENCY_KEY_REUSED`
  rather than a silent replay of something else.
- Payments (Sprint 11) and webhooks will use the same table and pattern.

### 4.9 Money

- `decimal` in C#, `numeric(12,2)` in SQL. Never `float` or `double`.
- Every night of a booking keeps its own price row (`reservation_rate_breakdowns`) with a CHECK that the total equals
  base plus extras, so invoices later use what was sold, not today's price.
- Splitting an amount (a package into room, food, transport) uses the **largest-remainder** rule
  (`MoneySplit` in `Domain/Rates/QuoteEngine.cs`): work in paise, give each part its whole paise, then hand the paise
  left over to the parts with the largest discarded fractions. Nothing is lost or gained.

### 4.10 Time and dates

- Instants are stored as UTC `timestamptz`. Business dates (check-in, a night, "today") are `date` in the property's
  own timezone (`properties.timezone`, Asia/Kolkata): `Property.LocalDate(utcNow)`. Never the server's clock zone.
- Services take the current time from an injected `TimeProvider`, so tests can stand the clock at 23:30 or 00:30 IST
  (Sprint 07's `FrontDeskClockTests`) instead of waiting for midnight.
- The header shows the property's local time; the app formats instants with `inZone()` (`core/property-context.ts`).
- Date fields use `IsoDateAdapter` (`core/dates.ts`): they show "15 Oct 2026", accept typed dates, and keep the
  API's ISO `2026-10-15` as the form value.

### 4.11 Edits that collide

- Every editable row exposes `version`, which is PostgreSQL's `xmin`. The client sends back the version it saw;
  `ConcurrencyGuard` turns it into `UPDATE … WHERE id = @id AND xmin = @version`. If someone saved in between, zero
  rows match and the API answers `409 CONCURRENCY_CONFLICT` instead of overwriting their change.
- Inventory uses row locks instead ([4.7](#47-no-double-booking)), because there the second person must get an answer,
  not a conflict.

### 4.12 Status machines

- Allowed moves are **rows** (`reservation_status_transitions`), enforced by a trigger, and mirrored in C#
  (`ReservationStatuses.Transitions`) so the API can answer first with `422 INVALID_STATUS_TRANSITION` and list the
  moves that are possible. Each move writes `reservation_status_history` with who, when and why.
- Later modules (folios, invoices, leave) follow the same pattern.

### 4.13 Migrations and seed data

Code: `DatabaseMigrator` (`Infrastructure/Persistence/DatabaseMigrator.cs`).

- The API applies `database/migrations/V###__name.sql` at start-up, each file in its own transaction, under a
  PostgreSQL advisory lock so two API instances starting together do not race.
- The SHA-256 of every applied file is stored; editing an applied migration stops the API from starting. Fix forward
  with a new file. CI also checks that migrations are only ever added.
- Development seeds (`database/seed/dev/S###`) add demo data in Development only, and must not touch the rooms that
  earlier sprints' review scripts block (Room 103, dorm beds B01–B04).

### 4.14 Errors that say why

- Every error is `{code, message, details, correlationId}`: a stable code the UI can act on, a sentence for people,
  and per-field messages keyed by field name. No stack traces or SQL leave the server.
- Database constraint names map to codes and messages in `Api/Infrastructure/Problems.cs` (`KnownConstraints`), so a
  constraint caught by PostgreSQL still produces a precise message.
- **Owner rule**: never a generic "Some fields need fixing". The page puts each message under the field it concerns
  (`showFieldErrors`, `controlPath`) and anything else next to the button, always with the reason.

### 4.15 Secrets and configuration

- Secrets (`Jwt__SigningKey`, `Encryption__Key`, database password) live in `.env` locally (gitignored, written by
  `scripts/setup-dev`) and in the host's secret store in production. The API refuses to start without them.
- Security headers (CSP `default-src 'none'`, no referrer, frame denial) and CORS are set in
  `Api/Infrastructure/HttpPipeline.cs`. Full threat model: [SECURITY.md](../SECURITY.md).

---

### 4.16 Moving a guest to another room

Code: `ReservationService.ReassignAsync`, `Domain/Reservations/Availability.cs`, `calendar.model.ts` (`moveProblem`).
Diagram: the room-move flow in section 3 (Sprint 06). Tests: `CalendarTests`.

The goal: dragging a booking from Room 101 to Room 103 is as safe as booking Room 103, even when another receptionist
drags a different booking onto Room 103 at the same moment, and the screen never shows a move the server refused.

1. **The screen checks first, the server decides.** On drop, `moveProblem` checks what the loaded calendar knows
   (same kind of room, active, big enough, no booking or block on those nights) and shows the reason at once, in
   the API's words. If it looks fine, the reason is asked and the request goes to the server. The bar is never moved
   by the browser: the calendar reloads after the server answers, so a refused move cannot leave a wrong picture.
2. **Same locks as a booking, in the same order.** One transaction: lock the reservation row (`FOR UPDATE`), check
   its version against what the screen showed (`409 CONCURRENCY_CONFLICT` if someone changed it), then lock the
   target room row. Booking, changing dates, blocking and moving all lock the room row, so anything touching Room 103
   queues; locking reservation before room everywhere means no deadlocks.
3. **Decide with fresh data, with the booking engine's own rules.** Holding the room lock, it loads held bookings and
   active blocks on that room and asks `AvailabilityEngine.Free`. Not free → `409 ROOM_UNAVAILABLE` naming the booking
   or the block ("it is blocked (AC repair)").
4. **History, not edits.** The old `reservation_units` row gets `superseded_at` and stops holding (saved first, so a
   move inside the same dorm can't collide with itself); a new row is inserted for the same nights with the nightly
   prices copied, so the price stays as booked. The reservation's version changes, the audit trail stores both rows
   with the user and the reason, and a note on the booking says "Moved from Room 101 to Room 103 … Reason: …".
5. **The database still guarantees it.** The EXCLUDE constraints and the block trigger from V007 refuse an overlapping
   row whatever the code does.

Limits, on purpose: a whole room line moves for all its nights (no splitting a stay half-way); checked-in guests move
with the front desk from today ([4.17](#417-the-front-desk)). Proved by tests: two moves into the same room raced 20 times, exactly one wins every
time and the loser's booking is untouched.

### 4.17 The front desk

Code: `Application/FrontDesk/FrontDeskService.cs`, `Domain/FrontDesk/FrontDesk.cs`, `features/front-desk/`. Diagram: the
check-in flow in section 3 (Sprint 07). Tests: `FrontDeskTests`, `FrontDeskClockTests`, `FrontDeskUnitTests`.

1. **Today is the resort's day.** Every list and rule uses `Property.LocalDate(clock.GetUtcNow())` with an injected
   `TimeProvider`. At 00:30 IST it is still the previous day in UTC; a server-date bug would show yesterday's
   arrivals. `FrontDeskClockTests` pin the clock to 23:30 and 00:30 IST and check the lists and check-in follow.
2. **Check-in is race-free.** Same order as a booking: lock the reservation row, check its status and date, then lock
   its rooms' rows by id. Two receptionists pressing Check in at once: the second waits on the row lock and then sees
   `CHECKED_IN` (409 `ALREADY_CHECKED_IN`, naming who and when). Raced 20 times in the tests; one wins every time.
3. **The room must be empty and ready.** A guest still in the room after their departure day blocks the next check-in
   (409 `ROOM_OCCUPIED`, naming them; they show as *overdue* on Departures). A room that isn't READY is refused with the
   state in words; reception can choose another room or, if the property allows it, check in anyway with a reason that
   goes into the status history and a note.
4. **PENDING → CHECKED_IN isn't a valid move** in the status table, so a pending booking is confirmed and saved first,
   then checked in, in the same transaction: the history shows both steps.
5. **Check-out writes the housekeeping event in the same transaction.** Each room being left becomes DIRTY with a
   `CHECK_OUT` history row, so the event exists exactly when the check-out does. Leaving early needs a reason and
   releases the nights left (rows superseded and recreated ending on the departure day, prices re-summed, a note with
   the amount). A balance above zero blocks check-out unless the property allows it (the balance is real from Sprint 8).
6. **Moving an in-house guest** splits the stay at today: the nights already slept stay on the first room (history is
   true), the rest move with their prices, and the old room becomes DIRTY (`ROOM_MOVE`).
7. **A walk-in is one transaction and one idempotency key:** guest, booking from today (ready rooms preferred) and
   check-in. If the room turns out not ready or the guest looks like a duplicate, nothing is saved and the screen asks.
   A retried click returns the same stay.

### 4.18 Folios: the bill of a stay

Code: `Application/Folios/`, `Domain/Folios/Folios.cs`, `database/migrations/V010__folio.sql`, `features/folios/`.
Diagram: section 3 (Sprint 08). Tests: `FolioTests`, `FolioUnitTests`.

1. **A bill line is never changed.** Accounting needs to show what was charged and what was corrected, so a mistake
   is a REVERSAL line (negative quantity, the original's id, a reason), never an edit. Database triggers refuse UPDATE
   and DELETE on `folio_items`, so this holds for any code or any person with SQL access. A charge can be reversed
   once (`UNIQUE (reverses_item_id)`), and a trigger checks the reversal is its exact negative on the same folio.
2. **Totals are summed, never stored.** The balance is the sum of the lines (minus payments from Sprint 10, [4.20](#420-refunds-that-cant-overspend-and-a-cash-drawer-that-balances)); there is no
   running total to drift. Reversed and reversing lines add up to ₹0 and both stay visible.
3. **Every writer locks the reservation row first.** Posting, reversing, moving lines, check-in, check-out and the
   nightly job all start with `SELECT … FOR UPDATE` on the booking, the same order as the front desk. Two managers
   reversing at once: the second waits, then sees the reversal and gets 409 `ALREADY_REVERSED` naming who and when
   (raced 10 times in the tests).
4. **Each night is charged once.** Room nights can come from check-in, the job or check-out. The partial unique index
   on `(reservation_id, service_date)` for room-night charges makes a second insert impossible; the code skips nights
   that already have a line. It is keyed by date, not by room row, because room moves and early departures replace the
   rows. A night's price is what was booked (the rate breakdowns), not today's rate.
5. **Leaving early reverses the nights released**, then the balance is checked, so a guest is never billed for nights
   the front desk gave back.
6. **Moving lines is reversal plus copy.** Splitting a bill (the company pays the room, the guest the bar) reverses
   each line on the old folio ("Moved to F00007") and posts it on the new one with `transferred_from_item_id`. The
   stay's total is unchanged and both folios show what happened.
7. **A retried click charges once.** Posting needs an `Idempotency-Key`, as bookings do ([4.8](#48-idempotency-a-retried-click-never-books-twice)).
8. **Tax is copied, not looked up** (`folio_item_taxes`, from Sprint 9), so a later rate change never alters a posted
   bill.

Limit, on purpose: a booking isn't linked to a package yet, so selling a package with a room part on top of the room
nights would charge the room twice; staff sell packages for extras until bookings carry their package.

### 4.19 GST: rules as data, the tax engine

Code: `Application/Tax/`, `Domain/Tax/Tax.cs`, `database/migrations/V011__tax.sql`, `features/settings/gst/`.
Diagram: section 3 (Sprint 09). Tests: `TaxTests`, `TaxUnitTests`. Examples: [GST.md](../GST.md#worked-examples).

1. **No rate in the code.** GST changes by notification, often mid-year (accommodation went from 12% to 5% on
   22 Sep 2025). Every rate, slab limit and SAC code is a row with `effective_from` / `effective_to`, so an accountant
   changes GST from a date in Settings → GST and nothing is deployed. The code only knows how to apply a rule.
2. **One rule per category per day, enforced by the database.** `EXCLUDE USING gist (tax_category_id WITH =,
   daterange(from, to, '[]') WITH &&)`; the same for a property's registrations. The service locks the category's row
   while adding a rule, ends the running rule the day before the new one starts and turns any other overlap into a
   field error naming the rule it clashes with.
3. **History never moves.** A new rule starts today (India time) or later; rules are never deleted, slabs never change,
   and a trigger lets a rule change only its end date and "verified". A posted line stores each tax with its rate and
   `tax_rule_id`, and reversals copy those rows, so a bill (and, from Sprint 12, an invoice) always shows what was
   charged at the time.
4. **The slab goes by the unit, not the line.** Accommodation GST depends on the value of one room or bed for one
   night. A night line of several rooms is passed to the engine as parts (one per booked room or bed row, with its price
   per unit), so a ₹6,000 room and a ₹9,000 suite on one line are taxed at 5% and 18%, not both at 18%. "Up to ₹7,500"
   includes ₹7,500.00.
5. **Splitting and rounding.** Inside the property's state the rate is split evenly into CGST and SGST; elsewhere it is
   IGST. Each tax is rounded to the paisa, half away from zero, once per rate on a line. The rate must be a multiple
   of 0.02% so the halves are whole hundredths (checked by the validator and the database).
6. **No GST is a decision, not a gap.** No registration on the day, a COMPOSITION registration, or a charge category
   no tax category covers means no GST, and the preview says which. But a registered property with a taxed category
   and no rule for the day refuses the charge (422 `TAX_RULE_MISSING`, in words) rather than posting it untaxed.
7. **One engine, tenant-safe without a user.** `TaxCalculator` filters by the property's organisation itself (it
   ignores the request's tenant filter), because the nightly room-charge job posts with no one signed in.
8. **Seeded rules are flagged.** V011 enters the current rules from public notifications with their source and marks
   them unverified; the GST page counts them until the accountant presses Verify.

## 5. Changing the system safely

| You want to | Do this |
|---|---|
| Add or change a table | New `database/migrations/V###__name.sql` (never edit an old one). Add `organization_id` / `property_id`, composite FKs, CHECKs for every rule, `version` if editable. Map it in `AppDbContext`. Update DATABASE.md and a `schema-sprint-NN.md`. |
| Add an endpoint | Contract records in `Resort.Contracts`; validator in `Application/Validation`; service method in `Application/<Module>`; map it in `Api/Endpoints/<Module>Endpoints.cs` with a permission policy. Errors are thrown as `AppException` subclasses (`Application/Common/Errors.cs`). Document it in API.md. |
| Write anything that sells inventory or takes money | Same transaction, lock rows in id order, a database constraint as the final guard, `Idempotency-Key` on the create, an audit entry, a concurrency test with a barrier (no sleeps). |
| Add a screen | `features/<module>/<name>.page.ts`, HTTP in `<module>.api.ts`, form rules in `<module>.forms.ts`. Errors under fields via `showFieldErrors`. A browser step in `frontend/e2e/sprint-NN.spec.ts`. |
| Store personal or secret data | Encrypt with `IFieldEncryptor` (context = table:id:field), mark the property `[PersonalData]`, add a keyed hash if you need lookups. |

Every pull request runs: backend build with warnings as errors, `dotnet format` check, backend tests on Linux and
Windows, frontend lint, unit tests and production build, the browser tests of every sprint, a migrations check and a
secrets and vulnerable-package scan.

---

## 6. Where to read more

| Topic | Document |
|---|---|
| Full target architecture, module map | [ARCHITECTURE.md](../ARCHITECTURE.md) |
| Every table, conventions, ER diagram | [DATABASE.md](../DATABASE.md), [tables by sprint](../database/tables-by-sprint.md), `docs/database/schema-sprint-NN.md` |
| Every endpoint | [API.md](../API.md), [endpoints by sprint](../api/endpoints-by-sprint.md) |
| Security, threat model, key rotation | [SECURITY.md](../SECURITY.md), [permission matrix](../security/permission-matrix.md) |
| The booking algorithm, exact SQL | [booking-algorithm.md](booking-algorithm.md) |
| Frontend structure and conventions | [frontend.md](frontend.md) |
| Business lifecycles (booking, guest, payment …) | [lifecycles.md](lifecycles.md) |
| Decisions and open questions | [decisions.md](decisions.md), [risks-and-open-questions.md](risks-and-open-questions.md) |
| GST rules and worked examples | [GST.md](../GST.md) |
| Running and testing everything | [RUN_AND_TEST.md](../RUN_AND_TEST.md), [TESTING.md](../TESTING.md) |
| Each sprint's files, results, screenshots | [docs/sprints/](../sprints/README.md) |

---

### 4.20 Refunds that can't overspend, and a cash drawer that balances

Code: `Application/Payments/`, `Domain/Payments/Payments.cs`, `database/migrations/V012__payments.sql`,
`features/payments/`. Diagram: section 3 (Sprint 10). Tests: `PaymentTests`, `PaymentUnitTests`.

1. **Money records are never edited.** A refund is a new row with its reason; a mistake is a void (status
   `CANCELLED`, with a reason), never a delete. A trigger lets a payment change only its status and void fields, and
   only along `payment_status_transitions` (desk payments start `CAPTURED`; the gateway states are ready for
   Razorpay). Allocations, refunds and drawer movements can't change at all.
2. **Two refunds at once can't exceed the payment.** Every writer locks in the same order as the bill and the front
   desk: the booking row, then the payment row (`SELECT … FOR UPDATE`), then the cash shift. The second refund waits,
   then sees the first and gets a field error naming what is left. `fn_payment_money_check` runs the same checks inside
   the database under the same lock (refunded ≤ amount; on bills + refunded from account ≤ amount; a bill gets back at
   most what the payment put on it), so they hold for any code or SQL. The test fires refunds through a barrier and
   checks the total never passes the payment.
3. **On account is a remainder, not a column.** A payment on a bill is capped at what the bill owes; anything else
   stays on account for the booking (an advance) until it is put on a bill. On account = amount − on bills − refunded
   from account. A refund takes from on account first, then from the bills it paid. What a bill has been paid is its
   allocations less refunds off it; the booking's balance (check-out, the front desk) also counts money on account.
   Nothing is stored twice, so nothing drifts.
4. **The drawer is a ledger.** Opening a shift writes the float as the first movement; every cash payment, cash
   refund and voided cash payment adds a signed movement. What the drawer should hold is their sum. Closing stores the
   expected and counted amounts; a difference needs a reason and leaves the shift `VARIANCE_PENDING` until someone with
   `cash_drawer.approve_variance` approves it with a note. Movements can be added only while the shift is OPEN
   (trigger), and a partial unique index allows one open shift per drawer.
5. **Cash rules follow the drawer.** Cash can't be taken without an open shift; a cash refund can't take more than the
   drawer should hold; a cash payment can be voided only while its shift is open (after the close the count already
   included it, so a refund is the honest correction).
6. **No card data, ever** (brief §3). There is no column for a card number, expiry or CVV. A card payment's reference
   is the terminal's approval code: the database refuses 13 or more digits in a row, and the API and the form refuse
   anything that looks like a card number (13–19 digits passing the Luhn check, spaces and dashes ignored) under its
   field. The check is on exact digit runs, so a 22-character RTGS UTR isn't mistaken for a card.
7. **A retried click moves money once.** Taking and refunding money need an `Idempotency-Key`
   ([4.8](#48-idempotency-a-retried-click-never-books-twice)).

## How this guide stays current

Closing a sprint ([PROCESS.md](../sprints/PROCESS.md), step 2) includes updating this guide in the acceptance pull
request:

1. Add the sprint to [section 3](#3-built-sprint-by-sprint): what it adds, its tables, the main classes, its screens
   and its diagram (an ER diagram in `tools/handbooks/diagrams/diagrams.html`, rendered with `npm run diagrams`, plus a
   flow diagram when the sprint has a tricky mechanism).
2. If the sprint solved a hard problem (locking, money, security, an integration), add or extend a part of
   [section 4](#4-system-design-how-the-hard-parts-work) explaining how and why, with the file names.
3. Update the status line at the top and anything in sections 1, 2 and 5 that changed.

| Sprint | Added to this guide |
|---|---|
| 01–05 | First edition (October 2026): foundation, accommodation, guests, rates, reservations; system design 4.1–4.15 |
| 06 | Reservation calendar (read model, drag-and-drop), room-move diagram; system design 4.16 Moving a guest to another room (accepted 17 Oct 2026) |
| 07 | Front desk (lists, check-in/out, walk-ins, room readiness), check-in flow diagram; system design 4.17 The front desk (accepted 18 Oct 2026) |
| 08 | Folio (bills, immutable lines, reversals, room nights, split), folio flow diagram; system design 4.18 Folios (accepted 18 Oct 2026) |
| 09 | GST configuration and the tax engine (registrations, categories, dated rules, slabs, tax on bills), tax flow diagram; system design 4.19 GST: rules as data (accepted 20 Oct 2026) |
| 10 | Payments, refunds and the cash drawer (status machine, allocations, on account, drawer ledger, variance approval), payment flow diagram; system design 4.20 Refunds that can't overspend (in review) |
