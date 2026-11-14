# Sprint 31 — Groups, company accounts, day close and bill handling

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 10 — Hardening, backup, monitoring, deployment | L | [Sprint 08](sprint-08-folio.md), [Sprint 12](sprint-12-gst-invoices-credit-debit-notes-receipts.md), [Sprint 14](sprint-14-menu-restaurant-pos-and-kot.md), [Sprint 23](sprint-23-reports-and-dashboards.md) |

**Goal:** What Indian hotel software has for weddings, corporate guests and the end of the day (market comparison of 13 Nov 2026, the should-have list).

**GitHub:** milestone *Sprint 31 — Groups, company accounts, day close and bill handling* · branch prefix `feature/S31-`

## Where this sprint stands

**Now:** Not started. Can start once Sprint 08, Sprint 12, Sprint 14, Sprint 23 are accepted.

**Done so far**

_Nothing yet._

**Files this sprint will add / change** (expected)

- database/migrations/V###__front_office.sql — new tables (next free number)
- backend/Resort.Domain/FrontOffice/ — entities
- backend/Resort.Contracts/Contracts.cs — request/response records
- backend/Resort.Application/FrontOffice/ — services; Validation/Validators.cs; Common/Permissions.cs if new codes; DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs — DbSets and mappings
- backend/Resort.Api/Endpoints/FrontOfficeEndpoints.cs + Program.cs registration
- backend/Resort.Tests/Integration/FrontOfficeTests.cs (+ Unit/ for pure calculations)
- frontend/src/app/features/groups/ — screens; app.routes.ts; layout/shell.ts menu entry
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-31.md, this guide, CHANGELOG.md

**Test results**

_Not run yet — see How to test below._

**Outcome**

Expected when accepted:

- A 10-room wedding block is checked in from a rooming list and leaves on one invoice

## Scope

**Tables**

- `reservation_groups`
- `company_accounts`
- `company_ledger_entries`
- `day_closes`

**Screens / routes**

- `/groups`
- `/companies`
- `/day-close`
- `/restaurant/orders/{id} (split, merge, move table)`
- `/reports (payment modes, outstanding by company, item-wise sales, cash book)`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] Group and wedding blocks: hold many rooms under one name, a rooming list, a master bill that takes chosen charges of every room, one invoice or one per room
- [ ] Company and travel-agent accounts: credit limit, bill to company at check-out, statement, payments against several invoices, TDS deducted recorded, commission for agents
- [ ] Night audit / day close: the checks (guests not checked out, open restaurant bills, cash drawer not counted, room charges not posted), then the business date moves; the day's summary to the owner on WhatsApp and email
- [ ] Split a restaurant bill by dish or by people, merge two tables, move an order to another table, with the kitchen told
- [ ] Reports Indian owners ask for: payment-mode summary (cash, UPI, card, room, company), outstanding by company with ageing, item-wise and category-wise sales, daily cash book
- [ ] Service charge only as a clearly optional line the guest can refuse (CCPA guidelines of July 2022); off by default

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S31-<issue-number>-<short-name>`
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

- Integration — a master bill never charges the same room night twice
- Integration — the day can't be closed twice, and not while a required check fails without a reason

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- A 10-room wedding block is checked in from a rooming list and leaves on one invoice

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Wedding block → rooming list → master bill → day close summary on WhatsApp

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-31` created on `main`
