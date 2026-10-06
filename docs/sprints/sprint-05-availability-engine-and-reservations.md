# Sprint 05 — Availability engine and reservations core

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 2 — Guests, reservations, availability, calendar, front desk | L | [Sprint 02](sprint-02-accommodation.md), [Sprint 03](sprint-03-guests.md), [Sprint 04](sprint-04-rate-plans-and-packages.md) |

**Goal:** Race-free booking of rooms and dorm beds with idempotent create and full status history (brief §14–16).

**GitHub:** milestone *Sprint 05 — Availability engine and reservations core* · branch prefix `feature/S05-`

## Scope

**Tables**

- `reservations`
- `reservation_units`
- `reservation_guests`
- `reservation_rate_breakdowns`
- `reservation_notes`
- `reservation_status_history`
- `idempotency_keys`

**API endpoints**

- `GET /api/v1/properties/{propertyId}/availability?from&to&adults`
- `POST /api/v1/properties/{propertyId}/reservations  (Idempotency-Key header required)`
- `GET /api/v1/properties/{propertyId}/reservations`
- `GET /api/v1/properties/{propertyId}/reservations/{id}`
- `POST /api/v1/properties/{propertyId}/reservations/{id}/confirm | cancel | no-show`
- `PUT /api/v1/properties/{propertyId}/reservations/{id}  (dates/guests; re-checks availability)`
- `POST /api/v1/properties/{propertyId}/reservations/{id}/notes`

**Screens / routes**

- `/reservations`
- `/reservations/new`
- `/reservations/:id`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] DB: reservation_units.stay daterange + EXCLUDE USING gist (unit_id WITH =, stay WITH &&) WHERE status is active; same for bed_id
- [ ] Algorithm: transaction → SELECT ... FOR UPDATE on the unit rows → availability check → insert; exclusion constraint is the final guarantee; map 23P01 to ROOM_UNAVAILABLE
- [ ] Idempotency: idempotency_keys(key, request_hash, response) — same key + same body returns the stored response; same key + different body = 422
- [ ] Status machine INQUIRY→PENDING→CONFIRMED→CHECKED_IN→CHECKED_OUT, CANCELLED, NO_SHOW, REFUNDED; transitions table-driven and audited
- [ ] Never delete reservations (brief §87); enquiry-stage delete is a status
- [ ] Docs: write docs/architecture/booking-algorithm.md with the exact PostgreSQL behaviour
- [ ] UI: new reservation wizard (dates → availability → guest → rate → confirm) in ≤ 5 clicks for a walk-in

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S05-<issue-number>-<short-name>`
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

- Reproduce races locally with tests/…/ConcurrencyTests using Task.WhenAll + a Barrier; never with sleeps.
- Look at pg_locks while a test is paused to see who holds what.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Concurrency — two simultaneous bookings for the last room → exactly one SUCCESS, one ROOM_UNAVAILABLE (run 50×)
- Concurrency — dorm with 1 bed left, 5 parallel requests → 1 success
- Idempotency — same request retried → one reservation, identical response
- Integration — cancelled reservation frees inventory
- Integration — status transitions outside the allowed graph → 422

Commands:

```bash
scripts/test.sh                                   # everything (backend + frontend)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

## Acceptance criteria

- Brief §16 scenario passes reliably in CI

## Sprint review demo

- Book the last room from two browsers at once

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-05` created on `main`
