# Sprint 7 — schema reference (front desk)

Source of truth: `database/migrations/V009__front_desk.sql` and `database/seed/dev/S006__front_desk.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).

## What changed

| Object | Change | Why |
|---|---|---|
| `reservations.checked_in_at`, `checked_out_at` | `timestamptz NULL`; `ck_reservations_checked_out`: no check-out time without a check-in time | When the guest actually arrived and left (the booked dates stay in `check_in` / `check_out`). Who did it is in `reservation_status_history.changed_by` and the audit trail. |
| `ix_reservations_property_check_out` | `ON reservations (property_id, check_out)` | The departures list reads by departure day (arrivals use Sprint 5's check-in index). |
| `unit_housekeeping_status` | new table, one row per room (`bed_id` NULL) or dorm bed | The cleaning state check-in reads and check-out writes. |
| `unit_housekeeping_history` | new table, append-only (delete refused by trigger) | Every change of that state; check-out rows are the "needs cleaning" events Sprint 13 turns into tasks. |

## `unit_housekeeping_status`

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id`, `property_id` | uuid | Standard tenant columns |
| `unit_id` | uuid | FK `(unit_id, property_id)` → `accommodation_units` |
| `bed_id` | uuid NULL | FK `(bed_id, unit_id)` → `beds`; NULL for the whole room |
| `status` | text | `DIRTY`, `CLEANING`, `CLEAN`, `INSPECTION`, `READY`, `OUT_OF_ORDER` |
| `reason` | text NULL | 1–500 characters, e.g. "Last guest left this morning" |
| `changed_at`, `changed_by` | | Last change and who made it |
| `created_*`, `updated_*` | | Standard audit columns |

`UNIQUE NULLS NOT DISTINCT (unit_id, bed_id)`: one row per room and one per bed. **No row means READY**: a room
that has never been used, or every room before this sprint, needs no data migration. Check-in needs READY (or an
override with a reason when the property setting `frontdesk.allow_checkin_when_not_ready` is on); `CLEAN` and
`INSPECTION` are not READY, so Sprint 13's inspection step will release rooms.

## `unit_housekeeping_history`

| Column | Type | Notes |
|---|---|---|
| `unit_id`, `bed_id` | uuid | As above |
| `from_status`, `to_status` | text | One of the six states |
| `source` | text | `CHECK_OUT` (guest left), `ROOM_MOVE` (in-house guest moved out of this room), `MANUAL` (someone set it) |
| `reservation_id` | uuid NULL | FK `(reservation_id, property_id)` → `reservations`, for `CHECK_OUT` and `ROOM_MOVE` |
| `reason` | text NULL | 1–500 characters |
| `changed_at`, `changed_by` | | When and who |
| `consumed_at` | timestamptz NULL | Set by Sprint 13 when it has created the cleaning task for this event |

`ix_unit_housekeeping_history_pending (property_id, changed_at) WHERE consumed_at IS NULL AND to_status = 'DIRTY'` is
the queue Sprint 13 reads. The state row and its history row are written in the same transaction as the check-out (or
move), so an event can never be lost or written for a check-out that rolled back.

## What a check-in, check-out and walk-in write

- **Check-in:** `reservations.status = CHECKED_IN`, `checked_in_at`; a `reservation_status_history` row (with the
  override reason, if any); for a PENDING booking first a CONFIRMED row. A note when checked in although not ready.
- **Check-out:** `status = CHECKED_OUT`, `checked_out_at`; a status history row; for each room or bed being left a
  `unit_housekeeping_status` row set to `DIRTY` and a `unit_housekeeping_history` row (`CHECK_OUT`). Leaving early also
  supersedes the `reservation_units` rows and recreates them ending on the departure day (the released nights' prices are
  dropped and `total_amount` recomputed), with a note "Left early on … instead of …; … released (₹… less). Reason: …".
- **Moving an in-house guest:** like Sprint 6's reassign, but from today: the current row is split into the nights
  already slept (old room) and the rest (new room); the old room gets `DIRTY` with a `ROOM_MOVE` history row.
- **Walk-in:** a guest (if new), a reservation from today with source Walk-in, its rows and prices, an idempotency key,
  then the check-in rows above, all in one transaction.

## Dev seed S006

- **R00007** — Room 105, yesterday → today, CHECKED_IN (phone): on today's departures for the check-out demo.
- **R00008** — Room 104, yesterday → in 2 days, CHECKED_IN (walk-in): in house, for the early check-out demo.
- **Room 101** is `DIRTY` ("Last guest left this morning", with a `MANUAL` history row), so today's arrival R00001
  (Room 101) shows *Room 101 is dirty, not ready*.

These stays end before R00002 (Room 105, from tomorrow) and R00003 (Room 104, in 3 days) begin. Room 103 and dorm beds
B01–B04 are left alone (earlier review scripts block them). Skipped when R00007 exists.

## Checks

`database/queries/check-sprint-07.sql`: the migration, who is in house, check-in/out times consistent, rooms not ready,
the housekeeping events, override reasons, early departures, "prices still add up", and walk-ins.
