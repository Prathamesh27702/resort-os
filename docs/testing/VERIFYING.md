# Verifying a sprint: UI, API, database

How to check, with your own eyes, that what was built works. You look at it in three layers, in this order:

| Layer | Question it answers | Automated (one command) | By hand |
|---|---|---|---|
| **1. UI** | Do the screens do the right thing? | Browser tests: `npm run e2e:ui` (you watch them click) | The scripts in [REVIEWING.md](../sprints/REVIEWING.md) |
| **2. API** | Does the server accept, refuse and answer correctly? | `node scripts/check-api.mjs` (PASS/FAIL per check) | Swagger at http://localhost:5180/swagger |
| **3. Database** | Is the right data stored, and are the rules enforced? | — | `database/queries/check-sprint-01-02.sql` |

Plus the developer test suites (`scripts/test.ps1`) that CI also runs on every pull request.
Section 5 lists, feature by feature, what to check in each layer for Sprints 01 and 02.

---

## 0. Before you start: be on the latest code

The sprint work lives on **`develop`**. `main` only gets a sprint after you accept it. If your test run shows
**30** backend tests and no `rooms.forms.spec` in the frontend output, you're on old code:

```powershell
cd D:\resort-os
git switch develop
git pull
cd frontend; npm ci; cd ..          # new packages arrived (browser test tools)
```

Then restart the API. With Sprints 01–02 plus this guide you should see **58** backend tests and **14** frontend
unit tests, all passing.

## 1. Your `.env` file: where it is and what's in it

It's in the **repository root**, next to `README.md`. With the repo cloned to `D:\resort-os`, that's
**`D:\resort-os\.env`**.

- **VS Code:** it's in the Explorer at the bottom of the root folder, shown greyed out because Git ignores it.
  Click to open.
- **Windows Explorer:** turn on *View → Show → File name extensions* to see it as `.env`. Open with Notepad.
- **Terminal:** `notepad D:\resort-os\.env`

`scripts/setup-dev.ps1` created it and generated every value. They exist only on your PC and are never in Git.

| Line | What it is | Where you use it |
|---|---|---|
| `Bootstrap__OwnerEmail` / `Bootstrap__OwnerPassword` | The local **owner sign-in** for the app | http://localhost:4300 sign-in; Swagger; `check-api.mjs` |
| `POSTGRES_USER` / `POSTGRES_PASSWORD` / `POSTGRES_DB` / `POSTGRES_PORT` | Your local **database** account (`resort` / … / `resort_dev` / `5433`) | VS Code PostgreSQL extension, any SQL tool |
| `DATABASE_URL` | The same database details as one string, for the API | Nothing to do |
| `RESORT_TEST_DB` | Same server, database `resort_test`, for the automated tests | Nothing to do |
| `Jwt__SigningKey`, `Encryption__Key` | Keys the API signs sign-in tokens and encrypts data with | Never needed by hand |
| `Razorpay__…`, `WhatsApp__…`, `ZohoBooks__…`, … | Integration keys, blank until their sprint | Filled in later |

Two things to know:

- **The owner password is read only when the database is first created.** If you change it in the app (Change
  password), the app uses the new one and `.env` keeps the old one. Editing `.env` later doesn't change the
  account either; reset the database for that (`scripts/db-reset.ps1`, then restart the API).
- `.env` is personal. Don't send it, paste it into chat or issues, or commit it. Each developer has their own.

## 2. UI: browser tests you can watch

These are Sprint 01 + 02's review scripts turned into automatic tests: 26 steps that sign in, fill forms, click
buttons and check what appears, in a real Chrome. They live in `frontend/e2e/`, and each test is named after its
REVIEWING.md step (`S02-03 block Room 103 for 2 nights…`).

**Safe to run any time.** They start their **own** API (port 5181) and web app (port 4301) against a throwaway
database `resort_e2e` that is wiped at the start of every run. Your own data in `resort_dev` and your running
API/web app (5180/4300) are not touched. Docker Desktop (the database) must be running.

```powershell
cd D:\resort-os\frontend
npm run e2e:install     # once per PC: downloads the test browser (free, a few minutes)

npm run e2e:headed      # BEST FOR WATCHING: real Chrome windows open and click through the screens,
                        # half a second between actions, start to finish
npm run e2e:ui          # Playwright's control window: ▶ runs tests and ticks them off. The screen
                        # preview there stays blank for these tests (the browser windows are created
                        # by the test itself), so use e2e:headed to see the screens
npm run e2e             # runs them hidden, prints ✓/✘ per step (what CI does)
npm run e2e:report      # opens the last run's report: every test, a screenshot, and for a failure a
                        # step-by-step replay (trace) and a video
```

The first run takes 3–5 minutes (it builds the API in Release). After that about 3 minutes. One step,
**S01-12 lockout**, pauses about a minute: sign-in allows 10 attempts a minute per PC, and the test waits for the
limit to reset. That pause means the limit works.

