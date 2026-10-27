# Sprint 16 — Suppliers and purchasing

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 5 — Restaurant, KOT, menu, inventory, purchasing | M | [Sprint 15](sprint-15-inventory-and-recipe-costing.md) |

**Goal:** Purchase request → PO → goods received → supplier invoice → payment (brief §38).

**GitHub:** milestone *Sprint 16 — Suppliers and purchasing* · branch prefix `feature/S16-`

## Where this sprint stands

**Now:** Built; pull request into develop waits for the owner's review.

**Done so far**

- Migration V018: suppliers (GSTIN optional, state from it), purchase_requests and items (PR00001…), purchase_orders and items (PO00001…, received and billed per line with billed ≤ received ≤ ordered by CHECK), goods_received_notes and grn_items (GRN00001…, never changed), supplier_invoices (SI00001…, fixed once saved except what is paid) and items, supplier_payments (SP00001…, never changed); stock_movements.grn_item_id and cash_drawer_movements.supplier_payment_id
- Purchase orders from a request or on their own; submit approves at once within the approval limit (purchasing.approval_limit, default ₹10,000 with GST), over it waits for purchase.approve from someone who did not raise or submit it; send back with a reason, cancel, close the rest
- Goods received per delivery: the order is locked FOR UPDATE so two deliveries at once never take more than is still to come (refused under the line, saying how much is left); each line is a RECEIVED stock movement at the order's price that moves the item's average cost; PARTLY_RECEIVED until every line is in
- Supplier GSTIN checked down to its check digit, state from it; bills against what came in with CGST + SGST or IGST, input tax and whether it is claimable, price differences flagged, a supplier bill number recorded once; unregistered suppliers charge no GST
- Payments by bank transfer, UPI or cheque (UTR or cheque number required) or cash out of the open drawer as a PAY_OUT; Idempotency-Key so a double tap pays once; never more than is left
- Screens Suppliers, Purchasing (Orders, Requests, Bills) and the order page with the steps a person may take; 360 px phone layouts; every refusal said under its field
- Dev seed S013: four suppliers (Goa, Maharashtra for IGST, one unregistered) and an open vegetable request PR00001
- [done] Docs: API.md, DATABASE.md, schema-sprint-16.md, system design Sprint 16 and 4.26 with the purchase flow diagram, ARCHITECTURE.md, RUN_AND_TEST, REVIEWING, CHANGELOG

**Files added / changed**

- database/migrations/V018__purchasing.sql; database/seed/dev/S013__purchasing.sql; database/queries/check-sprint-16.sql
- backend/Resort.Domain/Purchasing/Purchasing.cs (+ StockMovement.GrnItemId, CashDrawerMovement.SupplierPaymentId); backend/Resort.Contracts/PurchasingContracts.cs
- backend/Resort.Application/ — Purchasing/ (SupplierService.cs, PurchasingService.cs, SupplierInvoiceService.cs), Validation/PurchasingValidators.cs, Inventory/StockLedger.cs and InventoryService.cs (GRN source), Common/Permissions.cs, Common/Abstractions.cs, Common/PropertySettingsReader.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/ — Endpoints/PurchasingEndpoints.cs, Program.cs
- backend/Resort.Tests/ — Integration/PurchasingTests.cs, Unit/PurchasingUnitTests.cs, Integration/DevSeedTests.cs
- frontend/src/app/ — features/purchasing/ (suppliers.page.ts, purchases.page.ts, purchase-order.page.ts, purchasing-dialogs.ts, purchasing.api.ts, purchasing.spec.ts), core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-16.spec.ts; scripts/check-api.mjs (Sprint 16 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-16.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-16-screens/; tools/handbooks/diagrams (purchase-flow)

**Test results**

- Backend: 489 passed, 0 failed on real PostgreSQL 16 (16 new: PurchasingTests with the vegetable cycle from order to payment and stock going up, a partial GRN leaving 60 kg to come while a second delivery at the same moment can't take more, approval by a second person and send back, GSTIN checks with CGST + SGST, IGST and an unregistered supplier, cash out of the drawer paid once and records that can't be changed; PurchasingUnitTests; dev seed check)
- Frontend: 107 of 108 unit tests passed (4 new); the one failure is the Sprint 06 calendar timing test (first render under 200 ms), which also fails on develop in the cloud container (slow machine, about 290 ms); ESLint clean; production build OK
- Browser tests: Sprint 16's 8 steps pass (suppliers and a mistyped GSTIN refused, PR00001 raised as an ₹11,000 order that waits for approval, the owner approves, a part delivery with too much refused and 60 kg still to come, the rest in, the bill, paid by UPI with the UTR required, the clerk can't approve) on desktop and a 360 px phone; API checks 216 of 216 (9 new); database checks clean; screenshots in docs/sprints/sprint-16-screens

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

**API endpoints**

- `/api/v1/properties/{propertyId}/purchasing/*`

**Screens / routes**

- `/inventory/suppliers`
- `/inventory/purchases`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] PO approval (purchase.approve) with thresholds
- [x] GRN increases stock via stock_movements
- [x] Supplier GSTIN and input tax captured
- [x] Seed: sample suppliers

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

Sprint-specific:

- Received doesn't match the deliveries: database/queries/check-sprint-16.sql block 5 compares each order line with its GRN lines and block 6 each GRN line with its stock movement (both should return nothing).
- Can't approve: the person who raised or submitted an order can't approve it (SELF_APPROVAL); someone else with purchase.approve (owner or general manager) must. Within the limit an order is approved on submit; the limit is purchasing.approval_limit in property settings.
- A bill or payment that doesn't add up: block 9 checks paid = payments, total = lines and billed ≤ received; block 10 that each cash payment left the drawer as a PAY_OUT.

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
