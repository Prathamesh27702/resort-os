# Getting started — from a new PC to a running Resort OS

A step-by-step walkthrough for Windows 10/11 (macOS notes at the end of each step). It takes about 45 minutes the
first time, mostly downloads. Everything here is **free** (see [COSTS.md](COSTS.md)).

After this guide: [sprints/REVIEWING.md](sprints/REVIEWING.md) (how to review a sprint) and
[sprints/README.md](sprints/README.md) (what each sprint is doing). Reference material and troubleshooting:
[SETUP.md](SETUP.md).

---

## Step 1 — Install the tools

Install in this order and accept the defaults unless noted.

| # | Tool | Version | Download | Notes |
|---|---|---|---|---|
| 1 | **Git** | latest | https://git-scm.com/downloads | Also gives you *Git Bash* |
| 2 | **Visual Studio Code** | latest | https://code.visualstudio.com/ | Tick *"Add to PATH"* and *"Open with Code"* |
| 3 | **.NET 10 SDK** | 10.0.1xx | https://dotnet.microsoft.com/download/dotnet/10.0 | The **SDK** (not just the runtime). The repo pins it in `global.json` |
| 4 | **Node.js 22 LTS** | 22.x | https://nodejs.org/en/download | Pick version 22 (this is what CI uses) |
| 5 | **PowerShell 7** (Windows) | 7.x | https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows | The scripts need 7; Windows ships with 5.1 |
| 6 | **WSL 2** (Windows) | — | Open *PowerShell as Administrator* → `wsl --install` → restart | Docker Desktop needs it |
| 7 | **Docker Desktop** | latest | https://www.docker.com/products/docker-desktop/ | Runs PostgreSQL for you. Start it once and let it finish starting (whale icon steady) |
| 8 | GitHub CLI *(optional)* | latest | https://cli.github.com/ | Handy for pull requests from the terminal |

Shortcut on Windows (PowerShell, one line per tool):

```powershell
winget install Git.Git
winget install Microsoft.VisualStudioCode
winget install Microsoft.DotNet.SDK.10
winget install Microsoft.PowerShell
winget install Docker.DockerDesktop
```

Install Node 22 from the website link above so you get version 22 specifically.

**Check** in a *new* PowerShell 7 window (Start → "PowerShell 7"):

```powershell
git --version
dotnet --version      # 10.0.1xx
node --version        # v22.x
docker version        # shows Client and Server — Server missing = Docker Desktop not running
```

> macOS: install the same tools from the same links (Docker Desktop for Mac, no WSL step). Use the Terminal app and
> the `.sh` scripts instead of `.ps1`.

## Step 2 — Tell Git who you are (once per PC)

```powershell
git config --global user.name  "Prathamesh"
git config --global user.email "you@example.com"     # the email on your GitHub account
```

Line endings are handled by the repo's `.gitattributes`. You don't need to change `core.autocrlf`.

## Step 3 — Clone the repository in VS Code

1. Open VS Code → **Source Control** (left bar, the branch icon) → **Clone Repository** → **Clone from GitHub**.
2. VS Code asks to sign in to GitHub → **Allow** → sign in in the browser → back to VS Code.
3. Pick **Prathamesh27702/resort-os**.
4. Choose a folder **outside OneDrive**, e.g. `C:\dev`. OneDrive syncing `node_modules` makes everything slow.
5. When asked, **Open** the cloned repository.
6. In the bottom-left corner click the branch name (`main`) → choose **origin/develop**. Day-to-day work happens on `develop`.

Same thing from a terminal:

```powershell
cd C:\dev
git clone https://github.com/Prathamesh27702/resort-os.git
cd resort-os
git switch develop
code .
```

## Step 4 — Install the VS Code extensions

When the folder opens VS Code shows *"Do you want to install the recommended extensions?"* → **Install**.
(Or: Extensions panel → type `@recommended` → install each.)

