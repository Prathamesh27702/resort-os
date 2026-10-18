-- =====================================================================
-- DEV SEED S006 — something to do at the front desk (Sprint 7)
--   R00007  Room 105 (Standard)  yesterday → today      CHECKED_IN  phone    → on today's departures: check-out demo
--   R00008  Room 104 (Standard)  yesterday → in 2 days  CHECKED_IN  walk-in  → in-house
--   Room 101 is DIRTY (last guest left this morning), so today's arrival R00001 (Room 101) shows "Room 101 is dirty,
--   not ready": the review demo assigns it Room 102 (ready) and checks the guest in.
-- R00002 arrives in Room 105 tomorrow and R00003 in Room 104 in 3 days, so these stays don't overlap them.
-- Prices come from the BAR grid (S003); a night before the grid starts (yesterday) takes today's price.
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when R00007 exists).
-- =====================================================================
DO $$
DECLARE
    p_id    uuid;
    o_id    uuid;
    today   date;
    plan_id uuid;
    g       uuid[];
    s       record;
    u       record;
    r_id    uuid;
    ru_id   uuid;
    total   numeric(12,2);
BEGIN
    SELECT p.id, p.organization_id, (now() AT TIME ZONE p.timezone)::date INTO p_id, o_id, today
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S006: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM reservations WHERE property_id = p_id AND reference = 'R00007') THEN
        RAISE NOTICE 'dev seed S006: front desk demo already there, skipped';
        RETURN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM reservations WHERE property_id = p_id AND reference = 'R00006') THEN
        RAISE NOTICE 'dev seed S006: demo bookings (S004) not found, skipped';
        RETURN;
    END IF;
    SELECT id INTO plan_id FROM rate_plans WHERE property_id = p_id AND code = 'BAR';
    SELECT array_agg(id ORDER BY created_at, first_name) INTO g
      FROM (SELECT id, created_at, first_name FROM guests WHERE organization_id = o_id AND status = 'ACTIVE'
             ORDER BY created_at, first_name LIMIT 8) x;

    FOR s IN SELECT * FROM (VALUES
            (7, '105', 'PHONE',   g[8], today - 1, today,     2, 'Asked for a taxi to the airport at 11.'),
            (8, '104', 'WALK_IN', g[6], today - 1, today + 2, 1, NULL)
        ) AS t(ref_no, unit_code, src_code, guest_id, d_in, d_out, adults, note)
    LOOP
        SELECT un.id, un.accommodation_type_id INTO u FROM accommodation_units un WHERE un.property_id = p_id AND un.code = s.unit_code;
        IF EXISTS (SELECT 1 FROM reservation_units WHERE unit_id = u.id AND holds_inventory
                    AND stay && daterange(s.d_in, s.d_out, '[)'))
           OR EXISTS (SELECT 1 FROM unit_blocks WHERE unit_id = u.id AND status = 'ACTIVE'
                       AND period && daterange(s.d_in, s.d_out, '[)')) THEN
            RAISE NOTICE 'dev seed S006: Room % is taken, R0000% skipped', s.unit_code, s.ref_no;
            CONTINUE;
        END IF;
        r_id := gen_random_uuid();
        SELECT sum(coalesce(
                   (SELECT price FROM rate_plan_prices WHERE rate_plan_id = plan_id AND accommodation_type_id = u.accommodation_type_id AND stay_date = d::date),
                   (SELECT price FROM rate_plan_prices WHERE rate_plan_id = plan_id AND accommodation_type_id = u.accommodation_type_id AND stay_date = today)))
          INTO total FROM generate_series(s.d_in, s.d_out - 1, interval '1 day') AS d;

        INSERT INTO reservations (id, organization_id, property_id, reference, status, source_id, primary_guest_id, rate_plan_id,
                                  check_in, check_out, adults, children, total_amount, currency)
        VALUES (r_id, o_id, p_id, 'R' || lpad(s.ref_no::text, 5, '0'), 'CONFIRMED',
                (SELECT id FROM reservation_sources WHERE organization_id IS NULL AND code = s.src_code),
                s.guest_id, plan_id, s.d_in, s.d_out, s.adults, 0, total, 'INR');
        INSERT INTO reservation_guests (organization_id, property_id, reservation_id, guest_id) VALUES (o_id, p_id, r_id, s.guest_id);
        INSERT INTO reservation_units (organization_id, property_id, reservation_id, accommodation_type_id, unit_id,
                                       start_date, end_date, adults, children, holds_inventory)
        VALUES (o_id, p_id, r_id, u.accommodation_type_id, u.id, s.d_in, s.d_out, s.adults, 0, true)
        RETURNING id INTO ru_id;
        INSERT INTO reservation_rate_breakdowns (organization_id, property_id, reservation_id, reservation_unit_id, rate_plan_id,
                                                 stay_date, units, unit_price, base_amount, total)
        SELECT o_id, p_id, r_id, ru_id, plan_id, d::date, 1, x.price, x.price, x.price
          FROM generate_series(s.d_in, s.d_out - 1, interval '1 day') AS d
          CROSS JOIN LATERAL (SELECT coalesce(
                   (SELECT price FROM rate_plan_prices WHERE rate_plan_id = plan_id AND accommodation_type_id = u.accommodation_type_id AND stay_date = d::date),
                   (SELECT price FROM rate_plan_prices WHERE rate_plan_id = plan_id AND accommodation_type_id = u.accommodation_type_id AND stay_date = today)) AS price) x;

        -- booked, then checked in yesterday afternoon (the status graph allows only CONFIRMED → CHECKED_IN)
        UPDATE reservations SET status = 'CHECKED_IN', status_changed_at = (s.d_in + time '14:30') AT TIME ZONE 'Asia/Kolkata',
                                checked_in_at = (s.d_in + time '14:30') AT TIME ZONE 'Asia/Kolkata'
         WHERE id = r_id;
        INSERT INTO reservation_status_history (organization_id, property_id, reservation_id, from_status, to_status, changed_at) VALUES
            (o_id, p_id, r_id, NULL, 'CONFIRMED', (s.d_in + time '10:00') AT TIME ZONE 'Asia/Kolkata'),
            (o_id, p_id, r_id, 'CONFIRMED', 'CHECKED_IN', (s.d_in + time '14:30') AT TIME ZONE 'Asia/Kolkata');
        IF s.note IS NOT NULL THEN
            INSERT INTO reservation_notes (organization_id, property_id, reservation_id, note) VALUES (o_id, p_id, r_id, s.note);
        END IF;
    END LOOP;

    INSERT INTO property_counters (property_id, name, last_value) VALUES (p_id, 'reservation', 8)
    ON CONFLICT (property_id, name) DO UPDATE SET last_value = greatest(property_counters.last_value, 8);

    -- Room 101 was used last night by a guest who has left; housekeeping hasn't been yet.
    SELECT id INTO ru_id FROM accommodation_units WHERE property_id = p_id AND code = '101';
    INSERT INTO unit_housekeeping_status (organization_id, property_id, unit_id, status, reason)
    VALUES (o_id, p_id, ru_id, 'DIRTY', 'Last guest left this morning')
    ON CONFLICT (unit_id, bed_id) DO NOTHING;
    INSERT INTO unit_housekeeping_history (organization_id, property_id, unit_id, from_status, to_status, source, reason)
    VALUES (o_id, p_id, ru_id, 'READY', 'DIRTY', 'MANUAL', 'Last guest left this morning');

    RAISE NOTICE 'dev seed S006: R00007 (departing today) and R00008 (in house) checked in; Room 101 dirty';
END $$;
