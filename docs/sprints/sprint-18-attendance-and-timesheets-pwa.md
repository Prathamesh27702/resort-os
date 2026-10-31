# Sprint 18 — Attendance and timesheets (PWA)

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🟢 Done | Phase 6 — Employees, attendance, timesheets, leave, roster | L | [Sprint 17](sprint-17-employees-and-roster.md) |

**Goal:** Mobile clock-in/out with geofence, breaks, timesheets with approval and locked corrections (brief §43, §44, §74).

**GitHub:** milestone *Sprint 18 — Attendance and timesheets (PWA)* · branch prefix `feature/S18-`

## Where this sprint stands

**Now:** Accepted by the owner on 31 Oct 2026 and released to main (tag sprint-18).

**Done so far**

- Migration V020: geofences (radius 20–2000 m), attendance_events (append-only; a manager entry needs a reason), timesheets (one per person per week, Monday start; a locked one can't change), timesheet_lines (one per day; locked with the timesheet) and timesheet_corrections (append-only, old and new times, reason)
- Clocking in and out on a phone: the browser sends its position, the server measures the distance to each active geofence (the phone's accuracy counts up to 50 m) and refuses with how far away and which area (422 OUTSIDE_GEOFENCE); no geofence set up means it isn't checked; breaks don't check location
- A manager (attendance.update) records a clock event for someone with a reason, e.g. no signal; it shows as a manager entry everywhere; nobody records their own
- Timesheet maths in the property's time zone: worked = out − in − breaks, late and left early against the rostered shift, overtime past the shift's paid minutes (8 h without a shift), night shifts counted on the day they start, a day with no clock-out after 16 h shown as MISSING_OUT
- Timesheets DRAFT → SUBMITTED → APPROVED or REJECTED (with a reason, then resubmitted) → LOCKED for payroll; approve-all for a week skips anyone with a day without a clock-out and says why; corrections keep the old times, need a reason and are refused once locked; nobody approves or corrects their own
- Installable app (PWA): web manifest, icons, service worker in production builds (API calls never cached), install prompt on the dashboard and in the user menu, an iPhone hint
- Screens Attendance (the phone clock, plus the team board for managers with manager entries and geofence areas), Timesheets (mine and team, approve all) and one timesheet with corrections; the dashboard's Your day card; every refusal said in words
- Dev seed S015: the Main gate geofence (300 m), last week's roster and clock events with nine timesheets waiting for approval (Ravi forgot to clock out on Tuesday, Pooja was absent), and this week's events up to yesterday
- [done] Docs: API.md, DATABASE.md, schema-sprint-18.md, system design Sprint 18 and 4.28 with the attendance flow diagram, ARCHITECTURE.md, RUN_AND_TEST, REVIEWING, CHANGELOG

**Files added / changed**

- database/migrations/V020__attendance.sql; database/seed/dev/S015__attendance.sql; database/queries/check-sprint-18.sql
- backend/Resort.Domain/Attendance/Attendance.cs (TimesheetMath); backend/Resort.Contracts/AttendanceContracts.cs
- backend/Resort.Application/ — Attendance/ (AttendanceService.cs, TimesheetService.cs), Validation/AttendanceValidators.cs, Common/Permissions.cs, Common/Abstractions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/ — Endpoints/AttendanceEndpoints.cs, Infrastructure/Problems.cs, Program.cs
- backend/Resort.Tests/ — Unit/AttendanceUnitTests.cs, Integration/AttendanceTests.cs, Integration/DevSeedTests.cs
- frontend/ — ngsw-config.json, public/manifest.webmanifest, public/icons/, src/index.html, src/app/app.config.ts, src/app/core/install.ts
- frontend/src/app/ — features/attendance/ (attendance.page.ts, attendance-dialogs.ts, timesheets.page.ts, attendance.api.ts, attendance.spec.ts), features/dashboard/dashboard.page.ts, core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-18.spec.ts; scripts/check-api.mjs (Sprint 18 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-18.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-18-screens/; tools/handbooks/diagrams (attendance-flow)

**Test results**

- Backend: 510 passed, 0 failed on real PostgreSQL 16 (13 new: the brief's 08:03 in, 12:00–12:30 break, 16:10 out = 7 h 37 min; a night shift 22:55–07:10 counted on the day it started, and one on Sunday counted in that week; late, early, overtime, two sessions in a day, a missing clock-out after 16 h, the distance maths; clocking in refused outside the geofence then accepted at it and the week approved; a forgotten clock-out blocking approval until corrected, with the correction kept; a manager entry with a reason, never their own, and a rejected week going back; two clock-ins at the same moment making exactly one, 5 times; dev seed check)
- Frontend: 113 of 113 unit tests passed (2 new); ESLint clean; production build OK
- Browser tests: all 146 steps of Sprints 01–18 (21.5 min; one Sprint 16 step, S16-01, lost its typed supplier code under load in the full run and passed 8 of 8 when Sprint 16 was run again alone); Sprint 18's 7 steps cover a housekeeper on a 360 px phone refused 1.1 km from the gate then clocking in at it, a break and clock-out, a manager recording a clock-in with a reason, her week submitted and approved, last week approved all at once with Ravi's missing clock-out corrected, approved and locked, and the installable app's manifest and icons; API checks 231 of 231 (7 new); database checks clean; screenshots in docs/sprints/sprint-18-screens

**Outcome**

Accepted 31 Oct 2026 (the owner reviewed it on their PC). A housekeeper on a phone is refused 1.1 km from the gate with the distance, clocks in at the gate, takes a break and clocks out; a manager records a clock-in for someone with no signal, with a reason; she submits her week and the manager approves it; last week is approved all at once except Ravi's missing clock-out, which is corrected with a reason, approved and locked for payroll (AttendanceTests, browser steps S18-01 … S18-07). Code #157; on main as tag sprint-18. Sprint 19 (leave, holidays and payroll export) is next.

## Scope

**Tables**

- `attendance_events`
- `geofences`
- `timesheets`
- `timesheet_lines`
- `timesheet_corrections`

**API endpoints**

- `/api/v1/properties/{propertyId}/attendance/*, /timesheets/*`

**Screens / routes**

- `/attendance`
- `/timesheets`
- `employee dashboard`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] Angular PWA (service worker, install prompt)
- [x] Geofence check server-side from device coordinates; manager override with reason
- [x] Worked hours/breaks/overtime/late/early computed in property timezone
- [x] States DRAFT→SUBMITTED→APPROVED/REJECTED→LOCKED; corrections audited

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S18-<issue-number>-<short-name>`
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

- Refused as outside the area when you are at the resort: the message says how far and from which area. Check the area's centre and radius in Attendance → Areas (database/queries/check-sprint-18.sql block 4); the phone's accuracy counts up to 50 m, so a weak GPS fix indoors can push someone out. A manager can record the clock-in with a reason meanwhile.
- Approve all skipped someone: the list under the button says why, usually a day with a clock-in and no clock-out. Correct that day with a reason, then approve. Block 9 lists approved or locked timesheets with an open day (should be none).
- Hours look wrong: open the timesheet and expand the day's entries; times are in the property's time zone and a night shift counts on the day it starts. Block 8 checks totals equal the sum of the days; block 10 lists corrections with old and new times.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Timesheet — 08:03 in, 12:00–12:30 break, 16:10 out = 7h 37m (brief §44)
- Timesheet — shift crossing midnight
- E2E — employee acceptance test §74 (first half)

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Employee clocks in/out on a phone; manager approves the week

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Brief §74 up to approval

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-18` created on `main`