If a test fails, open `npm run e2e:report`, click the red test, then **Trace** to see the screen, the click and
the API call at the moment it failed.

CI runs the same tests on every pull request (*Browser tests — Sprint 01–02 screens*). The report with
screenshots is attached to each run: GitHub → **Actions** → the run → **Artifacts → browser-test-report**.

## 3. API: checking the server directly

### One command

With the API running (`cd backend/Resort.Api; dotnet watch run`):

```powershell
cd D:\resort-os
node scripts/check-api.mjs
```

It signs in as the owner (password from `.env`) and makes 34 calls, printing each one:

```
=== Sprint 02 — rooms, dormitory beds, blocks ===
 PASS  Block Room 103 for 2 nights (2026-12-07 → 2026-12-09)
       POST /api/v1/properties/{propertyId}/blocks → expected 201
 PASS  Same nights again → 409 BLOCK_OVERLAP
       POST /api/v1/properties/{propertyId}/blocks → expected 409
…
34 passed, 0 failed, 34 checks
```

What it leaves behind (safe to repeat): a staff account `apicheck.receptionist@demo.local`, and one released block
on Room 103 about 60 days ahead. If you changed the owner's password in the app, run
`$env:OWNER_PASSWORD='the-new-one'; node scripts/check-api.mjs`.

### By hand in Swagger

1. Open http://localhost:5180/swagger.
2. **Auth → `POST /api/v1/auth/login`** → *Try it out* → body
   `{ "email": "owner@demo.local", "password": "<Bootstrap__OwnerPassword from .env>" }` → **Execute**.
3. Copy the `accessToken` value from the response (without the quotes).
4. **Authorize** (padlock, top right) → paste the token → *Authorize* → *Close*. Tokens last 15 minutes; repeat
   steps 2–4 when calls start returning 401.
5. Most Sprint 02 calls need the property id: run **`GET /api/v1/properties`** and copy `id`.
6. Try any endpoint. Every error has the same shape: `{ code, message, details, correlationId }`.
   `correlationId` is the key to find the full error in the API terminal.

The meaning of every endpoint, its permission and its errors: [API.md](../API.md).

## 4. Database: looking at the stored data

All the checks are in **`database/queries/check-sprint-01-02.sql`**, 16 queries, read-only. Each has a comment
saying what you should see.

