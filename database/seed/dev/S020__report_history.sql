-- =====================================================================
-- DEV SEED S020 — a year of history for the dashboards and reports (Sprint 23)
--   * Past stays from the 1st of the month a year ago until a week ago: every room and dorm bed is booked on about
--     half of the nights (busier from October to May and at weekends, quiet in the monsoon), for 1–3 nights, from the
--     website, phone, walk-in, Booking.com, MakeMyTrip, Agoda or a travel agent. Each stay was checked in and out, has
--     its room nights on the bill with GST (12 % before 22 Sep 2025, 5 % after), dinner on some bills, a boat trip on a
--     few, and was paid in full by UPI or card on the day of departure.
--   * 64 guests for them (Ishaan Gawde, Sneha Sawant … with no phone or email, so they never look like duplicates).
--   * Budget departments for pay: Front office, Housekeeping, Kitchen and Restaurant (Food and beverage), Maintenance;
--     supplier bills of the kitchen store count for Food and beverage.
--   * The numbers are the same on every run (fixed random seed). The last week is left to the earlier sprints' demo data.
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when there is a booking more than 60 days old).
-- =====================================================================
DO $$
DECLARE
    p_id      uuid;
    o_id      uuid;
    tz        text;
    today     date;
    h_start   date;
    h_end     date;           -- the last departure
    plan_id   uuid;
    guests    uuid[] := '{}';
    gid       uuid;
    fn        text;
    ln        text;
    unit      record;
    bed       record;
    d         date;
    nights    int;
    p         float;
    ref_no    int;
    folio_no  int;
    pay_no    int;
    made      int := 0;
    src_codes text[] := ARRAY['WEBSITE','WEBSITE','PHONE','PHONE','WALK_IN','BOOKING_COM','BOOKING_COM','BOOKING_COM','MAKEMYTRIP','AGODA','TRAVEL_AGENT'];
