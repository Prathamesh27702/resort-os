# Sprint 04 — Rate plans and packages

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 2 — Guests, reservations, availability, calendar, front desk | M | [Sprint 02](sprint-02-accommodation.md) |

**Goal:** Configurable prices per type/date and packages whose revenue is split into room/food/transport/activity.

**GitHub:** milestone *Sprint 04 — Rate plans and packages* · branch prefix `feature/S04-`

## Where this sprint stands

**Now:** Built and in review: one pull request into develop with the database, API, screens, tests and docs. Waiting for the owner's review (REVIEWING.md → Sprint 04).

**Done so far**

- Migration V006: rate_plans, rate_plan_prices (one row per plan × room type × night, numeric(12,2), per-bed prices for dormitories), packages, package_components, reservation_sources (14 built-in channels); one default plan per property; a deferred trigger refuses any package whose split doesn't add up to its price
- Quote engine (pure, unit-tested): nightly breakdown, extra adults and children above standard occupancy, per-bed dormitory pricing, minimum stay on the arrival night, maximum occupancy; every refusal says exactly why (which nights have no price, how many guests the room sleeps)
- Package revenue split by percent with the largest-remainder rule: amounts always add up to exactly the price, to the paisa (rule in DATABASE.md → Money and rounding)
- Rate APIs: plans, grid read, bulk edit by date range and days of the week, quote by room or type (id or code), packages, reservation sources. Every price change audited with old and new values
- Dev seed S003: BAR with Friday/Saturday uplift for a year, Couple getaway and Room + breakfast packages
- Screens: Rates (plan picker, room type × 14-night grid, click a cell to edit, bulk edit with weekend shortcut, quote panel) and Packages (list with revenue chips, editor with live split preview). Every error is shown under its field with the reason

**Files added / changed**

- database/migrations/V006__rates.sql; database/seed/dev/S003__rates.sql; database/queries/check-sprint-04.sql
- backend/Resort.Domain/Rates/Rates.cs, Rates/QuoteEngine.cs (QuoteEngine, MoneySplit)
- backend/Resort.Contracts/RateContracts.cs
- backend/Resort.Application/ — Rates/RateService.cs, Rates/PackageService.cs, Validation/RateValidators.cs, Common/Permissions.cs, Common/Errors.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/ — Endpoints/RateEndpoints.cs, Program.cs, Infrastructure/Problems.cs
- backend/Resort.Tests/ — Integration/RateTests.cs, Integration/DevSeedTests.cs, Unit/RateUnitTests.cs
- frontend/src/app/ — features/settings/rates/*, features/settings/packages/*, core/models.ts, core/http.ts (nested field errors), app.routes.ts, layout/shell.ts, features/audit (labels)
- frontend/e2e/sprint-04.spec.ts; scripts/check-api.mjs (Sprint 04 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-04.md, docs/sprints/REVIEWING.md, sprint-04-screens/

**Test results**

- Backend: 138 passed, 0 failed on real PostgreSQL 16 (29 new: 11 integration, 18 unit including the weekend-boundary quote and 5,000 random package splits)
- Frontend: 31 of 31 unit tests passed; ESLint clean; production build OK
- Browser tests: 45 of 45 passed (Sprint 01–03 steps plus the 6 new Sprint 04 review steps); screenshots in docs/sprints/sprint-04-screens

**Outcome**

Expected when accepted: a quote for Room 103, 2 adults, 3 nights returns a nightly breakdown matching the rate grid (checked by DevSeedTests, check-api.mjs and browser step S04-03).

## Scope

**Tables**

- `rate_plans`
- `rate_plan_prices`
- `packages`
- `package_components`
- `reservation_sources`

**API endpoints**

- `GET|POST /api/v1/properties/{propertyId}/rate-plans`
- `PUT /api/v1/properties/{propertyId}/rate-plans/{id}`
- `PUT /api/v1/properties/{propertyId}/rate-plans/{id}/prices  (bulk date range)`
- `GET /api/v1/properties/{propertyId}/rates/quote?type&from&to&adults&children`
- `GET|POST /api/v1/properties/{propertyId}/packages`

**Screens / routes**

- `/settings/rates`
- `/settings/packages`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] DB: new migration — rate tables; prices stored per date (numeric(12,2)); per-bed prices for BED mode
- [ ] Domain: quote engine — nightly breakdown, extra adult/child, min stay; pure functions, fully unit-tested
- [ ] Domain: package revenue split must sum exactly to package price (rounding rule documented)
- [ ] API: price changes audited with old/new (brief §87)
- [ ] Seed: BAR (best available rate), weekend uplift, Couple and Room + breakfast packages
- [ ] UI: rate grid (type × date) with bulk edit

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S04-<issue-number>-<short-name>`
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

- Money is decimal in C# and numeric in SQL. Never float/double.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Unit — quote for 3 nights across a weekend boundary
- Unit — package split rounding never loses or gains a paisa
- Integration — price change audited

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- A quote for Room 103, 2 adults, 3 nights returns a nightly breakdown matching the rate grid

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Set weekend prices → quote → create a package and show its split

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-04` created on `main`
