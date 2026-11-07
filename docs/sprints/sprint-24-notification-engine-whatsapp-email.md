# Sprint 24 — Notification engine, WhatsApp, email, background jobs

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🟢 Done | Phase 9 — Notifications, WhatsApp, Zoho Books, OTA, booking site | L | [Sprint 05](sprint-05-availability-engine-and-reservations.md) |

**Goal:** Configurable templates, delivery tracking and idempotent scheduled jobs (brief §32, §69, §70).

**GitHub:** milestone *Sprint 24 — Notification engine, WhatsApp, email, background jobs* · branch prefix `feature/S24-`

## Where this sprint stands

**Now:** Accepted by the owner on 7 Nov 2026 (PR #178). Defaults kept: booking messages go on WhatsApp and by email unless the guest said No on their profile (no confirmation for walk-ins); job times reminder 10:00, pre-arrival 10:00 three days ahead, check-out 08:00, payment 11:00 (once per link, a day after it was made), low stock 08:00 and leave 09:00 (one digest a day to the people who can act); 5 tries at 1, 5, 15 and 60 minutes; templates and job times changed by the owner only (integration.manage), Send again and Run now by managers and receptionists; Resend for email; guest replies counted, not stored; the built-in test WhatsApp and email without keys, and in production without keys messages fail with the reason.

**Done so far**

- Migration V026: message_templates, outbox_messages (unique key per property, the guard trigger), whatsapp_messages, email_messages, notifications, scheduled_jobs, job_runs (unique property + job + business key, append-only); WHATSAPP webhooks; consents SERVICE_WHATSAPP and SERVICE_EMAIL
- Outbox: the booking confirmation is written in the booking's transaction (booked confirmed, confirmed by staff, paid online); a sender every 10 s claims rows with FOR UPDATE SKIP LOCKED; passing failures tried again 1, 5, 15, 60 min, at most 5 tries
- Scheduler in PostgreSQL (no Redis): scheduled_jobs claimed by compare-and-set; six idempotent jobs (day-before reminder, pre-arrival, check-out, payment, low stock, leave) through job_runs and an advisory lock per item
- WhatsApp Cloud API template messages with named parameters; status webhook signed with the app secret, each status once, forward only; Meta's URL check
- Email through Resend with the outbox id as the idempotency key; in-app notifications and the bell
- Built-in test WhatsApp and email (no keys, never in production) with Delivered / Read / Failed buttons through the real webhook
- Screens: /messages (messages with reasons and Send again, templates with variables and preview, daily jobs with Run now), the bell, the booking's messages card, booking-message consent on the guest; 360 px phone
- Dev seed S021: five WhatsApp confirmations as WhatsApp reported them and a line in the owner's bell
- [done] Docs: WHATSAPP.md, API.md, DATABASE.md, schema-sprint-24.md, system design Sprint 24 and 4.34 with the messages diagram, ARCHITECTURE.md, RUN_AND_TEST, REVIEWING, CHANGELOG

**Files added / changed**

- database/migrations/V026__notifications.sql; database/seed/dev/S021__messages.sql; database/queries/check-sprint-24.sql
- backend/Resort.Domain/Notifications/Notifications.cs; backend/Resort.Contracts/NotificationContracts.cs
- backend/Resort.Application/ — Notifications/ (NotificationCatalog, Outbox, OutboxDispatcher, NotificationJobs, WhatsAppWebhookService, MessageService, MessageProviders), Validation/NotificationValidators.cs, Reservations/ReservationService.cs, Guests/GuestService.cs, Common/Permissions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Notifications/ (WhatsAppCloudClient, FakeWhatsAppServer, ResendEmailClient); DependencyInjection.cs; Persistence/AppDbContext.cs
- backend/Resort.Api/ — Endpoints/NotificationsEndpoints.cs, Infrastructure/NotificationJobsHost.cs, Program.cs
- backend/Resort.Tests/ — Integration/NotificationsTests.cs, Unit/NotificationUnitTests.cs, Support/NotificationsFactory.cs, Integration/DevSeedTests.cs
- frontend/src/app/ — features/messages/ (messages.page.ts, message-dialogs.ts, booking-messages-panel.ts, messages.api.ts, messages.spec.ts), layout/bell.ts, layout/shell.ts, features/reservations/reservation.page.ts, features/guests/guest.page.ts, core/models.ts, app.routes.ts
- frontend/e2e/sprint-24.spec.ts, frontend/e2e/start-api.mjs; scripts/check-api.mjs (Sprint 24 checks); .env.example
- docs/WHATSAPP.md, docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-24.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-24-screens/; tools/handbooks/diagrams (message-flow)

**Test results**

- Backend: 618 passed, 0 failed on real PostgreSQL 16 (the acceptance: a guest booked for tomorrow gets the WhatsApp and email confirmation and the day-before reminder; the required job run twice, and twice at the same moment, sends each guest one reminder; the required WhatsApp status webhook refused with a wrong signature, each status once, only forward; the scheduler runs a due job once and moves it to tomorrow; no number, no email, opted out and template off say why; passing failures tried again, then failed; template field errors; payment, check-out and low stock jobs and the bell; anonymising blanks messages; another tenant sees nothing). Release build with warnings as errors and dotnet format clean
- Frontend: 135 of 135 unit tests passed (4 new); ESLint clean; production build OK
- Browser tests: Sprint 24's 8 steps pass (a confirmed booking's WhatsApp and email confirmation on the booking page; WhatsApp says delivered then read; the day-before reminder run twice sends once; the seeded failed message says why and is sent again; a template with an unknown variable refused under the text; a job moved to 9:30 and back; the owner's bell; the Messages page on a 360 px phone). Full run: 180 passed, 0 failed (27.6 min). API checks 291 of 291 (12 new); database checks clean; screenshots in docs/sprints/sprint-24-screens

**Outcome**

Expected when accepted:

- Guest gets confirmation + day-before reminder (brief §73)

## Scope

**Tables**

- `message_templates`
- `outbox_messages`
- `notifications`
- `whatsapp_messages`
- `email_messages`
- `scheduled_jobs`
- `job_runs`

**API endpoints**

- `/api/v1/webhooks/whatsapp`
- `/api/v1/properties/{propertyId}/messages/*, /message-templates/*, /jobs/*`
- `/api/v1/notifications/*`

**Screens / routes**

- `/messages`
- `the bell in the header`
- `/reservations/{id} (messages to the guest)`

**External APIs used**

- WhatsApp Business Platform (Cloud API)
- Resend (transactional email)

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] Outbox pattern: business transaction writes outbox row; background worker sends
- [x] Background jobs with PostgreSQL-backed scheduler (no Redis — brief §76); every job idempotent
- [x] Jobs: reservation reminder, pre-arrival, checkout reminder, payment reminder, inventory alert, leave reminder
- [x] WhatsApp template messages; status webhooks (sent/delivered/read/failed)

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S24-<issue-number>-<short-name>`
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

- Integration — job run twice sends once
- Integration — WhatsApp status webhook signature check

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Guest gets confirmation + day-before reminder (brief §73)

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Book → WhatsApp confirmation → reminder job

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-24` created on `main`
