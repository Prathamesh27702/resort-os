# Sprint 33 — Online food orders and QR table ordering (only if wanted)

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 10 — Hardening, backup, monitoring, deployment | L | [Sprint 14](sprint-14-menu-restaurant-pos-and-kot.md), [Sprint 30](sprint-30-india-readiness.md) |

**Goal:** Take orders from Swiggy and Zomato and from a QR code on the table, for properties whose restaurant also serves outside guests (market comparison of 13 Nov 2026, the could-have list). The owner decides whether this sprint is done at all.

**GitHub:** milestone *Sprint 33 — Online food orders and QR table ordering (only if wanted)* · branch prefix `feature/S33-`

## Where this sprint stands

**Now:** Not started. Can start once Sprint 14, Sprint 30 are accepted.

**Done so far**

_Nothing yet._

**Files this sprint will add / change** (expected)

- database/migrations/V###__restaurant.sql — new tables (next free number)
- backend/Resort.Domain/Restaurant/ — entities
- backend/Resort.Contracts/Contracts.cs — request/response records
- backend/Resort.Application/Restaurant/ — services; Validation/Validators.cs; Common/Permissions.cs if new codes; DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs — DbSets and mappings
- backend/Resort.Infrastructure/Restaurant/ — external API adapter + fake server for tests
- backend/Resort.Api/Endpoints/RestaurantEndpoints.cs + Program.cs registration
- backend/Resort.Tests/Integration/RestaurantTests.cs (+ Unit/ for pure calculations)
- frontend/src/app/features/online-orders/ — screens; app.routes.ts; layout/shell.ts menu entry
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-33.md, this guide, CHANGELOG.md

**Test results**

_Not run yet — see How to test below._

**Outcome**

Expected when accepted:

- A test Swiggy order prints its kitchen ticket once

## Scope

**Tables**

- `online_order_channels`
- `online_orders`
- `table_qr_codes`

**External APIs used**

- An aggregator hub such as UrbanPiper (one integration for Swiggy and Zomato)

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] Aggregator orders through one hub: menu and price sync, item on or off, an order accepted into the kitchen display and the bill, commission recorded
- [ ] QR on each table and in each room: the menu, an order that reaches the waiter to confirm, the bill and its UPI QR on the guest's phone

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S33-<issue-number>-<short-name>`
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

- Integration — an aggregator order delivered twice makes one order

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- A test Swiggy order prints its kitchen ticket once

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Scan the table QR → order → kitchen ticket → pay by UPI

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-33` created on `main`
