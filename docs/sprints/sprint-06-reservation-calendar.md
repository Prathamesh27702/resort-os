# Sprint 06 — Reservation calendar

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🟡 In review — **current sprint** | Phase 2 — Guests, reservations, availability, calendar, front desk | M | [Sprint 05](sprint-05-availability-engine-and-reservations.md) |

**Goal:** Day/week/month calendar showing room, guest, dates, status, source and payment status; safe drag-and-drop reassignment.

**GitHub:** milestone *Sprint 06 — Reservation calendar* · branch prefix `feature/S06-`

## Where this sprint stands

**Now:** Built on 15 Oct 2026 and in review: pull request into develop. Test it on the claude/sprint-06-vfny0k branch until it is merged.

**Done so far**

- Migration V008: index ix_reservation_units_calendar on current reservation rows (property, dates)
- Calendar read model (GET /calendar?from&to, up to 62 days): rooms and dorm beds, bookings with guest, status, source, payment status and version, and active blocks (maintenance, out of order …); one indexed query per layer
- Move a guest to another room or bed (POST /reservations/{id}/reassign, reason required): same locking as booking (reservation row, then room row), the availability engine's rules, the old row superseded and nightly prices copied (price unchanged), a note on the booking and audit entries with who and why; 409 names the booking or block in the way
- Screen /reservations/calendar: Day / Week / Month, bookings and blocks as bars, drag a booking up or down to another room (dates locked), details window with status, source and payment, move with a reason; the bar moves only after the server agrees; a drop that can't work says why at once
- Phone view: the same window as a list by day (arrives, in house, leaves, blocked)
- Dev seed S005: Room 106 blocked for maintenance from tomorrow (calendar demo)
- System design guide: Sprint 06 section, part 4.16 Moving a guest to another room, room-move flow diagram

**Files added / changed**

- database/migrations/V008__reservation_calendar.sql; database/seed/dev/S005__calendar.sql; database/queries/check-sprint-06.sql
- backend/Resort.Contracts/ReservationContracts.cs (CalendarDto, ReassignRoomRequest)
- backend/Resort.Application/ — Reservations/CalendarService.cs, Reservations/ReservationService.cs (ReassignAsync), Validation/ReservationValidators.cs, DependencyInjection.cs
- backend/Resort.Api/Endpoints/ReservationEndpoints.cs
- backend/Resort.Tests/ — Integration/CalendarTests.cs, Integration/DevSeedTests.cs, Support/BookingKit.cs
- frontend/src/app/ — features/reservations/calendar.page.ts, calendar-grid.ts, calendar.model.ts, move-dialog.ts, calendar.spec.ts, reservations.api.ts, reservations.page.ts; core/models.ts; app.routes.ts; layout/shell.ts
- frontend/e2e/sprint-06.spec.ts; scripts/check-api.mjs (Sprint 06 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-06.md, docs/architecture/system-design.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-06-screens/; tools/handbooks/diagrams (room-move-flow)

**Test results**

- Backend: 181 passed, 0 failed on real PostgreSQL 16 (8 new: CalendarTests with two moves into the same room raced 20 times, the audit trail's who and why, blocked and booked rooms, field errors, the window, permissions and the calendar index; plus the calendar dev seed check)
- Frontend: 54 of 54 unit tests passed (10 new, including 7 rooms × 31 days drawn in under 200 ms); ESLint clean; production build OK
- Browser tests: 60 of 60 passed (Sprints 01–05 plus the 8 new Sprint 06 steps, including a move by drag and reason and a drop on a blocked room refused); screenshots in docs/sprints/sprint-06-screens

**Outcome**

Expected when accepted:

- Reception moves a guest from 102 to 103 in two actions; the audit trail shows who/why

## Scope

**API endpoints**

- `GET /api/v1/properties/{propertyId}/calendar?from&to`
- `POST /api/v1/properties/{propertyId}/reservations/{id}/reassign  (reason required)`

**Screens / routes**

- `/reservations/calendar`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] API: calendar read model (single query, indexed) including blocks and maintenance blocks
- [x] API: reassign room — same locking as booking; audited with reason (brief §58)
- [x] UI: calendar component (CDK drag-drop); optimistic UI only after server confirms; conflicts shown inline
- [x] UI: mobile view = list by day

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S06-<issue-number>-<short-name>`
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

- If the UI shows a booking the server rejected, the optimistic update wasn't rolled back — check the reassign error handler.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Concurrency — two users reassigning into the same room → one wins
- Frontend — calendar renders 7 rooms × 31 days under 200 ms

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Reception moves a guest from 102 to 103 in two actions; the audit trail shows who/why

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Drag a booking to another room; drag onto a blocked room is refused

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-06` created on `main`
