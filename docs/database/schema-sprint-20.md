# Sprint 20 — schema reference (activities and equipment)

Source of truth: `database/migrations/V022__activities.sql`. Dev seed: `database/seed/dev/S017__activities.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.30](../architecture/system-design.md#430-activity-seats-weather-cancellation-equipment).

Seats work like rooms in Sprint 05: the API locks the **session** row (`SELECT … FOR UPDATE`) before it counts seats, so
two people booking the last seat queue and the second is told it is full. Behind that, the database keeps
`activity_schedules.seats_booked` from the bookings with a trigger in the same statement, and a CHECK refuses it above the
seats: even a booking that skipped the API's lock can't oversell. **Equipment** is one row per tagged item; its status can
only move along the allowed steps (a trigger), and an item has at most one open assignment (a unique index), so two
people can't be handed the same kayak.

## What changed

| Object | Change | Why |
|---|---|---|
| `equipment_types` | new table; never deleted (switched off instead) | Kinds of equipment: Life jacket, Kayak, Snorkel set, Bicycle. |
| `activities` | new table; never deleted (switched off instead) | What the resort offers, with seats, price and the equipment each person needs. |
| `activity_schedules` | new table; never deleted; `seats_booked` kept by a trigger, never above `capacity` | One session of an activity. |
| `activity_bookings` | new table; never deleted; keeps its session and seats | Seats on a session, how they are paid, and what happened if cancelled. |
| `equipment` | new table; never deleted (retired instead); status moves checked by `trg_equipment_status_transition` | One tagged item. |
| `equipment_assignments` | new table; never deleted; one open assignment per item | Who has an item, and how it came back. |
| `folio_items` | `ck_folio_items_source` also allows `ACTIVITY` | An activity booking's charge on the bill. |

## `equipment_types`

`code` (`^[A-Z][A-Z0-9]{0,9}$`, unique per property: `ux_equipment_types_code`), `name` (2–60, unique per property in any
case: `ux_equipment_types_name`), `sort_order`, `is_active`.

## `activities`

`code` (`^[A-Z][A-Z0-9]{0,9}$`, unique per property), `name` (2–60, unique in any case), `description` (up to 500),
`duration_minutes` (15–720), `capacity` (seats a session by default, 1–200), `price` (a person, by default, 0–10,00,000;
0 is free), `meeting_point` (up to 100), `weather_dependent`, `equipment_type_id` (the kind each person needs, same
property: `fk_activities_equipment`), `sort_order`, `is_active`.

## `activity_schedules` (sessions)

| Column | Notes |
|---|---|
| `activity_id` | Same property (composite FK) |
| `session_date` | The local date at the property |
| `starts_at`, `ends_at` | At most 12 hours apart (`ck_activity_schedules_times`); one scheduled session of an activity per start (`ux_activity_schedules_start`) |
| `capacity`, `price` | Copied from the activity when put on, changeable; capacity 1–200 |
| `seats_booked` | Kept by `trg_activity_bookings_seats`; `ck_activity_schedules_capacity`: `0 ≤ seats_booked ≤ capacity` |
| `status` | `SCHEDULED` → `CANCELLED` with `cancel_reason` (3–300), `cancelled_at`, `cancelled_by` (`ck_activity_schedules_cancel`) |
| `note` | Up to 200 |

## `activity_bookings`

| Column | Notes |
|---|---|
| `code` | `AB00001` … per property (`property_counters` name `activity_booking`) |
| `schedule_id`, `activity_id` | Same property; can't change after booking (`ck_activity_bookings_fixed`, raised by the trigger) |
| `reservation_id`, `guest_id`, `guest_name` | The stay and its guest, or only a name (2–100) |
| `seats`, `unit_price`, `amount` | 1–50 seats; `amount = seats × unit_price` (`ck_activity_bookings_amount`) |
| `settlement` | `ROOM_CHARGE` (needs the stay, the folio and the charge's `folio_item_id`), `FREE`, `COMPLIMENTARY` (needs `complimentary_reason`, 3–200) (`ck_activity_bookings_settlement`) |
| `status` | `BOOKED` → `CANCELLED` (never back); cancelling needs `cancel_kind` (`GUEST`, `WEATHER`), `cancel_reason`, `cancelled_at`, `refund` |
| `refund` | `REVERSED` (the charge is off the bill: `reversal_item_id`), `MOVED` (to `moved_to_booking_id`; the charge went with it), `NONE` (nothing was charged) (`ck_activity_bookings_refund`) |
| `moved_from_booking_id` | On the copy made by a weather move |

A booking can be inserted only on a scheduled session (`ck_activity_bookings_session_open`). One booked row per charge
(`ux_activity_bookings_charge`): a moved booking takes its charge with it. `fk_activity_bookings_moved_to` is
`DEFERRABLE INITIALLY DEFERRED`, so the old booking can be cancelled before its copy is inserted in the same transaction.

## `equipment`

`equipment_type_id` (same property), `tag` (`^[A-Z0-9][A-Z0-9-]{0,19}$`, unique per property: `ux_equipment_tag`),
`note`, `status`, `status_note` (why it is out of use, up to 300), `status_changed_at` (set by the trigger).

The allowed moves (anything else is refused with `ck_equipment_status_transition`; the API answers first, with the
steps it can take):

| From | To |
|---|---|
| `AVAILABLE` | `RESERVED`, `ISSUED`, `DAMAGED`, `MAINTENANCE`, `LOST`, `RETIRED` |
| `RESERVED` | `ISSUED`, `AVAILABLE` |
| `ISSUED` | `AVAILABLE` (returned good), `DAMAGED`, `LOST` |
| `DAMAGED` | `MAINTENANCE`, `AVAILABLE`, `RETIRED`, `LOST` |
| `MAINTENANCE` | `AVAILABLE`, `DAMAGED`, `RETIRED` |
| `LOST` | `AVAILABLE` (found), `RETIRED` |
| `RETIRED` | — |

`RETURNED` is a state of the assignment, not of the item: a returned item is `AVAILABLE`, `DAMAGED` or `LOST` again.

## `equipment_assignments`

| Column | Notes |
|---|---|
| `equipment_id` | The item. `ux_equipment_assignments_open`: at most one `RESERVED` or `ISSUED` assignment per item |
| `activity_booking_id`, `reservation_id`, `issued_to` | What it is for: a booking, a stay, or just a name (2–100) |
| `status` | `RESERVED` → `ISSUED` → `RETURNED` or `LOST`; `RESERVED` → `RELEASED`; the step times and who did them (`ck_equipment_assignments_steps`) |
| `due_back_at` | For a booking, the session's end |
| `return_condition`, `return_note` | `GOOD`, `DAMAGED`, `LOST`; damaged and lost need the note (`ck_equipment_assignments_note`) |

## Dev seed (S017)

Life jackets LJ-01…12, kayaks KY-01…06 (KY-06 in maintenance), snorkel sets SN-01…08 (SN-08 damaged), bicycles
BC-01…08 (BC-08 lost). Kayaking (90 min, 6 seats, ₹800, a life jacket each), Sunrise yoga (60 min, 20 seats, free),
Snorkelling trip (180 min, 8 seats, ₹1,500, a snorkel set each), Village cycle tour (120 min, 10 seats, ₹500, a bicycle
each; runs in any weather). Sessions today and the next 7 days: kayaking 07:00 and 16:00, yoga 06:30, snorkelling 10:00,
cycling 16:30. Tomorrow: AB00001 four seats on the 07:00 kayaking for an agent group (complimentary), AB00002–AB00003 on
yoga (free). No charges are seeded.
