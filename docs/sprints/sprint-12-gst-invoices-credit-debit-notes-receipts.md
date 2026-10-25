# Sprint 12 — GST invoices, credit/debit notes, receipts

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🟢 Done | Phase 3 — Folio, payments, Razorpay, GST, invoices | L | [Sprint 09](sprint-09-gst-configuration-and-tax-engine.md), [Sprint 10](sprint-10-payments-refunds-cash-drawer.md) |

**Goal:** Legally-numbered GST invoices from folios; cancellations only via credit notes (brief §24, §25).

**GitHub:** milestone *Sprint 12 — GST invoices, credit/debit notes, receipts* · branch prefix `feature/S12-`

## Where this sprint stands

**Now:** Accepted by the owner on 25 Oct 2026 and released to main (tag sprint-12).

**Done so far**

- Migration V014: invoice_series (one gap-free counter per property, document type and financial year), invoices, invoice_items, invoice_item_taxes, credit_notes (CANCEL or REDUCE), debit_notes, their lines and taxes, receipts (one per payment), einvoice_records (for Sprint 25); every document refuses UPDATE and DELETE and its totals are checked against its lines at commit; folio_items gets CREDIT lines
- Numbers BEACH/2627/00001 (invoice), BEACH/C2627/0001 (credit note), BEACH/D2627/0001 (debit note), BEACH/R2627/00001 (receipt), ≤ 16 characters; taken with one upsert inside the issuing transaction, so 20 invoices at once are numbered 1–20 with no gap or repeat
- Check-out issues the invoice of each folio (to the bill-to guest on file; company and GSTIN make it B2B) and a receipt for each payment; a folio that can't be invoiced doesn't stop the check-out and a booking note says why
- Taxes copied from the folio lines, never recalculated; place of supply = the property's state (CGST + SGST for every hotel stay); GSTIN validated with its check digit; composition scheme → bill of supply
- Credit notes: CANCEL (whole invoice; its charges can be invoiced again, e.g. B2B) and REDUCE (lines or part of them, GST at each line's rate, a CREDIT line on the bill); debit notes for charges posted after the invoice; billed charges can't be reversed or moved
- A4 PDFs for invoices, notes and receipts drawn on the server with PDFsharp/MigraDoc 6.2 (MIT; pinned below the AGPL 6.5) and an embedded font with ₹
- Screens: Invoices list and invoice page (credit and debit note dialogs, PDFs); Issue invoice on the bill with a preview; billed charges marked on the bill; invoice links on the booking; Receipt on a payment
- No new dev seed: invoices take real numbers, so the demo issues them by checking out R00007 (departing today)
- [done] Docs: API.md, DATABASE.md, schema-sprint-12.md, GST.md, system design Sprint 12 and 4.22 with the invoice flow diagram, RUN_AND_TEST, REVIEWING
- After the owner's first look (24 Oct 2026): the issue-invoice, credit note and invoice screens tidied (dialog widths, totals block beside the amount in words), and a Print button next to PDF on the invoice, its notes and receipts

**Files added / changed**

- database/migrations/V014__invoicing.sql; database/queries/check-sprint-12.sql
- backend/Resort.Domain/Invoicing/Invoicing.cs; backend/Resort.Domain/Folios/Folios.cs
- backend/Resort.Contracts/InvoiceContracts.cs; backend/Resort.Contracts/FolioContracts.cs
- backend/Resort.Application/ — Invoicing/ (InvoiceService.cs, FolioBilling.cs, ReceiptService.cs, InvoicePrinter.cs, Printing.cs), Validation/InvoiceValidators.cs, Folios/FolioPosting.cs, Folios/FolioService.cs, FrontDesk/FrontDeskService.cs, Common/Permissions.cs, Common/Abstractions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/ — Documents/PdfRenderer.cs, Documents/Fonts/ (DejaVu Sans + licence), Persistence/AppDbContext.cs, DependencyInjection.cs, Resort.Infrastructure.csproj; Directory.Packages.props; backend/Resort.Api/ — Endpoints/InvoiceEndpoints.cs, Program.cs
- backend/Resort.Tests/ — Integration/InvoiceTests.cs, Unit/InvoiceUnitTests.cs
- frontend/src/app/ — features/invoices/ (invoices.page.ts, invoice.page.ts, invoice-dialogs.ts, invoices.api.ts, invoices.spec.ts), features/folios/ (folio.page.ts, folios.api.ts, folios.spec.ts), features/payments/payment.page.ts, features/reservations/reservation.page.ts, features/front-desk/front-desk-dialogs.ts, core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-12.spec.ts; scripts/check-api.mjs (Sprint 12 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-12.md, docs/GST.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-12-screens/; tools/handbooks/diagrams (invoice-flow)

**Test results**

- Backend: 381 passed, 0 failed on real PostgreSQL 16 (41 new: InvoiceTests with the demo (check-out issues the invoice, receipt and PDFs; cancel and re-issue B2B), 20 parallel invoices numbered 1–20 with no gap or repeat, B2B intra- and inter-state, the database refusing UPDATE and DELETE of totals, REDUCE and debit notes, a wrong GSTIN at check-out, receipts and idempotency, permissions; InvoiceUnitTests with the financial year, prefixes within 16 characters, amounts in words, part credits and what is printed)
- Frontend: 88 of 88 unit tests passed (7 new: invoice helpers, PDF names, the credit tax estimate, invoiced charges off the reverse/move list); ESLint clean; production build OK
- Browser tests: 101 of 101 passed (Sprints 01–11 plus the 5 new Sprint 12 steps: check-out issues invoice and receipt, a debit note, a part credit note with too much refused under the amount, cancel and re-issue B2B with a GSTIN typo caught, the Invoices screen and a receipt download); API checks 159 of 159 (13 new); database checks clean (no gaps, totals add up, no IGST, the database refuses edits); screenshots in docs/sprints/sprint-12-screens

**Outcome**

Expected when accepted:

- Checkout produces a GST invoice and receipt PDF

## Scope

**Tables**

- `invoice_series`
- `invoices`
- `invoice_items`
- `invoice_item_taxes`
- `credit_notes`
- `credit_note_items`
- `credit_note_item_taxes`
- `debit_notes`
- `debit_note_items`
- `debit_note_item_taxes`
- `receipts`
- `einvoice_records`

**API endpoints**

- `GET /api/v1/properties/{propertyId}/folios/{id}/invoice-preview`
- `POST /api/v1/properties/{propertyId}/folios/{id}/invoice`
- `GET /api/v1/properties/{propertyId}/invoices`
- `GET /api/v1/properties/{propertyId}/invoices/{id}  (+ /pdf)`
- `POST /api/v1/properties/{propertyId}/invoices/{id}/credit-notes  (reason required)`
- `POST /api/v1/properties/{propertyId}/invoices/{id}/debit-notes  (reason required)`
- `GET /api/v1/properties/{propertyId}/credit-notes/{id}/pdf; GET …/debit-notes/{id}/pdf`
- `POST /api/v1/properties/{propertyId}/payments/{id}/receipt`
- `GET /api/v1/properties/{propertyId}/receipts/{id}/pdf`

**Screens / routes**

- `/invoices`
- `/invoices/:id`
- `Issue invoice on /folios/:id`
- `Receipt on /payments/:id`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] Gap-free numbering per series and financial year (row lock on invoice_series)
- [x] B2B (customer GSTIN validated) and B2C; place of supply
- [x] Invoice copies tax rule ids/rates from folio lines — never recalculates
- [x] PDF generation (PDFsharp/MigraDoc, server-side)
- [x] e-invoice fields stored (IRN, ack no/date, signed QR) for Sprint 25 integration

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S12-<issue-number>-<short-name>`
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

- A number is taken only after every check passes; if an invoice is refused, the response says why and no number is used. database/queries/check-sprint-12.sql block 4 lists any series with a gap (there should be none).
- PDFsharp must stay on 6.2.x: 6.5 is AGPL and throws a licence error at runtime.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Concurrency — 20 parallel invoices → numbers 1..20 with no gaps or duplicates
- Invoice — B2B intra-state and inter-state totals
- Integration — invoice DELETE/UPDATE of totals rejected

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Checkout produces a GST invoice and receipt PDF

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Check out → invoice PDF → credit note

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-12` created on `main`
