# Sprint 08 — Folio

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 3 — Folio, payments, Razorpay, GST, invoices | M | [Sprint 07](sprint-07-front-desk.md) |

**Goal:** The folio is the central financial object for a stay (brief §20).

**GitHub:** milestone *Sprint 08 — Folio* · branch prefix `feature/S08-`

## Where this sprint stands

**Now:** Built on branch claude/sprint-08-2574ky; pull request into develop, waiting for the owner's review.

**Done so far**

- Migration V010: folios (one main folio per confirmed booking, F00001 … per property, more to split a bill), folio_items (immutable CHARGE / REVERSAL lines, 8 categories, source MANUAL / ROOM_NIGHT / PACKAGE) and folio_item_taxes (filled from Sprint 9); UPDATE and DELETE refused by triggers
- Database guards: amount = quantity × price and total = amount + tax (CHECK); a reversal is the exact negative of a charge on the same folio (trigger); a line is reversed or moved once (UNIQUE); each night of a stay is charged once (partial UNIQUE index)
- Main folio opened when a booking becomes CONFIRMED; fn_open_missing_folios() opens them for existing bookings
- Room nights posted at check-in for the stay (folio.room_charge_posting = AT_CHECK_IN, default) or night by night (NIGHTLY) by a background job every 15 minutes; check-out posts missing nights and reverses the nights released by an early departure before checking the balance
- Post a charge (Idempotency-Key) or a package as one line per component; reverse with folio.adjust and a reason (409 ALREADY_REVERSED names who and when); move lines to another or a new folio (reversal here, copy there); FOLIO_CLOSED for bookings that don't take charges
- The front desk's balance and the check-out rule now use the real folio balance
- Screens: Folios (/folios: search, in house / arriving / all), the bill (/folios/:id: balance, sums by category, every line, reversed lines struck through with the reason, Post charge, Reverse, Move lines), Bill buttons on the booking page and the front desk
- Dev seed S007: main folios, R00008 room nights + airport pick-up + dinner, R00007 room night + laundry; the demo property allows check-out with a balance until payments (Sprint 10)
- System design guide: Sprint 08 section, part 4.18 Folios, folio flow diagram

**Files added / changed**

- database/migrations/V010__folio.sql; database/seed/dev/S007__folios.sql; database/queries/check-sprint-08.sql
- backend/Resort.Domain/Folios/Folios.cs
- backend/Resort.Contracts/FolioContracts.cs
- backend/Resort.Application/ — Folios/FolioService.cs, Folios/FolioPosting.cs (shared posting steps, FolioBalances, RoomChargeRun), Validation/FolioValidators.cs, FrontDesk/FrontDeskService.cs, FrontDesk/FolioBalances.cs, Reservations/ReservationService.cs, Common/Permissions.cs, Common/Abstractions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/Endpoints/FolioEndpoints.cs, Infrastructure/RoomChargeJob.cs + Program.cs
- backend/Resort.Tests/ — Integration/FolioTests.cs, Unit/FolioUnitTests.cs, Support/FolioFactory.cs, Support/ResortApiFactory.cs, Integration/DevSeedTests.cs
- frontend/src/app/ — features/folios/ (folios.page.ts, folio.page.ts, folio-dialogs.ts, folios.api.ts, folios.spec.ts), features/reservations/reservation.page.ts, features/front-desk/front-desk.page.ts, core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-08.spec.ts (and sprint-07.spec.ts S07-03); scripts/check-api.mjs (Sprint 08 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-08.md, docs/architecture/system-design.md, lifecycles.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-08-screens/; tools/handbooks/diagrams (folio-flow)

**Test results**

- Backend: 232 passed, 0 failed on real PostgreSQL 16 (22 new: FolioTests with the brief's ₹10,950 example, DELETE/UPDATE refused by the database, reversal permission and reason, the same charge reversed by 10 pairs of simultaneous clicks, idempotent posting, field errors, package lines and split, folio at confirmation and FOLIO_CLOSED, nightly posting with a pinned clock, early departure, permissions and tenancy; FolioUnitTests; the folio dev seed check)
- Frontend: 63 of 63 unit tests passed (4 new for the folio helpers); ESLint clean; production build OK
- Browser tests: 76 of 76 passed (Sprints 01–07 plus the 7 new Sprint 08 steps: find a bill, post three charges with a field error, reverse with a reason as a manager only, move a line to a new folio, the Bill button on the front desk, permissions, phone); API checks 100 of 100 (11 new); database checks clean; screenshots in docs/sprints/sprint-08-screens

**Outcome**

Expected when accepted:

- Folio shows charges, reversals and balance; nothing can be deleted

## Scope

**Tables**

- `folios`
- `folio_items`
- `folio_item_taxes`

**API endpoints**

- `GET /api/v1/properties/{propertyId}/folios?q&show`
- `GET /api/v1/properties/{propertyId}/reservations/{reservationId}/folios`
- `GET /api/v1/properties/{propertyId}/folios/{id}`
- `POST /api/v1/properties/{propertyId}/folios/{id}/charges  (Idempotency-Key)`
- `POST /api/v1/properties/{propertyId}/folios/{id}/charges/{itemId}/reverse  (reason required)`
- `POST /api/v1/properties/{propertyId}/folios/{id}/split`

**Screens / routes**

- `/folios`
- `/folios/:id`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] Folio created at reservation confirmation; room charges posted nightly (job) or at check-in for the stay
- [x] Charges are immutable; corrections are reversal lines (brief §85)
- [x] Charge categories: ROOM, FOOD, BEVERAGE, ACTIVITY, TRANSPORT, EQUIPMENT, LAUNDRY, OTHER
- [x] Package charges split by package_components

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S08-<issue-number>-<short-name>`
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

- A night charged twice is impossible (ux_folio_items_room_night); if a night is missing, check the booking is CHECKED_IN and folio.room_charge_posting, then run check-sprint-08.sql block 5 for that reference.
- 'Rows in folio_items cannot be changed' from the database is the immutability trigger working: correct a line with a reversal, never an UPDATE.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Integration — brief §20 example totals ₹10,950
- Integration — DELETE on folio_items rejected by DB trigger
- Integration — reversal requires folio.adjust and reason

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Folio shows charges, reversals and balance; nothing can be deleted

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Post restaurant/transport/activity charges → reverse one → balance

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-08` created on `main`
