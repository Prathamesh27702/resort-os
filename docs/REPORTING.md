# Reports and Power BI

Resort OS shows its reports on **Today** (the dashboard) and **Reports** (Sprint 23). The same numbers can be read by
Power BI, Excel or any tool that talks to PostgreSQL, through five read-only views. This page is for the owner and
whoever sets the database up.

## Where the numbers come from

Every few minutes Resort OS adds up each day of each property into a summary: one row per property and day for
occupancy, revenue, GST and food cost, one per booking source, one per budget department (expenses, supplier bills,
pay). The reports and the views read only that summary, so they stay fast however many bookings and bills there are.

- Every 15 minutes: the 1st of last month to 90 days ahead (late bills, approvals and new bookings show within 15 minutes).
- Every night: from 1 April of last financial year to 90 days ahead (a correction to an old day shows by the morning).
- **Refresh now** on the reports page (or `POST /api/v1/properties/{id}/reports/refresh`): at once.

The reports page and the dashboard say when the numbers were last added up.

## What the figures mean

- **Money is before GST**, unless the name says GST or tax.
- **A dorm bed counts as one unit, like a room.** Occupancy = units sold / units available; units out of order or under
  maintenance are not available. **ADR** = room revenue / units sold. **RevPAR** = room revenue / units available.
- **Revenue** is what was charged on the guests' bills for the day of the service (a night counts on that night), less
  reversals and credit notes, plus restaurant bills paid at the table.
- **Booking sources (OTA net)**: commission is worked out at each source's rate (set on Reports → Booking sources →
  Commission rates; else 18 % for an online travel agent and 10 % for a travel agent) until the OTAs' own statements come
  in (Sprint 24 onward).
- **GST** is an estimate for the accountant, not a return: output on invoices and debit notes less credit notes by their
  date, plus restaurant bills paid at the table; input that can be claimed on supplier bills and approved expenses.
- **Food cost** is the stock the restaurant's dishes used, at its cost; food cost % is that of restaurant sales.
- **Pay** is an estimate: a monthly salary spread over the days of the month, a daily rate for each day worked, before
  PF and ESI. Only the owner and HR see it in the app.
- **Profit and loss** is a management view before GST: supplier bills are not a cost there (what they bought counts
  when it is used or written off); in the **budget** they count when they are bought.

## Who sees what in the app

| Permission | Who (default roles) | Sees |
|---|---|---|
| `report.operational.read` | owner, general manager, manager, restaurant and inventory managers, accountant | Today's tonight and next 14 nights, Occupancy, Month at a glance without money, Refresh now |
| `financial_report.read` | owner, general manager, accountant | Revenue, ADR, RevPAR, booking sources, GST, costs, profit and loss |
| `employee_salary.read` | owner, HR | Pay in the costs, P&L and budget |
| `budget.read` | owner, general manager, manager, accountant | The budget card on Today |

## Connecting Power BI (or Excel)

1. **Make a login for it, once.** The database admin runs, on the server:

   ```
   psql -U postgres -d resort_dev -v pbi_password=a-long-password-you-choose -f scripts/powerbi-user.sql
   ```

   This makes `resort_powerbi`, a login that can only read the views below (read-only, at most 5 connections, queries
   stop after 60 seconds). Keep the password in your password manager; it is never stored in the code.
2. **Let it reach the database.** Over the internet, only through an SSH tunnel or a VPN to the server, never by opening
   port 5432 to the world. On the same network, allow the reporting PC's address in `pg_hba.conf`.
3. **In Power BI Desktop:** Get data → PostgreSQL database → server `localhost:5432` (through the tunnel) and database
   `resort_dev` (or your production database name) → Import → user `resort_powerbi` → pick the views under `reporting`.
4. **Refresh** the Power BI dataset daily or hourly; the views are always current to within 15 minutes.

| View | One row per | Use it for |
|---|---|---|
| `reporting.v_daily` | property and day | occupancy, ADR, RevPAR and revenue by day |
| `reporting.v_monthly` | property and month | the same by month: the owner's monthly pack |
| `reporting.v_channels_monthly` | property, month and booking source | OTA share, commission and net |
| `reporting.v_costs_monthly` | property, month and budget department | expenses, supplier bills and pay |
| `reporting.v_gst_monthly` | property and month | GST output, input and the estimate payable |

`v_costs_monthly` includes pay. Give the Power BI login only to people who may see pay, or leave that view out of the
Power BI file.

## Checking the numbers

`database/queries/check-sprint-23.sql` checks the summary against the raw tables (ADR of last month worked out by hand,
every day adding up) and shows what the Power BI role may read. How to run it: [RUN_AND_TEST.md](RUN_AND_TEST.md) →
Sprint 23.
