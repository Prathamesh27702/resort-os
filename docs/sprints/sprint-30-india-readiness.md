# Sprint 30 — India readiness — FSSAI and UPI on bills, printed bill and kitchen ticket, Form C

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🟡 In review — **current sprint** | Phase 10 — Hardening, backup, monitoring, deployment | M | [Sprint 12](sprint-12-gst-invoices-credit-debit-notes-receipts.md), [Sprint 14](sprint-14-menu-restaurant-pos-and-kot.md), [Sprint 24](sprint-24-notification-engine-whatsapp-email.md) |

**Goal:** Close the must-have gaps found against Indian hotel and restaurant software in the market comparison of 13 Nov 2026.

**GitHub:** milestone *Sprint 30 — India readiness — FSSAI and UPI on bills, printed bill and kitchen ticket, Form C* · branch prefix `feature/S30-`

## Where this sprint stands

**Now:** Built and waiting for the owner's review. Aadhaar masking (last 4 digits only) and the calendar date boxes, the two other must-haves of the comparison, were already in place.

**Done so far**

- Migration V027: properties.fssai_licence_no, upi_id, upi_payee_name; form_c_records (one per foreign guest and stay, sheet encrypted, never deleted); the form_c_reminder job
- UPI: the upi://pay link and its QR (made on the server, no money moves through the app) on the restaurant bill, in the restaurant Paid dialog, in the folio Take payment dialog, on the invoice PDF for the balance still owed, and as a preview in the property's settings
- FSSAI licence number on the restaurant bill and on the invoice PDF
- Printed restaurant bill (80 mm or 58 mm) with GSTIN, FSSAI number, each tax and the UPI QR; printed kitchen ticket from the order and from the kitchen display
- Form C: a sheet opened at check-in for each guest whose nationality isn't Indian, filled in from the profile, passport, visa and booking; copy buttons with dates the way the FRRO portal takes them; mark as filed with the FRRO number, not needed with a reason, reopen; the foreign guest register as CSV; a count on the front desk; a bell and email reminder every morning
- Dev seed S022: a made-up FSSAI number and UPI ID on the demo property
- Sprint plan: the remaining suggestions of the comparison written into Sprints 25 to 29 and the new Sprints 31 to 33

**Files added / changed**

- database/migrations/V027__india_readiness.sql; database/seed/dev/S022__india_readiness.sql; database/queries/check-sprint-30.sql
- backend/Resort.Domain/Compliance/ (Upi.cs, FormC.cs); backend/Resort.Contracts/ComplianceContracts.cs
- backend/Resort.Application/ — Compliance/ (UpiService, FormCService), Restaurant/OrderService.cs, Invoicing/ (InvoicePrinter, InvoiceService, Printing), Notifications/ (NotificationCatalog, NotificationJobs), FrontDesk/FrontDeskService.cs, Guests/GuestService.cs, Properties/PropertyService.cs
- backend/Resort.Infrastructure/ — Compliance/QrCodes.cs, Documents/PdfRenderer.cs; backend/Resort.Api/Endpoints/ComplianceEndpoints.cs
- backend/Resort.Tests/ — Integration/IndiaReadinessTests.cs, Unit/IndiaUnitTests.cs
- frontend/src/app/features/india/ (india.api.ts, upi-qr.ts, receipt.ts, receipt-dialog.ts, form-c.page.ts, form-c-sheet.page.ts, india.spec.ts); restaurant/order.page.ts, order-dialogs.ts, kitchen.page.ts; payments/payment-dialogs.ts; settings/properties/property-edit.page.ts; front-desk/front-desk.page.ts; layout/nav.ts; app.routes.ts
- frontend/e2e/sprint-30.spec.ts; scripts/check-api.mjs (Sprint 30 checks)
- docs/INDIA.md, docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-30.md, docs/architecture/system-design.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-30-screens/

**Test results**

- Backend: 664 passed, 0 failed on real PostgreSQL 16 (48 new: FSSAI and UPI checked under their fields; the QR link with the exact amount; the bill until it is paid; a foreign guest's check-in opens one Form C, refused with gaps naming them, filed, reopened, not needed; the register without a full passport number; the reminder run twice sends once; anonymising empties the sheet)
- Frontend: 145 of 145 unit tests passed (10 new); ESLint clean; production build OK
- Browser tests: Sprint 30's 7 steps pass (settings with a wrong value explained; the bill on 80 and 58 mm; the kitchen ticket and Paid by UPI; Form C on the front desk; the sheet filled, gaps named, filed; the Filed list and the register; a 360 px phone)

**Outcome**

Expected when accepted:

- A restaurant bill prints with the FSSAI number and a UPI QR that a UPI app reads with the right amount
- A foreign guest checked in appears under Form C with 24 hours to file, and is marked as filed with the FRRO number

## Scope

**Tables**

- `form_c_records`

**API endpoints**

- `/api/v1/properties/{propertyId}/upi-qr`
- `/api/v1/properties/{propertyId}/orders/{id}/bill`
- `/api/v1/properties/{propertyId}/form-c/*`

**Screens / routes**

- `/form-c and /form-c/{id}`
- `/restaurant/orders/{id} (Print bill, print ticket, UPI QR when paying)`
- `/settings/properties/{id} (Food licence and UPI)`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] FSSAI licence number and UPI ID per property, with their checks explained under the field
- [x] UPI QR for an amount: restaurant bill, Paid dialog, Take payment dialog, invoice PDF
- [x] Thermal bill (80 mm and 58 mm) and kitchen ticket printing from the browser
- [x] Form C sheet per foreign guest and stay, 24-hour deadline, reminder job, register
- [x] The comparison's later suggestions written into the plan

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S30-<issue-number>-<short-name>`
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

- Integration — a foreign guest's check-in opens one Form C; filing with gaps is refused naming the gaps
- Integration — the reminder job run twice sends once
- Unit — the UPI link carries the ID, the name and the exact amount

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- A restaurant bill prints with the FSSAI number and a UPI QR that a UPI app reads with the right amount
- A foreign guest checked in appears under Form C with 24 hours to file, and is marked as filed with the FRRO number

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Print a restaurant bill → scan the QR with a UPI app
- Check in a foreign guest → fill the Form C sheet → mark as filed → download the register

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-30` created on `main`
