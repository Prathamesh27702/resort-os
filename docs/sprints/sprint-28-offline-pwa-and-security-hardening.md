# Sprint 28 — Offline PWA and security hardening

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 10 — Hardening, backup, monitoring, deployment | L | [Sprint 13](sprint-13-housekeeping-and-maintenance-mobile.md), [Sprint 18](sprint-18-attendance-and-timesheets-pwa.md) |

**Goal:** Work through poor connectivity safely; MFA and OWASP verification (brief §56, §57, §68).

**GitHub:** milestone *Sprint 28 — Offline PWA and security hardening* · branch prefix `feature/S28-`

## Scope

**Other**

- PWA caching of today's reservations
- room status
- housekeeping tasks
- roster; ONLINE/OFFLINE/SYNCING/SYNC FAILED indicator

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] Read-only offline caches; no financial operations offline
- [ ] TOTP MFA for OWNER/GM/ACCOUNTANT
- [ ] OWASP Top 10 test pack (docs/SECURITY.md table) incl. SSRF on webhook/URL inputs
- [ ] Shared cache for access/permissions if >1 API instance

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S28-<issue-number>-<short-name>`
3. **Database up:** `docker compose --env-file .env -f infra/docker/docker-compose.yml up -d db`
4. **Schema changes:** add the next `database/migrations/V###__<name>.sql` (never edit an applied one). The API applies it on start.
5. **API:** `cd backend/Resort.Api && dotnet watch run` → http://localhost:5180/swagger
6. **Web:** `cd frontend && npm start` → http://localhost:4300
7. **Fresh data:** `scripts/db-reset.sh`, then restart the API (migrations + bootstrap owner run again; dev seed data from Sprint 2 onward)

## How to debug

- **API:** run the `Resort.Api` launch profile in VS Code (C# Dev Kit), Rider or Visual Studio and set breakpoints; Swagger's *Authorize* button takes the `accessToken` from `POST /api/v1/auth/login`.
- **Every error response has a `correlationId`.** Search the API console/log for it to find the full exception.
- **SQL:** set `Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore.Database.Command=Information` in `.env` to log every SQL statement.
- **Database:** `docker compose --env-file .env -f infra/docker/docker-compose.yml exec db psql -U $POSTGRES_USER $POSTGRES_DB`
- **Web:** Angular DevTools + the browser Network tab; API errors are `{code, message, details, correlationId}`.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Security — every OWASP row has an automated test or a documented manual check

Commands:

```bash
scripts/test.sh                                   # everything (backend + frontend)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

## Acceptance criteria

- Housekeeping works on airplane mode and syncs when back online

## Sprint review demo

- Toggle network off on a phone

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-28` created on `main`
