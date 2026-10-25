# Run and test handbook

Every command to run Resort OS on your PC and to test it, sprint by sprint and then all together. Commands are for
**PowerShell 7 on Windows** with the repository at `D:\resort-os`. On macOS/Linux use the `.sh` scripts and `export X=…`
instead of `$env:X = …`.

| You want to… | Go to |
|---|---|
| Set up a new PC (first time only) | [1. Once per PC](#1-once-per-pc) |
| Start the app and use it | [2. Run the program](#2-run-the-program) |
| Get a sprint's code, merged or still in review | [3. Get the code you want to test](#3-get-the-code-you-want-to-test) |
| Test one sprint | [4. Sprint by sprint](#4-sprint-by-sprint) |
| Test everything before accepting or releasing | [5. All sprints at once](#5-all-sprints-at-once) |
| Close a sprint (screenshots, GitHub milestones) | [6. After you accept a sprint](#6-after-you-accept-a-sprint) |
| Fix a problem | [7. When something goes wrong](#7-when-something-goes-wrong) |

Each sprint is checked in four layers. Every layer has one command, plus the by-hand script in
[sprints/REVIEWING.md](sprints/REVIEWING.md).

| Layer | What it proves | Command | Needs the app running? |
|---|---|---|---|
| Backend tests | Business rules and the API, on a real PostgreSQL database | `dotnet test` (below) | No (own temporary database) |
| Frontend unit tests | Form rules and error messages in the screens | `npm run test:ci` | No |
| Browser tests | The screens, clicked like a person would | `npm run e2e` | No (own API and throwaway database `resort_e2e`) |
| API checks | Your running API accepts, refuses and answers correctly | `node scripts/check-api.mjs` | **Yes**, the API |
| Database checks | The right data is stored | `database/queries/check-sprint-NN.sql` | Docker (database) |

---

## 1. Once per PC

Full walkthrough with screenshots of each choice: [GETTING_STARTED.md](GETTING_STARTED.md) Steps 1–5. In short:

```powershell
# Tools: Git, VS Code, .NET 10 SDK, Node.js 22, PowerShell 7, Docker Desktop
winget install Git.Git
winget install Microsoft.VisualStudioCode
winget install Microsoft.DotNet.SDK.10
winget install Microsoft.PowerShell
winget install Docker.DockerDesktop
# Node.js 22: install from https://nodejs.org/en/download (pick version 22)

# Clone and set up (Docker Desktop must be running)
cd D:\
git clone https://github.com/Prathamesh27702/resort-os.git
cd D:\resort-os
git switch develop
pwsh -ExecutionPolicy Bypass -File scripts/setup-dev.ps1     # creates .env, starts PostgreSQL, downloads packages

# Browser test tools (once)
cd frontend
npm run e2e:install
cd ..
```

`setup-dev.ps1` prints your local owner sign-in (`owner@demo.local` and a password). It's also in `D:\resort-os\.env`
as `Bootstrap__OwnerPassword`. `.env` stays on your PC; never commit or share it.

## 2. Run the program

1. Start **Docker Desktop** and wait for *Engine running*. The database container starts by itself.
2. Open two terminals in VS Code (**Terminal → New Terminal**, PowerShell):

   ```powershell
   # Terminal 1: the API (applies new database migrations and demo data when it starts)
   cd D:\resort-os\backend\Resort.Api
   dotnet watch run
   ```

   ```powershell
   # Terminal 2: the web app
   cd D:\resort-os\frontend
   npm start
   ```

3. Wait for `Now listening on: http://localhost:5180` (terminal 1) and `Local: http://localhost:4300/` (terminal 2).

| Open | What |
|---|---|
| http://localhost:4300 | **The staff app.** Sign in as `owner@demo.local` with the password from `.env` |
| http://localhost:5180/swagger | Every API endpoint, to try by hand ([VERIFYING.md → Swagger](testing/VERIFYING.md#by-hand-in-swagger)) |
| http://localhost:5180/health/ready | `Healthy` means the API and the database are OK |

Stop either one with **Ctrl+C** in its terminal.

**Start from a clean database** (all demo data back, everything you typed gone). Do this before a review so your
screen matches the review script:

```powershell
# stop the API first (Ctrl+C in terminal 1)
cd D:\resort-os
pwsh -ExecutionPolicy Bypass -File scripts/db-reset.ps1      # answer y
# then start the API again (terminal 1)
```

## 3. Get the code you want to test

The work for a sprint reaches `develop` only **after you merge its pull request**. Until then it lives on its own
branch, and so do its screenshots. To test a sprint that is still in review, switch to its branch:

| Sprint | Where the code is now | Branch to test |
|---|---|---|
| 01–12 | Accepted: on `develop` and `main` | `develop` |
| 13 | In progress: its pull request will be linked here when it is ready for review | the branch named in its pull request |

```powershell
cd D:\resort-os
git status                       # must say "nothing to commit"; if not, see section 7
git fetch origin

# An accepted sprint:
git switch develop
git pull

# A sprint still in review (use the branch name from its pull request):
git switch <branch-from-the-pull-request>
git pull

# After switching or pulling, every time:
cd frontend
npm ci                           # web packages as listed in package-lock.json
cd ..
# then restart the API (Ctrl+C, then dotnet watch run): it applies new migrations
```

To go back to normal work afterwards: `git switch develop`.

## 4. Sprint by sprint

Each sprint below has the same blocks: what's new, then the four test layers, then the by-hand review. Open a
terminal at `D:\resort-os` first. The backend commands work while the API is running: they build their own Release
copy.

The backend filters choose the test classes that belong to that sprint. The browser tests for one sprint run that
sprint's file only, but always start from a fresh `resort_e2e` database.

### Sprint 01: Foundation (sign-in, staff, roles, audit)

**New:** sign-in with lockout, forced password change, properties, staff accounts, roles and permissions, audit
trail. Nothing to install beyond section 1.

```powershell
# Backend (38 tests)
dotnet test ResortOS.sln -c Release --filter "FullyQualifiedName~AuthTests|FullyQualifiedName~RateLimitTests|FullyQualifiedName~AccessControlTests|FullyQualifiedName~DataIntegrityTests|FullyQualifiedName~PropertyLocalDateTests|FullyQualifiedName~TimezoneRuleTests|FullyQualifiedName~MigrationTests|FullyQualifiedName~ErrorMessageTests"

# Browser (12 steps, S01-01 … S01-12; S01-12 pauses about a minute on purpose to test the sign-in limit)
cd frontend
npm run e2e -- e2e/sprint-01.spec.ts
cd ..

# API checks (start the API first; the Sprint 01 block must be all PASS)
node scripts/check-api.mjs

# Database checks (queries for Sprints 01 and 02)
Get-Content database/queries/check-sprint-01-02.sql | docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db psql -U resort -d resort_dev
```

By hand: [REVIEWING.md → Sprint 01](sprints/REVIEWING.md#sprint-01--sign-in-staff-roles-audit).
Screenshots: [sprint-01-screens/](sprints/sprint-01-screens/).

### Sprint 02: Rooms, dormitory beds and blocks

**New:** room types, rooms, dormitory beds, blocking and releasing rooms, the room status board. Demo data: Deluxe
101–102, Standard 103–106, a dormitory with beds B01–B10. Nothing new to install; restart the API after pulling.

```powershell
# Backend (20 tests)
dotnet test ResortOS.sln -c Release --filter "FullyQualifiedName~AccommodationTests|FullyQualifiedName~InventoryCalculatorTests|FullyQualifiedName~UnitBlockDateTests|FullyQualifiedName~DevSeedDiscoveryTests|FullyQualifiedName~BlockMessageTests"

# Browser (14 steps, S02-01 … S02-14)
cd frontend
npm run e2e -- e2e/sprint-02.spec.ts
cd ..

# API checks: the Sprint 02 block must be all PASS
node scripts/check-api.mjs

# Database checks (same file as Sprint 01)
Get-Content database/queries/check-sprint-01-02.sql | docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db psql -U resort -d resort_dev
```

By hand: [REVIEWING.md → Sprint 02](sprints/REVIEWING.md#sprint-02--rooms-dormitory-and-blocks).
Screenshots: [sprint-02-screens/](sprints/sprint-02-screens/).

### Sprint 03: Guests, ID documents, privacy

**New:** guest profiles, search by phone, duplicate warning and merge, encrypted ID documents, anonymising a guest.
Demo data: 20 guests. Uses `Encryption__Key` from `.env`; don't change it once you have documents.

```powershell
# Backend (50 tests)
dotnet test ResortOS.sln -c Release --filter "FullyQualifiedName~GuestTests|FullyQualifiedName~PhoneNormalisationTests|FullyQualifiedName~GuestRuleTests|FullyQualifiedName~FieldEncryptionTests"

# Browser (13 steps, S03-01 … S03-13)
cd frontend
npm run e2e -- e2e/sprint-03.spec.ts
cd ..

# API checks: the Sprint 03 block must be all PASS
node scripts/check-api.mjs

# Database checks
Get-Content database/queries/check-sprint-03.sql | docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db psql -U resort -d resort_dev
```

By hand: [REVIEWING.md → Sprint 03](sprints/REVIEWING.md#sprint-03--guests-id-documents-privacy).
Screenshots: [sprint-03-screens/](sprints/sprint-03-screens/).

### Sprint 04: Rate plans and packages

**New:** **Rates** (rate plans, the nightly price grid, setting prices for a date range or weekends, quoting a stay)
and **Packages** (fixed-price stays with the revenue split by category). Demo data: plan BAR, Standard room ₹3,500 a
night and ₹4,200 on Friday and Saturday nights, and two packages. Nothing new to install and no new keys; restart the
API after pulling and watch for `Applying migration V006__rates` and `Dev seed S003__rates`.

```powershell
# Backend (28 tests)
dotnet test ResortOS.sln -c Release --filter "FullyQualifiedName~RateTests|FullyQualifiedName~QuoteEngineTests|FullyQualifiedName~MoneySplitTests"

# Browser (6 steps, S04-01 … S04-06). To watch them click, use e2e:headed instead of e2e
cd frontend
npm run e2e -- e2e/sprint-04.spec.ts
cd ..

# API checks: the Sprint 04 block must be all PASS
node scripts/check-api.mjs

# Database checks
Get-Content database/queries/check-sprint-04.sql | docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db psql -U resort -d resort_dev
```

Quick acceptance check, on a freshly reset database: **Rates → Quote a stay**, Room 103, arrival next Thursday,
3 nights, 2 adults gives ₹3,500 + ₹4,200 + ₹4,200 = **₹11,900**. (The review script first raises weekend prices to
₹4,500, so after its step 3 the same quote is ₹12,500.)

By hand: [REVIEWING.md → Sprint 04](sprints/REVIEWING.md#sprint-04--rate-plans-rate-grid-quotes-and-packages).
Screenshots: [sprint-04-screens/](sprints/sprint-04-screens/).

### Sprint 05: Availability and reservations

**New:** **Reservations** (list, search, enquiries), **New booking** (dates → room → guest → rate → confirm) and the
booking page (rooms, price night by night, notes, history, confirm, change dates or guests, cancel, no-show). Bookings
can't double-sell a room or dorm bed, even when two people book the last one at the same moment. Demo data: six
bookings from today (R00001–R00006); Room 102 is the last Deluxe room tonight. Nothing new to install and no new keys;
reset the database before the review (the demo bookings start from the day of the reset), restart the API and watch
for `Applying migration V007__reservations` and `Dev seed S004__reservations`.

```powershell
# Backend (34 tests, including the last room raced 50 times and the last dorm bed with 5 parallel requests)
dotnet test ResortOS.sln -c Release --filter "FullyQualifiedName~ReservationTests|FullyQualifiedName~AvailabilityEngineTests|FullyQualifiedName~ReservationStatusTests"

# Browser (7 steps, S05-01 … S05-07; S05-04 books the last room from two browsers at once)
cd frontend
npm run e2e -- e2e/sprint-05.spec.ts
cd ..

# API checks: the Sprint 05 block must be all PASS
node scripts/check-api.mjs

# Database checks (block 4 is the double-booking check: it must return no rows)
Get-Content database/queries/check-sprint-05.sql | docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db psql -U resort -d resort_dev
```

Quick acceptance check: open **New booking** in two browsers (owner and a receptionist), both choose *Deluxe room*
for tonight and go to **Confirm**, then press **Book** in both. One gets a booking for Room 102; the other is told
*No Deluxe room is free … Choose another room type or other dates.* How it works: [booking-algorithm.md](architecture/booking-algorithm.md).

By hand: [REVIEWING.md → Sprint 05](sprints/REVIEWING.md#sprint-05--availability-bookings-and-the-last-room).
Screenshots: [sprint-05-screens/](sprints/sprint-05-screens/).

### Sprint 06: Reservation calendar

**New:** **Calendar** (menu, or the Calendar button on Reservations): every room and dorm bed by day, week or month, with
bookings (guest, status, source, payment) and blocks. Drag a booking up or down to move the guest to another room: a
reason is asked, the server checks, and the bar moves only when the server says yes. Dropping onto a blocked or booked
room says why at once. On a phone the calendar is a list by day. Demo data: Room 106 is blocked for maintenance from
tomorrow for two nights. Nothing new to install and no new keys; reset the database before the review, restart the API
and watch for `Applying migration V008__reservation_calendar` and `Dev seed S005__calendar`.

```powershell
# Backend (8 tests: CalendarTests, including two moves into the same room raced 20 times, plus the calendar dev seed test)
dotnet test ResortOS.sln -c Release --filter "FullyQualifiedName~CalendarTests|FullyQualifiedName~Dev_seed_calendar"

# Frontend unit tests (calendar windows, rows, "why can't it go there", 7 rooms × 31 days drawn in under 200 ms)
cd frontend
npm run test:ci

# Browser (8 steps, S06-01 … S06-08; S06-04 moves a guest 101 → 102 by drag and reason)
npm run e2e -- e2e/sprint-06.spec.ts
cd ..

# API checks: the Sprint 06 block must be all PASS
node scripts/check-api.mjs

# Database checks (blocks 5 and 6 must return no rows)
Get-Content database/queries/check-sprint-06.sql | docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db psql -U resort -d resort_dev
```

Quick acceptance check: open **Calendar** as a receptionist, drag R00001 from Room 101 down to Room 102, type a reason and
press **Move** (two actions). The booking page shows "Moved from Room 101 to Room 102 … Reason: …" and **Audit trail**
shows who and why. Then drag R00002 (Room 105) onto Room 106: *Can't move R00002 to Room 106: … it is blocked (Plumbing
repair).* How it works: [system-design.md → 4.16](architecture/system-design.md#416-moving-a-guest-to-another-room).

By hand: [REVIEWING.md → Sprint 06](sprints/REVIEWING.md#sprint-06--reservation-calendar).
Screenshots: [sprint-06-screens/](sprints/sprint-06-screens/).

### Sprint 07: Front desk

**New:** **Front desk** (menu): tiles for today's arrivals, departures, in-house guests and rooms ready, then one tab
each for **Arrivals**, **Departures**, **In house** and **Rooms** (every room and dorm bed with who is in it and whether it
is clean). **Check in** is one click when the room is free and ready; otherwise a window says why and offers *Choose
another room* or *Check in anyway* with a reason. **Check out** shows what is owed, asks a reason when the guest leaves
early, and marks the room dirty. **Walk-in** books and checks in a guest with no booking on one screen. The booking page
gets Check in, Check out and Assign room buttons. "Today" is always the resort's date (India time), never the server's.
Demo data: R00007 (Room 105) leaves today, R00008 (Room 104) is in house for two more nights, Room 101 is dirty. Nothing
new to install and no new keys; reset the database before the review, restart the API and watch for
`Applying migration V009__front_desk` and `Dev seed S006__front_desk`.

```powershell
# Backend (29 tests: FrontDeskTests, FrontDeskClockTests, FrontDeskUnitTests and the front desk dev seed test;
# includes the same booking checked in by 20 pairs of clicks at once, and arrivals at 23:30 and 00:30 IST)
dotnet test ResortOS.sln -c Release --filter "FullyQualifiedName~FrontDesk|FullyQualifiedName~Dev_seed_front_desk"

# Frontend unit tests (front desk helpers: room names, which button a row shows, leaving early)
cd frontend
npm run test:ci

# Browser (9 steps, S07-01 … S07-09; S07-05 is a walk-in from the front desk to checked in on one screen)
npm run e2e -- e2e/sprint-07.spec.ts
cd ..

# API checks: the Sprint 07 block must be all PASS
node scripts/check-api.mjs

# Database checks (blocks 4 and 9 must return no rows)
Get-Content database/queries/check-sprint-07.sql | docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db psql -U resort -d resort_dev
```

Quick acceptance check: **Front desk → Walk-in**, leave *Leaving on* as tomorrow, **Find a room**, **Choose** a room
type, type a first name and phone, **Use this new guest**, **Check in** (three steps on one screen). You land on the
booking, *Checked in*, source *Walk-in*. Then **Front desk → Arrivals**: R00001 says *Room 101 is dirty, not ready*;
**Check in** → **Choose another room** → Room 102 → **Assign room** checks the guest in. How it works:
[system-design.md → 4.17](architecture/system-design.md#417-the-front-desk).

By hand: [REVIEWING.md → Sprint 07](sprints/REVIEWING.md#sprint-07--front-desk).
Screenshots: [sprint-07-screens/](sprints/sprint-07-screens/).

### Sprint 08: Folio

**New:** **Folios** (menu): the running bill of every stay. Each bill shows its balance, what it adds up to by category
(room, food, transport …) and every line with the day, who posted it and when. **Post charge** adds a charge (what for,
description, quantity, price, day) or sells a package (one line per part). Lines are **never edited or deleted**: a
manager presses **Reverse** and gives a reason, and the line stays on the bill struck through, next to its negative
reversal. **Move lines** moves charges to another bill of the same stay (e.g. a friend pays the activities). Room nights
go on the bill by themselves at check-in (or night by night, a property setting), and a guest leaving early is not
charged for the nights released. The booking page and the front desk get a **Bill** button, and the front desk's
balance is now real. Demo data: R00008 (Room 104) has its room nights, an airport pick-up (₹1,200) and dinner (₹1,450);
R00007 (Room 105) has its room night and laundry (4 × ₹80). Because payments only arrive in Sprint 10, the demo property
lets guests check out with a balance. Nothing new to install and no new keys; reset the database before the review,
restart the API and watch for `Applying migration V010__folio` and `Dev seed S007__folios`.

```powershell
# Backend (22 tests: FolioTests, FolioUnitTests and the folio dev seed test; includes the brief's ₹10,950 example,
# DELETE/UPDATE refused by the database, and the same charge reversed by 10 pairs of clicks at once)
dotnet test ResortOS.sln -c Release --filter "FullyQualifiedName~Folio|FullyQualifiedName~Dev_seed_folios"

# Frontend unit tests (folio helpers: category names, quantity text, what happened to a line)
cd frontend
npm run test:ci

# Browser (7 steps, S08-01 … S08-07; S08-02 and S08-03 are the demo: post charges, reverse one, the balance)
npm run e2e -- e2e/sprint-08.spec.ts
cd ..

# API checks: the Sprint 08 block must be all PASS
node scripts/check-api.mjs

# Database checks (blocks 3, 6 and 7 must return no rows)
Get-Content database/queries/check-sprint-08.sql | docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db psql -U resort -d resort_dev
```

Quick acceptance check: **Folios → R00008** → **Post charge**: *Food*, *Lunch at the shack*, 1 × 900 → **Post charge**;
the balance goes up by ₹900. As the owner press **Reverse** on that line, type *Posted to the wrong guest* →
**Reverse**: the line is struck through with the reason, a *Reversal: Lunch at the shack* line of −₹900 appears and the
balance is back. Nothing can be deleted. How it works:
[system-design.md → 4.18](architecture/system-design.md#418-folios-the-bill-of-a-stay).

By hand: [REVIEWING.md → Sprint 08](sprints/REVIEWING.md#sprint-08--folio).
Screenshots: [sprint-08-screens/](sprints/sprint-08-screens/).

### Sprint 09: GST and the tax engine

**New:** **GST** (menu, after Packages): the property's GST registration (GSTIN, state, from/to), what is taxed
(Accommodation, SAC 996311, and Restaurant service, SAC 996331) and each one's rules with their dates, rates and the
notification they come from. **No GST rate is in the code**: an accountant adds a rule from a future date (**Add rule**)
and the running one is ended the day before, with no deployment. **Try a charge** shows the GST of any amount on any
day (CGST + SGST inside the state, IGST across states). Every charge posted to a bill now carries its GST, stored with
the rule and rate used, so a later rule never changes it; the bill shows *amount + GST*. Seeded rules are marked *not
verified* until someone presses **Verify**. Demo data: the demo property is registered in Goa (made-up GSTIN
30AABCT1234C1Z9); the demo charges from Sprint 8 were posted before this and keep no GST. Nothing new to install and no
new keys; reset the database before the review, restart the API and watch for `Applying migration V011__tax` and
`Dev seed S008__gst`.

```powershell
# Backend (47 tests: TaxTests, TaxUnitTests and the GST dev seed test; includes the ₹7,500.00 / ₹7,500.01 slab
# boundary, the same room night at 12% and 5% either side of 22 Sep 2025, and a rule change that leaves posted charges alone)
dotnet test ResortOS.sln -c Release --filter "FullyQualifiedName~Tax|FullyQualifiedName~Dev_seed_registers"

# Frontend unit tests (GST helpers: dates, rates in words, a bill line's GST)
cd frontend
npm run test:ci

# Browser (7 steps, S09-01 … S09-07; S09-02 is the demo: the same charge before and after a rule change date,
# S09-03 the acceptance: the accountant adds next month's rule)
npm run e2e -- e2e/sprint-09.spec.ts
cd ..

# API checks: the Sprint 09 block must be all PASS (read-only: it adds no rule)
node scripts/check-api.mjs

# Database checks (blocks 5, 6, 7 and 9 must return no rows)
Get-Content database/queries/check-sprint-09.sql | docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db psql -U resort -d resort_dev
```

Quick acceptance check: **GST** → **Add rule** → *Restaurant service*, from the 1st of next month, 18%, *With input
tax credit*, a source → **Add rule**: *The rule before it now ends on …*. Then **Try a charge**: Food ₹1,000 today is
₹1,050.00, on the 1st of next month ₹1,180.00. Worked examples: [GST.md](GST.md#worked-examples). How it works:
[system-design.md → 4.19](architecture/system-design.md#419-gst-rules-as-data-the-tax-engine).

By hand: [REVIEWING.md → Sprint 09](sprints/REVIEWING.md#sprint-09--gst-and-the-tax-engine).
Screenshots: [sprint-09-screens/](sprints/sprint-09-screens/).

### Sprint 10: payments, refunds and the cash drawer

**New:** **Payments** (menu, after Folios): every payment with its status, totals by method for a day or a week,
search by payment, booking, reference or guest, and the **cash drawer** panel (also on the **Front desk**). On a bill,
**Take payment**: cash, UPI, card (the property's terminal) or bank transfer, onto the bill (at most what it owes) or
*on account* for the booking as an advance. A payment's page has **Refund** (with a reason; managers and the
accountant), **Void** (entered by mistake) and **Put on a bill** (an advance). The drawer is opened with the cash
counted in it; cash payments and refunds go in and out of it; closing compares the count with what it should hold, and
a short or over count needs a reason and a manager's approval. **No card number is ever stored**: anything that looks
like one is refused under its field. Balances, check-out and the calendar's *paid* now use real payments. Demo data:
yesterday's closed shift (₹500 cash from R00007) and a ₹2,000 UPI advance for R00002; no shift is open. Nothing new to
install and no new keys; reset the database before the review, restart the API and watch for
`Applying migration V012__payments` and `Dev seed S009__payments`.

```powershell
# Backend (38 tests: PaymentTests, PaymentUnitTests and the payments dev seed test; includes two refunds at once
# that can't exceed the payment, DELETE and UPDATE refused by the database, and the cash drawer demo)
dotnet test ResortOS.sln -c Release --filter "FullyQualifiedName~Payment|FullyQualifiedName~Dev_seed_has_a_closed_drawer"

# Frontend unit tests (payment helpers: the card-number check, the drawer variance, labels)
cd frontend
npm run test:ci

# Browser (7 steps, S10-01 … S10-07; S10-01 … S10-05 are the demo: open the drawer, cash + UPI, a ₹200 refund,
# close ₹50 short, the manager approves)
npm run e2e -- e2e/sprint-10.spec.ts
cd ..

# API checks: the Sprint 10 block must be all PASS (it takes ₹1 by UPI on account for R00002 and voids it)
node scripts/check-api.mjs

# Database checks (blocks 4, 5, 6, 7, 8, 10 and 11 must return no rows)
Get-Content database/queries/check-sprint-10.sql | docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db psql -U resort -d resort_dev
```

Quick acceptance check: **Front desk** → **Open drawer** with *5000* → **Folios → R00008** → **Take payment** *Cash 1000*,
then the rest by *UPI*: the balance is ₹0.00. Open the cash payment → **Refund** *200* with a reason (as the owner). Front
desk → *Should hold ₹5,800.00* → **Close drawer**, count *5750*, reason *Gave too much change* → *₹50.00 short; waiting
for a manager*. As the owner, **Payments** → **Approve**. How it works:
[system-design.md → 4.20](architecture/system-design.md#420-refunds-that-cant-overspend-and-a-cash-drawer-that-balances).

By hand: [REVIEWING.md → Sprint 10](sprints/REVIEWING.md#sprint-10--payments-refunds-and-the-cash-drawer).
Screenshots: [sprint-10-screens/](sprints/sprint-10-screens/).

### Sprint 11: Razorpay

**New:** on a booking, **Online payment links**: what the booking still owes, **Send payment link** for an amount (at
most what is owed, less links already open; ₹1 or more), the link copied to send by WhatsApp or SMS, **Cancel** with a
reason, and **History**: every notification Razorpay sent and what it did. The guest opens the link on their phone with
no sign-in (`/pay/…`) and pays with UPI or a card in Razorpay Checkout. Only Razorpay's signed webhook records the
payment: it goes on the bill (method *Online (Razorpay)*), the link is *Paid* and a *Pending* booking is *Confirmed*.
A declined try says why and the link stays open. Online payments are refunded back through Razorpay and can't be voided.
**No keys needed to try it:** with the three `Razorpay__…` values empty in `.env`, the API uses a built-in test Razorpay
and the guest's page shows *Pay (test)* and *Fail the payment (test)*. For the real test-mode demo (a Razorpay test card
and a real webhook) follow [RAZORPAY.md → Setting it up](RAZORPAY.md#setting-it-up-test-mode): test keys and the webhook
secret in `.env`, and `cloudflared tunnel --url http://localhost:5180` so Razorpay can reach your PC. No new demo data:
R00003 (pending, "Will send the advance by UPI") is the booking to send a link for. Restart the API and watch for
`Applying migration V013__razorpay`.

```powershell
# Backend (24 tests: RazorpayTests and RazorpayUnitTests; includes the same webhook delivered twice at once, a bad
# signature (401, nothing stored), captured before authorised, retries and timeouts, and online refunds)
dotnet test ResortOS.sln -c Release --filter "FullyQualifiedName~Razorpay"

# Frontend unit tests (Razorpay helpers, the send-link dialog, the guest's page)
cd frontend
npm run test:ci

# Browser (6 steps, S11-01 … S11-06; S11-01 … S11-04 are the demo: send a link, a declined try, pay, the booking
# confirmed; S11-05 refunds ₹500 through Razorpay)
npm run e2e -- e2e/sprint-11.spec.ts
cd ..

# API checks: the Sprint 11 block must be all PASS (it sends a ₹1 link for R00003 and cancels it)
node scripts/check-api.mjs

# Database checks (blocks 5, 6, 7 and 10 must return no rows)
Get-Content database/queries/check-sprint-11.sql | docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db psql -U resort -d resort_dev
```

Quick acceptance check (no keys): **Reservations → R00003** → *Online payment links* → **Send payment link** *2000* →
open the copied link in a private window → **Fail the payment (test)** (the reason is shown) → **Pay ₹2,000.00 (test)** →
*Paid*. Back on R00003 (refresh): *Confirmed*, the link *Paid* with its payment, and **History** lists what Razorpay
sent. With test keys and the tunnel, the same page shows **Pay**, which opens Razorpay Checkout: pay with card
4111 1111 1111 1111 (any future expiry, any CVV) or UPI `success@razorpay`. How it works:
[system-design.md → 4.21](architecture/system-design.md#421-razorpay-the-webhook-is-the-only-writer).

By hand: [REVIEWING.md → Sprint 11](sprints/REVIEWING.md#sprint-11--razorpay).
Screenshots: [sprint-11-screens/](sprints/sprint-11-screens/).

### Sprint 12: GST invoices, credit and debit notes, receipts

**New:** checking a guest out now issues the **GST tax invoice** of their bill and a **receipt** for each payment, by
itself. **Invoices** (menu) lists them; an invoice's page shows both parties with their GSTINs, the place of supply, each
line with its SAC and CGST/SGST, and buttons for **PDF**, **Credit note** (take lines, or part of them, off; or cancel the
whole invoice so it can be issued again, e.g. in a company's name) and **Debit note** (charges posted after the invoice).
On a bill, **Issue invoice** shows a preview and who it is billed to (type a company's GSTIN for a B2B invoice; a typing
mistake is caught under the field); charges on an invoice carry its number and can no longer be reversed. A payment has
**Receipt**. Numbers run without gaps per financial year: `BEACH/2627/00001`, credit notes `BEACH/C2627/0001`. No new
demo data: **R00007** (leaving today, with ₹500 cash P00001 on its bill) is the stay to check out. Restart the API and
watch for `Applying migration V014__invoicing`.

```powershell
# Backend (InvoiceTests and InvoiceUnitTests; includes 20 invoices at the same moment numbered 1–20 with no gap, B2B
# inside and across states, the database refusing to edit or delete an invoice, credit and debit notes, receipts)
dotnet test ResortOS.sln -c Release --filter "FullyQualifiedName~Invoice"

# Frontend unit tests (invoice helpers, the bill's reverse/move list without invoiced charges)
cd frontend
npm run test:ci

# Browser (5 steps, S12-01 … S12-05: check-out issues invoice and receipt, a debit note, a part credit note, cancel and
# re-issue B2B with a GSTIN typo caught, the Invoices screen and a receipt download)
npm run e2e -- e2e/sprint-12.spec.ts
cd ..

# API checks: the Sprint 12 block must be all PASS (it issues P00001's receipt once; nothing else is issued)
node scripts/check-api.mjs

# Database checks (blocks 4, 6, 7 and 8 must return no rows; block 9's "differs" column all false)
Get-Content database/queries/check-sprint-12.sql | docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db psql -U resort -d resort_dev
```

Quick acceptance check: on R00007's bill post a *Food* charge of ₹1,450 (the demo room night and laundry predate GST,
so they carry none) → **Front desk → Departures → R00007 → Check out** → **Reservations → R00007** → **Invoice
BEACH/2627/00001** (total ₹5,342.50, CGST and SGST ₹36.25 each, receipt BEACH/R2627/00001 for the ₹500 payment) → **PDF**
→ **Credit note** → tick the dinner, amount *450*, a reason → *Partly credited*, ₹472.50 credited. How it works:
[system-design.md → 4.22](architecture/system-design.md#422-gst-invoices-gap-free-numbers-and-corrections-only-by-note);
the tax rules: [GST.md → Invoices](GST.md#invoices-credit-and-debit-notes-sprint-12).

By hand: [REVIEWING.md → Sprint 12](sprints/REVIEWING.md#sprint-12--gst-invoices-credit-and-debit-notes-receipts).
Screenshots: [sprint-12-screens/](sprints/sprint-12-screens/).

### Adding the next sprint here

Each sprint adds a block like the ones above in the same pull request as its code: what's new, its backend filter
(its test classes), its browser test file, its database checks file, and links to its review script and screenshots.

## 5. All sprints at once

Run this before you accept a sprint or release `develop` to `main`. It's what CI runs on every pull request.

```powershell
cd D:\resort-os

# 1. Backend + frontend unit tests (expected: 381 backend passed, 88 frontend SUCCESS, "All tests passed.")
pwsh -ExecutionPolicy Bypass -File scripts/test.ps1

# 2. Lint and production build of the web app (expected: "All files pass linting." and a finished build)
cd frontend
npm run lint
npm run build:prod

# 3. Every browser test, Sprints 01–12 (expected: 101 passed, about 14 minutes)
npm run e2e
npm run e2e:report        # opens the report: every step with a screenshot; a failed step has a replay and a video
cd ..

# 4. API checks against your running API (expected: "N passed, 0 failed")
node scripts/check-api.mjs

# 5. Database checks, every sprint
Get-ChildItem database/queries/check-sprint-*.sql | ForEach-Object {
  Write-Host "=== $($_.Name) ==="
  Get-Content $_ | docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db psql -U resort -d resort_dev
}
```

Expected totals for Sprints 01–11:

| Layer | Expected |
|---|---|
| Backend tests | 340 passed, 0 failed |
| Frontend unit tests | 81 of 81 SUCCESS |
| Browser tests | 96 passed (12 + 14 + 13 + 6 + 7 + 8 + 9 + 7 + 7 + 7 + 6) |
| API checks | 0 failed |

Other useful commands:

```powershell
dotnet test ResortOS.sln -c Release --filter "FullyQualifiedName~Unit"      # backend unit tests only, no database
dotnet test ResortOS.sln -c Release --filter "FullyQualifiedName~Weekend"   # any test whose name contains a word
cd frontend; npm test                                                       # frontend unit tests, re-run on every save
cd frontend; npm run e2e:headed -- e2e/sprint-05.spec.ts                    # watch the browser click, slowly
cd frontend; npm run e2e:ui                                                 # Playwright's window: pick tests and run them
```

## 6. After you accept a sprint

Full checklist: [sprints/PROCESS.md → Closing a sprint](sprints/PROCESS.md). These steps need your PC because
they use the GitHub CLI (`gh auth login` once) or a real browser.

```powershell
cd D:\resort-os
git fetch origin --tags
git switch develop
git pull

# Refresh one sprint's screenshots (here Sprint 05) and check they changed as expected
cd frontend
$env:E2E_SCREENSHOTS = "../docs/sprints/sprint-05-screens"
npm run e2e -- e2e/sprint-05.spec.ts
Remove-Item Env:E2E_SCREENSHOTS
cd ..
git status                       # commit changed .png files on a chore/ branch and open a PR to develop

# GitHub milestones and the next sprint's task issues (here: the next sprint is 6)
python scripts/github/sync-sprints.py github Prathamesh27702/resort-os
python scripts/github/sync-sprints.py tasks  Prathamesh27702/resort-os 5
```

## 7. When something goes wrong

| Symptom | Fix |
|---|---|
| `git switch` or `git pull` says *Your local changes … would be overwritten* | See what changed: `git status`, then `git diff <file>`. To throw your change away: `git restore <file>`. Then switch or pull again |
| A sprint's screenshots or screens are missing on `develop` | Its pull request isn't merged yet. Test its branch (section 3), or merge the PR when you accept it |
| Build fails: *file is locked by "Resort.Api"* / *MSB3027* | An API is already running. Ctrl+C it, or `Get-Process Resort.Api \| Stop-Process` |
| `Set RESORT_TEST_DB` when running `scripts/test.ps1` | `.env` is missing. Run `scripts/setup-dev.ps1` again from `D:\resort-os` |
| Browser tests: *Executable doesn't exist* | `cd frontend; npm run e2e:install` |
| Browser tests: port 5181 or 4301 already in use | A previous test run is still open. Close its terminal, or restart VS Code |
| A browser test fails | `cd frontend; npm run e2e:report` → click the red test → **Trace** shows the screen, the click and the API call when it failed |
| `check-api.mjs`: *sign-in failed* | You changed the owner's password in the app. Run `$env:OWNER_PASSWORD = 'the-new-one'; node scripts/check-api.mjs` |
| Sign-in says *Email or password is incorrect* | Use `Bootstrap__OwnerPassword` from `.env`. If you changed `.env` after the database was created, reset the database (section 2) |
| Rates or Rooms page is empty after pulling | Restart the API and look for `Dev seed` lines in its terminal. *already has … skipped* means you added your own data; reset the database for the demo data |
| `docker: error during connect` | Start Docker Desktop and wait for *Engine running* |

More fixes: [GETTING_STARTED.md → Problems?](GETTING_STARTED.md#problems) and
[SETUP.md → Troubleshooting](SETUP.md#troubleshooting). Detailed layer-by-layer checks:
[testing/VERIFYING.md](testing/VERIFYING.md).
