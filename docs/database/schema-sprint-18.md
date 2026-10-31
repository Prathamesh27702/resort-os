# Sprint 18 — schema reference (attendance and timesheets)

Source of truth: `database/migrations/V020__attendance.sql`. Dev seed: `database/seed/dev/S015__attendance.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.28](../architecture/system-design.md#428-attendance-geofence-and-timesheet-maths).

Staff clock in and out on their phones; each week becomes a timesheet that the person submits and a manager approves,
then locks for payroll. Clock events are what happened and never change. The timesheet's days are worked out from them
by the API, in the property's time zone, while the timesheet is open (`DRAFT` or `REJECTED`); a clock event on a
`SUBMITTED` week puts it back to `DRAFT`. A manager's correction sets a day's times directly, keeps the old ones, and that
day is never worked out from events again. Once `LOCKED`, the database refuses any change to the timesheet or its days.

## What changed

| Object | Change | Why |
|---|---|---|
| `geofences` | new table; never deleted (switched off instead) | Where clocking in counts: a centre and a radius in metres. Several per property are allowed (the main campus, the beach shack). |
| `attendance_events` | new table; append-only (trigger) | One clock-in, break start, break end or clock-out, with where the phone was, or a manager's entry with the reason. |
| `timesheets` | new table; never deleted; locked rows can't change (trigger) | One person's week with its totals and its approval. |
| `timesheet_lines` | new table; can't change once the timesheet is locked (trigger) | One work day: shift, in, out, breaks, worked, overtime, late, early. |
| `timesheet_corrections` | new table; append-only (trigger) | A manager changing a day's times, with the reason and the old and new values. |

## `geofences`

`name` (unique per property in any case: `ux_geofences_name`), `latitude`, `longitude` (6 decimals, about 10 cm),
`radius_m` (20–2000), `is_active`. No active geofence at a property means clock-ins there aren't location-checked
(`NO_GEOFENCE`).

## `attendance_events`

| Column | Notes |
|---|---|
| `employee_id` | Same property (composite FK to `employees (id, property_id)`) |
| `event_type` | `CLOCK_IN`, `BREAK_START`, `BREAK_END`, `CLOCK_OUT` |
| `occurred_at` | The instant (UTC). A manager entry can be up to 7 days back (API) |
| `work_date`, `roster_shift_id` | The day it counts for: a clock-in takes the rostered shift starting within 3 hours of it (else the local date); later events take the open clock-in's. A night shift counts on the day it starts |
| `latitude`, `longitude`, `accuracy_m` | What the phone sent (personal data, masked in the audit trail); set together |
| `distance_m`, `geofence_id` | Metres from the nearest active geofence's centre, and the geofence it was inside |
| `location_status` | `INSIDE` (needs `geofence_id`), `NO_GEOFENCE` (none set up, or a break), `OVERRIDE` (exactly when `source = 'MANAGER'`) |
| `source`, `reason` | `DEVICE`, or `MANAGER` with the reason (3–300 characters): `ck_attendance_events_reason` |
| `device` | The browser's user agent, at most 200 characters |

Indexes: `(employee_id, occurred_at)` for one person's latest state, `(property_id, work_date)` for the day's board.
`trg_attendance_events_append_only` refuses UPDATE and DELETE. A person's events are written one at a time (the API
locks the employee row), so a double tap can't clock in twice.

## `timesheets`

| Column | Notes |
|---|---|
| `employee_id`, `week_start` | One per person per week (`ux_timesheets_week`); `week_start` is a Monday (`ck_timesheets_week`) |
| `status` | `DRAFT`, `SUBMITTED`, `APPROVED`, `REJECTED` (with `rejection_reason`: `ck_timesheets_rejected`), `LOCKED` (with `locked_at`: `ck_timesheets_locked`) |
| `worked_minutes`, `break_minutes`, `overtime_minutes`, `late_minutes`, `early_minutes`, `days_worked` | The sum of the lines (≥ 0; days 0–7) |
| `note`, `submitted_at`, `submitted_by` | The person's note when submitting (1–300) |
| `decided_at`, `decided_by` | Who approved or sent it back (set for `APPROVED`, `REJECTED`, `LOCKED`) |
| `locked_at`, `locked_by` | Locked for payroll |

Made by the API the first time a week is needed (`INSERT … ON CONFLICT DO NOTHING`, so two requests at once make one).
`trg_timesheets_locked` (`fn_timesheet_locked`) refuses any UPDATE of a `LOCKED` row; `trg_timesheets_no_delete` refuses
DELETE.

## `timesheet_lines`

| Column | Notes |
|---|---|
| `timesheet_id`, `work_date` | One per day with a shift or a clock-in (`ux_timesheet_lines_day`) |
| `roster_shift_id`, `scheduled_start`, `scheduled_end`, `scheduled_minutes` | The rostered shift and its paid minutes (length minus break) |
| `clock_in_at`, `clock_out_at` | First in, last out; out after in (`ck_timesheet_lines_times`) |
| `break_minutes`, `worked_minutes`, `overtime_minutes`, `late_minutes`, `early_minutes`, `sessions` | Worked = out − in − breaks over every session; at most 24 h |
| `status` | `WORKED` (in and out: `ck_timesheet_lines_worked`), `ABSENT` (rostered, never in), `ON_SHIFT` (in, no out yet, under 16 h), `MISSING_OUT` (in, no out after 16 h) |
| `overridden` | A manager recorded one of the day's clock events |
| `corrected` | Changed by a correction; never worked out from events again |

`trg_timesheet_lines_locked` (`fn_timesheet_line_locked`) refuses INSERT, UPDATE and DELETE while the timesheet is
`LOCKED`. Lines are read in `work_date` order.

## `timesheet_corrections`

`timesheet_id`, `work_date`, `timesheet_status` (the timesheet's status at the time), `old_clock_in_at`,
`old_clock_out_at`, `old_break_minutes`, `old_worked_minutes` (NULL when there was no line), `new_clock_in_at`,
`new_clock_out_at` (both NULL: the day wasn't worked), `new_break_minutes`, `new_worked_minutes`, `reason` (3–300),
`created_at`, `created_by`. `trg_timesheet_corrections_append_only` refuses UPDATE and DELETE. Read in `created_at` order.

## Data classification

`geofences`: internal. `attendance_events`: confidential (where a person was). `timesheets`, `timesheet_lines`,
`timesheet_corrections`: confidential.

## Dev seed S015

The `Main gate` geofence (300 m) at the property's coordinates (15.544000, 73.755300 when it has none). Last week's roster
copied from this week's, with clock events for it, and nine of last week's timesheets `SUBMITTED` waiting for approval
(Joel Rodrigues `E00009` still `DRAFT`): Ravi Kamat `E00007` clocked in on Tuesday and never out (his note says he left at
22:05), and Pooja Shetye `E00005` was absent on Tuesday. This week has clock events up to yesterday. Seeded directly, so
it has no audit rows.
