# Sprint 14 — Menu, restaurant POS and KOT

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 5 — Restaurant, KOT, menu, inventory, purchasing | L | [Sprint 08](sprint-08-folio.md), [Sprint 09](sprint-09-gst-configuration-and-tax-engine.md) |

**Goal:** Orders, kitchen tickets, and charging to a room folio (brief §35, §55).

**GitHub:** milestone *Sprint 14 — Menu, restaurant POS and KOT* · branch prefix `feature/S14-`

## Where this sprint stands

**Now:** In progress — see the open issues in this sprint's milestone.

**Done so far**

_Nothing yet._

**Files this sprint will add / change** (expected)

- database/migrations/V###__restaurant.sql — new tables (next free number)
- backend/Resort.Domain/Restaurant/ — entities
- backend/Resort.Contracts/Contracts.cs — request/response records
- backend/Resort.Application/Restaurant/ — services; Validation/Validators.cs; Common/Permissions.cs if new codes; DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs — DbSets and mappings
- backend/Resort.Api/Endpoints/RestaurantEndpoints.cs + Program.cs registration
- backend/Resort.Tests/Integration/RestaurantTests.cs (+ Unit/ for pure calculations)
- frontend/src/app/features/restaurant/ — screens; app.routes.ts; layout/shell.ts menu entry
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-14.md, this guide, CHANGELOG.md

**Test results**

_Not run yet — see How to test below._

**Outcome**

Expected when accepted:

- Waiter takes an order on a phone, kitchen sees the KOT, bill charged to room

## Scope

**Tables**

- `menu_categories`
- `menu_items`
- `menu_item_variants`
- `modifier_groups`
- `modifiers`
- `restaurant_tables`
- `orders`
- `order_items`
- `kots`
- `kot_items`

**API endpoints**

- `/api/v1/properties/{propertyId}/menu/*`
- `/api/v1/properties/{propertyId}/orders/*`
- `/api/v1/properties/{propertyId}/kitchen/*`

**Screens / routes**

- `/restaurant`
- `/restaurant/orders`
- `/restaurant/kitchen`
- `/restaurant/menu`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] Order → KOT → kitchen ready → served → paid or charged to room (folio)
- [ ] Optimistic concurrency on orders (two waiters editing)
- [ ] Discounts need pos.discount; voids need pos_order.void + reason
- [ ] Kitchen display auto-refresh (polling every 10 s; no websockets yet — brief §76)
- [ ] Seed: sample menu

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S14-<issue-number>-<short-name>`
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

- Concurrency — two users modifying the same order → second gets 409
- Integration — room charge posts ₹1,250 to the guest folio (brief §73)

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Waiter takes an order on a phone, kitchen sees the KOT, bill charged to room

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Order → KOT → ready → charge to Room 103

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-14` created on `main`
