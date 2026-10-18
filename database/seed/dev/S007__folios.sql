-- =====================================================================
-- DEV SEED S007 — bills to look at (Sprint 8)
--   * a main folio for every demo booking that is confirmed, in house or checked out (fn_open_missing_folios, V010)
--   * the room nights of the guests already in house (R00007, R00008), as check-in would have posted them
--   * R00008 (Room 104, in house): dinner yesterday and an airport pick-up → a folio with room, food and transport
--   * R00007 (Room 105, leaving today): laundry
--   * frontdesk.allow_checkout_with_balance = true on the demo property: payments arrive in Sprint 10, so until then a
--     guest with a bill could never check out in the demo. Real properties keep the default (off).
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when R00008's folio already has lines).
-- =====================================================================
DO $$
DECLARE
    p_id    uuid;
    o_id    uuid;
    today   date;
    r       record;
    f_id    uuid;
    opened  integer;
BEGIN
    SELECT p.id, p.organization_id, (now() AT TIME ZONE p.timezone)::date INTO p_id, o_id, today
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S007: bootstrap property not found, skipped';
        RETURN;
    END IF;

    opened := fn_open_missing_folios();

    INSERT INTO property_settings (organization_id, property_id, key, value)
    VALUES (o_id, p_id, 'frontdesk.allow_checkout_with_balance', 'true'::jsonb)
    ON CONFLICT DO NOTHING;

    IF EXISTS (SELECT 1 FROM folio_items i JOIN reservations x ON x.id = i.reservation_id
                WHERE x.property_id = p_id AND x.reference = 'R00008') THEN
        RAISE NOTICE 'dev seed S007: % folios opened; demo charges already there, skipped', opened;
        RETURN;
    END IF;

    -- Room nights of the guests in house: one line per night, all of the booking's rooms that night.
    FOR r IN SELECT x.id, f.id AS folio_id, b.stay_date, sum(b.total) AS price, coalesce(x.checked_in_at, now()) AS at,
                    string_agg(DISTINCT un.name, ', ') AS rooms
               FROM reservations x
               JOIN folios f ON f.reservation_id = x.id AND f.is_primary
               JOIN reservation_units u ON u.reservation_id = x.id AND u.superseded_at IS NULL
               JOIN accommodation_units un ON un.id = u.unit_id
               JOIN reservation_rate_breakdowns b ON b.reservation_unit_id = u.id
              WHERE x.property_id = p_id AND x.status = 'CHECKED_IN'
                AND NOT EXISTS (SELECT 1 FROM folio_items i WHERE i.reservation_id = x.id AND i.service_date = b.stay_date
                                   AND i.source = 'ROOM_NIGHT' AND i.item_type = 'CHARGE' AND i.transferred_from_item_id IS NULL)
              GROUP BY x.id, x.checked_in_at, f.id, b.stay_date
    LOOP
        INSERT INTO folio_items (organization_id, property_id, folio_id, reservation_id, item_type, category, description,
                                 service_date, quantity, unit_price, amount, total, source, posted_at)
        VALUES (o_id, p_id, r.folio_id, r.id, 'CHARGE', 'ROOM', 'Room night: ' || r.rooms, r.stay_date, 1, r.price, r.price, r.price,
                'ROOM_NIGHT', r.at);   -- posted at check-in, for the whole stay
    END LOOP;

    -- R00008: dinner and the airport pick-up yesterday
    SELECT f.id, x.id AS res_id INTO r FROM folios f JOIN reservations x ON x.id = f.reservation_id
     WHERE x.property_id = p_id AND x.reference = 'R00008' AND f.is_primary;
    IF r.id IS NOT NULL THEN
        INSERT INTO folio_items (organization_id, property_id, folio_id, reservation_id, item_type, category, description,
                                 service_date, quantity, unit_price, amount, total, source, posted_at) VALUES
            (o_id, p_id, r.id, r.res_id, 'CHARGE', 'TRANSPORT', 'Airport pick-up (Goa airport)', today - 1, 1, 1200, 1200, 1200, 'MANUAL',
             ((today - 1) + time '13:10') AT TIME ZONE 'Asia/Kolkata'),
            (o_id, p_id, r.id, r.res_id, 'CHARGE', 'FOOD', 'Dinner at the beach shack', today - 1, 1, 1450, 1450, 1450, 'MANUAL',
             ((today - 1) + time '21:40') AT TIME ZONE 'Asia/Kolkata');
    END IF;

    -- R00007: laundry this morning
    SELECT f.id, x.id AS res_id INTO r FROM folios f JOIN reservations x ON x.id = f.reservation_id
     WHERE x.property_id = p_id AND x.reference = 'R00007' AND f.is_primary;
    IF r.id IS NOT NULL THEN
        INSERT INTO folio_items (organization_id, property_id, folio_id, reservation_id, item_type, category, description,
                                 service_date, quantity, unit_price, amount, total, source, posted_at) VALUES
            (o_id, p_id, r.id, r.res_id, 'CHARGE', 'LAUNDRY', 'Laundry: shirts', today, 4, 80, 320, 320, 'MANUAL',
             (today + time '08:15') AT TIME ZONE 'Asia/Kolkata');
    END IF;

    RAISE NOTICE 'dev seed S007: % folios opened; room nights for in-house guests, R00008 dinner and pick-up, R00007 laundry', opened;
END $$;