BEGIN
    SELECT p.id, p.organization_id, p.timezone INTO p_id, o_id, tz
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S020: bootstrap property not found, skipped';
        RETURN;
    END IF;
    today := (now() AT TIME ZONE tz)::date;
    IF EXISTS (SELECT 1 FROM reservations WHERE property_id = p_id AND check_in < today - 60) THEN
        RAISE NOTICE 'dev seed S020: history already there, skipped';
        RETURN;
    END IF;
    SELECT id INTO plan_id FROM rate_plans WHERE property_id = p_id AND code = 'BAR';
    IF plan_id IS NULL OR NOT EXISTS (SELECT 1 FROM accommodation_units WHERE property_id = p_id) THEN
        RAISE NOTICE 'dev seed S020: demo rooms or rates (S001, S003) not found, skipped';
        RETURN;
    END IF;

    -- Budget departments for pay and supplier bills (Sprint 22 made the departments, Sprint 17 the employee ones).
    UPDATE departments d SET cost_centre_id = c.id
      FROM cost_centres c
     WHERE d.property_id = p_id AND c.property_id = p_id AND d.cost_centre_id IS NULL
       AND c.code = CASE d.code WHEN 'FRONT' THEN 'FO' WHEN 'HK' THEN 'HK' WHEN 'KITCHEN' THEN 'FB' WHEN 'REST' THEN 'FB'
                                WHEN 'MAINT' THEN 'MNT' END;
    UPDATE supplier_invoices si SET cost_centre_id = c.id
      FROM purchase_orders po
      JOIN stock_locations l ON l.id = po.location_id AND l.is_kitchen
      JOIN cost_centres c ON c.property_id = l.property_id AND c.code = 'FB'
     WHERE po.id = si.order_id AND si.property_id = p_id AND si.cost_centre_id IS NULL;

    PERFORM setseed(0.23);
    h_start := (date_trunc('month', today) - interval '12 months')::date;
    h_end := today - 7;

    FOREACH fn IN ARRAY ARRAY['Ishaan','Sneha','Nikhil','Tanvi','Omkar','Siddharth','Varun','Shruti'] LOOP
        FOREACH ln IN ARRAY ARRAY['Gawde','Sawant','Dessai','Prabhu','Shirodkar','Bapat','Gokhale','Ranade'] LOOP
            INSERT INTO guests (organization_id, first_name, last_name, nationality, created_at, updated_at)
            VALUES (o_id, fn, ln, 'IN', h_start::timestamp AT TIME ZONE tz, h_start::timestamp AT TIME ZONE tz)
            RETURNING id INTO gid;
            guests := guests || gid;
        END LOOP;
    END LOOP;

    SELECT coalesce(max(last_value), 0) INTO ref_no FROM property_counters WHERE property_id = p_id AND name = 'reservation';
    SELECT coalesce(max(last_value), 0) INTO folio_no FROM property_counters WHERE property_id = p_id AND name = 'folio';
    SELECT coalesce(max(last_value), 0) INTO pay_no FROM property_counters WHERE property_id = p_id AND name = 'payment';

    -- Rooms one at a time, then each dorm bed.
    FOR unit IN
        SELECT u.id AS unit_id, NULL::uuid AS bed_id, u.name, NULL::text AS bed_code, t.id AS type_id, t.inventory_mode,
               coalesce((SELECT round(avg(price)) FROM rate_plan_prices pr WHERE pr.accommodation_type_id = t.id), 3500) AS price
          FROM accommodation_units u JOIN accommodation_types t ON t.id = u.accommodation_type_id
         WHERE u.property_id = p_id AND u.is_active AND t.inventory_mode = 'UNIT'
        UNION ALL
        SELECT u.id, b.id, u.name, b.code, t.id, t.inventory_mode,
               coalesce((SELECT round(avg(price)) FROM rate_plan_prices pr WHERE pr.accommodation_type_id = t.id), 800)
          FROM beds b JOIN accommodation_units u ON u.id = b.unit_id JOIN accommodation_types t ON t.id = u.accommodation_type_id
         WHERE u.property_id = p_id AND b.is_active AND t.inventory_mode = 'BED'
         ORDER BY 6 DESC, 3, 4
    LOOP
        d := h_start;
        WHILE d < h_end LOOP
            p := CASE WHEN extract(month FROM d) IN (6, 7, 8, 9) THEN 0.22
                      WHEN extract(month FROM d) IN (11, 12, 1) THEN 0.62
                      ELSE 0.45 END
                 + CASE WHEN extract(isodow FROM d) IN (5, 6) THEN 0.2 ELSE 0 END
                 - CASE WHEN unit.bed_id IS NOT NULL THEN 0.12 ELSE 0 END;
            IF random() < p THEN
                nights := least(1 + floor(random() * 3)::int, h_end - d);
                ref_no := ref_no + 1;
                folio_no := folio_no + 1;
                pay_no := pay_no + 1;
                DECLARE
                    r_id    uuid := gen_random_uuid();
                    ru_id   uuid := gen_random_uuid();
                    f_id    uuid := gen_random_uuid();
                    pay_id  uuid := gen_random_uuid();
                    g       uuid := guests[1 + floor(random() * array_length(guests, 1))::int];
                    src     text := src_codes[1 + floor(random() * array_length(src_codes, 1))::int];
                    adults  int := CASE WHEN unit.bed_id IS NULL THEN 1 + (random() < 0.8)::int ELSE 1 END;
                    rate    numeric;
                    night   date;
                    item_id uuid;
                    amount  numeric;
                    tax_pct numeric;
                    tax     numeric;
                    total   numeric := 0;
                    stay_total numeric := 0;
                    arrived timestamptz := (d + time '14:00')::timestamp AT TIME ZONE tz;
                    left_at timestamptz := ((d + nights) + time '10:30')::timestamp AT TIME ZONE tz;
                BEGIN
                    -- The weekend and the busy months cost a little more.
                    rate := round(unit.price * (CASE WHEN extract(month FROM d) IN (6, 7, 8, 9) THEN 0.8
                                                     WHEN extract(month FROM d) IN (11, 12, 1) THEN 1.15 ELSE 1 END)
                                             * (CASE WHEN extract(isodow FROM d) IN (5, 6) THEN 1.1 ELSE 1 END) / 50) * 50;
                    INSERT INTO reservations (id, organization_id, property_id, reference, status, source_id, primary_guest_id, rate_plan_id,
                                              check_in, check_out, adults, children, total_amount, currency, status_changed_at,
                                              created_at, updated_at)
                    VALUES (r_id, o_id, p_id, 'R' || lpad(ref_no::text, 5, '0'), 'CONFIRMED',
                            (SELECT id FROM reservation_sources WHERE organization_id IS NULL AND code = src),
                            g, plan_id, d, d + nights, adults, 0, rate * nights, 'INR', arrived - interval '10 days',
                            arrived - interval '10 days', arrived - interval '10 days');
                    INSERT INTO reservation_guests (organization_id, property_id, reservation_id, guest_id) VALUES (o_id, p_id, r_id, g);
                    INSERT INTO reservation_units (id, organization_id, property_id, reservation_id, accommodation_type_id, unit_id, bed_id,
                                                   start_date, end_date, adults, children, holds_inventory)
                    VALUES (ru_id, o_id, p_id, r_id, unit.type_id, unit.unit_id, unit.bed_id, d, d + nights, adults, 0, false);
                    INSERT INTO reservation_rate_breakdowns (organization_id, property_id, reservation_id, reservation_unit_id, rate_plan_id,
                                                             stay_date, units, unit_price, base_amount, total)
                    SELECT o_id, p_id, r_id, ru_id, plan_id, n::date, 1, rate, rate, rate
                      FROM generate_series(d, d + nights - 1, interval '1 day') n;
                    UPDATE reservations SET status = 'CHECKED_IN', checked_in_at = arrived, status_changed_at = arrived WHERE id = r_id;
                    UPDATE reservations SET status = 'CHECKED_OUT', checked_out_at = left_at, status_changed_at = left_at WHERE id = r_id;
                    INSERT INTO reservation_status_history (organization_id, property_id, reservation_id, from_status, to_status, changed_at) VALUES
                        (o_id, p_id, r_id, NULL, 'CONFIRMED', arrived - interval '10 days'),
                        (o_id, p_id, r_id, 'CONFIRMED', 'CHECKED_IN', arrived),
                        (o_id, p_id, r_id, 'CHECKED_IN', 'CHECKED_OUT', left_at);

                    INSERT INTO folios (id, organization_id, property_id, reservation_id, number, name, is_primary, bill_to_guest_id, currency, created_at)
                    VALUES (f_id, o_id, p_id, r_id, 'F' || lpad(folio_no::text, 5, '0'), 'Main', true, g, 'INR', arrived);
                    tax_pct := CASE WHEN d < date '2025-09-22' THEN 12 ELSE 5 END;
                    FOR night IN SELECT n::date FROM generate_series(d, d + nights - 1, interval '1 day') n LOOP
                        tax := round(rate * tax_pct / 100, 2);
                        INSERT INTO folio_items (organization_id, property_id, folio_id, reservation_id, item_type, category, description,
                                                 service_date, quantity, unit_price, amount, tax_amount, total, source, posted_at)
                        VALUES (o_id, p_id, f_id, r_id, 'CHARGE', 'ROOM',
                                'Room night: ' || unit.name || coalesce(' bed ' || unit.bed_code, ''), night, 1, rate, rate, tax, rate + tax,
                                'ROOM_NIGHT', arrived)
                        RETURNING id INTO item_id;
                        INSERT INTO folio_item_taxes (organization_id, property_id, folio_item_id, tax_code, rate, taxable_value, amount) VALUES
                            (o_id, p_id, item_id, 'CGST', tax_pct / 2, rate, round(tax / 2, 2)),
                            (o_id, p_id, item_id, 'SGST', tax_pct / 2, rate, tax - round(tax / 2, 2));
                        total := total + rate + tax;
                        stay_total := stay_total + rate;
                        -- Dinner at the restaurant on some nights, a boat trip now and then.
                        IF random() < 0.35 THEN
                            amount := (6 + floor(random() * 30)::int) * 50;
                            tax := round(amount * 0.05, 2);
                            INSERT INTO folio_items (organization_id, property_id, folio_id, reservation_id, item_type, category, description,
                                                     service_date, quantity, unit_price, amount, tax_amount, total, source, posted_at)
                            VALUES (o_id, p_id, f_id, r_id, 'CHARGE', 'FOOD', 'Dinner at the beach shack', night, 1, amount, amount, tax,
                                    amount + tax, 'MANUAL', (night + time '21:30')::timestamp AT TIME ZONE tz)
                            RETURNING id INTO item_id;
                            INSERT INTO folio_item_taxes (organization_id, property_id, folio_item_id, tax_code, rate, taxable_value, amount) VALUES
                                (o_id, p_id, item_id, 'CGST', 2.5, amount, round(tax / 2, 2)),
                                (o_id, p_id, item_id, 'SGST', 2.5, amount, tax - round(tax / 2, 2));
                            total := total + amount + tax;
                        END IF;
                        IF random() < 0.08 THEN
                            amount := 800 * adults;
                            INSERT INTO folio_items (organization_id, property_id, folio_id, reservation_id, item_type, category, description,
                                                     service_date, quantity, unit_price, amount, tax_amount, total, source, posted_at)
                            VALUES (o_id, p_id, f_id, r_id, 'CHARGE', 'ACTIVITY', 'Dolphin boat trip', night, adults, 800, amount, 0, amount,
                                    'MANUAL', (night + time '09:00')::timestamp AT TIME ZONE tz);
                            total := total + amount;
                        END IF;
                    END LOOP;

                    INSERT INTO payments (id, organization_id, property_id, number, reservation_id, guest_id, method, status, amount, currency,
                                          note, received_at, captured_at, created_at, updated_at)
                    VALUES (pay_id, o_id, p_id, 'P' || lpad(pay_no::text, 5, '0'), r_id, g,
                            CASE WHEN random() < 0.6 THEN 'UPI' ELSE 'CARD' END, 'CAPTURED', total, 'INR', 'Paid at check-out',
                            left_at - interval '15 minutes', left_at - interval '15 minutes', left_at, left_at);
                    INSERT INTO payment_allocations (organization_id, property_id, payment_id, folio_id, amount, allocated_at)
                    VALUES (o_id, p_id, pay_id, f_id, total, left_at - interval '15 minutes');
                END;
                made := made + 1;
                d := d + nights;
            ELSE
                d := d + 1;
            END IF;
        END LOOP;
    END LOOP;

    INSERT INTO property_counters (property_id, name, last_value) VALUES
        (p_id, 'reservation', ref_no), (p_id, 'folio', folio_no), (p_id, 'payment', pay_no)
    ON CONFLICT (property_id, name) DO UPDATE SET last_value = greatest(property_counters.last_value, EXCLUDED.last_value);

    RAISE NOTICE 'dev seed S020: % past stays from % to %', made, h_start, h_end;
END $$;
