# Sprint 27 — Direct booking website

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 9 — Notifications, WhatsApp, Zoho Books, OTA, booking site | L | [Sprint 11](sprint-11-razorpay.md), [Sprint 24](sprint-24-notification-engine-whatsapp-email.md) |

**Goal:** Public booking site with live availability and Razorpay (brief §30).

**GitHub:** milestone *Sprint 27 — Direct booking website* · branch prefix `feature/S27-`

## Scope

**API endpoints**

- `/api/v1/public/* — separate`
- `rate-limited`
- `no admin data`

**Screens / routes**

- `public app — dates`
- `rooms`
- `packages`
- `extras`
- `details`
- `pay`
- `confirmation`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] Separate public API surface; captcha/turnstile on pay step
- [ ] SEO-friendly pages served via Cloudflare

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S27-<issue-number>-<short-name>`
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

- E2E — booking + payment (brief §73 start)

Commands:

```bash
scripts/test.sh                                   # everything (backend + frontend)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

## Acceptance criteria

- Guest books Room 103 on the website and receives confirmation

## Sprint review demo

- Brief §73 first eight steps

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-27` created on `main`
