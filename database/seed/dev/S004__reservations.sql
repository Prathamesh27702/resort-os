-- =====================================================================
-- DEV SEED S004 — demo reservations for the bootstrap property (brief §71)
--   R00001  Room 101 (Deluxe)     tonight, 2 nights   CONFIRMED  walk-in      → only Room 102 is left in Deluxe tonight,
--                                                                              for the "last room from two browsers" demo
--   R00002  Room 105 (Standard)   tomorrow, 3 nights  CONFIRMED  Booking.com
--   R00003  Room 104 (Standard)   in 3 days, 2 nights PENDING    phone
--   R00004  Dormitory beds B05–B06 tonight, 3 nights  CONFIRMED  own website  (two guests)
--   R00005  Room 106 (Standard)   in 7 days, 2 nights INQUIRY    WhatsApp     (holds nothing)
--   R00006  Room 106 (Standard)   in 2 days, 2 nights CANCELLED  phone        (confirmed, then cancelled)
-- Room 103 and dorm beds B01–B04 are left free: the Sprint 02 review blocks them.
-- Prices come from the BAR grid (S003), night by night, like the API does. Every status change is in the history.
-- Runs ONLY in Development (DevSeed:Enabled) and ONLY if the property has no reservations yet.
-- =====================================================================
CREATE OR REPLACE FUNCTION pg_temp.seed_booking(
    o_id uuid, p_id uuid, plan_id uuid, ref_no int, initial_status text, src_code text, guest_ids uuid[],
    unit_code text, bed_codes text[], d_in date, d_out date, adults int, note text) RETURNS uuid
LANGUAGE plpgsql AS $$
DECLARE
    r_id    uuid := gen_random_uuid();
    u       record;
    ru_id   uuid;
    b_id    uuid;
    holds   boolean := initial_status IN ('PENDING', 'CONFIRMED');
    n_units int := greatest(coalesce(array_length(bed_codes, 1), 0), 1);
    total   numeric(12,2);
BEGIN
    SELECT un.id, un.accommodation_type_id INTO u FROM accommodation_units un WHERE un.property_id = p_id AND un.code = unit_code;
    SELECT coalesce(sum(price), 0) * n_units INTO total
      FROM rate_plan_prices WHERE rate_plan_id = plan_id AND accommodation_type_id = u.accommodation_type_id
       AND stay_date >= d_in AND stay_date < d_out;

    INSERT INTO reservations (id, organization_id, property_id, reference, status, source_id, primary_guest_id, rate_plan_id,
                              check_in, check_out, adults, children, total_amount, currency)
    VALUES (r_id, o_id, p_id, 'R' || lpad(ref_no::text, 5, '0'), initial_status,
            (SELECT id FROM reservation_sources WHERE organization_id IS NULL AND code = src_code),
            guest_ids[1], plan_id, d_in, d_out, adults, 0, total, 'INR');
    INSERT INTO reservation_status_history (organization_id, property_id, reservation_id, from_status, to_status)
    VALUES (o_id, p_id, r_id, NULL, initial_status);
    INSERT INTO reservation_guests (organization_id, property_id, reservation_id, guest_id)
    SELECT o_id, p_id, r_id, g FROM unnest(guest_ids) AS g;

    FOR i IN 1..n_units LOOP
        b_id := NULL;
        IF bed_codes IS NOT NULL THEN
            SELECT id INTO b_id FROM beds WHERE unit_id = u.id AND code = bed_codes[i];
        END IF;
        INSERT INTO reservation_units (organization_id, property_id, reservation_id, accommodation_type_id, unit_id, bed_id,
                                       start_date, end_date, adults, children, holds_inventory)
        VALUES (o_id, p_id, r_id, u.accommodation_type_id, u.id, b_id, d_in, d_out,
                CASE WHEN b_id IS NULL THEN adults ELSE 1 END, 0, holds)
        RETURNING id INTO ru_id;
        INSERT INTO reservation_rate_breakdowns (organization_id, property_id, reservation_id, reservation_unit_id, rate_plan_id,
                                                 stay_date, units, unit_price, base_amount, total)
        SELECT o_id, p_id, r_id, ru_id, plan_id, stay_date, 1, price, price, price
          FROM rate_plan_prices WHERE rate_plan_id = plan_id AND accommodation_type_id = u.accommodation_type_id
           AND stay_date >= d_in AND stay_date < d_out;
    END LOOP;

    IF note IS NOT NULL THEN
        INSERT INTO reservation_notes (organization_id, property_id, reservation_id, note) VALUES (o_id, p_id, r_id, note);
    END IF;
    RETURN r_id;
