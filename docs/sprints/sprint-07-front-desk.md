# Sprint 07 — Front desk — arrivals, departures, in-house, check-in/out

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🟢 Done | Phase 2 — Guests, reservations, availability, calendar, front desk | M | [Sprint 05](sprint-05-availability-engine-and-reservations.md) |

**Goal:** Reception completes check-in and check-out in very few clicks.

**GitHub:** milestone *Sprint 07 — Front desk — arrivals, departures, in-house, check-in/out* · branch prefix `feature/S07-`

## Where this sprint stands

**Now:** Accepted by the owner on 18 Oct 2026 and released to main (tag sprint-07).

**Done so far**

- Migration V009: reservations.checked_in_at / checked_out_at; unit_housekeeping_status (one row per room or dorm bed, no row = READY) and unit_housekeeping_history (append-only events CHECK_OUT, ROOM_MOVE, MANUAL; consumed_at for Sprint 13)
- Business date everywhere: 'today' is the property's local date from its timezone (an injected clock), never the server's; tested at 23:30 and 00:30 IST
- Front desk lists: arrivals (with late arrivals still due), departures (with overdue guests), in-house, each with rooms, cleaning state, balance and the reason a check-in would be refused; reception dashboard with counts and a rooms board
- Check-in: reservation row locked, room must be free of the previous guest and READY; a room that isn't ready can be checked into anyway with a reason (setting frontdesk.allow_checkin_when_not_ready, default on); PENDING bookings are confirmed on the way in
- Assign or move a room: before arrival for the whole stay, in house from today (nights already slept stay on the first room, which is marked dirty)
- Check-out: blocked while the folio balance is above zero (setting frontdesk.allow_checkout_with_balance, default off; folios arrive in Sprint 8); leaving early needs a reason and releases the nights left; rooms become DIRTY with a CHECK_OUT event
- Walk-in in one call and one transaction: guest (found or new), booking from today on a ready room, check-in; Idempotency-Key replays the same stay
- Screens: Front desk (/front-desk/arrivals, departures, in-house, rooms), Walk-in (/front-desk/walk-in: stay, guest, check in on one screen), Check in / Check out / Assign room on the booking page
- Dev seed S006: R00007 in house leaving today, R00008 in house for two more nights, Room 101 dirty
- System design guide: Sprint 07 section, part 4.17 The front desk, check-in flow diagram

**Files added / changed**

- database/migrations/V009__front_desk.sql; database/seed/dev/S006__front_desk.sql; database/queries/check-sprint-07.sql
- backend/Resort.Domain/FrontDesk/FrontDesk.cs; Reservations/Reservations.cs (checked-in/out times)
- backend/Resort.Contracts/FrontDeskContracts.cs; ReservationContracts.cs
- backend/Resort.Application/ — FrontDesk/FrontDeskService.cs, FrontDesk/FolioBalances.cs, Reservations/ReservationService.cs (shared booking and room-move steps), Common/Idempotency.cs, Common/Permissions.cs, Validation/FrontDeskValidators.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/Endpoints/FrontDeskEndpoints.cs + Program.cs
- backend/Resort.Tests/ — Integration/FrontDeskTests.cs, Integration/FrontDeskClockTests.cs, Unit/FrontDeskUnitTests.cs, Support/FrontDeskFactory.cs, Integration/DevSeedTests.cs
- frontend/src/app/ — features/front-desk/ (front-desk.page.ts, walk-in.page.ts, front-desk-dialogs.ts, desk-actions.ts, front-desk.api.ts, front-desk.spec.ts), features/reservations/reservation.page.ts, core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-07.spec.ts; scripts/check-api.mjs (Sprint 07 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-07.md, docs/architecture/system-design.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-07-screens/; tools/handbooks/diagrams (check-in-flow)

**Test results**

- Backend: 210 passed, 0 failed on real PostgreSQL 16 (29 new: FrontDeskTests with the same booking checked in by 20 pairs of simultaneous clicks, the demo flow, readiness and its override, settings, the check-out event, balance, early departure, walk-in replay and rollback, field errors and permissions; FrontDeskClockTests at 23:30 and 00:30 IST; FrontDeskUnitTests; the front desk dev seed check)
- Frontend: 59 of 59 unit tests passed (5 new for the front desk helpers); ESLint clean; production build OK
- Browser tests: 69 of 69 passed (Sprints 01–06 plus the 9 new Sprint 07 steps: not-ready check-in with a reason, check-out and Mark ready, choose another room, walk-in, early departure, check-in from the booking page, permissions, phone); API checks 89 of 89 (12 new); screenshots in docs/sprints/sprint-07-screens

**Outcome**

Expected when accepted:

- Walk-in to checked-in in ≤ 3 screens

## Scope

**Tables**

- `unit_housekeeping_status`
- `unit_housekeeping_history`

**API endpoints**

- `GET /api/v1/properties/{propertyId}/front-desk/arrivals?date`
- `GET /api/v1/properties/{propertyId}/front-desk/departures?date`
- `GET /api/v1/properties/{propertyId}/front-desk/in-house`
- `GET /api/v1/properties/{propertyId}/front-desk/dashboard`
- `GET /api/v1/properties/{propertyId}/reservations/{id}/room-options?reservationUnitId`
- `POST /api/v1/properties/{propertyId}/reservations/{id}/assign-room`
- `POST /api/v1/properties/{propertyId}/reservations/{id}/check-in`
- `POST /api/v1/properties/{propertyId}/reservations/{id}/check-out`
- `POST /api/v1/properties/{propertyId}/walk-ins`
- `PUT /api/v1/properties/{propertyId}/rooms/{unitId}/housekeeping`

**Screens / routes**

- `/front-desk/arrivals`
- `/front-desk/departures`
- `/front-desk/in-house`
- `/front-desk/rooms`
- `/front-desk/walk-in`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] Business date from property timezone; 'today' never from the server clock
- [x] Check-in requires assigned room/bed with housekeeping READY (configurable override with reason)
- [x] Check-out sets housekeeping DIRTY (event consumed in Sprint 13) and is blocked while folio balance > 0 (setting frontdesk.allow_checkout_with_balance)
- [x] Walk-in = guest + reservation + check-in in one call (one transaction)
- [x] Reception dashboard (brief §54)

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S07-<issue-number>-<short-name>`
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

- If a list shows yesterday's or tomorrow's guests, something used the server's date: every 'today' must come from Property.LocalDate(clock) (FrontDeskClockTests pin the clock to 23:30 and 00:30 IST).
- A check-in refused with ROOM_OCCUPIED names the booking still in the room: check that guest out first (or they are overdue on Departures).

## How to test

Required tests for this sprint (the sprint is not done without them):

- Concurrency — simultaneous check-in of the same reservation → one succeeds
- Integration — arrivals use the property's local date at 23:30 IST and 00:30 IST

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Walk-in to checked-in in ≤ 3 screens

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Today's arrivals → assign room → check in → in-house list

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-07` created on `main`