**In VS Code (Microsoft's PostgreSQL extension, from Getting started Step 4):**
1. Open the PostgreSQL view (elephant icon, or **Ctrl+Alt+D**) → hover **Connections** → **+** (*Add New Connection*)
   → server `localhost`, port `5433`, authentication **Password**, user `resort`, password = `POSTGRES_PASSWORD` from
   `.env`, database `resort_dev` → **Save & Connect**. It shows green when connected.
2. Open `database/queries/check-sprint-01-02.sql`, pick the `resort_dev` connection when VS Code asks, select one
   query (from its comment to its `;`), and press **Ctrl+Shift+E** to run it. The rows appear in the **Results** panel.
3. To look at any table: right-click the connection → **New Query** → `SELECT * FROM unit_blocks;` → **Ctrl+Shift+E**.

**Or one command (all queries at once, printed in the terminal):**

```powershell
cd D:\resort-os
Get-Content database/queries/check-sprint-01-02.sql | docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db psql -U resort -d resort_dev
```

Passwords are never stored. `users.password_hash` holds a one-way hash (query 3 shows only its first characters),
and refresh tokens are stored hashed too. That is why nobody, including you, can read a user's password from the
database; a manager resets it instead.

---

## 5. Feature by feature: UI → API → database

*REVIEWING step* = the hands-on step in [REVIEWING.md](../sprints/REVIEWING.md) (S01-n / S02-n is also the browser
test's name). *API check* = the line in `check-api.mjs`'s output. *DB query* = the number in `check-sprint-01-02.sql`.

### Sprint 01 — sign-in, staff, roles, audit

| Feature | 1. UI (REVIEWING step → what you see) | 2. API (request → expected) | 3. Database (query → what you see) |
|---|---|---|---|
| Sign-in | S01-1: sign in → *Good morning, Demo*, IST clock | `POST /api/v1/auth/login` → 200 with `accessToken`; wrong password → 401 `INVALID_CREDENTIALS` | Q6: a `login_events` row per attempt, `success` true/false, `failure_reason` |
| Property and GST state | S01-2: edit address, PIN, phone, email → *Changes saved*; State (GST) = Goa (30) | `GET /api/v1/properties/{id}` → `timezone` Asia/Kolkata, `localDate`; `PUT` with `"timezone":"IST"` → 400 | Q2: `address_line1`, `postal_code`, `gst_state_code` = 30 |
| Staff accounts | S01-3: add Riya as Receptionist at the resort → *Awaiting first sign-in* | `POST /api/v1/users` → 201; empty form → 400 with a message per field | Q3: Riya, `must_change_password` = true, roles `RECEPTIONIST @ BEACH01`, only a hash stored |
| Roles and permissions | S01-4: 17 roles, salary row ticked only for Owner and HR | `GET /api/v1/roles` → 17; `GET /api/v1/permissions` → 117 | Q4: 17 roles, OWNER 117; Q5: OWNER and HR |
| First sign-in | S01-6: Riya must choose her own password, then signs in again | login with the temporary password → `me.mustChangePassword: true`; `POST /api/v1/auth/change-password` → 204 | Q3: `must_change_password` = false, `last_login_at` set |
| Access limits | S01-7: Riya sees Today and Properties only; `/users` sends her back | as receptionist `GET /api/v1/users` → 403 | Q3: her roles column shows one role, one property |
| Deactivation | S01-8/9: deactivate with a reason → she's signed out and can't sign in | `POST /api/v1/users/{id}/deactivate` → 200; her sign-in → 401 | Q3: `is_active` = false; Q7: her tokens have `revoked_at`; Q6: `INACTIVE` |
| Audit trail | S01-5/10: every change, old → new, with reason | `GET /api/v1/audit-logs?pageSize=5` | Q8: rows with `changes` JSON and `reason`; Q9: editing a row fails, *append-only* |
| Lockout | S01-12: 5 wrong passwords → *Too many failed attempts…* | 6th login → 401 `ACCOUNT_LOCKED` | Q3: `failed_login_count` 5, `lockout_until` 15 min ahead; Q6: `LOCKED_OUT` |
| Phone layout | S01-11: 390 px → ☰ menu, no sideways scroll | — | — |

### Sprint 02 — rooms, dormitory beds, blocks

| Feature | 1. UI (REVIEWING step → what you see) | 2. API (request → expected) | 3. Database (query → what you see) |
|---|---|---|---|
| Demo rooms | S02-1/2: *6 rooms and 10 dorm beds*, three groups | `GET …/accommodation-types`, `GET …/rooms` → 101–106, DORM | Q10: 3 types; Q11: 7 rooms, DORM 10 beds; Q12: sellable 2 / 4 / 10 |
| Block a room | S02-3: block 103 for 2 nights with a reason → *Blocked* | `POST …/blocks` → 201, `nights: 2` | Q13: ACTIVE row, `end_date` − `start_date` = 2 |
| Overlap refused | S02-4: second block on 103 → refused in the dialog with the existing dates | same body again → 409 `BLOCK_OVERLAP` | Q14: 0 rows (no overlapping active blocks) |
| Room status | S02-5: 103 orange tonight; two nights later green (end date not included) | `GET …/rooms/status?date=YYYY-MM-DD` → `status: BLOCKED` / `AVAILABLE` | Q13: the dates |
| Dorm beds | S02-6: B10 out of use → *9 dorm beds* | `PUT …/beds/{id}` with `isActive: false` | Q11: `beds_in_use` 9; Q12: DORMITORY sellable 9 |
| Bed vs whole dorm | S02-7: block bed B01 → *1 of 9 beds blocked*; whole dorm same night → refused | bed block → 201; dorm block → 409 | Q13: one row with `bed` = B01 |
| Beds only in dorms | S02-8: Room 101 has no beds section | `POST …/rooms/{101}/beds` → 422 `BEDS_NOT_ALLOWED` | Q15: 0 rows |
| New type and room | S02-9: *Garden cottage* (code becomes `GARDEN_COTTAGE`) → room C1 | `POST …/accommodation-types` → 201; `POST …/rooms` → 201 | Q10/Q11: the new rows |
| Deactivate, never delete | S02-10: type with an active room can't be deactivated; room first, then type | `PUT …/accommodation-types/{id}` `isActive:false` → 422 `TYPE_HAS_ACTIVE_UNITS` | Q10/Q11: `is_active` = false, rows still there |
| Release | S02-11: release with a reason → *Available* | `POST …/blocks/{id}/release` → 200, `status: RELEASED` | Q13: RELEASED with `release_reason` |
| Audit with reasons | S02-12: both reasons in the Audit trail | `GET /api/v1/audit-logs?entityType=UnitBlock` | Q16: INSERT with the block reason, UPDATE with the release reason |
| Receptionist read-only | S02-13: no Add/Block/Release buttons; fields read-only | as receptionist `POST …/rooms` or `…/blocks` → 403 | — |
| Phone layout | S02-14: 390 px, no sideways scroll | — | — |

`…` = `/api/v1/properties/{propertyId}`.

## 6. When something doesn't match

1. Note the **layer** and the **step / check / query number**.
2. UI: a screenshot (no passwords in it). API: the `code`, `message` and `correlationId` from the response.
   Database: the query and its output.
3. **Issues → New issue → Bug**, milestone = the sprint. The developer can reproduce it from the step number.
