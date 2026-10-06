# Sprint 18 — Attendance and timesheets (PWA)

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 6 — Employees, attendance, timesheets, leave, roster | L | [Sprint 17](sprint-17-employees-and-roster.md) |

**Goal:** Mobile clock-in/out with geofence, breaks, timesheets with approval and locked corrections (brief §43, §44, §74).

**GitHub:** milestone *Sprint 18 — Attendance and timesheets (PWA)* · branch prefix `feature/S18-`

## Scope

**Tables**

- `attendance_events`
- `geofences`
- `timesheets`
- `timesheet_lines`
- `timesheet_corrections`

**Screens / routes**

- `/attendance`
- `/timesheets`
- `employee dashboard`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] Angular PWA (service worker, install prompt)
- [ ] Geofence check server-side from device coordinates; manager override with reason
- [ ] Worked hours/breaks/overtime/late/early computed in property timezone
- [ ] States DRAFT→SUBMITTED→APPROVED/REJECTED→LOCKED; corrections audited

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S18-<issue-number>-<short-name>`
3. **Database up:** `docker compose --env-file .env -f infra/docker/docker-compose.yml up -d db`
4. **Schema changes:** add the next `database/migrations/V###__<name>.sql` (never edit an applied one). The API applies it on start.
5. **API:** `cd backend/Resort.Api && dotnet watch run` → http://localhost:5180/swagger
6. **Web:** `cd frontend && npm start` → http://localhost:4300
7. **Fresh data:** `scripts/db-reset.sh`, then restart the API (migrations + bootstrap owner run again; dev seed data from Sprint 2 onward)

## How to debug

- **API:** run the `Resort.Api` launch profile in VS Code (C# Dev Kit), Rider or Visual Studio and set breakpoints; Swagger's *Authorize* button takes the `accessToken` from `POST /api/v1/auth/login`.
- **Every error response has a `correlationId`.** Search the API console/log for it to find the full exception.
- **SQL:** set `Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore.Database.Command=Information` in `.env` to log every SQL statement.
- **Database:** `docker compose --env-file .env -f infra/docker/docker-compose.yml exec db psql -U $POSTGRES_USER $POSTGRES_DB`
- **Web:** Angular DevTools + the browser Network tab; API errors are `{code, message, details, correlationId}`.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Timesheet — 08:03 in, 12:00–12:30 break, 16:10 out = 7h 37m (brief §44)
- Timesheet — shift crossing midnight
- E2E — employee acceptance test §74 (first half)

Commands:

```bash
scripts/test.sh                                   # everything (backend + frontend)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

## Acceptance criteria

- Employee clocks in/out on a phone; manager approves the week

## Sprint review demo

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
