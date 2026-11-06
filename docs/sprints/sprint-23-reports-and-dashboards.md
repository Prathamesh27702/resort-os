# Sprint 23 — Reports and dashboards

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 8 — Expenses, budgets, reports | L | [Sprint 12](sprint-12-gst-invoices-credit-debit-notes-receipts.md), [Sprint 15](sprint-15-inventory-and-recipe-costing.md), [Sprint 19](sprint-19-leave-holidays-and-payroll-export.md), [Sprint 22](sprint-22-expenses-and-budgets.md) |

**Goal:** Owner and role dashboards plus the report list in brief §50–55, Power-BI-ready.

**GitHub:** milestone *Sprint 23 — Reports and dashboards* · branch prefix `feature/S23-`

## Where this sprint stands

**Now:** Built, in review (PR to develop). Defaults to confirm: the numbers are added up every 15 minutes (and nightly from April last year), not live; a dorm bed counts as one unit like a room; money is before GST; revenue is counted by the day of the service; commission is worked out at 18 % for an OTA and 10 % for a travel agent unless set on Reports (until OTA statements, Sprint 24 on); GST is an estimate for the accountant; pay is an estimate (monthly salary spread over the days, daily rate per day worked, no PF/ESI) shown only to the owner and HR; the budget now counts supplier bills (by the bill date) and pay; the P&L counts stock when used, not when bought; Power BI reads five views through a read-only login (scripts/powerbi-user.sql).

**Done so far**

- Migration V025: schema reporting with daily_stats, daily_channel_stats, daily_costs and refresh_state (one row per property and day), the functions refresh (one transaction, a lock per property, at most 900 days), labour_cost and commission_percent, the views v_daily, v_monthly, v_channels_monthly, v_costs_monthly, v_gst_monthly, the read-only role resort_reporting; date indexes; departments.cost_centre_id and supplier_invoices.cost_centre_id
- ReportRefreshJob: every 15 minutes the 1st of last month to 90 days ahead, nightly from 1 April of last financial year; Refresh now
- Reports from the summary only (brief §88): role-aware dashboard on Today, occupancy and revenue by day or month, booking sources with commission and net, GST by month, food and labour cost, P&L, the month at a glance (printable); CSV of each
- Budget actuals with supplier bills and pay (owner and HR); budget departments on supplier bills and employee departments
- Commission rates per booking source, audited
- Screens /reports and the dashboard sections on Today, on a 360 px phone
- Dev seed S020: a year of past stays (about 1,600) for the dashboards
- Power BI: scripts/powerbi-user.sql and docs/REPORTING.md
- [done] Docs: API.md, DATABASE.md, schema-sprint-23.md, REPORTING.md, system design Sprint 23 and 4.33 with the reporting diagram, ARCHITECTURE.md, RUN_AND_TEST, REVIEWING, CHANGELOG

**Files added / changed**

- database/migrations/V025__reporting.sql; database/seed/dev/S020__report_history.sql; database/queries/check-sprint-23.sql; scripts/powerbi-user.sql
- backend/Resort.Domain/Reports/Reports.cs; backend/Resort.Contracts/ReportContracts.cs
- backend/Resort.Application/ — Reports/ReportService.cs, Validation/ReportValidators.cs, Finance/BudgetService.cs, Finance/ExpenseService.cs, Purchasing/SupplierInvoiceService.cs, Employees/EmployeeService.cs, Common/Permissions.cs, DependencyInjection.cs
- backend/Resort.Api/ — Endpoints/ReportEndpoints.cs, Infrastructure/ReportRefreshJob.cs, Program.cs
- backend/Resort.Tests/ — Unit/ReportUnitTests.cs, Integration/ReportTests.cs, Integration/DevSeedTests.cs
- frontend/src/app/ — features/reports/ (reports.page.ts, report-bits.ts, reports.api.ts, reports.spec.ts), features/dashboard/owner-board.ts, features/expenses/budgets.page.ts, purchasing and employee dialogs, core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-23.spec.ts; scripts/check-api.mjs (Sprint 23 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-23.md, docs/REPORTING.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-23-screens/; tools/handbooks/diagrams (report-flow)

**Test results**

- RESULTS_BACKEND
- RESULTS_FRONTEND
- RESULTS_BROWSER

**Outcome**

Expected when accepted:

- Owner dashboard loads < 1 s on a year of seed data

## Scope

**Tables**

- `reporting.daily_stats`
- `reporting.daily_channel_stats`
- `reporting.daily_costs`
- `reporting.refresh_state`

**API endpoints**

- `/api/v1/properties/{propertyId}/reports/*`

**Screens / routes**

- `/dashboard (role-aware)`
- `/reports`

**Other**

- reporting schema with views/materialised views
- refreshed by jobs

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] reporting.* views: occupancy, ADR, RevPAR, revenue splits, OTA net, food/labour cost, GST, P&L
- [x] No dashboard query scans raw transaction tables unbounded (brief §88)
- [x] Read-only DB role for Power BI

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S23-<issue-number>-<short-name>`
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

- Database — ADR/RevPAR against a hand-calculated fixture month

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Owner dashboard loads < 1 s on a year of seed data

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Owner dashboard + monthly report

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-23` created on `main`
