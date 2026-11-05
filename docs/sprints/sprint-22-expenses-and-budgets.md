# Sprint 22 — Expenses and budgets

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 8 — Expenses, budgets, reports | M | [Sprint 10](sprint-10-payments-refunds-cash-drawer.md) |

**Goal:** Expense capture with approval and budget vs actual (brief §40, §41).

**GitHub:** milestone *Sprint 22 — Expenses and budgets* · branch prefix `feature/S22-`

## Where this sprint stands

**Now:** Built and in review (pull request into develop). Defaults for the owner to confirm: every expense waits for approval; nobody approves their own unless they plan budgets (owner, general manager); a receipt is needed over ₹500; receipts are photos or PDFs, 2 MB each, at most 4; an approved, rejected or cancelled expense can't be changed (record a new one), and approved ones can't be reversed yet; the budget counts the cost (the taxable value when the GST is claimed back); budgets are per department and month, one per financial year, for last, this or next year; the forecast is the run rate; supplier bills and payroll are not in the actuals yet (Sprint 23).

**Done so far**

- Migration V024: expense_categories (GST claimable or not) and cost_centres (departments), expenses (EX00001…; GSTIN and bill recorded once; CGST/SGST or IGST split, taxable value, input credit and cost held by CHECKs; SUBMITTED → APPROVED, REJECTED or CANCELLED and frozen by a trigger afterwards), expense_attachments (append-only, only while waiting), budgets (one per financial year) and budget_lines (department × month inside the year); setting expenses.receipt_required_over = 500
- Recording on a phone with an Idempotency-Key: the GSTIN checked including its check digit, the split shown before saving, receipt photos shrunk on the phone or a PDF, sniffed and stored encrypted
- Approval audited with what counted ("EX00013 approved: ₹1,000 for Spa"); no approval of your own expense or over ₹500 without a receipt, with the reason shown in place of the button; reject or cancel with a reason
- Budgets made empty or copied with an increase; each department planned for the year or month by month
- Budget against actual per department, April to a chosen month: budget so far, spent (approved only, at cost), variance, the year at this rate, standing, what waits, spending by category, a month chart with a table view
- Screens /expenses and /budgets on a 360 px phone
- Dev seed S019: default categories and departments, this year's budget (₹2,05,000 a month), ten approved expenses including a GST bill and two waiting
- [done] Docs: API.md, DATABASE.md, schema-sprint-22.md, system design Sprint 22 and 4.32 with the expense flow diagram, ARCHITECTURE.md, RUN_AND_TEST, REVIEWING, CHANGELOG

**Files added / changed**

- database/migrations/V024__expenses.sql; database/seed/dev/S019__expenses.sql; database/queries/check-sprint-22.sql
- backend/Resort.Domain/Finance/Finance.cs; backend/Resort.Contracts/FinanceContracts.cs
- backend/Resort.Application/ — Finance/ (ExpenseService.cs, BudgetService.cs), Validation/FinanceValidators.cs, Common/Permissions.cs, Common/Abstractions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/ — Endpoints/FinanceEndpoints.cs, Infrastructure/Problems.cs, Program.cs
- backend/Resort.Tests/ — Unit/FinanceUnitTests.cs, Integration/FinanceTests.cs, Integration/DevSeedTests.cs
- frontend/src/app/ — features/expenses/ (expenses.page.ts, budgets.page.ts, expense-dialogs.ts, expenses.api.ts, expenses.spec.ts), core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-22.spec.ts; scripts/check-api.mjs (Sprint 22 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-22.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-22-screens/; tools/handbooks/diagrams (expense-flow)

**Test results**

- Backend: 582 passed, 0 failed on real PostgreSQL 16 (an unapproved expense left out of the actuals until approved, rejected and cancelled ones never counted; the GST split by the vendor's state and only the taxable value counted when the credit can be claimed; approval needs the receipt over ₹500 and someone else, is audited and freezes the expense in the database; receipts checked by their bytes, encrypted, at most four; recording is idempotent; the budget report's variance, run rate and standing; the status moves in code match the migration; dev seed check). Release build with warnings as errors and dotnet format clean
- Frontend: 126 of 126 unit tests passed (4 new); ESLint clean; production build OK
- Browser tests: Sprint 22's 5 steps pass (the owner adds a department and plans ₹12,000; a manager on a 360 px phone records ₹1,180 on a GST bill with a photo, a wrong GSTIN check digit refused under the field; the budget counts nothing while it waits; the owner approves and ₹1,000 counts; the budget reads on the phone). Full run 164 of 165: S19-05 missed its payroll warning once and passes on a rerun (Sprint 19, not touched here). API checks 267 of 267 (9 new); database checks clean; screenshots in docs/sprints/sprint-22-screens

**Outcome**

Expected when accepted:

- Owner sees budget vs actual per department

## Scope

**Tables**

- `expense_categories`
- `cost_centres`
- `expenses`
- `expense_attachments`
- `budgets`
- `budget_lines`

**API endpoints**

- `/api/v1/properties/{propertyId}/expenses/*, /budgets/*`

**Screens / routes**

- `/expenses`
- `/budgets`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] Receipt upload; GST info; approval (expense.approve) audited
- [x] Monthly/annual/department budgets; variance and simple forecast

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S22-<issue-number>-<short-name>`
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

- Integration — unapproved expense excluded from actuals

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Owner sees budget vs actual per department

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Submit expense with photo → approve → budget variance

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-22` created on `main`
