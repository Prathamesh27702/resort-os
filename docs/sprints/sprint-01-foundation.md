# Sprint 01 — Foundation — identity, RBAC, organisation, properties, audit

| Status | Phase | Size | Depends on |
|---|---|---|---|
| 🟡 In review — **current sprint** | Phase 1 — Foundation, property, rooms, beds | L | [Sprint 00](sprint-00-architecture-and-requirements.md) |

**Goal:** A secure, multi-tenant base that every later module plugs into.

**GitHub:** milestone *Sprint 01 — Foundation — identity, RBAC, organisation, properties, audit* · branch prefix `feature/S01-`

## Scope

**Tables**

- `organizations`
- `properties`
- `property_settings`
- `system_settings`
- `users`
- `roles`
- `permissions`
- `role_permissions`
- `user_roles`
- `refresh_tokens`
- `login_events`
- `audit_logs`
- `schema_migrations`

**Migrations**

- `V001__foundation`
- `V002__seed_permissions_roles`
- `V003__audit_request_context`

**API endpoints**

- `POST /api/v1/auth/login`
- `POST /api/v1/auth/refresh`
- `POST /api/v1/auth/logout`
- `GET /api/v1/auth/me`
- `POST /api/v1/auth/change-password`
- `GET|PUT /api/v1/organization`
- `GET|POST /api/v1/properties`
- `GET|PUT /api/v1/properties/{propertyId}`
- `GET /api/v1/properties/{propertyId}/settings`
- `PUT /api/v1/properties/{propertyId}/settings/{key}`
- `GET|POST /api/v1/users`
- `GET|PUT /api/v1/users/{id}`
- `PUT /api/v1/users/{id}/roles`
- `POST /api/v1/users/{id}/deactivate | activate | reset-password`
- `GET /api/v1/roles`
- `GET /api/v1/permissions`
- `GET /api/v1/audit-logs`

**Screens / routes**

- `/login`
- `/dashboard`
- `/account/password`
- `/settings/properties`
- `/users`
- `/roles`
- `/audit`

**Other**

- Screenshots of the built screens: docs/sprints/sprint-01-screens/

Full endpoint specs go in [API.md](../API.md); tables in [DATABASE.md](../DATABASE.md). Update both in this sprint.

## Tasks

Each task becomes a GitHub issue under this sprint's milestone.

- [x] Modular-monolith solution, SQL migrator with checksum guard
- [x] JWT access tokens + rotating refresh cookie with reuse detection
- [x] Permission-based authorisation with property-scoped grants and escalation guard
- [x] Organisation/property/settings APIs with optimistic concurrency
- [x] User administration APIs (create, roles, deactivate, reset)
- [x] Audit interceptor + append-only triggers + audit query API
- [x] Standard error shape, correlation/request IDs, /api/v1 prefix
- [x] Angular shell, login, properties, users, roles matrix, audit screens
- [x] 30 backend + 6 frontend tests
- [ ] Review and sign-off by owner (open issues for any change requests)

## How to run this sprint

1. **Start from the latest `develop`:** `git switch develop && git pull`
2. **One branch per issue:** `git switch -c feature/S01-<issue-number>-<short-name>`
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

- 401 on every call after a password change is expected: the security stamp rotated. Sign in again.
- 403 PASSWORD_CHANGE_REQUIRED: the account still has a temporary password.
- Access is cached for 30 s per user; restart the API if you edit user_roles directly in SQL.

## How to test

Required tests for this sprint (the sprint is not done without them):

- Login, lockout, same error for unknown email vs wrong password
- Refresh rotation and reuse detection revoking the family
- Permission denial (403) and property scoping
- Tenant isolation (404 across tenants)
- Escalation guard (GM cannot grant OWNER)
- Optimistic concurrency (409)
- Audit with masked secrets, reason, device, request id; append-only at DB level
- Migrator idempotent; edited migration refused

Commands:

```bash
scripts/test.sh                                   # everything (backend + frontend)
dotnet test --filter "FullyQualifiedName~Integration"   # backend integration only
cd frontend && npm run test:ci                    # frontend unit tests
```

Rules: integration tests use a real PostgreSQL (each test class gets its own database); no in-memory EF provider; concurrency tests use `Task.WhenAll` with a barrier, never `Thread.Sleep`. See [TESTING.md](../TESTING.md).

## Acceptance criteria

- Owner signs in, creates a property, creates a receptionist limited to one property, and sees both actions in the audit trail
- Receptionist sees only Today and Properties; /users redirects
- Deactivating a user signs them out immediately

## Sprint review demo

- Sign in as owner → Today checklist → edit property → add staff → roles matrix → audit trail

## Definition of Done

- [ ] All tasks merged to `develop` through reviewed PRs with green CI
- [ ] Required tests above exist and pass in CI
- [ ] Migrations applied cleanly to an empty database and to the previous sprint's database
- [ ] Every state-changing endpoint has a permission check, validation, audit entry and (where money/booking) idempotency
- [ ] API.md, DATABASE.md and this guide updated; new permissions added to the matrix
- [ ] Dev seed data extended for the new module
- [ ] Demo given to the product owner; feedback captured as issues
- [ ] `docs/sprints/sprints.yml` status updated; release tag `sprint-01` created on `main`
