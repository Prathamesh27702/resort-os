# Sprint 14 — Menu, restaurant POS and KOT

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 5 — Restaurant, KOT, menu, inventory, purchasing | L | [Sprint 08](sprint-08-folio.md), [Sprint 09](sprint-09-gst-configuration-and-tax-engine.md) |

**Goal:** Orders, kitchen tickets, and charging to a room folio (brief §35, §55).

**GitHub:** milestone *Sprint 14 — Menu, restaurant POS and KOT* · branch prefix `feature/S14-`

## Where this sprint stands

**Now:** Built; pull request into develop waits for the owner's review.

**Done so far**

- Migration V016: menu (sections, dishes with veg / non-veg / egg and sold out, portions, groups of choices; switched off, never deleted), restaurant_tables, orders (O00001…, one open order per table, totals and CHECKs on the row, final once settled or voided), order_items (name and price copied; a sent line can only be voided), kots (K00001…) and kot_items; cash_drawer_movements POS_SALE with the order; folio source RESTAURANT
- Order → send → kitchen Start / Ready → served → paid at the restaurant (cash into the open drawer, UPI, card, bank transfer) or charged to the room: one FOOD line on the guest's main folio with the same GST as the order
- Every change sends the order's version; the row is locked first, so of two waiters changing one order at once the second gets 409 naming the order, and the phone reloads with the first one's change
- Discounts need pos.discount and voids pos_order.void, each with a reason; voided lines are struck through on the kitchen ticket and a ticket with nothing left is cancelled
- Kitchen display polls every 10 s (oldest first, red after 15 min); the order page follows the kitchen the same way while food is cooking
- Screens for a 360 px phone: Restaurant floor, order page with the menu picker, today's orders; Kitchen display for a tablet; Menu editor with portions, choices, sold out and tables
- Dev seed S011: 14-dish menu (Paneer Tikka Half / Full with spice level and add-ons, Masala Chai sold out), tables T1–T4, B1, B2, and O00001 at T2 in the kitchen as K00001
- [done] Docs: API.md, DATABASE.md, schema-sprint-14.md, system design Sprint 14 and 4.24 with the restaurant flow diagram, ARCHITECTURE.md, RUN_AND_TEST, REVIEWING, CHANGELOG

**Files added / changed**

- database/migrations/V016__restaurant.sql; database/seed/dev/S011__restaurant.sql; database/queries/check-sprint-14.sql
- backend/Resort.Domain/Restaurant/Restaurant.cs; backend/Resort.Domain/Folios/Folios.cs; backend/Resort.Domain/Payments/Payments.cs
- backend/Resort.Contracts/RestaurantContracts.cs
- backend/Resort.Application/ — Restaurant/ (OrderService.cs, MenuService.cs), Validation/RestaurantValidators.cs, Payments/CashDrawerService.cs, Common/Permissions.cs, Common/Abstractions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/ — Endpoints/RestaurantEndpoints.cs, Program.cs
- backend/Resort.Tests/ — Integration/RestaurantTests.cs, Unit/RestaurantUnitTests.cs, Integration/DevSeedTests.cs
- frontend/src/app/ — features/restaurant/ (floor.page.ts, order.page.ts, order-dialogs.ts, orders.page.ts, kitchen.page.ts, menu.page.ts, restaurant.api.ts, restaurant.spec.ts), core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-14.spec.ts; scripts/check-api.mjs (Sprint 14 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-14.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-14-screens/; tools/handbooks/diagrams (restaurant-flow)

**Test results**

- Backend: 446 passed, 0 failed on real PostgreSQL 16 (29 new: RestaurantTests with two waiters adding to one order at the same moment (exactly one 409, ten times over), the ₹1,250 room charge as one FOOD line on the guest's folio with its GST, voids and discounts by permission, 5% GST with a discount on a registered property, cash into the drawer, one order per table, room service only for in-house rooms, no room charge after check-out, portions, choices and sold-out dishes, database triggers refusing changes to sent lines and settled orders; RestaurantUnitTests; dev seed check)
- Frontend: 100 of 100 unit tests passed (5 new: line prices, what is still to pick, one-tap dishes, menu search, kitchen columns and the 10 s refresh); ESLint clean; production build OK
- Browser tests: full run of Sprints 01–14, 115 of 117 in about 18 minutes: Sprint 09's S09-06 GSTIN check failed once (the field showed the format message instead of the check-digit one) and S09-07 after it was skipped; run again on its own, Sprint 09 passed 7 of 7. The 9 Sprint 14 steps (waiter on a 360 px phone, kitchen on a tablet, second waiter refused with 409, the ticket appearing on the kitchen display by itself, ₹1,250 charged to Room 104) pass; API checks 197 of 197 (20 new); database checks clean (totals add up, room charge matches its folio line, one open order per table, tickets match their lines, numbers gap-free); screenshots in docs/sprints/sprint-14-screens

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

- [x] Order → KOT → kitchen ready → served → paid or charged to room (folio)
- [x] Optimistic concurrency on orders (two waiters editing)
- [x] Discounts need pos.discount; voids need pos_order.void + reason
- [x] Kitchen display auto-refresh (polling every 10 s; no websockets yet — brief §76)
- [x] Seed: sample menu

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

Sprint-specific:

- A table that shows an order nobody can find: database/queries/check-sprint-14.sql block 9 lists tables with two open orders (there should be none); the floor shows the open one.
- A room charge missing from a bill: block 6 compares each ROOM_CHARGE order with its folio line (amount, GST, source RESTAURANT); block 7 finds orders without a line and lines without an order.
- Kitchen display not updating: it asks GET /kitchen/kots every 10 s only while the tab is visible; the 'last HH:mm:ss' under the title shows the last answer.

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

- Order → KOT → ready → charge to Room 104 (R00008, in house; the plan said Room 103, which is kept free for Sprint 2's review)

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-14` created on `main`
