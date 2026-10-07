# Sprint 24 — Notification engine, WhatsApp, email, background jobs

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 9 — Notifications, WhatsApp, Zoho Books, OTA, booking site | L | [Sprint 05](sprint-05-availability-engine-and-reservations.md) |

**Goal:** Configurable templates, delivery tracking and idempotent scheduled jobs (brief §32, §69, §70).

**GitHub:** milestone *Sprint 24 — Notification engine, WhatsApp, email, background jobs* · branch prefix `feature/S24-`

## Where this sprint stands

**Now:** Not started. Can start once Sprint 05 is accepted.

**Done so far**

_Nothing yet._

**Files this sprint will add / change** (expected)

- database/migrations/V###__notifications.sql — new tables (next free number)
- backend/Resort.Domain/Notifications/ — entities
- backend/Resort.Contracts/Contracts.cs — request/response records
- backend/Resort.Application/Notifications/ — services; Validation/Validators.cs; Common/Permissions.cs if new codes; DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs — DbSets and mappings
- backend/Resort.Infrastructure/Notifications/ — external API adapter + fake server for tests
- backend/Resort.Api/Endpoints/NotificationsEndpoints.cs + Program.cs registration
- backend/Resort.Tests/Integration/NotificationsTests.cs (+ Unit/ for pure calculations)
- frontend/src/app/features/integrations/ — screens; app.routes.ts; layout/shell.ts menu entry
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-24.md, this guide, CHANGELOG.md

**Test results**

_Not run yet — see How to test below._

**Outcome**

Expected when accepted:

- Guest gets confirmation + day-before reminder (brief §73)

## Scope

**Tables**

- `message_templates`
- `outbox_messages`
- `notifications`
- `whatsapp_messages`
- `email_messages`
- `job_runs`

**API endpoints**

- `/api/v1/webhooks/whatsapp`

**External APIs used**

- WhatsApp Business Platform (Cloud API)
- transactional email provider

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] Outbox pattern: business transaction writes outbox row; background worker sends
- [ ] Background jobs with PostgreSQL-backed scheduler (no Redis — brief §76); every job idempotent
- [ ] Jobs: reservation reminder, pre-arrival, checkout reminder, payment reminder, inventory alert, leave reminder
- [ ] WhatsApp template messages; status webhooks (sent/delivered/read/failed)

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S24-<issue-number>-<short-name>`
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

- Integration — job run twice sends once
- Integration — WhatsApp status webhook signature check

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Guest gets confirmation + day-before reminder (brief §73)

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Book → WhatsApp confirmation → reminder job

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-24` created on `main`
