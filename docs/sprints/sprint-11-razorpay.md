# Sprint 11 — Razorpay

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🟢 Done | Phase 3 — Folio, payments, Razorpay, GST, invoices | M | [Sprint 10](sprint-10-payments-refunds-cash-drawer.md) |

**Goal:** Online payments where the server-verified webhook is authoritative (brief §22).

**GitHub:** milestone *Sprint 11 — Razorpay* · branch prefix `feature/S11-`

## Where this sprint stands

**Now:** Accepted by the owner on 23 Oct 2026 and released to main (tag sprint-11).

**Done so far**

- Migration V013: payment_gateway_orders (a payment link: Razorpay order, amount, the guest's private token, CREATED / PAID / CANCELLED, declined tries) and webhook_events (every verified delivery, unique on Razorpay's event id, with what it did); payments get method ONLINE (unique on Razorpay's payment id) and refunds ONLINE with Razorpay's refund id; nothing can be deleted
- IPaymentGateway + RazorpayPaymentGateway over HttpClient: a 10 s timeout per try, up to 3 tries with exponential backoff and jitter when safe (a refund is never retried on an unclear failure); a built-in fake Razorpay (same HTTP calls, signed webhooks) for tests and for development without keys, never in production
- Webhook: HMAC-SHA256 over the raw body (wrong → 401, nothing stored); the event id is claimed once inside the transaction (duplicates, even at the same moment, change nothing); the payment is fetched from Razorpay and only moves forward, so captured-before-authorised is handled; our errors are stored FAILED and retried by Razorpay
- One transaction: the payment is recorded, the link marked paid, the money put on the main bill (the rest on account) and a pending booking confirmed
- Browser callback only checks the signature and shows "confirming"; it never records a payment. Online payments can't be voided; refunds go back through Razorpay and wait for refund.processed (refund.failed puts the money back)
- Screens: Online payment links on a booking (send with the amount checked against what is owed, copy, WhatsApp, cancel with a reason, History of what Razorpay sent); the guest's page /pay/:token without sign-in (Razorpay Checkout, or test buttons with the fake); online details and Razorpay refunds on a payment
- No new dev seed: a link needs a real order in Razorpay; the seeded pending booking R00003 is the demo booking
- [done] Docs: RAZORPAY.md, API.md, DATABASE.md, schema-sprint-11.md, system design Sprint 11 and 4.21 with the Razorpay flow diagram, RUN_AND_TEST, REVIEWING

**Files added / changed**

- database/migrations/V013__razorpay.sql; database/queries/check-sprint-11.sql
- backend/Resort.Domain/Payments/Payments.cs
- backend/Resort.Contracts/PaymentContracts.cs
- backend/Resort.Application/ — Payments/PaymentGateway.cs, Payments/PaymentLinkService.cs, Payments/RazorpayWebhookService.cs, Payments/PaymentService.cs, Validation/PaymentValidators.cs, Reservations/ReservationService.cs, Common/SystemActor.cs, Common/Errors.cs, Common/Abstractions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/ — Payments/RazorpayPaymentGateway.cs, Payments/FakeRazorpayServer.cs, Persistence/AppDbContext.cs, DependencyInjection.cs; backend/Resort.Api/ — Endpoints/RazorpayEndpoints.cs, Infrastructure/CurrentUser.cs, Program.cs
- backend/Resort.Tests/ — Integration/RazorpayTests.cs, Unit/RazorpayUnitTests.cs, Support/RazorpayFactory.cs
- frontend/src/app/ — features/pay/pay.page.ts, features/payments/ (payment-links-panel.ts, payments.api.ts, payment-dialogs.ts, payment.page.ts, payments.page.ts, payment-links.spec.ts), features/reservations/reservation.page.ts, core/models.ts, app.routes.ts
- frontend/e2e/sprint-11.spec.ts; scripts/check-api.mjs (Sprint 11 checks); .env.example
- docs/RAZORPAY.md, docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-11.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-11-screens/; tools/handbooks/diagrams (razorpay-flow)

**Test results**

- Backend: 340 passed, 0 failed on real PostgreSQL 16 (24 new: RazorpayTests with the demo, duplicate delivery processed once (also two at the same moment), bad signature → 401 with nothing stored, captured before authorised, a declined try, a cancelled link paid anyway, the guest page and fake checkout, link limits and idempotency, permissions and tenants, retries and timeouts, online refunds processed and failed, the database refusing edits and deletes; RazorpayUnitTests with HMAC vectors, paise and the forward-only rank)
- Frontend: 81 of 81 unit tests passed (9 new: Razorpay helpers, the send-link dialog's limits and card-number check, the guest page's test buttons, polling while confirming and an invalid link); ESLint clean; production build OK
- Browser tests: 96 of 96 passed (Sprints 01–10 plus the 6 new Sprint 11 steps: send a link with the amount checked, the guest page on a phone with a declined try, pay, the booking confirmed with the webhook history, a ₹500 refund through Razorpay, a bad signature and a cancelled link); API checks 147 of 147 (16 new); database checks clean; screenshots in docs/sprints/sprint-11-screens. Real Razorpay test mode is for the owner's review (needs their test keys and a tunnel)

**Outcome**

Expected when accepted:

- Test-mode payment in Razorpay dashboard confirms the reservation via webhook

## Scope

**Tables**

- `payment_gateway_orders`
- `webhook_events`

**API endpoints**

- `POST /api/v1/properties/{propertyId}/payments/razorpay/orders`
- `GET /api/v1/properties/{propertyId}/payments/razorpay/orders?reservationId | /{id}`
- `POST /api/v1/properties/{propertyId}/payments/razorpay/orders/{id}/cancel  (reason required)`
- `POST /api/v1/webhooks/razorpay  (no auth; HMAC signature)`
- `GET /api/v1/pay/{token}; POST /api/v1/pay/{token}/callback  (guest page, no auth; token)`

**Screens / routes**

- `online payment links on /reservations/:id`
- `/pay/:token (guest page`
- `no sign-in)`

**External APIs used**

- Razorpay Orders API
- Razorpay Payments API
- Razorpay webhooks

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] IPaymentGateway + RazorpayPaymentGateway (HttpClient, retries with jitter, timeouts)
- [x] Webhook: verify X-Razorpay-Signature over the raw body; dedupe on event id (webhook_events unique); process in a transaction
- [x] Browser callback only shows status; it never marks a payment captured
- [x] Fake Razorpay server for tests (tests/fakes)

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
