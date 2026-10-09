# Sprint 03 — Guests

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 2 — Guests, reservations, availability, calendar, front desk | M | [Sprint 01](sprint-01-foundation.md) |

**Goal:** Guest profiles with only the data that is needed, encrypted sensitive fields, de-duplication and retention.

**GitHub:** milestone *Sprint 03 — Guests* · branch prefix `feature/S03-`

## Where this sprint stands

**Now:** In progress — see the open issues in this sprint's milestone.

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
- frontend/src/app/features/guests/ — screens; app.routes.ts; layout/shell.ts menu entry
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-03.md, this guide, CHANGELOG.md

**Test results**

_Not run yet — see How to test below._

**Outcome**

Expected when accepted:

- Reception finds a returning guest by phone in one search

## Scope

**Tables**

- `guests`
- `guest_addresses`
- `guest_documents`
- `guest_preferences`
- `guest_consents`

**API endpoints**

- `GET|POST /api/v1/guests`
- `GET|PUT /api/v1/guests/{id}`
- `GET /api/v1/guests/{id}/stays`
- `POST /api/v1/guests/{id}/documents`
- `GET /api/v1/guests/{id}/documents/{docId}  (guest_document.read only)`
- `POST /api/v1/guests/{id}/anonymise`
- `POST /api/v1/guests/merge`

**Screens / routes**

- `/guests`
- `/guests/:id`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] DB: new migration — guest tables; data-classification comment on every column (public/internal/confidential/sensitive, brief §59)
- [ ] Infra: field-level encryption (AES-GCM, key from configuration) for ID numbers and document metadata; key rotation design documented
- [ ] Infra: blob storage abstraction (IFileStore) with local-disk dev implementation for ID scans
- [ ] API: search by name/phone/email (trigram index), de-duplication hint on create, merge with audit
- [ ] API: anonymise (guest.delete) replaces PII, keeps stay history for accounting
- [ ] Job: retention dry-run report (what would be purged) — actual purge enabled only after owner approval
- [ ] Seed: 20 realistic guests (Indian + international)
- [ ] UI: guest list/search, profile, documents tab hidden without guest_document.read

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S03-<issue-number>-<short-name>`
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

- Lost the encryption key = lost ID data. Dev key lives in .env (Encryption__Key); never reuse across environments.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Integration — WAITER and HOUSEKEEPER cannot read guest documents (403) — brief §59
- Integration — ID number stored encrypted (raw column is not plaintext)
- Integration — anonymise keeps stays, removes PII, audited with reason
- Unit — phone normalisation (+91 and international)

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Reception finds a returning guest by phone in one search

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Create guest → duplicate warning → upload ID → view as receptionist vs waiter

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-03` created on `main`
