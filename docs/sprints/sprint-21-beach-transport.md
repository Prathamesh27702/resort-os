# Sprint 21 — Beach transport

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🟢 Done | Phase 7 — Activities, transport, equipment | S | [Sprint 20](sprint-20-activities-and-equipment.md) |

**Goal:** Vehicles, drivers, trips, capacity and manifests for the 8 km beach run (brief §47).

**GitHub:** milestone *Sprint 21 — Beach transport* · branch prefix `feature/S21-`

## Where this sprint stands

**Now:** Accepted by the owner on 5 Nov 2026 (PR #168), after fixing the trip rows and the driver's card from the owner's screenshots. Defaults kept: ₹500 a seat one way and children pay the same; a no-show keeps the charge; a cancelled trip refunds everyone and moving is done by booking again; the driver can mark a trip as left from its day; drivers are staff accounts with the Driver role (the demo data has none); visitors ride only free or complimentary; no GST on transport.

**Done so far**

- Migration V023: vehicles (registration unique in the organisation), transport_routes, vehicle_trips (seats_booked kept from the passengers by a trigger and a CHECK that it never passes the seats; EXCLUDE so a vehicle or a driver is on one trip at a time; forward-only moves checked by a trigger) and trip_passengers (TP00001…; on the bill, free or complimentary; booked, boarded, no-show or cancelled with the charge reversed); folio item source TRANSPORT
- Booking locks the trip row first (the Sprint 05 approach), so the last seat goes to one person; a full trip says so (409 TRIP_FULL with the seats left); an Idempotency-Key makes a double tap book once
- A staying guest's seats go on their bill as one Transport charge (₹500 a seat on the demo route, no GST); cancelling a seat or the whole trip reverses it; a no-show keeps it
- The driver's phone view (My trips): today's own trips with the passengers, a big tick box as each boards, Left (the rest become no-shows) and Arrived; a driver runs only their own trips
- Passenger manifest PDF (manifest-BEACH-20261103-0800.pdf) with room, seats, note and a box to tick
- Putting trips on for a range of days and times with clashes skipped and named; editing a trip's vehicle, driver, seats and price within the vehicle's seats and the seats booked
- Screen /transport (Trips, My trips, Vehicles and routes) on a 360 px phone
- Dev seed S018: a 12-seat van and a 7-seat jeep, the beach shuttle and its return, trips today and 7 days ahead, tomorrow's agent group on the 08:00
- [done] Docs: API.md, DATABASE.md, schema-sprint-21.md, system design Sprint 21 and 4.31 with the transport flow diagram, ARCHITECTURE.md, RUN_AND_TEST, REVIEWING, CHANGELOG

**Files added / changed**

- database/migrations/V023__transport.sql; database/seed/dev/S018__transport.sql; database/queries/check-sprint-21.sql
- backend/Resort.Domain/Transport/Transport.cs, Folios/Folios.cs; backend/Resort.Contracts/TransportContracts.cs
- backend/Resort.Application/ — Transport/ (TransportService.cs, TransportBookingService.cs), Validation/TransportValidators.cs, Common/Permissions.cs, Common/Abstractions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/ — Endpoints/TransportEndpoints.cs, Infrastructure/Problems.cs, Program.cs
- backend/Resort.Tests/ — Unit/TransportUnitTests.cs, Integration/TransportTests.cs, Integration/DevSeedTests.cs
- frontend/src/app/ — features/transport/ (transport.page.ts, transport-dialogs.ts, transport.api.ts, transport.spec.ts), features/invoices/invoices.api.ts, core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-21.spec.ts; scripts/check-api.mjs (Sprint 21 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-21.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-21-screens/; tools/handbooks/diagrams (transport-flow)

**Test results**

- Backend: 556 passed, 0 failed on real PostgreSQL 16 (12 new: the trip and passenger moves in code match the triggers; nothing moves back; a transfer booking posts ₹500 a seat to the folio and cancelling reverses it; the last seat booked twice at the same moment going to exactly one, 10 times; the database refusing an extra passenger over the seats or on a trip that left on its own; the driver seeing today's manifest, ticking who boarded and leaving with the rest as no-shows; a driver running only their own trips; trips put on with vehicle and driver clashes skipped; a cancelled trip refunding everyone; the manifest PDF; dev seed check). Release build with warnings as errors and dotnet format clean
- Frontend: 121 of 122 unit tests passed (3 new); the one failure is the calendar 200 ms timing test, which is slow only on the cloud machine; ESLint clean; production build OK
- Browser tests: Sprint 21's 4 steps pass: a transport manager adding a van (a wrong registration refused under the field) and putting a trip on with the driver, a receptionist on a 360 px phone booking 3 seats (₹1,500 on the bill), the manifest PDF downloaded, the driver on a phone seeing the passengers, ticking them on board and marking the trip as left; API checks 258 of 258 (8 new); database checks clean; screenshots in docs/sprints/sprint-21-screens

**Outcome**

Expected when accepted:

- Driver sees today's manifest

## Scope

**Tables**

- `vehicles`
- `transport_routes`
- `vehicle_trips`
- `trip_passengers`

**API endpoints**

- `/api/v1/properties/{propertyId}/transport/*`

**Screens / routes**

- `/transport`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] Trip capacity locking; driver view (DRIVER role) on phone
- [x] Charge ₹500 to folio (brief §73)
- [x] Passenger manifest PDF
- [x] Seed: sample vehicles

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S21-<issue-number>-<short-name>`
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

- Integration — transfer booking posts to folio

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Driver sees today's manifest

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Book transfer → manifest → driver marks boarded

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-21` created on `main`
