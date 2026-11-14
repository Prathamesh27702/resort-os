# Sprint 32 — Guest self-service, languages and the DPDP Act

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 10 — Hardening, backup, monitoring, deployment | L | [Sprint 24](sprint-24-notification-engine-whatsapp-email.md), [Sprint 27](sprint-27-direct-booking-website.md), [Sprint 30](sprint-30-india-readiness.md) |

**Goal:** What guests and staff in India now expect on their phone, and what the Digital Personal Data Protection Act asks for (market comparison of 13 Nov 2026).

**GitHub:** milestone *Sprint 32 — Guest self-service, languages and the DPDP Act* · branch prefix `feature/S32-`

## Where this sprint stands

**Now:** Not started. Can start once Sprint 24, Sprint 27, Sprint 30 are accepted.

**Done so far**

_Nothing yet._

**Files this sprint will add / change** (expected)

- database/migrations/V###__guests.sql — new tables (next free number)
- backend/Resort.Domain/Guests/ — entities
- backend/Resort.Contracts/Contracts.cs — request/response records
- backend/Resort.Application/Guests/ — services; Validation/Validators.cs; Common/Permissions.cs if new codes; DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs — DbSets and mappings
- backend/Resort.Api/Endpoints/GuestsEndpoints.cs + Program.cs registration
- backend/Resort.Tests/Integration/GuestsTests.cs (+ Unit/ for pure calculations)
- frontend/src/app/features/guest-experience/ — screens; app.routes.ts; layout/shell.ts menu entry
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-32.md, this guide, CHANGELOG.md

**Test results**

_Not run yet — see How to test below._

**Outcome**

Expected when accepted:

- A guest completes online check-in on their phone and reception only hands over the key

## Scope

**Tables**

- `guest_checkins`
- `feedback_responses`
- `privacy_notices`
- `retention_rules`
- `data_breach_log`
- `pricing_rules`

**Screens / routes**

- `public: online check-in, feedback`
- `/settings/privacy`
- `/settings/pricing-rules`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] Online check-in link on WhatsApp: the guest sends ID, signature and arrival time; a digital guest registration card (C-form details for foreign guests feed the Form C sheet of Sprint 30)
- [ ] Invoice PDF and payment receipt to the guest on WhatsApp at check-out
- [ ] Feedback request after check-out; a happy guest is asked for a Google review, an unhappy one reaches the manager first
- [ ] Hindi, Marathi and Konkani on the staff phone screens (housekeeping tasks, kitchen display, attendance, leave); English stays for the desk
- [ ] DPDP: a privacy notice in plain words at every point data is collected, consent kept with its notice version, retention periods with automatic erasure (ID documents a set time after check-out, Form C sheets kept as long as the law asks), a breach log with the 72-hour report to the Data Protection Board, a parent's consent for a child's data, a named person to contact
- [ ] Simple pricing rules: weekend, long weekend and festival prices, a price rise when occupancy passes a level, shown on the rate calendar before they apply

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S32-<issue-number>-<short-name>`
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

- Integration — erasure removes what the retention rule names and nothing the law says to keep
- E2E — online check-in on a 360 px phone

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- A guest completes online check-in on their phone and reception only hands over the key

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Booking → WhatsApp link → online check-in → key handed over → invoice on WhatsApp

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-32` created on `main`
