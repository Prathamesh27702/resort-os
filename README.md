# Resort OS

India-first resort operating system: reservations, rooms and dorm beds, front desk, folio, GST invoicing, payments,
housekeeping, restaurant, inventory, staff, activities, transport and reporting, in one modular monolith.

> **Current sprint: [Sprint 01 — Foundation](docs/sprints/sprint-01-foundation.md)** (🟡 in review)
> All sprints and their status: [docs/sprints](docs/sprints/README.md) · GitHub *Milestones* show the same.

| | |
|---|---|
| Backend | .NET 10 LTS · ASP.NET Core minimal APIs · EF Core 10 + Npgsql · FluentValidation · Serilog · OpenTelemetry |
| Database | PostgreSQL 16 · versioned SQL migrations |
| Frontend | Angular 20 · standalone components · signals · Angular Material · Tailwind CSS · PWA |
| Delivery | GitHub Actions CI · Docker (local) · Cloudflare + managed PostgreSQL (production, Sprint 29) |

## Quick start

```bash
git clone https://github.com/Prathamesh27702/resort-os.git && cd resort-os
git switch develop
scripts/setup-dev.sh                       # Windows: scripts/setup-dev.ps1  — creates .env, starts PostgreSQL
cd backend/Resort.Api && dotnet watch run  # API  → http://localhost:5180/swagger
cd frontend && npm start                   # Web  → http://localhost:4300
scripts/test.sh                            # all tests
```

Prerequisites and troubleshooting: **[docs/SETUP.md](docs/SETUP.md)**.

Secrets are **not** in this repository. `setup-dev` generates local-only values; integration keys (Razorpay, Zoho,
WhatsApp, channel manager, email, storage) are issued privately by the project owner. See [.env.example](.env.example).

## Repository layout

```
backend/                    .NET solution (ResortOS.sln at the root)
  Resort.Api/               endpoints, auth policies, middleware, Program.cs
  Resort.Application/       use cases per module, validators, permission codes
  Resort.Domain/            entities and domain rules
  Resort.Infrastructure/    EF Core, migrator, audit, JWT, external adapters
  Resort.Contracts/         API request/response records
  Resort.Tests/             unit + integration tests (real PostgreSQL)
frontend/                   Angular staff app (core / shared / layout / features)
database/migrations/        V###__name.sql — the schema's source of truth
infra/docker/               docker-compose for local PostgreSQL
scripts/                    setup-dev, test, db-reset, GitHub sprint sync
docs/                       everything below
.github/                    CI, PR/issue templates, CODEOWNERS, Dependabot
```

## Documentation

| Topic | Doc |
|---|---|
| Set up a machine, run, debug | [docs/SETUP.md](docs/SETUP.md) |
| How we work: branches, PRs, commits | [CONTRIBUTING.md](CONTRIBUTING.md) · [docs/sprints/PROCESS.md](docs/sprints/PROCESS.md) |
| Sprint plan and per-sprint guides | [docs/sprints](docs/sprints/README.md) |
| Architecture and module map | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Database, migrations, ERD | [docs/DATABASE.md](docs/DATABASE.md) |
| API conventions, catalogue, adding an endpoint | [docs/API.md](docs/API.md) |
| Security, permission matrix, OWASP | [docs/SECURITY.md](docs/SECURITY.md) · [permission matrix](docs/security/permission-matrix.md) |
| Testing | [docs/TESTING.md](docs/TESTING.md) |
| Deployment and operations | [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) · [docs/OPERATIONS.md](docs/OPERATIONS.md) |
| Integrations | [INTEGRATIONS](docs/INTEGRATIONS.md) · [GST](docs/GST.md) · [Razorpay](docs/RAZORPAY.md) · [Zoho Books](docs/ZOHO_BOOKS.md) · [OTA](docs/OTA.md) · [WhatsApp](docs/WHATSAPP.md) |
| Open questions for the owner | [docs/architecture/risks-and-open-questions.md](docs/architecture/risks-and-open-questions.md) |

## Status

| Built (Sprint 1) | Next |
|---|---|
| Sign-in, sessions, lockout, password policy · roles & 117 permissions · organisation & properties · staff accounts · audit trail · staff web app shell | Sprint 2: room types, 6 rooms, dormitory beds, blocks, dev seed data |

Private and proprietary. © the project owner. Not licensed for redistribution.
