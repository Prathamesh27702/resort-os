# Sprint 05 — Availability engine and reservations core

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🟢 Done | Phase 2 — Guests, reservations, availability, calendar, front desk | L | [Sprint 02](sprint-02-accommodation.md), [Sprint 03](sprint-03-guests.md), [Sprint 04](sprint-04-rate-plans-and-packages.md) |

**Goal:** Race-free booking of rooms and dorm beds with idempotent create and full status history (brief §14–16).

**GitHub:** milestone *Sprint 05 — Availability engine and reservations core* · branch prefix `feature/S05-`

## Where this sprint stands

**Now:** Accepted by the owner on 15 Oct 2026 and released to main (tag sprint-05). Review fixes: every date field shows 15 Oct 2026 with a calendar, field labels no longer cut off, finished wizard steps show a tick, Back beside Continue. New guide for joiners: docs/architecture/system-design.md.

**Done so far**

- Migration V007: reservations, reservation_units (rooms and dorm beds held, EXCLUDE USING gist on room and on bed over the stay), reservation_guests, reservation_rate_breakdowns, reservation_notes, reservation_status_history, reservation_status_transitions (status graph enforced by a trigger), idempotency_keys, property_counters (references R00001 …). Nothing is ever deleted; blocks and bookings can't cover the same room and night
- Availability engine (pure, unit-tested): free rooms and beds for every night, allocation of rooms and one bed per dorm guest, and the exact reason when something doesn't fit (which room, which nights, which booking or block holds it)
- Booking algorithm in one transaction: claim the Idempotency-Key, lock the rooms of the requested types in id order, allocate, price every night from the rate grid, store the response. The exclusion constraints are the final guard (23P01 → 409 ROOM_UNAVAILABLE). Written up in docs/architecture/booking-algorithm.md
- APIs: availability; reservations list, get, create (Idempotency-Key, replay header), change dates or guests (re-checks and re-prices), confirm, cancel and no-show with a reason, notes. Permissions reservation.read/create/update/cancel
- Dev seed S004: six demo bookings from today; Room 102 is the last Deluxe room tonight for the two-browser demo
- Owner review fixes (15 Oct 2026): date fields across the app show 15 Oct 2026 with a calendar and accept typed dates; first-row field labels in dialogs and wizard steps no longer clipped; change-booking dialog lines rooms up under the dates; finished steps show a tick; Back sits next to each step's main button
- Architecture and system design guide for new joiners (docs/architecture/system-design.md) with diagrams per sprint; updating it is now a step of closing every sprint
- Screens: Reservations list (search, Booked / Enquiries / All), New booking wizard (dates → room → guest → rate → confirm, quick-add of a walk-in guest), booking page (rooms, nightly prices, guests, notes, history, confirm, change, cancel, no-show). Guest profiles list stays with their reference. Every error is shown under its field or next to its button with the reason

**Files added / changed**

