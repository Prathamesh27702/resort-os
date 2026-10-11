# Sprint 03 — Guests

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🟢 Done | Phase 2 — Guests, reservations, availability, calendar, front desk | M | [Sprint 01](sprint-01-foundation.md) |

**Goal:** Guest profiles with only the data that is needed, encrypted sensitive fields, de-duplication and retention.

**GitHub:** milestone *Sprint 03 — Guests* · branch prefix `feature/S03-`

## Where this sprint stands

**Now:** Accepted by the owner on 11 Oct 2026 after the review fixes in pull request [#105](https://github.com/Prathamesh27702/resort-os/pull/105) (Aadhaar number check, errors that say why). Merged to main and tagged `sprint-03`.

**Done so far**

- Migration V005: 5 tables with a data classification on every column; pg_trgm indexes for contains-search on name, email, company and phone digits; database guards (never delete guests, anonymised rows empty, Aadhaar never in full, consent history append-only)
- Field-level encryption (AES-256-GCM, HKDF-derived keys, key id per row, row-bound associated data) for ID numbers, document details and scans; key rotation design and steps in SECURITY.md
- File storage abstraction (IFileStore) with a local-disk implementation; scans checked by their bytes (JPEG/PNG/PDF), ≤ 5 MB, stored encrypted
- Guest APIs: search by any part of a phone number typed any way, or name/email/company; duplicate warning on create (phone, email, full name, same ID document); edit with optimistic concurrency; consent history; merge with reason; anonymise with reason; retention dry-run report
- Phone numbers normalised to E.164 (Indian numbers typed locally, international with + or 00)
- Audit trail names changed guest fields but never stores personal values; opening an ID number or scan is a SENSITIVE_READ audit row
- Dev seed S002: 20 invented guests (14 Indian, 6 international) with addresses, preferences and consents
- Angular screens: Guests list with search-as-you-type, add/edit guest with duplicate warning, GST state and preferences, guest profile with consent, stays and ID documents (only with guest_document.read), add-document dialog, merge dialog, anonymise

**Files added / changed**

- database/migrations/V005__guests.sql; database/seed/dev/S002__guests.sql; database/queries/check-sprint-03.sql
- backend/Resort.Domain/Guests/Guests.cs
- backend/Resort.Contracts/GuestContracts.cs
- backend/Resort.Application/ — Guests/GuestService.cs, Guests/GuestDocumentService.cs, Validation/GuestValidators.cs, Common/Protection.cs, Common/Permissions.cs, Common/Errors.cs
- backend/Resort.Infrastructure/ — Protection/FieldEncryption.cs, Protection/LocalDiskFileStore.cs, Persistence/AppDbContext.cs, Persistence/AuditInterceptor.cs (personal-data masking)
- backend/Resort.Api/ — Endpoints/GuestEndpoints.cs, Program.cs (key check at start-up), appsettings.json (Guests, FileStore)
- backend/Resort.Tests/ — Integration/GuestTests.cs, Unit/GuestUnitTests.cs, Support/ResortApiFactory.cs
- frontend/src/app/ — features/guests/*, core/models.ts, app.routes.ts, layout/shell.ts, features/audit (labels)
- frontend/e2e/sprint-03.spec.ts, e2e/helpers.ts (optional screenshots); scripts/check-api.mjs (Sprint 03 checks)
- docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-03.md, docs/SECURITY.md, docs/GETTING_STARTED.md, docs/sprints/REVIEWING.md, docs/testing/VERIFYING.md, sprint-03-screens/

**Test results**

- Backend: 102 passed, 0 failed on real PostgreSQL 16 (44 new: 13 integration, 31 unit including phone and encryption cases)
- Frontend: 20 of 20 unit tests passed; ESLint clean; production build OK
- Browser tests: 39 of 39 passed (Sprint 01 + 02 + the 13 new Sprint 03 review steps)
- Review fixes (#105): Aadhaar takes 12 digits or the last 4 with the reason under the field; first sign-in password page shows why a password is refused; API validation messages list the reasons instead of a generic line
- API checks: 46 of 46 passed on a fresh database (45 on later runs); SQL checks: all 14 Sprint 03 queries as expected

**Outcome**

Accepted 11 Oct 2026. On main as tag sprint-03. Sprint 04 (rate plans and packages) started.

## Scope

**Tables**

- `guests`
- `guest_addresses`
- `guest_documents`
- `guest_preferences`
- `guest_consents`

**Migrations**

- `V005__guests`

**API endpoints**

- `GET|POST /api/v1/guests`
- `GET /api/v1/guests/possible-duplicates`
- `GET|PUT /api/v1/guests/{id}`
- `GET /api/v1/guests/{id}/stays`
- `POST /api/v1/guests/{id}/consents`
- `POST /api/v1/guests/merge`
- `POST /api/v1/guests/{id}/anonymise`
- `GET /api/v1/guests/retention/dry-run`
- `GET|POST /api/v1/guests/{id}/documents`
- `GET /api/v1/guests/{id}/documents/{docId}`
- `GET /api/v1/guests/{id}/documents/{docId}/file`

**Screens / routes**

- `/guests`
- `/guests/new`
- `/guests/:id`
- `/guests/:id/edit`

**Other**

- Development seed: 20 demo guests (database/seed/dev/S002__guests.sql)
- Screenshots of the built screens: docs/sprints/sprint-03-screens/

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] DB: new migration — guest tables; data-classification comment on every column (public/internal/confidential/sensitive, brief §59)
- [x] Infra: field-level encryption (AES-GCM, key from configuration) for ID numbers and document metadata; key rotation design documented
- [x] Infra: blob storage abstraction (IFileStore) with local-disk dev implementation for ID scans
- [x] API: search by name/phone/email (trigram index), de-duplication hint on create, merge with audit
- [x] API: anonymise (guest.delete) replaces PII, keeps stay history for accounting
- [x] Job: retention dry-run report (what would be purged) — actual purge enabled only after owner approval
- [x] Seed: 20 realistic guests (Indian + international)
- [x] UI: guest list/search, profile, documents tab hidden without guest_document.read
- [ ] Review and sign-off by owner (open issues for any change requests)

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S03-<issue-number>-<short-name>`
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

- Lost the encryption key = lost ID data. Dev key lives in .env (Encryption__Key); never reuse across environments.
- API won't start: 'Encryption:Key (Encryption__Key) is not set' or 'must be 32 bytes' → run scripts/setup-dev, or set a base64 32-byte key.
- CryptographicException when opening a document: the key changed since it was saved. Put the old key back (or add it as Encryption__PreviousKeys__k1).
- 409 POSSIBLE_DUPLICATE is expected the first time; the UI's 'Create a new profile anyway' resends with ignoreDuplicates.
- No demo guests? The seed only fills an organisation with no guests. Reset the database and restart the API.
- Scans live in backend/Resort.Api/App_Data/files (encrypted). A database reset doesn't delete them.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Integration — WAITER and HOUSEKEEPER cannot read guest documents (403) — brief §59
- Integration — ID number stored encrypted (raw column is not plaintext)
- Integration — anonymise keeps stays, removes PII, audited with reason
- Unit — phone normalisation (+91 and international)
- Integration — returning guest found by any part of the phone, typed any way
- Integration — duplicate warning (409 POSSIBLE_DUPLICATE) until confirmed
- Integration — Aadhaar keeps only the last 4 digits
- Integration — scans: file type from the bytes, ≤ 5 MB, stored encrypted, every view audited
- Integration — merge moves documents, consents, preferences; duplicate hidden
- Integration — personal data never written to the audit trail; stale edit → 409; tenant isolation (404)
- Integration — retention dry run deletes nothing; database refuses deleting guests and rewriting consents
- Unit — AES-256-GCM: tampering and wrong row detected, rotation keeps old data readable, missing key fails fast

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Reception finds a returning guest by phone in one search

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Create guest → duplicate warning → upload ID → view as receptionist vs waiter

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-03` created on `main`
