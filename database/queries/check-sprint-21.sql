-- Database checks for Sprint 21 (beach transport). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 21. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 21).

-- 1. Migration V023 applied. Expect V001 … V023.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new tables exist. Expect 4 rows.
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('vehicles', 'transport_routes', 'vehicle_trips', 'trip_passengers')
ORDER BY table_name;

-- 3. The guards are there. Expect 6 rows: seats kept by the passengers, the trip's forward-only moves, the seats CHECK,
--    a vehicle and a driver on one trip at a time, and folio source TRANSPORT allowed.
SELECT tgname AS name, tgrelid::regclass AS on_table FROM pg_trigger
WHERE tgname IN ('trg_trip_passengers_seats', 'trg_vehicle_trips_status')
UNION ALL
SELECT conname, conrelid::regclass FROM pg_constraint
WHERE conname IN ('ck_vehicle_trips_capacity', 'ex_vehicle_trips_vehicle', 'ex_vehicle_trips_driver')
   OR (conname = 'ck_folio_items_source' AND pg_get_constraintdef(oid) LIKE '%TRANSPORT%')
ORDER BY 1;

-- 4. Vehicles and routes. (demo) VAN1 Force Traveller 12 seats, JEEP1 Mahindra Bolero 7; BEACH and BACK, 8 km, 20 min, ₹500.
SELECT code, name, registration, seats, is_active FROM vehicles ORDER BY sort_order, code;
SELECT code, name, origin, destination, distance_km, duration_minutes, price, is_active FROM transport_routes ORDER BY sort_order, code;

-- 5. Seats booked on every trip equal the seats of its passengers that aren't cancelled, and never go past the seats.
--    Expect NO rows.
SELECT t.id, t.trip_date, t.departs_at, t.capacity, t.seats_booked,
       coalesce(sum(p.seats) FILTER (WHERE p.status <> 'CANCELLED'), 0) AS counted
FROM vehicle_trips t LEFT JOIN trip_passengers p ON p.trip_id = t.id
GROUP BY t.id
HAVING t.seats_booked <> coalesce(sum(p.seats) FILTER (WHERE p.status <> 'CANCELLED'), 0) OR t.seats_booked > t.capacity;

-- 6. No vehicle and no driver on two trips at the same time (the EXCLUDE constraints refuse it). Expect NO rows.
SELECT a.id, b.id AS clashes_with, a.vehicle_id, a.driver_user_id
FROM vehicle_trips a JOIN vehicle_trips b ON b.id > a.id AND a.status <> 'CANCELLED' AND b.status <> 'CANCELLED'
 AND tstzrange(a.departs_at, a.arrives_at) && tstzrange(b.departs_at, b.arrives_at)
 AND (a.vehicle_id = b.vehicle_id OR a.driver_user_id = b.driver_user_id);

-- 7. Tomorrow's trips with the seats left. (demo) BEACH 08:00 4/12 (the agent group), 10:00, JEEP 11:00, BACK 12:00,
--    BEACH 16:00, BACK 18:30; no driver until the owner adds one.
SELECT r.code AS route, to_char(t.departs_at AT TIME ZONE 'Asia/Kolkata', 'HH24:MI') AS departs, v.code AS vehicle,
       u.full_name AS driver, t.status, t.seats_booked, t.capacity, t.capacity - t.seats_booked AS seats_left
FROM vehicle_trips t JOIN transport_routes r ON r.id = t.route_id JOIN vehicles v ON v.id = t.vehicle_id
LEFT JOIN users u ON u.id = t.driver_user_id
WHERE t.trip_date = (now() AT TIME ZONE 'Asia/Kolkata')::date + 1
ORDER BY t.departs_at, r.code;

-- 8. Every seat put on a bill has exactly its TRANSPORT charge there, and a refunded one has its reversal. Expect NO rows.
SELECT p.code, p.status, p.refund, p.amount, i.amount AS charged, x.amount AS reversed
FROM trip_passengers p
LEFT JOIN folio_items i ON i.id = p.folio_item_id
LEFT JOIN folio_items x ON x.id = p.reversal_item_id
WHERE p.settlement = 'ROOM_CHARGE'
  AND (i.id IS NULL OR i.amount <> p.amount OR i.source <> 'TRANSPORT' OR i.category <> 'TRANSPORT'
       OR (p.refund = 'REVERSED' AND (x.id IS NULL OR x.amount <> -p.amount)));

-- 9. A trip that left has nobody still "booked": each passenger is on board, a no-show or cancelled. Expect NO rows.
SELECT t.id, p.code, p.status
FROM vehicle_trips t JOIN trip_passengers p ON p.trip_id = t.id
WHERE t.status IN ('DEPARTED', 'COMPLETED') AND p.status = 'BOOKED';

-- 10. Passengers with how they paid and what happened. (demo) TP00001 complimentary agent group of 4; after the review,
--     a guest's seats on their bill, ticked on board by the driver.
SELECT p.code, r.code AS route, to_char(t.departs_at AT TIME ZONE 'Asia/Kolkata', 'DD Mon HH24:MI') AS trip, p.guest_name, p.seats,
       p.settlement, p.amount, p.status, p.boarded_at, p.refund, p.cancel_reason
FROM trip_passengers p JOIN vehicle_trips t ON t.id = p.trip_id JOIN transport_routes r ON r.id = t.route_id
ORDER BY p.code;

-- 11. Who did what to trips and passengers (made, left, arrived, boarded, cancelled), newest first. The demo seed's
--     rows were inserted directly, so they have no audit entries.
SELECT occurred_at, entity_type, action, reason
FROM audit_logs WHERE entity_type IN ('VehicleTrip', 'TripPassenger', 'Vehicle', 'TransportRoute')
ORDER BY occurred_at DESC LIMIT 30;
