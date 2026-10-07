# Changelog

One entry per accepted sprint, tagged `sprint-NN` on `main`. Format: [Keep a Changelog](https://keepachangelog.com).

## [Unreleased] — Sprint 02 (in review)

### Added
- Migration V004: accommodation types, rooms, dormitory beds, amenities (22 built in), unit images, unit blocks.
  `btree_gist` `EXCLUDE` constraints and triggers stop overlapping blocks and beds in per-room types at database level.
- Room-based (UNIT) and bed-based (BED) inventory with a shared capacity calculation.
- APIs under `/api/v1/properties/{propertyId}`: amenities, accommodation types, rooms, beds, room status board, blocks and
  release. All permission-checked, validated and audited; block and release reasons go into the audit trail.
- Development seed runner (`database/seed/dev`, Development only, idempotent) with demo rooms: Deluxe 101–102,
  Standard 103–106, a 10-bed dormitory.
- Staff app: Rooms, room/dorm editor with bulk bed add, block dialog, release with reason, Room status board, Room types
  editor with amenities; *Set up rooms* on the Today checklist.
- 21 backend tests (incl. 10 concurrent block requests → exactly one wins) and 8 frontend tests.
- Docs: Sprint 02 API spec in `docs/API.md`, `docs/database/schema-sprint-02.md`, *What each sprint needs* in
  `docs/GETTING_STARTED.md`, Sprint 02 review script in `docs/sprints/REVIEWING.md`, screenshots.

### Changed
- An audit reason sent in the request body (e.g. a block's reason) is recorded in preference to the `X-Change-Reason` header.

## [Unreleased] — Sprint 01 (in review)

### Added
- Modular-monolith solution (.NET 10): Api, Application, Domain, Infrastructure, Contracts, Tests.
- SQL migrations V001–V003 with checksum-guarded migrator; 13 tables; 117 permissions; 17 system roles.
- Sign-in with lockout, rotating refresh cookie with reuse detection, forced password change, security-stamp sign-out.
- Organisation, properties and property settings; staff accounts with property-scoped roles and escalation guard.
- Audit trail with old/new values, reason, device, IP, correlation and request ids; append-only at database level.
- `/api/v1` routes; standard error body `{code, message, details, correlationId}`; security headers; rate limiting; health checks; OpenAPI.
- Angular 20 staff app: login, dashboard setup checklist, properties, staff accounts, roles matrix, audit trail, change password.
- 30 backend tests (integration on real PostgreSQL) and 6 frontend tests; GitHub Actions CI.
- Owner and developer guides: `docs/GETTING_STARTED.md` (Windows/macOS step by step, VS Code), `docs/sprints/REVIEWING.md`,
  `docs/COSTS.md`; every sprint guide now opens with *Where this sprint stands* (now / done / files / results / outcome).
- Shared VS Code workspace (`.vscode/`: recommended extensions, F5 debugging, tasks); `.gitattributes` (LF everywhere).
- `Onboarding` workflow: runs the setup script on a clean Linux runner through to a signed-in API, and the PowerShell
  script on Windows.

### Fixed
- `scripts/setup-dev.ps1` failed to generate keys (PowerShell unrolled the byte array); now requires PowerShell 7 and
  stops on any failed step. Added `scripts/db-reset.ps1`.
