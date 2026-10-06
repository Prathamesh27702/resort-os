# Sprint 09 — GST configuration and tax engine

| Status | Phase | Size | Depends on |
|---|---|---|---|
| ⚪ Planned | Phase 3 — Folio, payments, Razorpay, GST, invoices | M | [Sprint 08](sprint-08-folio.md) |

**Goal:** GST as a first-class, versioned, configurable domain (brief §23, §86).

**GitHub:** milestone *Sprint 09 — GST configuration and tax engine* · branch prefix `feature/S09-`

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
- `POST /api/v1/tax/calculate  (preview)`

**Screens / routes**

- `/settings/gst`

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [ ] No GST percentage in code. Rules keyed by HSN/SAC category, effective dates and (for accommodation) value slabs
- [ ] CGST+SGST when place of supply = supplier state, else IGST
- [ ] Every folio_item_tax row stores the rule id + rate used (historical invoices never change)
- [ ] Seed: current accommodation and restaurant rules as DATA, with source and effective date recorded; owner/accountant must verify before go-live
- [ ] Docs: GST.md worked examples

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S09-<issue-number>-<short-name>`
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

- Tax is calculated on taxable value after discount; check discount allocation first when totals differ by a few paise.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Unit — slab boundaries (value exactly at threshold)
- Unit — intra- vs inter-state split, rounding per line
- Integration — changing a rule doesn't change an already-posted charge's tax

Commands:

```bash
scripts/test.sh                                   # everything (backend + frontend)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

## Acceptance criteria

- Accountant can add a new rule effective next month without a deployment

## Sprint review demo

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
