# Sprint 6 — schema reference (reservation calendar)

Source of truth: `database/migrations/V008__reservation_calendar.sql` and `database/seed/dev/S005__calendar.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
The tables are Sprint 5's ([schema-sprint-05.md](schema-sprint-05.md)); this sprint adds no table.

## What changed

| Object | Change | Why |
|---|---|---|
| `ix_reservation_units_calendar` | `ON reservation_units (property_id, start_date, end_date) WHERE superseded_at IS NULL` | The calendar reads every **current** row overlapping a window of up to 62 days, including rows that no longer hold a room (checked-out stays). Sprint 5's `ix_reservation_units_held` only covers rows that hold inventory. |

## Moving a guest to another room (no new table)

A move (`POST /reservations/{id}/reassign`) uses the history mechanism from Sprint 5:

1. The current `reservation_units` row gets `superseded_at = now()` and `holds_inventory = false` (kept forever).
2. A new row is inserted for the same `[start_date, end_date)`, the new `unit_id` (and `bed_id` for a dorm bed) and the
   new room's `accommodation_type_id` (the V007 trigger requires the type to match the room).
3. Its `reservation_rate_breakdowns` rows are copied from the old row, so the price stays as booked.
4. A `reservation_notes` row says "Moved from Room 101 to Room 102 (for the nights of …). Reason: …".
5. `audit_logs` gets an UPDATE (old row) and an INSERT (new row) with the user and the reason, and the reservation's
   `xmin` version changes.

The EXCLUDE constraints and block trigger from V007 apply to the new row like any booking, so a move can never
double-book a room or land on a blocked night, whatever the code does.

## Dev seed S005

Room 106 is blocked (`MAINTENANCE`, "Plumbing repair") from tomorrow for two nights, so the calendar shows a block and
the review can drag R00002 (Room 105, from tomorrow) onto Room 106 and see it refused. Skipped if the block exists or
Room 106 is booked then. Room 103 and dorm beds B01–B04 are left alone (the Sprint 02 review blocks them).

## Checks

`database/queries/check-sprint-06.sql`: the migration and index, the demo block, every room move with who and why,
"a move keeps the price", and the double-booking check again.
