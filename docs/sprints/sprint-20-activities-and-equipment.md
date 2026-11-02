# Sprint 20 — Activities and equipment

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🟢 Done | Phase 7 — Activities, transport, equipment | M | [Sprint 08](sprint-08-folio.md) |

**Goal:** Capacity-safe activity bookings and equipment issue/return (brief §48, §49).

**GitHub:** milestone *Sprint 20 — Activities and equipment* · branch prefix `feature/S20-`

## Where this sprint stands

**Now:** Accepted by the owner on 2 Nov 2026 (PR #164). Defaults kept: paid activities only on a staying guest's bill, cancelling until the start with no fee, weather refund on the bill or a move within 14 days, no automatic damage or loss charge, equipment due back at the session's end.

**Done so far**

- Migration V022: activities, activity_schedules (seats_booked kept from the bookings by a trigger, a CHECK that it never passes the seats), activity_bookings (AB00001…; on the bill, free or complimentary; cancelled by the guest or the weather with the charge reversed or moved), equipment_types, equipment (tagged items; a trigger allows only the listed status moves), equipment_assignments (one open assignment per item); folio item source ACTIVITY
- Booking locks the session row first (the Sprint 05 approach), so the last seat goes to one person; a full session says so (409 ACTIVITY_FULL with the seats left); an Idempotency-Key makes a double tap book once
- A staying guest's booking goes on their bill as one Activity charge; cancelling before the start reverses it; visitors only on free sessions or complimentary with a reason
- Weather cancellation: one reason, each booking refunded on the bill or moved to another session of the same activity within 14 days with its charge; kept-aside equipment released
- Equipment: issue a number of one kind (first free by tag) for a booking or anyone by name, or keep aside and hand over later; return good, damaged or lost with what happened; maintenance and back into use; add kinds and numbered items
- Screens /activities (the day's sessions with seats left, bookings, Book, Equipment, Cancel, Cancel for weather, Put sessions on, the list of activities) and /equipment (out now with overdue first, items by kind, issue and return), both on a 360 px phone
- Dev seed S017: kayaking, sunrise yoga, snorkelling, a village cycle tour with sessions today and 7 days ahead; life jackets, kayaks, snorkel sets, bicycles with one each out of use; tomorrow's agent group on the 07:00 kayaking
- [done] Docs: API.md, DATABASE.md, schema-sprint-20.md, system design Sprint 20 and 4.30 with the activity flow diagram, ARCHITECTURE.md, RUN_AND_TEST, REVIEWING, CHANGELOG

**Files added / changed**

- database/migrations/V022__activities.sql; database/seed/dev/S017__activities.sql; database/queries/check-sprint-20.sql
- backend/Resort.Domain/Activities/ (Activities.cs, Equipment.cs), Folios/Folios.cs; backend/Resort.Contracts/ActivityContracts.cs
- backend/Resort.Application/ — Activities/ (ActivityService.cs, ActivityBookingService.cs, EquipmentService.cs), Validation/ActivityValidators.cs, Common/Permissions.cs, Common/Abstractions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/ — Endpoints/ActivityEndpoints.cs, Infrastructure/Problems.cs, Program.cs
- backend/Resort.Tests/ — Unit/ActivityUnitTests.cs, Integration/ActivitiesTests.cs, Integration/DevSeedTests.cs
- frontend/src/app/ — features/activities/ (activities.page.ts, equipment.page.ts, activity-dialogs.ts, equipment-dialogs.ts, activities.api.ts, activities.spec.ts), core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-20.spec.ts; scripts/check-api.mjs (Sprint 20 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-20.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-20-screens/; tools/handbooks/diagrams (activity-flow)

**Test results**

- Backend: 544 passed, 0 failed on real PostgreSQL 16 (19 new: the equipment moves in code match the trigger; allowed and refused moves; kayaking at 7am with 6 seats not oversold and the price on the bill; the last kayak seat booked twice at the same moment going to exactly one, 10 times; the database refusing an oversell or a booking on a cancelled session on its own; cancelling before and after the start; the weather refunding one booking and moving another with reserved kit released; life jackets issued, one back damaged, then maintenance; bookings needing a stay for a paid session and a retry replaying; putting sessions on; dev seed check). Release build with warnings as errors and dotnet format clean
- Frontend: 119 of 119 unit tests passed (3 new); ESLint clean; production build OK
- Browser tests: Sprint 20's 5 steps pass (run twice in a row): an activity manager adding kayaking (0 seats refused under the field) and putting sessions on, a receptionist on a 360 px phone booking 4 seats for a staying guest, the session filled and a 7th seat refused with ACTIVITY_FULL, 4 life jackets issued, one back good and one damaged (the reason required), the weather cancelling 07:00 with one refund and one move, the damaged jacket to maintenance; API checks 250 of 250 (8 new); database checks clean; screenshots in docs/sprints/sprint-20-screens

**Outcome**

Expected when accepted:

- Kayaking at 7am with 6 seats cannot be oversold

## Scope

**Tables**

- `activities`
- `activity_schedules`
- `activity_bookings`
- `equipment_types`
- `equipment`
- `equipment_assignments`

**API endpoints**

- `/api/v1/properties/{propertyId}/activities/*, /equipment/*`

**Screens / routes**

- `/activities`
- `/equipment`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] Capacity enforced with the same locking approach as Sprint 5
- [x] Weather cancellation flow with refunds/credits
- [x] Equipment states AVAILABLE→RESERVED→ISSUED→RETURNED, DAMAGED, MAINTENANCE, LOST
- [x] Seed: sample activities and equipment

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S20-<issue-number>-<short-name>`
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

- Seats look wrong: check-sprint-20.sql block 5 lists any session whose seats_booked differs from its bookings or passes the seats (should be none); seats_booked is never set by hand, only by trg_activity_bookings_seats.
- A booking refused as full though a seat seems free: another booking took it a moment before (the session row lock queues them); reload the day. ACTIVITY_FULL says how many seats are left.
- A charge missing or doubled on the bill: block 7 lists room-charge bookings whose charge or reversal doesn't match (should be none); a charge reversed by hand on the bill is reused when the booking is cancelled.
- An item can't be changed: reserved or issued items are moved only by Return, Hand over or Release (422 EQUIPMENT_OUT); block 11 lists items whose status disagrees with their open assignment (should be none).

## How to test

Required tests for this sprint (the sprint is not done without them):

- Concurrency — last kayak seat booked twice → one wins

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Kayaking at 7am with 6 seats cannot be oversold

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Book activity → issue life jackets → return one damaged

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-20` created on `main`
