# Sprint 17 — Employees and roster

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🟢 Done | Phase 6 — Employees, attendance, timesheets, leave, roster | M | [Sprint 01](sprint-01-foundation.md) |

**Goal:** Employee records (salary restricted) and shift rosters with clash detection (brief §42, §46).

**GitHub:** milestone *Sprint 17 — Employees and roster* · branch prefix `feature/S17-`

## Where this sprint stands

**Now:** Accepted by the owner on 29 Oct 2026 and released to main (tag sprint-17).

**Done so far**

- Migration V019: departments and designations, employees (E00001…, optional staff account, one employee per account, ACTIVE / LEFT), employee_documents (numbers encrypted, Aadhaar last 4 only, scans encrypted), employee_compensation (append-only), shift_templates (people needed every day) and roster_shifts with the EXCLUDE USING gist constraint ex_roster_shifts_clash on a generated tstzrange
- Pay behind its own endpoint and employee_salary.read (owner, HR; never a manager or general manager); the audit trail shows amounts as ***
- Documents behind employee_document.read: masked, opening one is a SENSITIVE_READ audit row; PAN and Aadhaar format checks under the field
- Roster: shifts from a template or own times in the property's time zone (night shifts end the next day); an overlap is refused naming the other shift (409 SHIFT_CLASH) unless allowed with a reason; the person is locked FOR UPDATE so two managers at once can't both win; shortages per template × day; warnings for a day off, over 48 hours and allowed overlaps; copy a week day for day with what was skipped listed
- Leaving refused while still on the roster after the last day (the days are listed); my shifts through the linked account (roster.self)
- Screens Employees, the employee profile and Roster (week grid on a desktop, one day at a time on a 360 px phone, tap a gap to fill it); every refusal said under its field
- Dev seed S014: five departments, ten employees with their pay, nine shift templates, this week's roster; next week empty
- [done] Docs: API.md, DATABASE.md, schema-sprint-17.md, system design Sprint 17 and 4.27 with the roster flow diagram, ARCHITECTURE.md, RUN_AND_TEST, REVIEWING, CHANGELOG

**Files added / changed**

- database/migrations/V019__employees.sql; database/seed/dev/S014__employees.sql; database/queries/check-sprint-17.sql
- backend/Resort.Domain/Employees/Employees.cs; backend/Resort.Contracts/EmployeeContracts.cs
- backend/Resort.Application/ — Employees/ (EmployeeService.cs, CompensationService.cs, EmployeeDocumentService.cs, RosterService.cs), Validation/EmployeeValidators.cs, Common/Permissions.cs, Common/Abstractions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/ — AppDbContext.cs, AuditInterceptor.cs ([SalaryData]); backend/Resort.Api/ — Endpoints/EmployeesEndpoints.cs, Infrastructure/Problems.cs, Program.cs
- backend/Resort.Tests/ — Integration/EmployeesTests.cs, Integration/DevSeedTests.cs
- frontend/src/app/ — features/employees/ (employees.page.ts, employee.page.ts, employee-dialogs.ts, roster.page.ts, roster-dialogs.ts, employees.api.ts, employees.spec.ts), core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-17.spec.ts; scripts/check-api.mjs (Sprint 17 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-17.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-17-screens/; tools/handbooks/diagrams (roster-flow)

**Test results**

- Backend: 497 passed, 0 failed on real PostgreSQL 16 (8 new: EmployeesTests with a manager refused the pay and the audit trail showing ***, an overlapping shift refused naming the other one unless allowed with a reason, two managers at the same moment with exactly one winning 10 times, next week's roster with its shortages and warnings, copying a week, account links and leaving, encrypted documents; dev seed check)
- Frontend: 111 of 111 unit tests passed (3 new); ESLint clean; production build OK
- Browser tests: all 139 steps of Sprints 01–17 pass (19 min); Sprint 17's 7 steps cover ten employees, HR adding Kavya with her pay and PAN (a mistyped phone and PAN refused under the field), a manager on a phone seeing her profile without pay, next week copied with its gaps, a clash refused then allowed as a handover, a gap filled, and the phone roster one day at a time; API checks 224 of 224 (8 new); database checks clean; screenshots in docs/sprints/sprint-17-screens

**Outcome**

Accepted 29 Oct 2026 (the owner checked it on their PC: everything works and the screens are right). A manager copies last week into next week, sees the gaps listed, is refused a second overlapping shift for Asha with the other shift named, allows it as a handover with a reason, and fills a gap with a tap; a manager never sees pay (EmployeesTests, browser steps S17-01 … S17-07). Code #154; on main as tag sprint-17. Sprint 18 (attendance and timesheets) is next.

## Scope

**Tables**

- `departments`
- `designations`
- `employees`
- `employee_documents`
- `employee_compensation`
- `shift_templates`
- `roster_shifts`

**API endpoints**

- `/api/v1/properties/{propertyId}/departments, /designations, /employees/*, /roster/*`

**Screens / routes**

- `/employees`
- `/roster`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] employee_compensation readable only with employee_salary.read (separate table, separate endpoint)
- [x] Link employee ↔ user (optional)
- [x] Roster: prevent overlapping shifts (EXCLUDE) unless explicitly allowed; staffing shortage view
- [x] Seed: 10 employees

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S17-<issue-number>-<short-name>`
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

- A clash you didn't expect (409 SHIFT_CLASH): the message names the other shift. database/queries/check-sprint-17.sql block 5 lists overlapping scheduled shifts (should be none) and block 6 the overlaps a manager allowed, with their reasons.
- A shortage that won't go away: shortages count scheduled shifts on that template that day; a shift with its own times counts towards no template. Block 8 lists next week's gaps the same way the screen does.
- Someone sees pay who shouldn't (or can't when they should): only employee_salary.read opens /employees/{id}/compensation (owner and HR by default; check the role in Staff accounts → Roles). Block 10 checks that the audit trail never shows an amount.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Authorization — MANAGER cannot read salaries
- Integration — overlapping shift rejected

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Manager builds next week's roster; shortages highlighted

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Create roster → clash → shortage view

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-17` created on `main`
