# Developer setup reference

First time? Follow the step-by-step **[GETTING_STARTED.md](GETTING_STARTED.md)** instead. This page is the
compact reference (versions, database commands, troubleshooting).

## 1. Install

| Tool | Version | Why | Get it |
|---|---|---|---|
| Git | 2.40+ | source control | https://git-scm.com |
| .NET SDK | **10.0.1xx** (pinned in `global.json`) | backend | https://dotnet.microsoft.com/download/dotnet/10.0 |
| Node.js | **22 LTS** | frontend build | https://nodejs.org (or `nvm install 22`) |
| Docker Desktop / Docker Engine | 24+ with Compose v2 | local PostgreSQL 16 | https://docs.docker.com/get-docker |
| An editor | VS Code + *C# Dev Kit* + *Angular Language Service*, or JetBrains Rider, or Visual Studio 2026 | | |
| GitHub CLI (optional) | 2.50+ | PRs, sprint sync script | https://cli.github.com |
| Python 3.9+ with PyYAML (optional) | | `scripts/github/sync-sprints.py` | `pip install pyyaml` |
| psql / DBeaver / pgAdmin (optional) | | inspecting the database | |

Windows: use PowerShell 7 for the `.ps1` scripts, or WSL2 for the `.sh` ones. Make sure Git keeps LF line endings
(`git config --global core.autocrlf input`).

Check:

```bash
dotnet --version     # 10.0.1xx
node --version       # v22.x
docker compose version
```

## 2. Get access

Ask the product owner for:

1. **GitHub access** to `Prathamesh27702/resort-os` (private).
2. **Integration keys**, only when your sprint needs them (Razorpay test mode from Sprint 11, WhatsApp/email from
   Sprint 24, Zoho Books sandbox from Sprint 25, channel manager from Sprint 26). They are shared privately, never
   in Git, chat channels or issues.

## 3. Clone and configure

```bash
git clone https://github.com/Prathamesh27702/resort-os.git
cd resort-os
git switch develop

# macOS / Linux / WSL
scripts/setup-dev.sh
# Windows PowerShell
pwsh -ExecutionPolicy Bypass -File scripts/setup-dev.ps1
```

`setup-dev` will:

1. copy `.env.example` → `.env` (gitignored)
2. generate **local-only** values: database password, JWT signing key, encryption key, and a password for the
   development owner account (printed once, also stored in `.env`)
3. start PostgreSQL 16 in Docker on `localhost:5433` (only bound to 127.0.0.1)
4. `dotnet restore` and `npm ci`

It never overwrites a value already in `.env`, so it's safe to re-run. When the owner gives you integration keys,
paste them into `.env` yourself.

## 4. Run

Two terminals:

```bash
# Terminal 1 — API (applies migrations and creates the dev owner account on first start)
cd backend/Resort.Api
dotnet watch run                 # http://localhost:5180  ·  Swagger UI: http://localhost:5180/swagger

# Terminal 2 — staff web app
cd frontend
npm start                        # http://localhost:4300  (proxies /api to :5180)
```

Sign in with `owner@demo.local` and the password `setup-dev` printed (`Bootstrap__OwnerPassword` in `.env`).

## 5. Test

```bash
scripts/test.sh          # Windows: pwsh -File scripts/test.ps1 — backend unit+integration and frontend unit tests
```

Integration tests create a throwaway database per test class on your local PostgreSQL (the `RESORT_TEST_DB` login
needs CREATEDB, which the Docker superuser has). See [TESTING.md](TESTING.md).

## 6. The database

| Task | Command |
|---|---|
| Open a SQL shell | `docker compose --env-file .env -f infra/docker/docker-compose.yml exec db psql -U resort resort_dev` |
| Connect a GUI | host `localhost`, port `5433`, user/password/db from `.env` |
| Reset to empty | `scripts/db-reset.sh` (Windows: `pwsh -File scripts/db-reset.ps1`), then restart the API |
| Back up your local DB | `docker compose … exec db pg_dump -U resort -Fc resort_dev > local.dump` |
| Restore it | `docker compose … exec -T db pg_restore -U resort -d resort_dev --clean < local.dump` |
| Stop / start | `docker compose --env-file .env -f infra/docker/docker-compose.yml stop` / `up -d db` |

Schema changes are **SQL files** in `database/migrations` (`V004__rooms.sql`, …), applied automatically when the API
starts. Never edit a migration that has been merged: the API refuses to start if an applied file changed. Details in
[DATABASE.md](DATABASE.md).

## 7. Your first change

1. Pick an issue in the current sprint milestone (see [docs/sprints/README.md](sprints/README.md)); assign yourself.
2. `git switch -c feature/S02-41-room-editor develop`
3. Build it. Keep business logic in `Resort.Application`, not endpoints or Angular components.
4. `scripts/test.sh`
5. Push, open a PR into `develop`, fill in the template. CI must be green.

Full rules: [CONTRIBUTING.md](../CONTRIBUTING.md).

## Troubleshooting

| Symptom | Fix |
|---|---|
| `No database configured. Set DATABASE_URL` | Run `scripts/setup-dev.sh`, or check `.env` is in the repo root |
| `Jwt:SigningKey must be at least 32 bytes` | `Jwt__SigningKey` missing from `.env` — re-run `setup-dev` |
| `Bootstrap is enabled but OwnerEmail/OwnerPassword … not set` | Set `Bootstrap__OwnerPassword` in `.env` (min 10 chars) |
| `Migration V00X … was modified after it was applied` | You edited an applied migration. Revert it and add a new `V###` file, or `scripts/db-reset.sh` if it's only your local DB |
| Port 5433 already in use | Change `POSTGRES_PORT` in `.env`, and the port inside `DATABASE_URL` / `RESORT_TEST_DB` |
| Sign-in says *Too many requests* | 10 sign-ins per minute per IP; wait a minute or restart the API |
| Every call returns 401 after changing password | Expected: all sessions are revoked. Sign in again |
| Web app shows a blank page | Check the browser console; usually the API isn't running on :5180 |
