# Sprint 07 — Front desk — arrivals, departures, in-house, check-in/out

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 2 — Guests, reservations, availability, calendar, front desk | M | [Sprint 05](sprint-05-availability-engine-and-reservations.md) |

**Goal:** Reception completes check-in and check-out in very few clicks.

**GitHub:** milestone *Sprint 07 — Front desk — arrivals, departures, in-house, check-in/out* · branch prefix `feature/S07-`

## Where this sprint stands

**Now:** Not started. Can start once Sprint 05 is accepted.

**Done so far**

_Nothing yet._

**Files this sprint will add / change** (expected)

- backend/Resort.Domain/FrontDesk/ — entities
- backend/Resort.Contracts/Contracts.cs — request/response records
- backend/Resort.Application/FrontDesk/ — services; Validation/Validators.cs; Common/Permissions.cs if new codes; DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs — DbSets and mappings
- backend/Resort.Api/Endpoints/FrontDeskEndpoints.cs + Program.cs registration
- backend/Resort.Tests/Integration/FrontDeskTests.cs (+ Unit/ for pure calculations)
- frontend/src/app/features/front-desk/ — screens; app.routes.ts; layout/shell.ts menu entry
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-07.md, this guide, CHANGELOG.md

**Test results**

_Not run yet — see How to test below._

**Outcome**

Expected when accepted:

- Walk-in to checked-in in ≤ 3 screens

## Scope

**API endpoints**

- `GET /api/v1/properties/{propertyId}/front-desk/arrivals?date`
- `GET /api/v1/properties/{propertyId}/front-desk/departures?date`
- `GET /api/v1/properties/{propertyId}/front-desk/in-house`
- `POST /api/v1/properties/{propertyId}/reservations/{id}/assign-room`
- `POST /api/v1/properties/{propertyId}/reservations/{id}/check-in`
- `POST /api/v1/properties/{propertyId}/reservations/{id}/check-out`
- `POST /api/v1/properties/{propertyId}/walk-ins`

**Screens / routes**

- `/front-desk/arrivals`
- `/front-desk/departures`
- `/front-desk/in-house`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] Business date from property timezone; 'today' never from the server clock
- [ ] Check-in requires assigned room/bed with housekeeping READY (configurable override with reason)
- [ ] Check-out sets housekeeping DIRTY (event consumed in Sprint 13) and is blocked while folio balance > 0 (setting frontdesk.allow_checkout_with_balance)
- [ ] Walk-in = guest + reservation + check-in in one call (one transaction)
- [ ] Reception dashboard (brief §54)

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
