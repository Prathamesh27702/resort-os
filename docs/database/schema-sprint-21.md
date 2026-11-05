# Sprint 21 — schema reference (beach transport)

Source of truth: `database/migrations/V023__transport.sql`. Dev seed: `database/seed/dev/S018__transport.sql`.
Conventions are the same as Sprint 1: see [schema-sprint-01.md](schema-sprint-01.md#conventions-every-table-every-sprint).
How it works: [system-design.md → 4.31](../architecture/system-design.md#431-beach-trips-seats-the-drivers-manifest-boarding).

Seats work like activity sessions in Sprint 20: the API locks the **trip** row (`SELECT … FOR UPDATE`) before it counts
seats, so two people booking the last seat queue and the second is told it is full. Behind that, the database keeps
`vehicle_trips.seats_booked` from the passengers with a trigger in the same statement, and a CHECK refuses it above the
seats. Two EXCLUDE constraints keep a vehicle, and a driver, on one trip at a time.

## What changed

| Object | Change | Why |
|---|---|---|
| `vehicles` | new table; never deleted (switched off instead) | The resort's vehicles: registration and passenger seats. |
| `transport_routes` | new table; never deleted (switched off instead) | Where a trip goes, how far and how long, and the price a seat. |
| `vehicle_trips` | new table; never deleted; `seats_booked` kept by a trigger, never above `capacity`; moves checked by `trg_vehicle_trips_status` | One trip with its vehicle and driver. |
| `trip_passengers` | new table; never deleted; keeps its trip and seats; moves checked by `trg_trip_passengers_seats` | Seats on a trip, how they are paid, and whether the guest boarded. |
| `folio_items` | `ck_folio_items_source` also allows `TRANSPORT` | A transfer's charge on the bill. |

## `vehicles`

`code` (`^[A-Z][A-Z0-9]{0,9}$`, unique per property), `name` (2–60), `registration` (stored without spaces or dashes, upper
case, `^[A-Z0-9]{4,12}$`; unique in the **organisation**: `ux_vehicles_reg`), `seats` (passenger seats, not counting the
driver, 1–60), `note` (up to 200), `sort_order`, `is_active`. The API refuses lowering the seats below the seats booked on
a trip still to come.

## `transport_routes`

`code` (unique per property), `name` (2–60, unique in any case: `ux_transport_routes_name`), `origin`, `destination`
(2–60 each), `distance_km` (numeric(5,1), above 0 and up to 500), `duration_minutes` (5–360; a trip arrives this long after
it departs), `price` (a seat, one way, 0–10,00,000; a new trip gets it), `sort_order`, `is_active`.

## `vehicle_trips`

| Column | Notes |
|---|---|
| `route_id`, `vehicle_id` | Same property (composite FKs) |
| `driver_user_id` | A staff account that can run trips (`transport.trip_operate`); optional until one is chosen |
| `trip_date` | The local date at the property |
| `departs_at`, `arrives_at` | At most 6 hours apart (`ck_vehicle_trips_times`) |
| `capacity`, `price` | From the vehicle's seats and the route's price when put on; changeable (capacity within the vehicle's seats and not below the seats booked) |
| `seats_booked` | Kept by `trg_trip_passengers_seats`; `ck_vehicle_trips_capacity`: `0 ≤ seats_booked ≤ capacity` |
| `status` | `SCHEDULED` → `DEPARTED` → `COMPLETED`, or `SCHEDULED` → `CANCELLED` with `cancel_reason` (3–300); the step times and who (`ck_vehicle_trips_steps`); any other move is refused as `ck_vehicle_trips_transition` |
| `note` | Up to 200 |

`ex_vehicle_trips_vehicle` and `ex_vehicle_trips_driver` (EXCLUDE with btree_gist on `tstzrange(departs_at, arrives_at)`,
for trips that aren't cancelled): a vehicle, or a driver, can't be on two trips that overlap. Updating `seats_booked`
changes the trip's row version, so an edit loaded before a booking gets a concurrency conflict and reloads.

## `trip_passengers`

| Column | Notes |
|---|---|
| `code` | `TP00001` … per property (`property_counters` name `trip_passenger`) |
| `trip_id` | Same property; can't change after booking (`ck_trip_passengers_fixed`, raised by the trigger) |
| `reservation_id`, `guest_id`, `guest_name` | The stay and its guest, or only a name (2–100) |
| `seats`, `unit_price`, `amount` | 1–60 seats; `amount = seats × unit_price` (`ck_trip_passengers_amount`); seats can't change |
| `settlement` | `ROOM_CHARGE` (needs the stay and the charge's `folio_item_id`, amount above 0), `FREE`, `COMPLIMENTARY` (needs `complimentary_reason`, 3–200) |
| `note` | For the driver, e.g. "Pick up at the cottages" (up to 200) |
| `status` | `BOOKED` ↔ `BOARDED` (`boarded_at`, `boarded_by`), `BOOKED` → `NO_SHOW` (when the trip leaves), `BOOKED` or `BOARDED` → `CANCELLED` (`cancel_reason`, `cancelled_at`, `refund`) |
| `refund` | `REVERSED` (the charge is off the bill: `reversal_item_id`), `NONE` (nothing was charged) (`ck_trip_passengers_refund`) |

A passenger can be inserted only as `BOOKED` on a scheduled trip (`ck_trip_passengers_trip_open`). One passenger line per
charge (`ux_trip_passengers_charge`). The API refuses cancelling a boarded passenger (untick first); the database allows
`BOARDED` → `CANCELLED` so cancelling a whole trip never gets stuck.

## Dev seed (S018)

VAN1 Force Traveller (12 seats, MH07AB1234) and JEEP1 Mahindra Bolero (7 seats, MH07CD4321); made-up registrations.
Routes BEACH (Resort gate → Tarkarli beach) and BACK, 8 km, 20 minutes, ₹500 a seat. Trips today and the next 7 days: the
van to the beach 08:00, 10:00 and 16:00 and back 12:00 and 18:30; the jeep to the beach 11:00. No driver is set (the owner
adds a Driver account and picks it). Tomorrow: TP00001, four seats on the 08:00 for an agent group (complimentary). No
charges are seeded.