- database/migrations/V007__reservations.sql; database/seed/dev/S004__reservations.sql; database/queries/check-sprint-05.sql
- backend/Resort.Domain/Reservations/Reservations.cs, Reservations/Availability.cs
- backend/Resort.Contracts/ReservationContracts.cs
- backend/Resort.Application/ — Reservations/ReservationService.cs, AvailabilityService.cs, InventoryLoader.cs, Common/Idempotency.cs, Validation/ReservationValidators.cs, Common/Permissions.cs; Accommodation/BlockService.cs and Guests/GuestService.cs (bookings vs blocks, stays, merge)
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/ — Endpoints/ReservationEndpoints.cs, Program.cs, Infrastructure/Problems.cs
- backend/Resort.Tests/ — Integration/ReservationTests.cs, Integration/DevSeedTests.cs, Unit/ReservationUnitTests.cs
- frontend/src/app/ — features/reservations/*, features/guests/guest.page.ts, features/audit (labels), core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-05.spec.ts; scripts/check-api.mjs (Sprint 05 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-05.md, docs/architecture/booking-algorithm.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-05-screens/
- frontend/src/app/core/dates.ts (date fields); docs/architecture/system-design.md; tools/handbooks/diagrams (er-reservations, booking-flow)

**Test results**

- Backend: 173 passed, 0 failed on real PostgreSQL 16 (35 new: 34 for Sprint 05 including the last room raced 50 times, the last dorm bed with 5 parallel requests, the database guard and idempotency, plus the demo-booking seed check)
- Frontend: 44 of 44 unit tests passed; ESLint clean; production build OK
- Browser tests: 52 of 52 passed (Sprints 01–04 plus the 7 new Sprint 05 steps, including the last room booked from two browsers); screenshots in docs/sprints/sprint-05-screens
- API checks: 0 failed (12 new Sprint 05 checks: idempotent replay, key reuse, room taken, cancel with reason, status graph); database checks: no room or bed held twice

**Outcome**

Expected when accepted:

- Brief §16 scenario passes reliably in CI

## Scope

**Tables**

- `reservations`
- `reservation_units`
- `reservation_guests`
- `reservation_rate_breakdowns`
- `reservation_notes`
- `reservation_status_history`
- `idempotency_keys`

**API endpoints**

- `GET /api/v1/properties/{propertyId}/availability?from&to&adults`
- `POST /api/v1/properties/{propertyId}/reservations  (Idempotency-Key header required)`
- `GET /api/v1/properties/{propertyId}/reservations`
- `GET /api/v1/properties/{propertyId}/reservations/{id}`
- `POST /api/v1/properties/{propertyId}/reservations/{id}/confirm | cancel | no-show`
- `PUT /api/v1/properties/{propertyId}/reservations/{id}  (dates/guests; re-checks availability)`
- `POST /api/v1/properties/{propertyId}/reservations/{id}/notes`

**Screens / routes**

- `/reservations`
- `/reservations/new`
- `/reservations/:id`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] DB: reservation_units.stay daterange + EXCLUDE USING gist (unit_id WITH =, stay WITH &&) WHERE status is active; same for bed_id
- [x] Algorithm: transaction → SELECT ... FOR UPDATE on the unit rows → availability check → insert; exclusion constraint is the final guarantee; map 23P01 to ROOM_UNAVAILABLE
- [x] Idempotency: idempotency_keys(key, request_hash, response) — same key + same body returns the stored response; same key + different body = 422
- [x] Status machine INQUIRY→PENDING→CONFIRMED→CHECKED_IN→CHECKED_OUT, CANCELLED, NO_SHOW, REFUNDED; transitions table-driven and audited
- [x] Never delete reservations (brief §87); enquiry-stage delete is a status
- [x] Docs: write docs/architecture/booking-algorithm.md with the exact PostgreSQL behaviour
- [x] UI: new reservation wizard (dates → availability → guest → rate → confirm) in ≤ 5 clicks for a walk-in

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S05-<issue-number>-<short-name>`
3. **Database up:** Docker Desktop running, then `docker compose --env-file .env -f infra/docker/docker-compose.yml up -d db` (or VS Code task *db: start PostgreSQL*)
4. **Schema changes:** add the next `database/migrations/V###__<name>.sql` (never edit an applied one). The API applies it on start.
5. **API:** `cd backend/Resort.Api && dotnet watch run` → http://localhost:5180/swagger
6. **Web:** `cd frontend && npm start` → http://localhost:4300
7. **Fresh data:** `pwsh -File scripts/db-reset.ps1` (macOS/Linux `bash scripts/db-reset.sh`), then restart the API (migrations + bootstrap owner run again; dev seed data from Sprint 2 onward)

## How to debug

- **API:** in VS Code press **F5 → API (Resort.Api)** (C# Dev Kit) and set breakpoints; Swagger's *Authorize* button takes the `accessToken` from `POST /api/v1/auth/login`.
- **Every error response has a `correlationId`.** Search the API console/log for it to find the full exception.
- **SQL:** set `Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore.Database.Command=Information` in `.env` to log every SQL statement.
- **Database:** `docker compose --env-file .env -f infra/docker/docker-compose.yml exec db psql -U $POSTGRES_USER $POSTGRES_DB`
- **Web:** Angular DevTools + the browser Network tab; API errors are `{code, message, details, correlationId}`.

Sprint-specific:

- Reproduce races locally with tests/…/ConcurrencyTests using Task.WhenAll + a Barrier; never with sleeps.
- Look at pg_locks while a test is paused to see who holds what.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Concurrency — two simultaneous bookings for the last room → exactly one SUCCESS, one ROOM_UNAVAILABLE (run 50×)
- Concurrency — dorm with 1 bed left, 5 parallel requests → 1 success
- Idempotency — same request retried → one reservation, identical response
- Integration — cancelled reservation frees inventory
- Integration — status transitions outside the allowed graph → 422

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Brief §16 scenario passes reliably in CI

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Book the last room from two browsers at once

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-05` created on `main`
