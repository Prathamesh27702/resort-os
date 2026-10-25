# Sprint 13 — Housekeeping and maintenance (mobile-first)

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 4 — Housekeeping, maintenance | M | [Sprint 07](sprint-07-front-desk.md) |

**Goal:** Room status workflow and maintenance tickets that staff use on phones (brief §33, §34, §53).

**GitHub:** milestone *Sprint 13 — Housekeeping and maintenance (mobile-first)* · branch prefix `feature/S13-`

## Where this sprint stands

**Now:** Built and in review: pull request into develop, waiting for the owner's acceptance.

**Done so far**

- Migration V015: housekeeping_tasks (one open task per room or dorm bed, one task per state change, never deleted), checklist templates and lines (one active per property, kind of cleaning and room type), maintenance_assets, maintenance_tickets (M00001…), maintenance_comments and maintenance_attachments (append-only); every property gets a 7-line Departure clean checklist; rooms already dirty get their task
- A check-out, room move or repair makes the room DIRTY and its cleaning task in the same transaction; the task points at the state change (unique), so three people checking the same guest out at once make one task
- Two taps for a housekeeper: Start, then Done (every line ticked); with inspection on (property setting housekeeping.inspection_required, default on) the room waits as CLEAN until a supervisor marks it READY or sends it back with what to redo
- Maintenance tickets: report with room, bed, equipment or place, kind and priority; Out of order blocks the room from tonight to the expected date (at most 90 days; refused, saying which booking, when a guest is booked in it); resolving releases the block and sends the room to cleaning (Clean after repair) before it can be sold
- Photos shrunk on the phone (at most 1600 px, JPEG), checked by their content on the server (JPEG, PNG or WebP, at most 2 MB, 6 per ticket) and stored encrypted through IFileStore
- Screens for a 360 px phone: Housekeeping board (one card per room, rush rooms first, one big Start/Done/Mark ready button, task sheet with checklist, assign, send back, report a problem), Maintenance list, Report a problem with photos, ticket page with timeline, Checklists editor; Room status shows today's cleaning state and open repairs
- Dev seed S010: the departure checklist, the task for dirty Room 101, three assets and two open tickets (M00001 pool pump, M00002 tap in Room 104)
- [done] Docs: API.md, DATABASE.md, schema-sprint-13.md, system design Sprint 13 and 4.23 with the housekeeping flow diagram, RUN_AND_TEST, REVIEWING

**Files added / changed**

- database/migrations/V015__housekeeping.sql; database/seed/dev/S010__housekeeping.sql; database/queries/check-sprint-13.sql
- backend/Resort.Domain/Housekeeping/ (Housekeeping.cs, Maintenance.cs); backend/Resort.Domain/FrontDesk/FrontDesk.cs
- backend/Resort.Contracts/HousekeepingContracts.cs; backend/Resort.Contracts/AccommodationContracts.cs
- backend/Resort.Application/ — Housekeeping/ (HousekeepingState.cs, HousekeepingService.cs, MaintenanceService.cs), Validation/HousekeepingValidators.cs, Accommodation/BlockService.cs, Accommodation/AccommodationService.cs, FrontDesk/FrontDeskService.cs, Common/PropertySettingsReader.cs, Common/Permissions.cs, Common/Abstractions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/ — Endpoints/HousekeepingEndpoints.cs, Program.cs
- backend/Resort.Tests/ — Integration/HousekeepingTests.cs, Integration/MaintenanceTests.cs, Unit/HousekeepingUnitTests.cs, Integration/FrontDeskTests.cs
- frontend/src/app/ — features/housekeeping/ (housekeeping.page.ts, task-sheet.ts, checklists.page.ts, housekeeping.api.ts, housekeeping.spec.ts), features/maintenance/ (maintenance.page.ts, report.page.ts, ticket.page.ts, maintenance.api.ts), features/rooms/room-status.page.ts, features/front-desk/front-desk.api.ts, core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-13.spec.ts; scripts/check-api.mjs (Sprint 13 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-13.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-13-screens/; tools/handbooks/diagrams (housekeeping-flow)

**Test results**

- Backend: 417 passed, 0 failed on real PostgreSQL 16 (36 new: HousekeepingTests with check-outs of the same guest at the same moment making one task, the two-tap demo, send back to the same housekeeper, no-inspection mode, two housekeepers starting one room at once (exactly one gets it, 10 times), assigning, required checklist lines and room-type checklists, one open task per room; MaintenanceTests with out of order blocking the room and resolving sending it to cleaning, a guest booked tonight refusing out of order, photos checked by their bytes, encrypted and limited to six, reporters seeing their own tickets, a retried report saved once, equipment codes; HousekeepingUnitTests)
- Frontend: 95 of 95 unit tests passed (7 new: board filters, the one big button, photo sizing, the property's date for out of order); ESLint clean; production build OK
- BROWSER_RESULTS

**Outcome**

Expected when accepted:

- Housekeeper finishes a room in 2 taps; supervisor marks READY

## Scope

**Tables**

- `housekeeping_tasks`
- `housekeeping_checklist_templates`
- `housekeeping_checklist_items`
- `maintenance_assets`
- `maintenance_tickets`
- `maintenance_comments`
- `maintenance_attachments`

**API endpoints**

- `GET /api/v1/properties/{propertyId}/housekeeping/board`
- `GET /api/v1/properties/{propertyId}/housekeeping/staff`
- `POST /api/v1/properties/{propertyId}/housekeeping/tasks; GET …/tasks/{id}`
- `POST /api/v1/properties/{propertyId}/housekeeping/tasks/{id}/assign | start | finish | inspect`
- `GET|POST /api/v1/properties/{propertyId}/housekeeping/checklists; PUT …/checklists/{id}`
- `GET|POST /api/v1/properties/{propertyId}/maintenance/tickets; GET …/tickets/{id}`
- `POST /api/v1/properties/{propertyId}/maintenance/tickets/{id}/assign | progress | resolve | close | comments`
- `POST /api/v1/properties/{propertyId}/maintenance/tickets/{id}/photos; GET …/photos/{photoId}`
- `GET /api/v1/properties/{propertyId}/maintenance/staff; GET|POST …/assets; PUT …/assets/{id}`

**Screens / routes**

- `/housekeeping`
- `/housekeeping/checklists`
- `/maintenance`
- `/maintenance/new`
- `/maintenance/:id`
- `/rooms/status`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] States DIRTY→CLEANING→CLEAN→INSPECTION→READY, OUT_OF_ORDER; check-out event creates the task
- [x] Out-of-order ticket creates a unit block automatically
- [x] Photo upload via IFileStore (compressed on device)
- [x] Housekeeping dashboard (brief §53) usable one-handed on a 360 px phone

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S13-<issue-number>-<short-name>`
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

- A room that is dirty but has no task: database/queries/check-sprint-13.sql block 7 lists them (there should be none); block 8 lists state changes never turned into a task.
- An out-of-order room that can't be sold: its block is released only when the ticket is resolved (block 10 checks they agree).

## How to test

Required tests for this sprint (the sprint is not done without them):

- Integration — checkout → DIRTY task created once (idempotent on retry)
- Frontend — housekeeping flow on mobile viewport (Playwright)

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Housekeeper finishes a room in 2 taps; supervisor marks READY

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Checkout → task → clean → inspect → READY → room available

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-13` created on `main`