END $$;

DO $$
DECLARE
    p_id    uuid;
    o_id    uuid;
    today   date;
    plan_id uuid;
    g       uuid[];
    r_id    uuid;
BEGIN
    SELECT p.id, p.organization_id, (now() AT TIME ZONE p.timezone)::date INTO p_id, o_id, today
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S004: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM reservations WHERE property_id = p_id) THEN
        RAISE NOTICE 'dev seed S004: property already has reservations, skipped';
        RETURN;
    END IF;
    SELECT id INTO plan_id FROM rate_plans WHERE property_id = p_id AND code = 'BAR';
    SELECT array_agg(id ORDER BY created_at, first_name) INTO g
      FROM (SELECT id, created_at, first_name FROM guests WHERE organization_id = o_id AND status = 'ACTIVE'
             ORDER BY created_at, first_name LIMIT 8) x;
    IF plan_id IS NULL OR coalesce(array_length(g, 1), 0) < 8
       OR NOT EXISTS (SELECT 1 FROM accommodation_units WHERE property_id = p_id AND code = '101') THEN
        RAISE NOTICE 'dev seed S004: demo rooms, guests or rates (S001–S003) not found, skipped';
        RETURN;
    END IF;

    PERFORM pg_temp.seed_booking(o_id, p_id, plan_id, 1, 'CONFIRMED', 'WALK_IN',     ARRAY[g[1]],       '101',  NULL, today,     today + 2, 2, NULL);
    PERFORM pg_temp.seed_booking(o_id, p_id, plan_id, 2, 'CONFIRMED', 'BOOKING_COM', ARRAY[g[2]],       '105',  NULL, today + 1, today + 4, 2, 'Booking.com prepaid. Late arrival, around 11 pm.');
    PERFORM pg_temp.seed_booking(o_id, p_id, plan_id, 3, 'PENDING',   'PHONE',       ARRAY[g[3]],       '104',  NULL, today + 3, today + 5, 1, 'Will send the advance by UPI.');
    PERFORM pg_temp.seed_booking(o_id, p_id, plan_id, 4, 'CONFIRMED', 'WEBSITE',     ARRAY[g[4], g[5]], 'DORM', ARRAY['B05', 'B06'], today, today + 3, 2, NULL);
    PERFORM pg_temp.seed_booking(o_id, p_id, plan_id, 5, 'INQUIRY',   'WHATSAPP',    ARRAY[g[6]],       '106',  NULL, today + 7, today + 9, 2, 'Asked about a sea-facing room.');
    r_id := pg_temp.seed_booking(o_id, p_id, plan_id, 6, 'CONFIRMED', 'PHONE',       ARRAY[g[7]],       '106',  NULL, today + 2, today + 4, 2, NULL);
    UPDATE reservation_units SET holds_inventory = false WHERE reservation_id = r_id;
    UPDATE reservations SET status = 'CANCELLED', status_changed_at = now() WHERE id = r_id;
    INSERT INTO reservation_status_history (organization_id, property_id, reservation_id, from_status, to_status, reason)
    VALUES (o_id, p_id, r_id, 'CONFIRMED', 'CANCELLED', 'Guest changed plans');

    INSERT INTO property_counters (property_id, name, last_value) VALUES (p_id, 'reservation', 6)
    ON CONFLICT (property_id, name) DO UPDATE SET last_value = greatest(property_counters.last_value, 6);

    RAISE NOTICE 'dev seed S004: 6 reservations created (R00001–R00006)';
END $$;
