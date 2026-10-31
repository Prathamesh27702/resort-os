# Sprint 19 — Leave, holidays and payroll export

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 6 — Employees, attendance, timesheets, leave, roster | M | [Sprint 18](sprint-18-attendance-and-timesheets-pwa.md) |

**Goal:** Leave requests with balance/overlap/coverage checks feeding roster and payroll (brief §45).

**GitHub:** milestone *Sprint 19 — Leave, holidays and payroll export* · branch prefix `feature/S19-`

## Where this sprint stands

**Now:** Built; pull request into develop waits for the owner's review.

**Done so far**

- Migration V021: leave_types and leave_policies (days a year, earned monthly or yearly, a maximum; for everyone or one employment type), leave_requests (LV00001…, counted days stored, an EXCLUDE constraint refusing overlapping waiting or approved leave), leave_ledger (append-only) and leave_balances (kept equal to the ledger by a trigger, never below zero), holidays, payroll_exports (append-only with SHA-256); permission payroll.export for Owner and HR
- Asking for leave on a phone or for someone: half days at either end, holidays and the weekly day off not counted, a live preview with the balance after and the roster warnings (the shift that comes off, a shift left short, a colleague also off); refusals say why under the field (balance short counting what is waiting, dates the wrong way round, overlap, before joining, more than 30 days back)
- Approving locks the balance, posts TAKEN and cancels shifts on full days of leave; refusing needs a reason; cancelling approved leave before it starts gives the days back; nobody decides their own leave
- Balances and each person's ledger with the running balance; HR adjustments and opening balances with a reason; the monthly accrual on the 1st (background job or by hand; twice adds nothing; capped)
- Holidays by year; kinds of leave with their policies; defaults CL 12, SL 6, EL 15, LOP made on first use
- Payroll export: the month's rows (days employed, holidays, days and hours worked, overtime, paid and unpaid leave, days not in, payable days) with what isn't ready; the file needs a confirmation if anything is listed, locks the approved timesheets it used and is kept to download again; format GENERIC_CSV_V1 documented in docs/PAYROLL.md
- The roster shows leave and refuses a shift on a day of leave (409 ON_LEAVE); copy week skips those days; the dashboard shows Leave waiting for you
- Dev seed S016: leave types and policies, national holidays and Goa Liberation Day for this year and next, opening balances and this month's accrual, two requests waiting, one approved, one refused
- [done] Docs: PAYROLL.md, API.md, DATABASE.md, schema-sprint-19.md, system design Sprint 19 and 4.29 with the leave flow diagram, ARCHITECTURE.md, permission matrix, INTEGRATIONS.md, Q3 note, RUN_AND_TEST, REVIEWING, CHANGELOG

**Files added / changed**

- database/migrations/V021__leave.sql; database/seed/dev/S016__leave.sql; database/queries/check-sprint-19.sql
- backend/Resort.Domain/Leave/Leave.cs (LeaveMath, PayrollCsv); backend/Resort.Contracts/LeaveContracts.cs, EmployeeContracts.cs
- backend/Resort.Application/ — Leave/ (LeaveService.cs, LeaveRequestService.cs, PayrollService.cs), Employees/RosterService.cs, Validation/LeaveValidators.cs, Common/Permissions.cs, Common/Abstractions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/ — Endpoints/LeaveEndpoints.cs, Infrastructure/LeaveAccrualJob.cs, Infrastructure/Problems.cs, Program.cs
- backend/Resort.Tests/ — Unit/LeaveUnitTests.cs, Integration/LeaveTests.cs, Integration/DevSeedTests.cs
- frontend/src/app/ — features/leave/ (leave.page.ts, leave-dialogs.ts, payroll.page.ts, leave.api.ts, leave.spec.ts), features/employees/roster.page.ts, features/dashboard/dashboard.page.ts, core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-19.spec.ts; scripts/check-api.mjs (Sprint 19 checks)
- docs/PAYROLL.md, docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-19.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/security/permission-matrix.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-19-screens/; tools/handbooks/diagrams (leave-flow)

**Test results**

- Backend: 525 passed, 0 failed on real PostgreSQL 16 (15 new: counting days with half days, a holiday and a day off; monthly and pro-rated yearly accrual; the CSV with its escaping; the brief's leave refused while the balance is short, then approved with the full day taken off the roster and the half day kept; half days and holidays inside the range and impossible ones refused under the field; overlap, own leave, refusing and cancelling; the accrual run twice adding nothing and stopping at the maximum; two requests for the same days at the same moment making one, 5 times; four approvals at the same moment against a balance for two approving exactly two, 5 times; the payroll file waiting for unready weeks unless confirmed and locking the approved ones; dev seed check)
- Frontend: 116 of 116 unit tests passed (3 new); ESLint clean; production build OK
- Browser tests: all 151 steps of Sprints 01–19 passed in one run (25.6 min); Sprint 19's 5 steps cover a housekeeper on a 360 px phone asking for 1.5 days over a holiday (a wrong date refused under the field), her manager approving it from the dashboard with her shift taken off the roster, her balance going down, HR adjusting it with a reason and adding a holiday (a duplicate refused), and this month's payroll file made after confirming what isn't ready; API checks 242 of 242 (11 new); database checks clean; screenshots in docs/sprints/sprint-19-screens

**Outcome**

Expected when accepted:

- Brief §74 passes end to end

## Scope

**Tables**

- `leave_types`
- `leave_policies`
- `leave_balances`
- `leave_ledger`
- `leave_requests`
- `holidays`
- `payroll_exports`

**API endpoints**

- `/api/v1/properties/{propertyId}/leave/*, /payroll/*`

**Screens / routes**

- `/leave`
- `/payroll`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] Balance = ledger sum; accrual job
- [x] Approval updates roster and creates ledger entries; audited
- [x] Payroll export CSV in a documented generic format (GENERIC_CSV_V1; the provider, Q3, is still open)

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S19-<issue-number>-<short-name>`
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

- A balance looks wrong: open Leave → Balances → the person for their history with the running balance. check-sprint-19.sql block 5 lists any balance that differs from its ledger (should be none); block 13 lists adjustments with their reasons.
- Leave refused for overlap: the message names the other request; cancel it or choose other dates. Block 7 lists overlapping waiting or approved requests (should be none).
- The accrual added nothing: it posts once per person, kind and month (block 12), only for people employed on the 1st, and stops at the policy's maximum (the result says so per person).
- Payroll file missing someone's hours: only approved or locked timesheets count; the preview lists weeks not approved or with no timesheet. Block 16 lists approved timesheets a file should have locked.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Leave — half days, holidays inside range, insufficient balance
- E2E — brief §74 second half

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Brief §74 passes end to end

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Request → notify manager → approve → balance & roster updated

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-19` created on `main`
