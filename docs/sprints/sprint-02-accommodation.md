# Sprint 02 — Accommodation — room types, rooms, dormitory beds, blocks

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 1 — Foundation, property, rooms, beds | M | [Sprint 01](sprint-01-foundation.md) |

**Goal:** Model the 6 rooms and the dormitory correctly (room-based vs bed-based inventory) and let managers block units.

**GitHub:** milestone *Sprint 02 — Accommodation — room types, rooms, dormitory beds, blocks* · branch prefix `feature/S02-`

## Where this sprint stands

**Now:** Next up. Starts once Sprint 01 is accepted. Its 7 task issues are already in the Sprint 02 milestone.

**Done so far**

_Nothing yet._

**Files this sprint will add / change** (expected)

- database/migrations/V###__accommodation.sql — new tables (next free number)
- backend/Resort.Domain/Accommodation/ — entities
- backend/Resort.Contracts/Contracts.cs — request/response records
- backend/Resort.Application/Accommodation/ — services; Validation/Validators.cs; Common/Permissions.cs if new codes; DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs — DbSets and mappings
- backend/Resort.Api/Endpoints/AccommodationEndpoints.cs + Program.cs registration
- backend/Resort.Tests/Integration/AccommodationTests.cs (+ Unit/ for pure calculations)
- frontend/src/app/features/rooms/ — screens; app.routes.ts; layout/shell.ts menu entry
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-02.md, this guide, CHANGELOG.md

**Test results**

_Not run yet — see How to test below._

**Outcome**

Expected when accepted:

- A manager configures 6 rooms and a 10-bed dorm from the UI in under 10 minutes
- Blocking Room 103 for 2 nights shows it as blocked; a second overlapping block is refused

## Scope

**Tables**

- `accommodation_types`
- `accommodation_units`
- `beds`
- `amenities`
- `accommodation_type_amenities`
- `unit_images`
- `unit_blocks`

**API endpoints**

- `GET|POST /api/v1/properties/{propertyId}/accommodation-types`
- `GET|PUT /api/v1/properties/{propertyId}/accommodation-types/{id}`
- `GET|POST /api/v1/properties/{propertyId}/rooms`
- `GET|PUT /api/v1/properties/{propertyId}/rooms/{id}`
- `GET|POST /api/v1/properties/{propertyId}/rooms/{id}/beds`
- `PUT /api/v1/properties/{propertyId}/beds/{id}`
- `GET|POST /api/v1/properties/{propertyId}/blocks`
- `POST /api/v1/properties/{propertyId}/blocks/{id}/release`
- `GET /api/v1/properties/{propertyId}/amenities`

**Screens / routes**

- `/rooms`
- `/rooms/status (read-only for now)`
- `/settings/room-types`

**Other**

- Development seed-data runner (database/seed/dev) — brief §71

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] DB: new migration — accommodation tables. accommodation_types.inventory_mode CHECK IN ('UNIT','BED'); beds only allowed under BED-mode units (trigger or app rule + test)
- [ ] DB: unit_blocks with tstzrange/daterange + EXCLUDE constraint preventing overlapping active blocks on the same unit/bed
- [ ] API: accommodation types CRUD (accommodation.manage), rooms CRUD, beds CRUD, blocks create/release (unit_block.manage) — all audited, reason required on block
- [ ] API: deactivate rather than delete units that have history
- [ ] Seed: dev seed runner (Development only, idempotent) + seed 1 property, 6 rooms (2 deluxe, 4 standard), 1 dormitory with 10 beds
- [ ] UI: /rooms list grouped by type with bed counts; room editor; dorm bed editor; block dialog with date range + reason
- [ ] Docs: update DATABASE.md table list, API.md catalogue, sprint-02 guide status

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S02-<issue-number>-<short-name>`
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

- EXCLUDE constraints need the btree_gist extension: the migration must CREATE EXTENSION IF NOT EXISTS btree_gist.
- Date ranges are [check-in, check-out) in the property's local dates — never convert to UTC for availability.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Integration — cannot add beds to a UNIT-mode room (422)
- Integration — overlapping blocks on the same unit rejected by the DB (409 OVERLAP)
- Integration — receptionist can read rooms but not create them (403)
- Integration — block create/release audited with reason
- Unit — inventory capacity calculation (room = 1 sellable unit, dorm = N beds)
- Frontend — room editor form validation

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- A manager configures 6 rooms and a 10-bed dorm from the UI in under 10 minutes
- Blocking Room 103 for 2 nights shows it as blocked; a second overlapping block is refused

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Create types → rooms → dorm with beds → block a room → try an overlapping block

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-02` created on `main`
