# Sprint 23 — schema reference (reports and dashboards)

Source of truth: `database/migrations/V025__reporting.sql`. Dev seed: `database/seed/dev/S020__report_history.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.33](../architecture/system-design.md#433-reports-the-daily-summary-and-power-bi).
Connecting Power BI: [REPORTING.md](../REPORTING.md).

No report adds up the raw tables when someone opens a page (brief §88). A **refresh** adds one property's days up into
small summary tables in the schema `reporting`, **one row per property and day**; dashboards and reports read only those.
A refresh runs every 15 minutes for the 1st of last month to 90 days ahead (the job `ReportRefreshJob`,
`Reporting:RefreshMinutes`), once a night from 1 April of last financial year to 90 days ahead, and on **Refresh now**.
It replaces the days it covers in one transaction under an advisory lock per property, so two refreshes of the same
property queue instead of clashing. The summary tables can always be rebuilt from the raw ones: nothing is lost by
emptying them.

## What changed

| Object | Change | Why |
|---|---|---|
| schema `reporting` | new | Everything the reports read, apart from the raw tables. |
| `reporting.daily_stats` | new table, PK (property, day) | Occupancy, revenue, GST, food cost: one row a day. |
| `reporting.daily_channel_stats` | new table, PK (property, day, source) | Nights sold, room revenue and commission per booking source. |
| `reporting.daily_costs` | new table, unique (property, day, department or none) | Expenses, supplier bills and pay per budget department. |
| `reporting.refresh_state` | new table, PK property | When the numbers were last added up, which days, how long it took. |
| `reporting.labour_cost(property, from, to)` | new function | What each employee costs each day (an estimate; payroll is outside Resort OS). |
| `reporting.commission_percent(property, source)` | new function | The commission a booking source takes. |
| `reporting.refresh(property, from, to, full)` | new function | Recomputes the days (at most 900 at once; `ck_reporting_refresh_range` otherwise). |
| `reporting.v_daily`, `v_monthly`, `v_channels_monthly`, `v_costs_monthly`, `v_gst_monthly` | new views | What Power BI and Excel read. |
| role `resort_reporting` | new, `NOLOGIN` | `USAGE` on `reporting` and `SELECT` on the five views, nothing else. Made when the migrating login may create roles; else by `scripts/powerbi-user.sql`. |
| `departments.cost_centre_id` | new column, FK (same property) to `cost_centres` | The budget department an employee department's pay counts for. Filled by name or code where both exist. |
| `supplier_invoices.cost_centre_id` | new column, FK (same property) | The budget department a supplier bill counts for. Bills of a kitchen store default to Food and beverage. |
| indexes | `ix_folio_items_service_date`, `ix_refunds_property`, `ix_credit_notes_date`, `ix_debit_notes_date`, `ix_supplier_invoices_date`, `ix_reservations_check_in`, `ix_reservations_check_out` | So a refresh reads a range of days, never a whole table. |

## `reporting.daily_stats`

| Columns | What |
|---|---|
| `rooms_available`, `beds_available` | Active rooms sold one at a time and active dorm beds, less those out of order or under maintenance that night (a block on a whole dorm takes all its beds) |
| `rooms_sold`, `beds_sold` | Current rows of bookings `CONFIRMED`, `CHECKED_IN` or `CHECKED_OUT` that cover the night (cancelled and no-show sell nothing) |
| `out_of_order`, `guests_in_house`, `arrivals`, `departures` | Rooms and beds blocked; adults + children staying; bookings arriving or leaving that day |
| `room_revenue`, `fnb_revenue`, `activity_revenue`, `transport_revenue`, `other_revenue`, `total_revenue` | Folio lines by `service_date` and category, before GST: charges, reversals (negative) and credit notes (negative); restaurant bills paid at the table added to food and drink. `ck_daily_stats_total`: the total is the sum |
| `tax_charged` | GST on those lines and on the restaurant bills paid at the table |
| `payments_received`, `refunds_paid` | Captured payments and completed refunds, by the local day |
| `restaurant_sales`, `restaurant_orders`, `food_cost`, `stock_written_off` | Settled orders by business day (less discounts); the stock their dishes used; wastage by the local day |
| `output_taxable`, `output_cgst`, `output_sgst`, `output_igst`, `output_cess` | GST invoices and debit notes less credit notes, by their date |
| `pos_gst_uninvoiced` | GST on restaurant bills paid at the table (no GST invoice for them yet, Sprint 14) |
| `input_cgst`, `input_sgst`, `input_igst` | GST that can be claimed on supplier bills (by the bill date) and approved expenses |
| `refreshed_at` | When this day was added up |

Occupancy = (rooms_sold + beds_sold) / (rooms_available + beds_available); ADR = room_revenue / units sold; RevPAR =
room_revenue / units available. The views and the API use the same formulas, rounded to 1 decimal (occupancy) and to the
paisa (ADR, RevPAR).

## The views (for Power BI)

All have `property_code` and `property_name` to filter and label by.

| View | One row per | Columns |
|---|---|---|
| `v_daily` | property, day | everything in `daily_stats`, units available and sold, occupancy %, ADR, RevPAR |
| `v_monthly` | property, month | the same summed by month |
| `v_channels_monthly` | property, month, booking source | source code and name, kind (DIRECT, OTA …), nights sold, room revenue, commission, net |
| `v_costs_monthly` | property, month, department | department code and name (or none), expenses, supplier bills, pay |
| `v_gst_monthly` | property, month | output by tax, restaurant bills at the table, input by tax, payable, credit, GST on the bills |

## Defaults to know

- Pay is an estimate for the reports and the budget: a monthly salary spread over the days of the month the person was
  employed; a daily rate for each day worked on a timesheet that wasn't rejected. Employer PF and ESI and unpaid leave
  are left out. Only the owner and HR see it (`employee_salary.read`); the database role for Power BI can read it through
  `v_costs_monthly`, so give that login only to people who may see pay.
- Commission: the property setting `reports.commission_percent` (`{"BOOKING_COM": 15}`), else the source's own
  `commission_percent`, else 18 % for an OTA, 10 % for a travel agent, else nothing.
- The dev seed S020 adds a year of past stays (about 1,600, checked out and paid) from the 1st of the month a year ago to a
  week ago, 64 guests for them, and maps the demo departments to budget departments.
