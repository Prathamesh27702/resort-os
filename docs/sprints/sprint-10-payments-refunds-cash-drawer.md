# Sprint 10 — Payments, refunds, cash drawer

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 3 — Folio, payments, Razorpay, GST, invoices | M | [Sprint 08](sprint-08-folio.md) |

**Goal:** Record cash/UPI/card/bank payments, allocate them to folios, refund safely, reconcile the cash drawer (brief §21, §39).

**GitHub:** milestone *Sprint 10 — Payments, refunds, cash drawer* · branch prefix `feature/S10-`

## Where this sprint stands

**Now:** Built and in review: pull request into develop, waiting for the owner's acceptance.

**Done so far**

- Migration V012: payments (P00001 numbers; CASH, UPI, CARD, BANK_TRANSFER; status machine in a table and a trigger), payment_allocations (what part of a payment is on which bill), refunds (reason required), cash_drawers (one Front desk drawer per property), cash_drawer_sessions (OPEN, VARIANCE_PENDING, CLOSED; one open shift per drawer) and cash_drawer_movements (every rupee in and out of the drawer); nothing can be deleted, and only a payment's status and void fields can change
- No card data: a card reference that holds 13 or more digits in a row is refused by the database, and anything that looks like a card number (13–19 digits passing the Luhn check) is refused under its field by the API and the form
- Refunds are safe under load: the payment row is locked, and a database trigger checks refunded ≤ received and that a bill never gets back more than the payment put on it, so two refunds at once can't exceed the payment
- A payment goes on a bill (at most what it owes) or on account for the booking as an advance, and is put on a bill later; booking and bill balances, check-out and the calendar's paid / part paid now use real payments net of refunds
- Cash needs an open drawer; a cash refund can't take more than the drawer holds. Closing counts the cash against what it should hold; a short or over count needs a reason and waits for cash_drawer.approve_variance
- Void (a payment entered by mistake) with a reason, refused once anything is refunded or its cash shift is closed; Idempotency-Key on taking and refunding money
- Screens: Payments (/payments: totals by method, filters, search) and one payment (/payments/:id: Refund, Void, Put on a bill); Take payment and the payments list on the bill; the cash drawer panel on the front desk and on Payments
- Dev seed S009: yesterday's closed shift with a ₹500 cash payment, and a ₹2,000 UPI advance on R00002
- [done] Docs: API.md, DATABASE.md, schema-sprint-10.md, system design Sprint 10 and 4.20 with the payment flow diagram, RUN_AND_TEST, REVIEWING

**Files added / changed**

- database/migrations/V012__payments.sql; database/seed/dev/S009__payments.sql; database/queries/check-sprint-10.sql
- backend/Resort.Domain/Payments/Payments.cs; Organization.cs (Property.StartOfDay)
- backend/Resort.Contracts/PaymentContracts.cs, FolioContracts.cs
- backend/Resort.Application/ — Payments/PaymentService.cs, Payments/CashDrawerService.cs, Payments/PaymentLedger.cs, Validation/PaymentValidators.cs, Folios/FolioPosting.cs, Folios/FolioService.cs, Reservations/CalendarService.cs, Common/Permissions.cs, Common/Abstractions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/Endpoints/PaymentEndpoints.cs + Program.cs
- backend/Resort.Tests/ — Integration/PaymentTests.cs, Unit/PaymentUnitTests.cs, Integration/DevSeedTests.cs
- frontend/src/app/ — features/payments/ (payments.page.ts, payment.page.ts, payment-dialogs.ts, cash-drawer-panel.ts, payments.api.ts, payments.spec.ts), features/folios/folio.page.ts, features/front-desk/front-desk.page.ts, core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-10.spec.ts; scripts/check-api.mjs (Sprint 10 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-10.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-10-screens/; tools/handbooks/diagrams (payment-flow)

**Test results**

- Backend: 316 passed, 0 failed on real PostgreSQL 16 (38 new: PaymentTests with the demo flow, two refunds at once that can't exceed the payment, DELETE and UPDATE refused by the database, idempotent take and refund, field errors, cash needing an open drawer, an advance put on a bill, a refund when a guest leaves early, void rules, the list and permissions; PaymentUnitTests with the status machine, refundable amounts, variance and the card-number check; the payments dev seed check)
- Frontend: 72 of 72 unit tests passed (5 new for the payment helpers); ESLint clean; production build OK
- Browser tests: 90 of 90 passed (Sprints 01–09 plus the 7 new Sprint 10 steps: open the drawer, cash + UPI with a card number refused, a ₹200 refund the receptionist can't make, close ₹50 short with a reason, the manager approves and the totals by method, API refusals, phone); API checks 131 of 131 (17 new); database checks clean; screenshots in docs/sprints/sprint-10-screens

**Outcome**

Expected when accepted:

- End of shift: drawer closes with variance approval flow

## Scope

**Tables**

- `payments`
- `payment_allocations`
- `refunds`
- `cash_drawers`
- `cash_drawer_sessions`
- `cash_drawer_movements`

**API endpoints**

- `GET|POST /api/v1/properties/{propertyId}/payments  (Idempotency-Key)`
- `GET /api/v1/properties/{propertyId}/payments/{id}`
- `POST /api/v1/properties/{propertyId}/payments/{id}/allocate`
- `POST /api/v1/properties/{propertyId}/payments/{id}/refund  (reason required)`
- `POST /api/v1/properties/{propertyId}/payments/{id}/void  (reason required)`
- `GET /api/v1/properties/{propertyId}/cash-drawer`
- `POST /api/v1/properties/{propertyId}/cash-drawer/open | close`
- `POST /api/v1/properties/{propertyId}/cash-drawer/sessions/{id}/approve-variance`

**Screens / routes**

- `/payments`
- `/payments/:id`
- `cash drawer panel on front desk`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] No card data stored, ever (brief §3)
- [x] Payment status machine PENDING→AUTHORIZED→CAPTURED, FAILED, REFUNDED, PARTIALLY_REFUNDED, CANCELLED
- [x] Refund ≤ captured − already refunded, enforced in a transaction with row lock
- [x] Drawer close computes expected vs actual; variance needs cash_drawer.approve_variance

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S10-<issue-number>-<short-name>`
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

- A balance looks wrong: GET /payments?reservationId=… lists every payment of the stay with what is on account; on-account money counts for the booking but not for one bill until it is put on it.
- A cash payment or refund is refused with "cash drawer isn't open": open a shift on the front desk (Cash drawer → Open drawer); a cash refund is also capped at what the drawer should hold now.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Concurrency — two refunds on the same payment cannot exceed the captured amount
- Integration — DELETE payments rejected by DB

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- End of shift: drawer closes with variance approval flow

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Take cash + UPI → partial refund → close drawer with ₹50 short

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-10` created on `main`
