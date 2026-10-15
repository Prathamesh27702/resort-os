# Changelog

One entry per accepted sprint, tagged `sprint-NN` on `main`. Format: [Keep a Changelog](https://keepachangelog.com).

## [Unreleased] — Sprint 05 — Availability engine and reservations (in review)

### Added
- Migration V007: `reservations`, `reservation_units` (rooms and dorm beds held, with `EXCLUDE USING gist` on
  `(unit_id, stay)` and `(bed_id, stay)` as the final guard against double booking), `reservation_guests`,
  `reservation_rate_breakdowns`, `reservation_notes`, `reservation_status_history`, `reservation_status_transitions`
  (the status graph, enforced by a trigger), `idempotency_keys`, `property_counters` (references R00001 …). Reservations,
  their rooms, prices, notes and history are never deleted. A block and a booking can't cover the same room and night
  (triggers both ways).
- Availability engine (`Resort.Domain/Reservations/Availability.cs`, pure and unit-tested): free rooms and beds for every
  night of a stay, allocation of rooms and one bed per dorm guest, and the reason when something doesn't fit.
- Booking algorithm (`docs/architecture/booking-algorithm.md`): one transaction that claims the Idempotency-Key, locks the
  rooms of the requested types in id order, allocates, prices every night from the rate grid and stores the response.
- APIs under `/api/v1/properties/{id}`: `availability`, `reservations` (list, get, create with `Idempotency-Key`, change
  dates/guests, confirm, cancel, no-show, notes). Permissions `reservation.read/create/update/cancel`.
- Dev seed S004: six demo bookings from today (R00001–R00006), leaving Room 102 as the last Deluxe room tonight.
- Staff app: Reservations (list with search and Booked / Enquiries / All), New booking wizard (dates → room → guest →
  rate → confirm, with quick-add of a walk-in guest), booking page (rooms, price night by night, guests, notes, history,
  confirm, change dates or guests, cancel and no-show with a reason). Guest profiles list their stays with references.
- Tests: backend `ReservationTests` (last room raced 50 times, last dorm bed with 5 parallel requests, database guard,
  idempotency, transitions, cancel frees the room, blocks vs bookings) and `ReservationUnitTests`; frontend form tests;
  browser steps S05-01 … S05-07 (including the last room booked from two browsers); `check-api.mjs` Sprint 05 block;
  `database/queries/check-sprint-05.sql`.

### Changed
- Creating a block on a booked room is refused with 409 `BLOCK_OVERLAPS_RESERVATION` naming the booking.
- `GET /guests/{id}/stays` returns real reservations (with `reference`); merging guests moves their reservations.
- Constraint errors map to their own codes (`ROOM_UNAVAILABLE`, `BLOCK_OVERLAPS_RESERVATION`, `INVALID_STATUS_TRANSITION`).
- Every date field in the staff app shows "15 Oct 2026" with a calendar button and accepts typed dates (15 Oct 2026,
  15-10-2026, 2026-10-15); a date it can't read says "Enter a date like 15 Oct 2026." under the field
  (`core/dates.ts`, from owner review).
- Field labels on the first row of dialogs and wizard steps are no longer cut off; the change-booking dialog lines up
  rooms under the dates; finished wizard steps show a tick; Back sits next to the step's main button.

## [sprint-04] — 2026-10-14 — Rate plans and packages (accepted)

### Added
- Migration V006: `rate_plans`, `rate_plan_prices`, `packages`, `package_components`, `reservation_sources` (14 built-in
  booking channels). One default plan per property; a deferred trigger refuses a package whose split doesn't add up.
- Quote engine (`Resort.Domain/Rates/QuoteEngine.cs`): nightly breakdown, extra adult/child above standard occupancy,
  per-bed dormitory prices, minimum stay on the arrival night; `MoneySplit` with the largest-remainder rounding rule.
- Rate APIs under `/api/v1/properties/{id}`: `rate-plans`, `rate-plans/{id}/prices` (read and bulk edit by date range
  and days of the week), `rates/quote` (by room or type, id or code), `packages`, `reservation-sources`. Every price
  change is audited with old and new values.
- Dev seed S003: BAR with Friday/Saturday uplift for a year; Couple getaway and Room + breakfast packages.
- Staff app: Rates (grid, bulk edit, quote) and Packages (list, editor with live revenue split).
- Tests: 29 backend (11 integration, 18 unit), 11 frontend, 6 browser steps (S04-01…S04-06); `check-api.mjs` Sprint 04
  block; `database/queries/check-sprint-04.sql` (9 queries).
- `docs/RUN_AND_TEST.md`: one handbook with every command to run the app and test it, sprint by sprint (backend
  filter, browser test file, API and database checks) and all sprints together.

### Changed
- `BusinessRuleAppException` can carry details (the quote lists the nights that have no price).
- Clear reasons for unique-constraint races on rate tables instead of the rule name.
- `showFieldErrors` also reaches fields inside lists (`Components[0].Description`).

### Fixed
- Rate grid: prices sat one column right of their dates because the table's style name clashed with a layout style.
  Browser step S04-01 now checks each price sits under its date.

## [sprint-03] — 2026-10-11 — Guests (accepted)

### Added
- Migration V005: `guests`, `guest_addresses`, `guest_documents`, `guest_preferences`, `guest_consents`, a data
  classification on every column, `pg_trgm` search indexes and database guards (guests never deleted, anonymised rows
  empty, Aadhaar never stored in full, consent history append-only).
- Field-level encryption (AES-256-GCM, HKDF-derived keys, key id per row, row-bound associated data) for guest ID
  numbers, document details and scans; key rotation steps in `docs/SECURITY.md`. The API refuses to start without a
  valid `Encryption__Key`.
- File storage abstraction (`IFileStore`) with a local-disk implementation (`App_Data/files`, gitignored).
- Guest APIs under `/api/v1/guests`: search by any part of a phone number or by name/email/company, duplicate warning
  (`409 POSSIBLE_DUPLICATE`), edit with versions, consent history, merge, anonymise, retention dry run, ID documents
  (multipart upload, masked list, audited opening of number and scan).
- Phone numbers normalised to E.164 (Indian numbers can be typed the local way).
- Audit trail: personal guest fields are named but stored as `***`; `SENSITIVE_READ` rows when a document is opened;
  labels for room, block and guest entries in the Audit trail screen.
- Dev seed S002: 20 invented guests (14 Indian, 6 international).
- Staff app: Guests list and search, add/edit guest with duplicate warning, guest profile with consent, stays and
  ID documents, add-document, merge and anonymise dialogs.
- Tests: 44 backend (13 integration, 31 unit), 6 frontend, 13 browser steps (S03-01…S03-13);
  `check-api.mjs` Sprint 03 block (12 checks); `database/queries/check-sprint-03.sql` (14 queries).
- `E2E_SCREENSHOTS=<folder>` makes the browser tests save the review screenshots.

### Changed
- CI job *Browser tests — Sprint 01–02 screens* renamed *Browser tests — sprint review scripts*.
- `ConflictAppException` can carry details in the error body (used for the duplicate list).
- Handbooks: `docs/handbooks/Resort-OS-Product-Overview.docx` (for clients) and
  `Resort-OS-Software-Documentation.docx` (architecture, UI/API/database design per sprint, code, tests), generated
  from the repository by `tools/handbooks` and rebuilt at every sprint close. Diagrams in `tools/handbooks/diagrams`.
- `sprints.yml`: a plain-language `client:` line for every sprint and a `handbook:` block for accepted sprints.
- *Release tags* workflow: tags `sprint-NN` on `main` for every sprint marked done.
- PROCESS.md: *Closing a sprint* checklist.

### Fixed
- Add ID document, Aadhaar: a number that isn't 12 digits (or the last 4) was refused with only *Some fields need
  fixing*, because the field's own message never showed. The dialog now checks the number as it is typed and says why
  under the field (e.g. "An Aadhaar number has 12 digits; this has 16"); other documents are checked for 4–30 letters
  or digits. Any message the API sends for the number also shows under the field. Tests: validator unit test (backend),
  2 frontend tests, browser step S03-06 extended.
- Choose your own password: reusing the temporary password as the new one was refused with only *Some fields need
  fixing*. The page now says so under New password before sending, and every API field message on this page and in
  Add ID document shows under its field (`showFieldErrors` in `core/http.ts`). Browser step S01-06 extended.
- Errors say why: a validation error's `message` is now the reasons themselves (up to 3, e.g. "New password must
  differ from the current one."), never "Some fields need fixing". Database rule errors name the rule, and an
  unreadable request says what couldn't be read.

## [sprint-02] — 2026-10-10 — Accommodation (accepted)

### Added
- Migration V004: accommodation types, rooms, dormitory beds, amenities (22 built in), unit images, unit blocks.
  `btree_gist` `EXCLUDE` constraints and triggers stop overlapping blocks and beds in per-room types at database level.
- Room-based (UNIT) and bed-based (BED) inventory with a shared capacity calculation.
- APIs under `/api/v1/properties/{propertyId}`: amenities, accommodation types, rooms, beds, room status board, blocks and
  release. All permission-checked, validated and audited; block and release reasons go into the audit trail.
- Development seed runner (`database/seed/dev`, Development only, idempotent) with demo rooms: Deluxe 101–102,
  Standard 103–106, a 10-bed dormitory.
- Staff app: Rooms, room/dorm editor with bulk bed add, block dialog, release with reason, Room status board, Room types
  editor with amenities; *Set up rooms* on the Today checklist.
- 21 backend tests (incl. 10 concurrent block requests → exactly one wins) and 8 frontend tests.
- Docs: Sprint 02 API spec in `docs/API.md`, `docs/database/schema-sprint-02.md`, *What each sprint needs* in
  `docs/GETTING_STARTED.md`, Sprint 02 review script in `docs/sprints/REVIEWING.md`, screenshots.

### Added — review tooling
- Browser tests (`frontend/e2e`, Playwright): the Sprint 01 and 02 review scripts automated, 26 steps; watch them
  with `npm run e2e:headed`. They use their own API and a throwaway database.
- `scripts/check-api.mjs` (34 API checks, PASS/FAIL) and `database/queries/check-sprint-01-02.sql` (16 read-only checks).
- `docs/testing/VERIFYING.md`: UI → API → database checks per feature, and where `.env` is.
- CI: *Browser tests* and *Backend — tests on Windows* jobs.

### Fixed
- Time zone "IST" was accepted on Windows (ICU alias) but refused on Linux. Only IANA `Area/City` names or `UTC`
  are accepted everywhere now. Found by the owner's test run on Windows.

### Changed
- An audit reason sent in the request body (e.g. a block's reason) is recorded in preference to the `X-Change-Reason` header.

## [sprint-01] — 2026-10-10 — Foundation (accepted)

### Added
- Modular-monolith solution (.NET 10): Api, Application, Domain, Infrastructure, Contracts, Tests.
- SQL migrations V001–V003 with checksum-guarded migrator; 13 tables; 117 permissions; 17 system roles.
- Sign-in with lockout, rotating refresh cookie with reuse detection, forced password change, security-stamp sign-out.
- Organisation, properties and property settings; staff accounts with property-scoped roles and escalation guard.
- Audit trail with old/new values, reason, device, IP, correlation and request ids; append-only at database level.
- `/api/v1` routes; standard error body `{code, message, details, correlationId}`; security headers; rate limiting; health checks; OpenAPI.
- Angular 20 staff app: login, dashboard setup checklist, properties, staff accounts, roles matrix, audit trail, change password.
- 30 backend tests (integration on real PostgreSQL) and 6 frontend tests; GitHub Actions CI.
- Owner and developer guides: `docs/GETTING_STARTED.md` (Windows/macOS step by step, VS Code), `docs/sprints/REVIEWING.md`,
  `docs/COSTS.md`; every sprint guide now opens with *Where this sprint stands* (now / done / files / results / outcome).
- Shared VS Code workspace (`.vscode/`: recommended extensions, F5 debugging, tasks); `.gitattributes` (LF everywhere).
- `Onboarding` workflow: runs the setup script on a clean Linux runner through to a signed-in API, and the PowerShell
  script on Windows.

### Fixed
- `scripts/setup-dev.ps1` failed to generate keys (PowerShell unrolled the byte array); now requires PowerShell 7 and
  stops on any failed step. Added `scripts/db-reset.ps1`.
