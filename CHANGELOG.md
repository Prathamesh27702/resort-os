# Changelog

One entry per accepted sprint, tagged `sprint-NN` on `main`. Format: [Keep a Changelog](https://keepachangelog.com).

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
