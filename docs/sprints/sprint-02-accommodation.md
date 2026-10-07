# Sprint 02 — Accommodation — room types, rooms, dormitory beds, blocks

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🟡 In review — **current sprint** | Phase 1 — Foundation, property, rooms, beds | M | [Sprint 01](sprint-01-foundation.md) |

**Goal:** Model the 6 rooms and the dormitory correctly (room-based vs bed-based inventory) and let managers block units.

**GitHub:** milestone *Sprint 02 — Accommodation — room types, rooms, dormitory beds, blocks* · branch prefix `feature/S02-`

## Where this sprint stands

**Now:** Built and merged to develop. Being reviewed together with Sprint 01 in pull request [#74](https://github.com/Prathamesh27702/resort-os/pull/74) — script: [REVIEWING.md → Sprint 02](REVIEWING.md#sprint-02--rooms-dormitory-and-blocks).

**Done so far**

- Migration V004: 7 tables, 22 built-in amenities, btree_gist EXCLUDE constraints for overlapping blocks, triggers that keep beds under bed-based rooms only
- Room-based (UNIT) and bed-based (BED) inventory with a capacity calculation the availability engine (Sprint 05) will reuse
- APIs: room types with amenities, rooms, dorm beds, room status board, blocks with reason and release — all permission-checked, validated and audited
- Blocks: property-local dates, end date not included, row lock + database guard so 10 simultaneous requests give exactly one block
- Rooms are deactivated, never deleted; blocks are released, never deleted
- Development seed runner (database/seed/dev) + demo data: 2 deluxe, 4 standard, a 10-bed dormitory
- Angular screens: Rooms (grouped by type, today's status, blocks list), room editor with dorm bed editor and bulk-add, block dialog, release with reason, Room status board, Room types list and editor with amenities
- Today checklist gains a *Set up rooms* step; menu gains Rooms, Room status, Room types

**Files added / changed**

- database/migrations/V004__accommodation.sql; database/seed/dev/S001__accommodation.sql
- backend/Resort.Domain/Accommodation/Accommodation.cs
- backend/Resort.Contracts/AccommodationContracts.cs
- backend/Resort.Application/ — Accommodation/AccommodationService.cs, Accommodation/BlockService.cs, Validation/AccommodationValidators.cs, Common/AuditContext.cs, Common/Permissions.cs
- backend/Resort.Infrastructure/ — Persistence/AppDbContext.cs, AuditInterceptor.cs (reason from the request body), DevSeeder.cs
- backend/Resort.Api/ — Endpoints/AccommodationEndpoints.cs, Program.cs, appsettings.Development.json (DevSeed)
- backend/Resort.Tests/ — Integration/AccommodationTests.cs, Integration/DevSeedTests.cs, Unit/AccommodationUnitTests.cs
- frontend/src/app/ — features/rooms/*, features/settings/room-types/*, core/models.ts, app.routes.ts, layout/shell.ts, features/dashboard
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-02.md, docs/GETTING_STARTED.md, docs/sprints/REVIEWING.md, sprint-02-screens/

**Test results**

- Backend: 51 passed, 0 failed (30 from Sprint 01 + 21 new), real PostgreSQL 16
- Concurrency: 10 simultaneous block requests on one room → exactly 1 created, 9 × 409; repeated 20 times, no flakes
- Frontend: 14 of 14 passed; ESLint clean; production build OK, no warnings
- All 14 steps of the REVIEWING.md Sprint 02 script run in Chromium as owner and receptionist, desktop and 390 px phone width, no page errors ([screenshots](sprint-02-screens/))

**Outcome**

Pending owner acceptance together with Sprint 01. On acceptance: merge to main, tag sprint-02, Sprint 03 (guests) starts.

## Scope

**Tables**

- `accommodation_types`
- `accommodation_units`
- `beds`
- `amenities`
- `accommodation_type_amenities`
- `unit_images`
- `unit_blocks`

**Migrations**

- `V004__accommodation`

**API endpoints**

- `GET|POST /api/v1/properties/{propertyId}/accommodation-types`
- `GET|PUT /api/v1/properties/{propertyId}/accommodation-types/{id}`
- `GET|POST /api/v1/properties/{propertyId}/rooms`
- `GET /api/v1/properties/{propertyId}/rooms/status`
- `GET|PUT /api/v1/properties/{propertyId}/rooms/{id}`
- `GET|POST /api/v1/properties/{propertyId}/rooms/{id}/beds`
- `PUT /api/v1/properties/{propertyId}/beds/{id}`
- `GET|POST /api/v1/properties/{propertyId}/blocks`
- `POST /api/v1/properties/{propertyId}/blocks/{id}/release`
- `GET /api/v1/properties/{propertyId}/amenities`

**Screens / routes**

- `/rooms`
- `/rooms/new, /rooms/{id}`
- `/rooms/status (read-only for now)`
- `/settings/room-types, /settings/room-types/new, /settings/room-types/{id}`

**Other**

- Development seed-data runner (database/seed/dev) — brief §71
- unit_images table only; the photo upload API comes with file storage in Sprint 03
- Screenshots of the built screens: docs/sprints/sprint-02-screens/

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] DB: new migration — accommodation tables. accommodation_types.inventory_mode CHECK IN ('UNIT','BED'); beds only allowed under BED-mode units (trigger or app rule + test)
- [x] DB: unit_blocks with tstzrange/daterange + EXCLUDE constraint preventing overlapping active blocks on the same unit/bed
- [x] API: accommodation types CRUD (accommodation.manage), rooms CRUD, beds CRUD, blocks create/release (unit_block.manage) — all audited, reason required on block
- [x] API: deactivate rather than delete units that have history
- [x] Seed: dev seed runner (Development only, idempotent) + seed 1 property, 6 rooms (2 deluxe, 4 standard), 1 dormitory with 10 beds
- [x] UI: /rooms list grouped by type with bed counts; room editor; dorm bed editor; block dialog with date range + reason
- [x] Docs: update DATABASE.md table list, API.md catalogue, sprint-02 guide status
- [ ] Review and sign-off by owner (open issues for any change requests)

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
- No demo rooms after pulling? The seed only fills a property that has no room types. Reset the database and restart the API.
- 409 OVERLAP (not BLOCK_OVERLAP) means two requests raced and the database guard caught the second one. That's correct behaviour.
- Configuring rooms needs accommodation.manage (Owner, GM). A Manager can block rooms but not edit them; a Receptionist can only view.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Integration — cannot add beds to a UNIT-mode room (422)
- Integration — overlapping blocks on the same unit rejected by the DB (409 OVERLAP)
- Integration — receptionist can read rooms but not create them (403)
- Integration — block create/release audited with reason
- Unit — inventory capacity calculation (room = 1 sellable unit, dorm = N beds)
- Frontend — room editor form validation
- Integration — 10 concurrent blocks on one room: exactly one wins
- Integration — whole-dorm block vs single-bed block conflict, both directions
- Integration — tenant isolation (404) and stale version (409)
- Integration — dev seed creates the demo rooms once and skips on restart
- Unit — overlap message names the blocked nights, not the day the room is free again

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
