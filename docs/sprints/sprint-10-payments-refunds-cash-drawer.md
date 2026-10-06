# Sprint 10 — Payments, refunds, cash drawer

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 3 — Folio, payments, Razorpay, GST, invoices | M | [Sprint 08](sprint-08-folio.md) |

**Goal:** Record cash/UPI/card/bank payments, allocate them to folios, refund safely, reconcile the cash drawer (brief §21, §39).

**GitHub:** milestone *Sprint 10 — Payments, refunds, cash drawer* · branch prefix `feature/S10-`

## Scope

**Tables**

- `payments`
- `payment_allocations`
- `refunds`
- `cash_drawers`
- `cash_drawer_sessions`
- `cash_drawer_movements`

**API endpoints**

- `POST /api/v1/properties/{propertyId}/payments  (Idempotency-Key)`
- `POST /api/v1/properties/{propertyId}/payments/{id}/allocate`
- `POST /api/v1/properties/{propertyId}/payments/{id}/refund  (reason required)`
- `POST /api/v1/properties/{propertyId}/payments/{id}/void  (reason required)`
- `POST /api/v1/properties/{propertyId}/cash-drawer/open | close`
- `POST /api/v1/properties/{propertyId}/cash-drawer/sessions/{id}/approve-variance`

**Screens / routes**

- `/payments`
- `cash drawer panel on front desk`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] No card data stored, ever (brief §3)
- [ ] Payment status machine PENDING→AUTHORIZED→CAPTURED, FAILED, REFUNDED, PARTIALLY_REFUNDED, CANCELLED
- [ ] Refund ≤ captured − already refunded, enforced in a transaction with row lock
- [ ] Drawer close computes expected vs actual; variance needs cash_drawer.approve_variance

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S10-<issue-number>-<short-name>`
3. **Database up:** `docker compose --env-file .env -f infra/docker/docker-compose.yml up -d db`
4. **Schema changes:** add the next `database/migrations/V###__<name>.sql` (never edit an applied one). The API applies it on start.
5. **API:** `cd backend/Resort.Api && dotnet watch run` → http://localhost:5180/swagger
6. **Web:** `cd frontend && npm start` → http://localhost:4300
7. **Fresh data:** `scripts/db-reset.sh`, then restart the API (migrations + bootstrap owner run again; dev seed data from Sprint 2 onward)

## How to debug

- **API:** run the `Resort.Api` launch profile in VS Code (C# Dev Kit), Rider or Visual Studio and set breakpoints; Swagger's *Authorize* button takes the `accessToken` from `POST /api/v1/auth/login`.
- **Every error response has a `correlationId`.** Search the API console/log for it to find the full exception.
- **SQL:** set `Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore.Database.Command=Information` in `.env` to log every SQL statement.
- **Database:** `docker compose --env-file .env -f infra/docker/docker-compose.yml exec db psql -U $POSTGRES_USER $POSTGRES_DB`
- **Web:** Angular DevTools + the browser Network tab; API errors are `{code, message, details, correlationId}`.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Concurrency — two refunds on the same payment cannot exceed the captured amount
- Integration — DELETE payments rejected by DB

Commands:

```bash
scripts/test.sh                                   # everything (backend + frontend)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

## Acceptance criteria

- {'End of shift': 'drawer closes with variance approval flow'}

## Sprint review demo

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
