-- =====================================================================
-- DEV SEED S017 — activities and equipment (Sprint 20)
--   Equipment: 12 life jackets (LJ-01…LJ-12), 6 kayaks (KY-01…KY-06), 8 snorkel sets (SN-01…SN-08), 8 bicycles
--   (BC-01…BC-08). KY-06 is in maintenance (hull crack), SN-08 is damaged (mask strap torn), BC-08 is lost.
--   Activities: Kayaking (90 min, 6 seats, ₹800 a person, a life jacket each), Sunrise yoga (60 min, 20 seats, free),
--   Snorkelling trip (3 h, 8 seats, ₹1,500, a snorkel set each), Village cycle tour (2 h, 10 seats, ₹500, a bicycle each).
--   Sessions for today and the next 7 days: kayaking 07:00 and 16:00, yoga 06:30, snorkelling 10:00, cycling 16:30.
--   Bookings (none on a guest's bill, so the earlier sprints' folio demos keep their totals): tomorrow's 07:00 kayaking
--   has 4 of 6 seats taken by a travel agent's familiarisation group (complimentary), and two yoga regulars are on
--   tomorrow's yoga (free). Book an in-house guest on the remaining kayak seats in the review demo.
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when the property has activities).
-- =====================================================================
DO $$
DECLARE
    p_id      uuid;
    o_id      uuid;
    tz        text;
    owner_id  uuid;
    today     date;
    lj        uuid;
    ky        uuid;
    sn        uuid;
    bc        uuid;
    kayak     uuid;
    yoga      uuid;
    s_id      uuid;
    d         date;
    a         record;
    t         time;