| Extension | Why |
|---|---|
| [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit) | C# editing, build, debug, test explorer. Free for personal use and commercial teams of up to 5 developers |
| [Angular Language Service](https://marketplace.visualstudio.com/items?itemName=Angular.ng-template) | Angular templates |
| [ESLint](https://marketplace.visualstudio.com/items?itemName=dbaeumer.vscode-eslint) | Frontend lint errors as you type |
| [EditorConfig](https://marketplace.visualstudio.com/items?itemName=EditorConfig.EditorConfig) | Indentation/line-ending rules from `.editorconfig` |
| [Container Tools](https://marketplace.visualstudio.com/items?itemName=ms-azuretools.vscode-containers) | See/stop the PostgreSQL container |
| [PostgreSQL](https://marketplace.visualstudio.com/items?itemName=ms-ossdata.vscode-pgsql) | Browse tables and run SQL |
| [GitHub Pull Requests](https://marketplace.visualstudio.com/items?itemName=GitHub.vscode-pull-request-github) | Review and comment on PRs inside VS Code |
| [YAML](https://marketplace.visualstudio.com/items?itemName=redhat.vscode-yaml) | Editing `docs/sprints/sprints.yml` |
| [Markdown Mermaid](https://marketplace.visualstudio.com/items?itemName=bierner.markdown-mermaid) | Diagrams in the docs preview (Ctrl+Shift+V) |

C# Dev Kit may ask you to sign in with a Microsoft account. That's free.

## Step 5 — One-time project setup

Make sure **Docker Desktop is running**, then in VS Code open a terminal (**Terminal → New Terminal**). Pick
**PowerShell** (pwsh) from the `+ ˅` dropdown if it opened Windows PowerShell 5.

```powershell
pwsh -ExecutionPolicy Bypass -File scripts/setup-dev.ps1
```

macOS/Linux: `bash scripts/setup-dev.sh`

What it does:

1. Creates **`.env`** in the repo root from `.env.example`. `.env` is ignored by Git and never leaves your PC.
2. Generates **local-only** secrets in it: database password, JWT signing key, encryption key, and a password for the
   local owner account.
3. Starts **PostgreSQL 16** in Docker on `localhost:5433`.
4. Downloads the .NET and npm packages (the slow part, about 5 minutes).

It prints a line like:

```
Local owner sign-in: owner@demo.local / Dev-3f9a1c2b7d4e   (also in .env)
```

Keep it; it's also stored in `.env` as `Bootstrap__OwnerPassword`. Running the script again is safe; it never
overwrites values.

Integration keys (Razorpay, WhatsApp, Zoho …) stay **blank** until the sprint that needs them. You paste them
into `.env` yourself and share them with developers privately, never in Git, chat or issues.

## Step 6 — Run the system

You need two things running: the API and the web app. Either:

**Using VS Code tasks:** **Terminal → Run Task… → `api: dotnet watch`**, then again → **`web: npm start`**.

**Or two terminals:**

```powershell
# terminal 1 — API (first start creates the tables and the owner account)
cd backend/Resort.Api
dotnet watch run
```

```powershell
# terminal 2 — web app
cd frontend
npm start
```

Wait for `Now listening on: http://localhost:5180` (API) and `Local: http://localhost:4300/` (web).

| Open | What |
|---|---|
| http://localhost:4300 | **The staff app.** Sign in with `owner@demo.local` and the password from Step 5 |
| http://localhost:5180/swagger | API explorer: every endpoint, try them live (*Authorize* → paste `accessToken` from `POST /api/v1/auth/login`) |
| http://localhost:5180/health/ready | `Healthy` = API and database OK |

**Debugging instead of running:** press **F5** and choose **API (Resort.Api)**. Breakpoints in any `.cs` file
work. For Angular, start `web: npm start`, then F5 → **Web (Chrome on :4300)**.

## Step 7 — Run the tests

```powershell
pwsh -ExecutionPolicy Bypass -File scripts/test.ps1      # macOS/Linux: bash scripts/test.sh
```

Expected result:

- Backend: **30 passed, 0 failed.** These include real-database tests; each test class creates and deletes its own temporary database.
- Frontend: **Executed 6 of 6 SUCCESS.**

You can also run backend tests one by one from VS Code's **Testing** panel (beaker icon).

## Step 8 — Look at the database (optional)

PostgreSQL extension (elephant icon) → **Add connection**: host `localhost`, port `5433`, user `resort`, password
`POSTGRES_PASSWORD` from `.env`, database `resort_dev`. Tables like `users`, `properties` and `audit_logs` are under
`public`.

## Every day after this

```powershell
git switch develop
git pull                         # get everyone's merged work
# start Docker Desktop if it isn't running; the database container restarts by itself
cd backend/Resort.Api; dotnet watch run       # terminal 1
cd frontend; npm start                        # terminal 2
```

If `git pull` brought new npm packages (`package-lock.json` changed), run `npm ci` in `frontend` once.
New database migrations apply automatically when the API starts.

Fresh local database: `pwsh -File scripts/db-reset.ps1` (macOS/Linux `bash scripts/db-reset.sh`), then restart the API.

## Problems?

| Symptom | Fix |
|---|---|
| `pwsh` not found | Install PowerShell 7 (Step 1) and open a new terminal |
| *"running scripts is disabled on this system"* | Use the exact command `pwsh -ExecutionPolicy Bypass -File …` |
| `docker: error during connect` / setup stops at *Starting PostgreSQL* | Start Docker Desktop and wait until it says *Engine running* |
| Docker Desktop says WSL is missing or outdated | PowerShell as Administrator: `wsl --update`, then restart |
| Port **5433** already used | Change `POSTGRES_PORT` in `.env` **and** the `Port=` inside `DATABASE_URL` and `RESORT_TEST_DB` |
| API stops at *No database configured* | `.env` is missing; run Step 5 again from the repo root |
| Sign-in says *Email or password is incorrect* | Use the password in `.env` → `Bootstrap__OwnerPassword`. It's set when the database is first created, so if you changed `.env` afterwards, reset the DB |
| Blank page at :4300 | The API isn't running, or check the browser console (F12) |
| `npm ci` very slow or fails on Windows | Move the repo out of OneDrive; add `C:\dev` to Windows Defender exclusions |

More: [SETUP.md → Troubleshooting](SETUP.md#troubleshooting).
