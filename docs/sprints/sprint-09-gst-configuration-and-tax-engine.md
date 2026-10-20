# Sprint 09 — GST configuration and tax engine

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🔵 In progress — **current sprint** | Phase 3 — Folio, payments, Razorpay, GST, invoices | M | [Sprint 08](sprint-08-folio.md) |

**Goal:** GST as a first-class, versioned, configurable domain (brief §23, §86).

**GitHub:** milestone *Sprint 09 — GST configuration and tax engine* · branch prefix `feature/S09-`

## Where this sprint stands

**Now:** Built and in review: pull request into develop, waiting for the owner's acceptance.

**Done so far**

- Migration V011: gst_registrations (GSTIN per property with dates, REGULAR or COMPOSITION), tax_categories (SAC/HSN, the folio charge category covered), tax_rules (dates, FLAT or SLAB, source, verified by) and tax_rule_slabs (up to, rate, input tax credit); EXCLUDE constraints stop overlapping periods; rules and slabs never deleted, slabs never changed, a rule changes only its end date and verification (trigger)
- Seeded as data for every organisation (and every new one, by trigger), unverified: Accommodation 996311 (18 Jul 2022 – 21 Sep 2025 up to ₹7,500 12%, above 18%; from 22 Sep 2025 up to ₹7,500 5% without ITC, above 18%) and Restaurant service 996331 (5% from 15 Nov 2017), each with its notification
- One tax engine (TaxCalculator): the registration, category and rule of the service day; slab by the value of one unit for one night; CGST + SGST inside the state, IGST across; rounded per tax to the paisa; no GST without a registration, under composition or for an untaxed category; 422 TAX_RULE_MISSING when a taxed charge has no rule
- Every new folio charge (room nights room by room, manual charges, package parts) stores its tax rows with the rule id and rate; reversals and moved lines copy them
- API: registrations, categories, rules (start today or later; the running rule is ended the day before; other overlaps refused under the field), Verify, and the calculate preview; tax.read / tax.manage
- Screens: GST (/settings/gst: registration, what is taxed with every rule and its source, Add registration / category / rule, Verify, Try a charge); the bill shows amount + GST on each line
- Dev seed S008: the demo property registered in Goa (made-up GSTIN 30AABCT1234C1Z9)
- [done] Docs: GST.md worked examples, schema-sprint-09.md, API.md, system design 4.19 and the tax flow diagram, RUN_AND_TEST, REVIEWING

**Files added / changed**

- database/migrations/V011__tax.sql; database/seed/dev/S008__gst.sql; database/queries/check-sprint-09.sql
- backend/Resort.Domain/Tax/Tax.cs
- backend/Resort.Contracts/TaxContracts.cs, FolioContracts.cs
- backend/Resort.Application/ — Tax/TaxCalculator.cs, Tax/TaxService.cs, Validation/TaxValidators.cs, Folios/FolioPosting.cs, Folios/FolioService.cs, Common/Permissions.cs, Common/Abstractions.cs, DependencyInjection.cs
- backend/Resort.Infrastructure/Persistence/AppDbContext.cs; backend/Resort.Api/Endpoints/TaxEndpoints.cs + Program.cs
- backend/Resort.Tests/ — Integration/TaxTests.cs, Unit/TaxUnitTests.cs, Integration/DevSeedTests.cs
- frontend/src/app/ — features/settings/gst/ (gst.page.ts, gst-dialogs.ts, gst.api.ts, gst.spec.ts), features/folios/folio.page.ts, folio-dialogs.ts, core/models.ts, app.routes.ts, layout/shell.ts
- frontend/e2e/sprint-09.spec.ts (and sprint-08.spec.ts: dinner now carries 5% GST); scripts/check-api.mjs (Sprint 09 checks)
- docs/GST.md, docs/API.md, docs/DATABASE.md, docs/database/schema-sprint-09.md, docs/architecture/system-design.md, docs/ARCHITECTURE.md, docs/RUN_AND_TEST.md, docs/sprints/REVIEWING.md, sprint-09-screens/; tools/handbooks/diagrams (tax-flow)

**Test results**

- Backend: 278 passed, 0 failed on real PostgreSQL 16 (46 new: TaxTests with the same room night at 12% and 5% either side of 22 Sep 2025, a rule change that leaves a posted charge and its reversal alone, the accountant's next-month rule closing the running one, IGST, unregistered and composition properties, rooms of one night in different slabs, packages, TAX_RULE_MISSING, field errors, permissions and tenancy; TaxUnitTests with the ₹7,500.00 / ₹7,500.01 boundary, rounding per tax and GSTIN check digits; the GST dev seed check)
- Frontend: 67 of 67 unit tests passed (4 new for the GST helpers); ESLint clean; production build OK
- Browser tests: 83 of 83 passed (Sprints 01–08 plus the 7 new Sprint 09 steps: the GST page, the same charge before and after the rule change date, next month's rule with field errors, Verify, GST on a bill, a wrong GSTIN and permissions, phone); API checks 114 of 114 (14 new); database checks clean; screenshots in docs/sprints/sprint-09-screens

**Outcome**

Expected when accepted:

- Accountant can add a new rule effective next month without a deployment

## Scope

**Tables**

- `gst_registrations`
- `tax_categories`
- `tax_rules`
- `tax_rule_slabs`

**API endpoints**

- `GET|POST /api/v1/properties/{propertyId}/gst-registrations`
- `GET|POST /api/v1/tax/categories`
- `GET|POST /api/v1/tax/rules  (effective_from/effective_to)`
- `POST /api/v1/tax/rules/{id}/verify`
- `POST /api/v1/tax/calculate  (preview)`

**Screens / routes**

- `/settings/gst`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] No GST percentage in code. Rules keyed by HSN/SAC category, effective dates and (for accommodation) value slabs
- [x] CGST+SGST when place of supply = supplier state, else IGST
- [x] Every folio_item_tax row stores the rule id + rate used (historical invoices never change)
- [x] Seed: current accommodation and restaurant rules as DATA, with source and effective date recorded; owner/accountant must verify before go-live
- [x] Docs: GST.md worked examples

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S09-<issue-number>-<short-name>`
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

- Tax is calculated on taxable value after discount; check discount allocation first when totals differ by a few paise.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Unit — slab boundaries (value exactly at threshold)
- Unit — intra- vs inter-state split, rounding per line
- Integration — changing a rule doesn't change an already-posted charge's tax

Commands:

```bash
pwsh -File scripts/test.ps1                       # everything, Windows  (macOS/Linux: bash scripts/test.sh)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

Results: CI shows *Backend tests: N passed, 0 failed* on every PR (Checks → Backend → annotations).

## Acceptance criteria

- Accountant can add a new rule effective next month without a deployment

## Sprint review demo

The owner runs this during review, following [REVIEWING.md](REVIEWING.md):

- Same charge before/after a rule change date

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-09` created on `main`
