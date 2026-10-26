# Sprint 15 — Inventory and recipe costing

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 5 — Restaurant, KOT, menu, inventory, purchasing | M | [Sprint 14](sprint-14-menu-restaurant-pos-and-kot.md) |

**Goal:** Stock ledger, consumption from recipes, wastage, and food-cost reporting (brief §36, §37).

**GitHub:** milestone *Sprint 15 — Inventory and recipe costing* · branch prefix `feature/S15-`

## Where this sprint stands

**Now:** In progress — see the open issues in this sprint's milestone.

**Done so far**

_Nothing yet._

**Files this sprint will add / change** (expected)

- database/migrations/V###__inventory.sql — new tables (next free number)
- backend/Resort.Domain/Inventory/ — entities
- backend/Resort.Contracts/Contracts.cs — request/response records
- backend/Resort.Application/Inventory/ — services; Validation/Validators.cs; Common/Permissions.cs if new codes; DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs — DbSets and mappings
- backend/Resort.Api/Endpoints/InventoryEndpoints.cs + Program.cs registration
- backend/Resort.Tests/Integration/InventoryTests.cs (+ Unit/ for pure calculations)
- frontend/src/app/features/inventory/ — screens; app.routes.ts; layout/shell.ts menu entry
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-15.md, this guide, CHANGELOG.md

**Test results**

_Not run yet — see How to test below._

**Outcome**

Expected when accepted:

- Owner sees highest/lowest margin items

## Scope

**Tables**

- `inventory_categories`
- `inventory_units`
- `inventory_items`
- `stock_locations`
- `stock_movements`
- `stock_adjustments`
- `stock_counts`
- `recipes`
- `recipe_ingredients`

**Screens / routes**

- `/inventory`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] stock_movements is an append-only ledger; on-hand = sum of movements (materialised per item/location)
- [ ] Served order lines consume recipe ingredients
- [ ] Adjustments require reason; audited (brief §58)
- [ ] Food cost %, contribution, margin per item
- [ ] Seed: sample inventory

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S15-<issue-number>-<short-name>`
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

- Concurrency — two adjustments on the same item don't lose updates
- Unit — Paneer Tikka ₹280 / ₹105 → 37.5 % food cost

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Owner sees highest/lowest margin items

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Sell 3 Paneer Tikka → stock reduced → food-cost report

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-15` created on `main`
