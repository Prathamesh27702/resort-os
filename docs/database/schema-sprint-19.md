# Sprint 19 — schema reference (leave, holidays and payroll export)

Source of truth: `database/migrations/V021__leave.sql`. Dev seed: `database/seed/dev/S016__leave.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.29](../architecture/system-design.md#429-leave-balance--ledger-overlap-guard-payroll-export).
The payroll file format: [PAYROLL.md](../PAYROLL.md).

Leave works like stock in Sprint 15: every change to a balance is a row in an append-only **ledger** (brought in, earned,
taken, given back, adjusted), and the **balance** row is kept equal to the ledger's sum by a trigger in the same
transaction, with a CHECK that it never goes below zero. A **leave request** stores the days it counts (holidays and the
weekly day off skipped) so later changes to holidays don't change leave already asked for. Two waiting or approved
requests of one person can't share a day: an exclusion constraint on the date range. The **payroll export** keeps each
month's file as it was sent, with its SHA-256.

## What changed

| Object | Change | Why |
|---|---|---|
| `leave_types` | new table; never deleted (switched off instead) | Kinds of leave: CL, SL, EL, LOP … paid or not, needing a balance or not, half days or not. |
| `leave_policies` | new table; never deleted | Days a year and how they are earned, for everyone or one employment type. |
| `leave_requests` | new table; never deleted; overlap guard | Leave asked for, with the counted days and its decision. |
| `leave_ledger` | new table; append-only (trigger) | Every change to a balance. |
| `leave_balances` | new table; kept by `trg_leave_ledger_balance`; never below zero | One row per person and kind: the ledger's sum, read and locked fast. |
| `holidays` | new table; never deleted (switched off instead) | The property's holidays, not counted as leave. |
| `payroll_exports` | new table; append-only (trigger) | Each month's payroll file as made. |
| `permissions` | `payroll.export` (module `payroll`, sensitive), granted to the system roles `OWNER` and `HR` | Making and downloading payroll files. 118 permissions. |

## `leave_types`

`code` (`^[A-Z][A-Z0-9]{0,9}$`, unique per property: `ux_leave_types_code`), `name` (2–60, unique per property in any
case: `ux_leave_types_name`), `is_paid`, `needs_balance` (only paid leave can need one: `ck_leave_types_balance`),
`allow_half_day`, `sort_order`, `is_active`. The API makes CL, SL, EL and LOP the first time a property opens leave.

## `leave_policies`

`leave_type_id`, `employment_type` (NULL: everyone; or `FULL_TIME`, `PART_TIME`, `CONTRACT`, `SEASONAL`; one per type and
employment type: `ux_leave_policies_type`), `days_per_year` (0–365), `accrual` (`MONTHLY`: a twelfth on the 1st of each
month, rounded down to 0.01; `YEARLY`: all on 1 January, pro-rated for someone who joined during the year, rounded down to
0.5; `NONE`: by hand only), `max_balance` (NULL: no cap; the accrual adds only up to it), `is_active`.

## `leave_requests`

| Column | Notes |
|---|---|
| `code` | `LV00001` … per property (`property_counters` name `leave_request`) |
| `employee_id`, `leave_type_id` | Same property (composite FKs) |
| `from_date`, `to_date` | At most 91 days (`ck_leave_requests_dates`); the API allows 30 days back to a year ahead |
| `half_day_start`, `half_day_end` | Off from lunch on the first day; back after lunch on the last; not both on one day |
| `days`, `counted_dates` | The dates counted (no holidays, no weekly day off) and the days they make: `cardinality − 0.5` per half (`ck_leave_requests_days`) |
| `period` | Generated `daterange(from_date, to_date, '[]')` |
| `status` | `PENDING` → `APPROVED` or `REJECTED` (needs `decision_note`); `PENDING` or `APPROVED` → `CANCELLED` (needs `cancelled_at`, `cancel_reason`) |
| `requested_by`, `decided_at`, `decided_by`, `decision_note` | Who asked (the person, or a manager for them) and who decided |
| `shifts_cancelled` | How many roster shifts the approval cancelled |
| `reason` | Optional, 1–300 characters; confidential (may mention health) |

`ex_leave_requests_overlap EXCLUDE USING gist (employee_id WITH =, period WITH &&) WHERE (status IN ('PENDING',
'APPROVED'))`: the database refuses a second request on the same day even when two arrive at once (`409 LEAVE_OVERLAP`
from the API).

## `leave_ledger`

| Column | Notes |
|---|---|
| `employee_id`, `leave_type_id` | Same property |
| `entry_type`, `days` | `OPENING` (> 0, needs `reason`), `ACCRUAL` (≥ 0, needs `period`), `TAKEN` (< 0) and `RETURNED` (> 0) (need `leave_request_id`), `ADJUSTMENT` (≠ 0, needs `reason`) |
| `entry_date` | The day it counts for (the 1st for an accrual, 1 January for yearly) |
| `period` | `yyyy-MM` for a monthly accrual, `yyyy` for a yearly one; once per person, kind and period (`ux_leave_ledger_accrual`) |
| `leave_request_id` | One `TAKEN` and at most one `RETURNED` per request (`ux_leave_ledger_request`) |
| `reason`, `created_at`, `created_by` | HR's reason for an opening balance or an adjustment |

`trg_leave_ledger_append_only` refuses UPDATE and DELETE. `trg_leave_ledger_balance` (`fn_leave_ledger_balance`, AFTER
INSERT) adds the row's days to `leave_balances`: an UPDATE first, then an INSERT when the row doesn't exist yet (not
`INSERT … ON CONFLICT`, because PostgreSQL checks the new row's CHECK before resolving the conflict, so a first `TAKEN`
row would trip `ck_leave_balances_not_negative`).

## `leave_balances`

`employee_id`, `leave_type_id` (unique together), `balance` (`ck_leave_balances_not_negative`), `updated_at`. The API
locks the row (`SELECT … FOR UPDATE`) before approving or adjusting, so approvals at the same moment queue and the second
sees the first's result.

## `holidays`

`holiday_date`, `name` (2–60), `is_active`. One active holiday a date per property (`ux_holidays_date`).

## `payroll_exports`

`period_start` (the 1st), `period_end` (the month's last day: `ck_payroll_exports_month`), `format` (`GENERIC_CSV_V1`),
`file_name`, `content` (the CSV bytes, UTF-8), `sha256` (lowercase hex of `content`), `employees` (rows), `timesheets_locked`
(approved timesheets the export locked), `warnings` (what wasn't ready and was confirmed), `created_at`, `created_by`.
`trg_payroll_exports_append_only` refuses UPDATE and DELETE.

## Data classification

`leave_types`, `leave_policies`, `holidays`: internal. `leave_requests` (its `reason` may mention health),
`leave_ledger`, `leave_balances`, `payroll_exports`: confidential.

## Dev seed S016

Casual leave CL (12 a year monthly, at most 12; 6 a year for `SEASONAL`), Sick leave SL (6 on 1 January, at most 12),
Earned leave EL (15 a year monthly, at most 45, whole days) and Leave without pay LOP. Holidays for this year and next:
Republic Day, Independence Day, Gandhi Jayanti, Goa Liberation Day (19 December) and Christmas. For every active employee
an `OPENING` CL and EL balance and this month's accrual (and SL for the year). Requests: Sunil Gawas `E00004` one day of
SL from tomorrow (`PENDING`), Maria Fernandes `E00003` 1.5 days of CL in ten days (`PENDING`), Neha Parab `E00008` two days
of CL in two weeks (`APPROVED`, with its `TAKEN` row), Ravi Kamat `E00007` a day of CL last month (`REJECTED`). Seeded
directly, so it has no audit rows.
