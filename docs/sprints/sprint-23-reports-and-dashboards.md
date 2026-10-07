# Sprint 23 — Reports and dashboards

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 8 — Expenses, budgets, reports | L | [Sprint 12](sprint-12-gst-invoices-credit-debit-notes-receipts.md), [Sprint 15](sprint-15-inventory-and-recipe-costing.md), [Sprint 19](sprint-19-leave-holidays-and-payroll-export.md), [Sprint 22](sprint-22-expenses-and-budgets.md) |

**Goal:** Owner and role dashboards plus the report list in brief §50–55, Power-BI-ready.

**GitHub:** milestone *Sprint 23 — Reports and dashboards* · branch prefix `feature/S23-`

## Where this sprint stands

**Now:** Not started. Can start once Sprint 12, Sprint 15, Sprint 19, Sprint 22 are accepted.

**Done so far**

_Nothing yet._

**Files this sprint will add / change** (expected)

- backend/Resort.Domain/Reports/ — entities
- backend/Resort.Contracts/Contracts.cs — request/response records
- backend/Resort.Application/Reports/ — services; Validation/Validators.cs; Common/Permissions.cs if new codes; DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs — DbSets and mappings
- backend/Resort.Api/Endpoints/ReportsEndpoints.cs + Program.cs registration
- backend/Resort.Tests/Integration/ReportsTests.cs (+ Unit/ for pure calculations)
- frontend/src/app/features/reports/ — screens; app.routes.ts; layout/shell.ts menu entry
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-23.md, this guide, CHANGELOG.md

**Test results**

_Not run yet — see How to test below._

**Outcome**

Expected when accepted:

- Owner dashboard loads < 1 s on a year of seed data

## Scope

**Screens / routes**

- `/dashboard (role-aware)`
- `/reports`

**Other**

- reporting schema with views/materialised views
- refreshed by jobs

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] reporting.* views: occupancy, ADR, RevPAR, revenue splits, OTA net, food/labour cost, GST, P&L
- [ ] No dashboard query scans raw transaction tables unbounded (brief §88)
- [ ] Read-only DB role for Power BI

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S23-<issue-number>-<short-name>`
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

- Database — ADR/RevPAR against a hand-calculated fixture month

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Owner dashboard loads < 1 s on a year of seed data

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Owner dashboard + monthly report

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-23` created on `main`
