# Sprint 06 — Reservation calendar

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 2 — Guests, reservations, availability, calendar, front desk | M | [Sprint 05](sprint-05-availability-engine-and-reservations.md) |

**Goal:** Day/week/month calendar showing room, guest, dates, status, source and payment status; safe drag-and-drop reassignment.

**GitHub:** milestone *Sprint 06 — Reservation calendar* · branch prefix `feature/S06-`

## Scope

**API endpoints**

- `GET /api/v1/properties/{propertyId}/calendar?from&to`
- `POST /api/v1/properties/{propertyId}/reservations/{id}/reassign  (reason required)`

**Screens / routes**

- `/reservations/calendar`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] API: calendar read model (single query, indexed) including blocks and maintenance blocks
- [ ] API: reassign room — same locking as booking; audited with reason (brief §58)
- [ ] UI: calendar component (CDK drag-drop); optimistic UI only after server confirms; conflicts shown inline
- [ ] UI: mobile view = list by day

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S06-<issue-number>-<short-name>`
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

Sprint-specific:

- If the UI shows a booking the server rejected, the optimistic update wasn't rolled back — check the reassign error handler.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Concurrency — two users reassigning into the same room → one wins
- Frontend — calendar renders 7 rooms × 31 days under 200 ms

Commands:

```bash
scripts/test.sh                                   # everything (backend + frontend)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

## Acceptance criteria

- Reception moves a guest from 102 to 103 in two actions; the audit trail shows who/why

## Sprint review demo

- Drag a booking to another room; drag onto a blocked room is refused

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-06` created on `main`
