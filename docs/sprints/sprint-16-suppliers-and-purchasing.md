# Sprint 16 — Suppliers and purchasing

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 5 — Restaurant, KOT, menu, inventory, purchasing | M | [Sprint 15](sprint-15-inventory-and-recipe-costing.md) |

**Goal:** Purchase request → PO → goods received → supplier invoice → payment (brief §38).

**GitHub:** milestone *Sprint 16 — Suppliers and purchasing* · branch prefix `feature/S16-`

## Where this sprint stands

**Now:** Not started. Can start once Sprint 15 is accepted.

**Done so far**

_Nothing yet._

**Files this sprint will add / change** (expected)

- database/migrations/V###__purchasing.sql — new tables (next free number)
- backend/Resort.Domain/Purchasing/ — entities
- backend/Resort.Contracts/Contracts.cs — request/response records
- backend/Resort.Application/Purchasing/ — services; Validation/Validators.cs; Common/Permissions.cs if new codes; DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs — DbSets and mappings
- backend/Resort.Api/Endpoints/PurchasingEndpoints.cs + Program.cs registration
- backend/Resort.Tests/Integration/PurchasingTests.cs (+ Unit/ for pure calculations)
- frontend/src/app/features/inventory/ — screens; app.routes.ts; layout/shell.ts menu entry
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-16.md, this guide, CHANGELOG.md

**Test results**

_Not run yet — see How to test below._

**Outcome**

Expected when accepted:

- PO raised, approved, received, invoiced, paid — stock updated

## Scope

**Tables**

- `suppliers`
- `purchase_requests`
- `purchase_request_items`
- `purchase_orders`
- `purchase_order_items`
- `goods_received_notes`
- `grn_items`
- `supplier_invoices`
- `supplier_invoice_items`
- `supplier_payments`

**Screens / routes**

- `/inventory/suppliers`
- `/inventory/purchases`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] PO approval (purchase.approve) with thresholds
- [ ] GRN increases stock via stock_movements
- [ ] Supplier GSTIN and input tax captured
- [ ] Seed: sample suppliers

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S16-<issue-number>-<short-name>`
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

- Integration — partial GRN, remaining quantity tracked

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- PO raised, approved, received, invoiced, paid — stock updated

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Full purchase cycle for vegetables

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-16` created on `main`
