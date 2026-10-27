# Sprint 15 — Inventory and recipe costing

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 5 — Restaurant, KOT, menu, inventory, purchasing | M | [Sprint 14](sprint-14-menu-restaurant-pos-and-kot.md) |

**Goal:** Stock ledger, consumption from recipes, wastage, and food-cost reporting (brief §36, §37).

**GitHub:** milestone *Sprint 15 — Inventory and recipe costing* · branch prefix `feature/S15-`

## Where this sprint stands

**Now:** Built; pull request into develop waits for the owner's review.

**Done so far**

- Migration V017: inventory_units (kg, g, l, ml, pcs, dozen with kind and size), inventory_categories, stock_locations (one kitchen per property), inventory_items (moving-average cost before GST, reorder level), stock_movements (append-only ledger), stock_levels (on hand per item and store, kept by a trigger in the same statement), stock_adjustments (SA00001…) and stock_counts (SC00001…), never changed, recipes (per dish or portion) and recipe_ingredients
- Stock received, wastage, expired, damaged, corrections and transfers between stores, each with a reason and in the audit log; counts post their differences with a reason; items locked FOR UPDATE in id order so two changes at once both count and stock never goes below zero (except a served dish)
- Serving a kitchen ticket uses each live line's recipe (the portion's, else the dish's) from the kitchen store, in the same transaction, once per line and item
- Recipe cost, food cost %, contribution and margin per dish and portion; food-cost report for a period with sales, cost of sales, written off, and the highest and lowest margins
- Inventory screen (Stock, Recipes, Food cost, Ledger) with record, count, item, recipe, group and store dialogs; 360 px phone layout; every refusal said under its field
- Dev seed S012: main store and kitchen, 21 items in 6 groups, recipes for 8 dishes (Paneer Tikka Full ₹105 of ₹280 = 37.5 %), wastage SA00001
- [done] Docs: API.md, DATABASE.md, schema-sprint-15.md, system design Sprint 15 and 4.25 with the stock ledger diagram, ARCHITECTURE.md, RUN_AND_TEST, REVIEWING, CHANGELOG

**Files added / changed**

- database/migrations/V017__inventory.sql; database/seed/dev/S012__inventory.sql; database/queries/check-sprint-15.sql
- backend/Resort.Domain/Inventory/Inventory.cs; backend/Resort.Contracts/InventoryContracts.cs
- backend/Resort.Application/ — Inventory/ (StockLedger.cs, InventoryService.cs, RecipeService.cs), Validation/InventoryValidators.cs, Restaurant/OrderService.cs (serve uses recipes), Common/Permissions.cs, Common/Abstractions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/ — Endpoints/InventoryEndpoints.cs, Program.cs
- backend/Resort.Tests/ — Integration/InventoryTests.cs, Unit/InventoryUnitTests.cs, Integration/DevSeedTests.cs
- frontend/src/app/ — features/inventory/ (inventory.page.ts, inventory-dialogs.ts, inventory.api.ts, inventory.spec.ts), core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-15.spec.ts; scripts/check-api.mjs (Sprint 15 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-15.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-15-screens/; tools/handbooks/diagrams (stock-ledger)

**Test results**

- Backend: 473 passed, 0 failed on real PostgreSQL 16 (27 new: InventoryTests with two write-offs of the same item at the same moment (both count, neither below zero), serving 3 Paneer Tikka uses 0.6 kg paneer once and the report shows 37.5 %, reasons, audit and an unchangeable ledger, counts, recipe units and portions, one kitchen; InventoryUnitTests incl. ₹280 / ₹105 → 37.5 %; dev seed check)
- Frontend: 104 of 104 unit tests passed (4 new); ESLint clean; production build OK
- Browser tests: Sprint 15's 7 steps pass (stock, recipe at 37.5 %, sell 3 → 0.6 kg less paneer with the ledger saying why, food-cost report with highest and lowest margins on desktop and a 360 px phone, wastage and count refusals under their fields, kitchen can't change stock); Sprint 14 run before it still passes; API checks 207 of 207 (10 new); database checks clean; screenshots in docs/sprints/sprint-15-screens

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
- `stock_levels`
- `stock_adjustments`
- `stock_counts`
- `recipes`
- `recipe_ingredients`

**API endpoints**

- `/api/v1/properties/{propertyId}/inventory/*`

**Screens / routes**

- `/inventory`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] stock_movements is an append-only ledger; on-hand = sum of movements (materialised per item/location)
- [x] Served order lines consume recipe ingredients
- [x] Adjustments require reason; audited (brief §58)
- [x] Food cost %, contribution, margin per item
- [x] Seed: sample inventory

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

Sprint-specific:

- Stock that doesn't add up: database/queries/check-sprint-15.sql block 4 compares each level with the sum of its movements and block 5 each on_hand_after with the row before (both should return nothing).
- A dish served but no stock used: the dish (or its portion) has no active recipe, or the property has no kitchen store; block 7 lists consumption rows, and the Ledger view filtered to Used (served) shows the order and ticket.
- Stock below zero: only a served dish can do that; the Stock view says Below zero: count it. Record stock received or count the store.

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
