-- =====================================================================
-- DEV SEED S018 — beach transport (Sprint 21)
--   Vehicles: VAN1 Force Traveller (12 seats, MH07AB1234) and JEEP1 Mahindra Bolero (7 seats, MH07CD4321). Both
--   registrations are made up.
--   Routes: BEACH Beach shuttle (Resort gate → Tarkarli beach, 8 km, 20 minutes, ₹500 a seat) and BACK Beach return
--   (Tarkarli beach → Resort gate, the same).
--   Trips for today and the next 7 days in the van: to the beach at 08:00, 10:00 and 16:00, back at 12:00 and 18:30; the
--   jeep to the beach at 11:00. No driver is chosen (the dev seed makes no staff accounts): in the review demo the owner
--   adds a user with the Driver role and puts them on a trip.
--   Passengers (none on a guest's bill, so the earlier sprints' folio demos keep their totals): tomorrow's 08:00 has 4 of
--   12 seats taken by the travel agent's group (complimentary). Book an in-house guest on it in the review demo.
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when the property has vehicles).
-- =====================================================================
DO $$
DECLARE
    p_id      uuid;
    o_id      uuid;
    tz        text;
    owner_id  uuid;
    today     date;
    van       uuid;
    jeep      uuid;
    t_id      uuid;
    d         date;
    x         record;
BEGIN
    SELECT p.id, p.organization_id, p.timezone INTO p_id, o_id, tz
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S018: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM vehicles WHERE property_id = p_id) THEN
        RAISE NOTICE 'dev seed S018: transport demo already there, skipped';
        RETURN;
    END IF;
    SELECT id INTO owner_id FROM users WHERE organization_id = o_id ORDER BY created_at LIMIT 1;
    today := (now() AT TIME ZONE tz)::date;

    INSERT INTO vehicles (organization_id, property_id, code, name, registration, seats, note, sort_order, created_by, updated_by) VALUES
        (o_id, p_id, 'VAN1',  'Force Traveller', 'MH07AB1234', 12, 'Roof rack for beach bags', 10, owner_id, owner_id),
        (o_id, p_id, 'JEEP1', 'Mahindra Bolero', 'MH07CD4321', 7,  NULL,                       20, owner_id, owner_id);
    SELECT id INTO van  FROM vehicles WHERE property_id = p_id AND code = 'VAN1';
    SELECT id INTO jeep FROM vehicles WHERE property_id = p_id AND code = 'JEEP1';

    INSERT INTO transport_routes (organization_id, property_id, code, name, origin, destination, distance_km, duration_minutes, price, sort_order,
                                  created_by, updated_by) VALUES
        (o_id, p_id, 'BEACH', 'Beach shuttle', 'Resort gate',    'Tarkarli beach', 8, 20, 500, 10, owner_id, owner_id),
        (o_id, p_id, 'BACK',  'Beach return',  'Tarkarli beach', 'Resort gate',    8, 20, 500, 20, owner_id, owner_id);

    -- Trips: today and the next 7 days (today's that have already left are left out).
    FOR x IN SELECT v.*, r.id AS route_id, r.duration_minutes, r.price, ve.id AS vehicle_id, ve.seats
               FROM (VALUES ('BEACH', 'VAN1', time '08:00'), ('BEACH', 'VAN1', time '10:00'), ('BACK', 'VAN1', time '12:00'),
                            ('BEACH', 'VAN1', time '16:00'), ('BACK', 'VAN1', time '18:30'), ('BEACH', 'JEEP1', time '11:00'))
                    AS v(route, vehicle, at)
               JOIN transport_routes r ON r.property_id = p_id AND r.code = v.route
               JOIN vehicles ve ON ve.property_id = p_id AND ve.code = v.vehicle
    LOOP
        FOR i IN 0..7 LOOP
            d := today + i;
            CONTINUE WHEN (d + x.at) AT TIME ZONE tz <= now();
            INSERT INTO vehicle_trips (organization_id, property_id, route_id, vehicle_id, trip_date, departs_at, arrives_at, capacity, price,
                                       created_by, updated_by)
            VALUES (o_id, p_id, x.route_id, x.vehicle_id, d, (d + x.at) AT TIME ZONE tz,
                    (d + x.at + make_interval(mins => x.duration_minutes)) AT TIME ZONE tz, x.seats, x.price, owner_id, owner_id);
        END LOOP;
    END LOOP;

    -- Tomorrow: the agent's group on the 08:00 shuttle (complimentary).
    SELECT t.id INTO t_id FROM vehicle_trips t JOIN transport_routes r ON r.id = t.route_id
     WHERE r.property_id = p_id AND r.code = 'BEACH' AND t.vehicle_id = van AND t.departs_at = ((today + 1) + time '08:00') AT TIME ZONE tz;
    INSERT INTO trip_passengers (organization_id, property_id, code, trip_id, guest_name, seats, unit_price, amount, settlement, complimentary_reason,
                                 note, created_by, updated_by)
    VALUES (o_id, p_id, 'TP00001', t_id, 'Coastline Travels (agent group)', 4, 500, 2000, 'COMPLIMENTARY', 'Travel agent familiarisation trip',
            'Drop at the boat jetty end of the beach', owner_id, owner_id);

    INSERT INTO property_counters (property_id, name, last_value) VALUES (p_id, 'trip_passenger', 1)
    ON CONFLICT (property_id, name) DO UPDATE SET last_value = EXCLUDED.last_value;
END $$;
