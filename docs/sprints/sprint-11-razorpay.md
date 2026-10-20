# Sprint 11 — Razorpay

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 3 — Folio, payments, Razorpay, GST, invoices | M | [Sprint 10](sprint-10-payments-refunds-cash-drawer.md) |

**Goal:** Online payments where the server-verified webhook is authoritative (brief §22).

**GitHub:** milestone *Sprint 11 — Razorpay* · branch prefix `feature/S11-`

## Where this sprint stands

**Now:** In progress — see the open issues in this sprint's milestone.

**Done so far**

_Nothing yet._

**Files this sprint will add / change** (expected)

- database/migrations/V###__payments.sql — new tables (next free number)
- backend/Resort.Domain/Payments/ — entities
- backend/Resort.Contracts/Contracts.cs — request/response records
- backend/Resort.Application/Payments/ — services; Validation/Validators.cs; Common/Permissions.cs if new codes; DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs — DbSets and mappings
- backend/Resort.Infrastructure/Payments/ — external API adapter + fake server for tests
- backend/Resort.Api/Endpoints/PaymentsEndpoints.cs + Program.cs registration
- backend/Resort.Tests/Integration/PaymentsTests.cs (+ Unit/ for pure calculations)
- frontend/src/app/features/payments/ — screens; app.routes.ts; layout/shell.ts menu entry
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-11.md, this guide, CHANGELOG.md

**Test results**

_Not run yet — see How to test below._

**Outcome**

Expected when accepted:

- Test-mode payment in Razorpay dashboard confirms the reservation via webhook

## Scope

**Tables**

- `payment_gateway_orders`
- `webhook_events`

**API endpoints**

- `POST /api/v1/properties/{propertyId}/payments/razorpay/orders`
- `POST /api/v1/webhooks/razorpay  (no auth; HMAC signature)`

**External APIs used**

- Razorpay Orders API
- Razorpay Payments API
- Razorpay webhooks

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] IPaymentGateway + RazorpayPaymentGateway (HttpClient, retries with jitter, timeouts)
- [ ] Webhook: verify X-Razorpay-Signature over the raw body; dedupe on event id (webhook_events unique); process in a transaction
- [ ] Browser callback only shows status; it never marks a payment captured
- [ ] Fake Razorpay server for tests (tests/fakes)

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S11-<issue-number>-<short-name>`
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

- Use ngrok/cloudflared to expose /api/v1/webhooks/razorpay to Razorpay test mode. Never point test webhooks at production.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Payment webhook — duplicate delivery processed once
- Payment webhook — bad signature → 401, nothing stored
- Payment webhook — out-of-order events (captured before authorized)

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Test-mode payment in Razorpay dashboard confirms the reservation via webhook

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Pay with a Razorpay test card → webhook → reservation confirmed

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-11` created on `main`