BEGIN
    SELECT p.id, p.organization_id, p.timezone INTO p_id, o_id, tz
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S017: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM activities WHERE property_id = p_id) THEN
        RAISE NOTICE 'dev seed S017: activities demo already there, skipped';
        RETURN;
    END IF;
    SELECT id INTO owner_id FROM users WHERE organization_id = o_id ORDER BY created_at LIMIT 1;
    today := (now() AT TIME ZONE tz)::date;

    -- Equipment
    INSERT INTO equipment_types (organization_id, property_id, code, name, sort_order, created_by, updated_by) VALUES
        (o_id, p_id, 'LJ', 'Life jacket',  10, owner_id, owner_id),
        (o_id, p_id, 'KY', 'Kayak',        20, owner_id, owner_id),
        (o_id, p_id, 'SN', 'Snorkel set',  30, owner_id, owner_id),
        (o_id, p_id, 'BC', 'Bicycle',      40, owner_id, owner_id);
    SELECT id INTO lj FROM equipment_types WHERE property_id = p_id AND code = 'LJ';
    SELECT id INTO ky FROM equipment_types WHERE property_id = p_id AND code = 'KY';
    SELECT id INTO sn FROM equipment_types WHERE property_id = p_id AND code = 'SN';
    SELECT id INTO bc FROM equipment_types WHERE property_id = p_id AND code = 'BC';
    INSERT INTO equipment (organization_id, property_id, equipment_type_id, tag, created_by, updated_by)
    SELECT o_id, p_id, x.type_id, x.prefix || '-' || lpad(n::text, 2, '0'), owner_id, owner_id
      FROM (VALUES (lj, 'LJ', 12), (ky, 'KY', 6), (sn, 'SN', 8), (bc, 'BC', 8)) AS x(type_id, prefix, how_many)
     CROSS JOIN LATERAL generate_series(1, x.how_many) AS n;
    -- Through the allowed moves (the trigger checks them): out of use with the reason.
    UPDATE equipment SET status = 'MAINTENANCE', status_note = 'Hull crack near the seat; with the fibreglass repairer'
     WHERE property_id = p_id AND tag = 'KY-06';
    UPDATE equipment SET status = 'DAMAGED', status_note = 'Mask strap torn' WHERE property_id = p_id AND tag = 'SN-08';
    UPDATE equipment SET status = 'LOST', status_note = 'Not back from the village tour; guest says it was left at the temple'
     WHERE property_id = p_id AND tag = 'BC-08';

    -- Activities
    INSERT INTO activities (organization_id, property_id, code, name, description, duration_minutes, capacity, price, meeting_point,
                            weather_dependent, equipment_type_id, sort_order, created_by, updated_by) VALUES
        (o_id, p_id, 'KAYAK',   'Kayaking',            'Guided paddle through the mangroves; no experience needed.', 90, 6, 800, 'Boat jetty', true, lj, 10, owner_id, owner_id),
        (o_id, p_id, 'YOGA',    'Sunrise yoga',        'Hatha yoga on the beach deck. Mats provided.',                60, 20, 0, 'Beach deck', true, NULL, 20, owner_id, owner_id),
        (o_id, p_id, 'SNORKEL', 'Snorkelling trip',    'Boat to Grande Island, two snorkel stops, lunch on board.',  180, 8, 1500, 'Boat jetty', true, sn, 30, owner_id, owner_id),
        (o_id, p_id, 'CYCLE',   'Village cycle tour',  'Paddy fields, a spice farm and the old church.',            120, 10, 500, 'Reception', false, bc, 40, owner_id, owner_id);
    SELECT id INTO kayak FROM activities WHERE property_id = p_id AND code = 'KAYAK';
    SELECT id INTO yoga  FROM activities WHERE property_id = p_id AND code = 'YOGA';

    -- Sessions: today and the next 7 days (today's that have already started are left out).
    FOR a IN SELECT x.* , ac.id AS activity_id, ac.duration_minutes, ac.capacity, ac.price
               FROM (VALUES ('KAYAK', time '07:00'), ('KAYAK', time '16:00'), ('YOGA', time '06:30'), ('SNORKEL', time '10:00'),
                            ('CYCLE', time '16:30')) AS x(code, at)
               JOIN activities ac ON ac.property_id = p_id AND ac.code = x.code
    LOOP
        FOR i IN 0..7 LOOP
            d := today + i;
            t := a.at;
            CONTINUE WHEN (d + t) AT TIME ZONE tz <= now();
            INSERT INTO activity_schedules (organization_id, property_id, activity_id, session_date, starts_at, ends_at, capacity, price,
                                            created_by, updated_by)
            VALUES (o_id, p_id, a.activity_id, d, (d + t) AT TIME ZONE tz, (d + t + make_interval(mins => a.duration_minutes)) AT TIME ZONE tz,
                    a.capacity, a.price, owner_id, owner_id);
        END LOOP;
    END LOOP;

    -- Tomorrow: the agent's group on the 07:00 kayaking (complimentary), two regulars on yoga (free).
    SELECT id INTO s_id FROM activity_schedules WHERE activity_id = kayak AND session_date = today + 1 AND starts_at = ((today + 1) + time '07:00') AT TIME ZONE tz;
    INSERT INTO activity_bookings (organization_id, property_id, code, schedule_id, activity_id, guest_name, seats, unit_price, amount, settlement,
                                   complimentary_reason, note, created_by, updated_by)
    VALUES (o_id, p_id, 'AB00001', s_id, kayak, 'Coastline Travels (agent group)', 4, 800, 3200, 'COMPLIMENTARY',
            'Travel agent familiarisation trip', 'Two have never kayaked', owner_id, owner_id);
    SELECT id INTO s_id FROM activity_schedules WHERE activity_id = yoga AND session_date = today + 1;
    INSERT INTO activity_bookings (organization_id, property_id, code, schedule_id, activity_id, guest_name, seats, unit_price, amount, settlement,
                                   created_by, updated_by)
    VALUES (o_id, p_id, 'AB00002', s_id, yoga, 'Meera Kulkarni', 1, 0, 0, 'FREE', owner_id, owner_id),
           (o_id, p_id, 'AB00003', s_id, yoga, 'Daniel Fernandes', 1, 0, 0, 'FREE', owner_id, owner_id);

    INSERT INTO property_counters (property_id, name, last_value) VALUES (p_id, 'activity_booking', 3)
    ON CONFLICT (property_id, name) DO UPDATE SET last_value = EXCLUDED.last_value;
END $$;
