# Sprint 13 — Housekeeping and maintenance (mobile-first)

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 4 — Housekeeping, maintenance | M | [Sprint 07](sprint-07-front-desk.md) |

**Goal:** Room status workflow and maintenance tickets that staff use on phones (brief §33, §34, §53).

**GitHub:** milestone *Sprint 13 — Housekeeping and maintenance (mobile-first)* · branch prefix `feature/S13-`

## Where this sprint stands

**Now:** In progress — see the open issues in this sprint's milestone.

**Done so far**

_Nothing yet._

**Files this sprint will add / change** (expected)

- database/migrations/V###__housekeeping.sql — new tables (next free number)
- backend/Resort.Domain/Housekeeping/ — entities
- backend/Resort.Contracts/Contracts.cs — request/response records
- backend/Resort.Application/Housekeeping/ — services; Validation/Validators.cs; Common/Permissions.cs if new codes; DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs — DbSets and mappings
- backend/Resort.Api/Endpoints/HousekeepingEndpoints.cs + Program.cs registration
- backend/Resort.Tests/Integration/HousekeepingTests.cs (+ Unit/ for pure calculations)
- frontend/src/app/features/housekeeping/ — screens; app.routes.ts; layout/shell.ts menu entry
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-13.md, this guide, CHANGELOG.md

**Test results**

_Not run yet — see How to test below._

**Outcome**

Expected when accepted:

- Housekeeper finishes a room in 2 taps; supervisor marks READY

## Scope

**Tables**

- `housekeeping_tasks`
- `housekeeping_checklist_templates`
- `housekeeping_checklist_items`
- `maintenance_assets`
- `maintenance_tickets`
- `maintenance_comments`
- `maintenance_attachments`

**API endpoints**

- `GET /api/v1/properties/{propertyId}/housekeeping/board`
- `POST /api/v1/properties/{propertyId}/housekeeping/tasks/{id}/assign | start | finish | inspect`
- `GET|POST /api/v1/properties/{propertyId}/maintenance/tickets`
- `POST /api/v1/properties/{propertyId}/maintenance/tickets/{id}/assign | progress | resolve | close`

**Screens / routes**

- `/housekeeping`
- `/maintenance`
- `/rooms/status`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] States DIRTY→CLEANING→CLEAN→INSPECTION→READY, OUT_OF_ORDER; check-out event creates the task
- [ ] Out-of-order ticket creates a unit block automatically
- [ ] Photo upload via IFileStore (compressed on device)
- [ ] Housekeeping dashboard (brief §53) usable one-handed on a 360 px phone

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S13-<issue-number>-<short-name>`
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

- Integration — checkout → DIRTY task created once (idempotent on retry)
- Frontend — housekeeping flow on mobile viewport (Playwright)

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Housekeeper finishes a room in 2 taps; supervisor marks READY

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Checkout → task → clean → inspect → READY → room available

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-13` created on `main`
