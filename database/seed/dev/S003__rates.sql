-- =====================================================================
-- DEV SEED S003 — demo rates and packages for the bootstrap property (brief §71)
--   Rate plan BAR (best available rate, room only, the default) with prices from a week ago to a year ahead:
--     Deluxe room    ₹5,000 a night, Friday and Saturday nights ₹6,000 (weekend uplift); extra adult ₹1,200, child ₹600
--     Standard room  ₹3,500 a night, weekend ₹4,200; extra adult ₹1,000, child ₹500
--     Dormitory bed  ₹800 a bed, weekend ₹950
--   Packages: Couple getaway (2 nights Deluxe, ₹14,999) and Room + breakfast (1 night Standard, ₹4,100), with their
--   revenue split. Amounts follow the API's rounding rule and add up to the price (the database checks it).
-- Runs ONLY in Development (DevSeed:Enabled) and ONLY if the property has no rate plans yet.
-- =====================================================================
DO $$
DECLARE
    p_id     uuid;
    o_id     uuid;
    today    date;
    plan_id  uuid;
    t_deluxe uuid;
    t_std    uuid;
    t_dorm   uuid;
    pkg_id   uuid;
BEGIN
    SELECT p.id, p.organization_id, (now() AT TIME ZONE p.timezone)::date INTO p_id, o_id, today
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S003: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM rate_plans WHERE property_id = p_id) THEN
        RAISE NOTICE 'dev seed S003: property already has rate plans, skipped';
        RETURN;
    END IF;
    SELECT id INTO t_deluxe FROM accommodation_types WHERE property_id = p_id AND code = 'DELUXE_ROOM';
    SELECT id INTO t_std    FROM accommodation_types WHERE property_id = p_id AND code = 'STANDARD_ROOM';
    SELECT id INTO t_dorm   FROM accommodation_types WHERE property_id = p_id AND code = 'DORMITORY';
    IF t_deluxe IS NULL OR t_std IS NULL OR t_dorm IS NULL THEN
        RAISE NOTICE 'dev seed S003: demo room types (S001) not found, skipped';
        RETURN;
    END IF;

    INSERT INTO rate_plans (organization_id, property_id, code, name, description, meal_plan, is_default, sort_order)
    VALUES (o_id, p_id, 'BAR', 'Best available rate', 'Room only. Friday and Saturday nights cost more.', 'EP', true, 10)
    RETURNING id INTO plan_id;

    -- extract(isodow): 5 = Friday, 6 = Saturday
    INSERT INTO rate_plan_prices (organization_id, property_id, rate_plan_id, accommodation_type_id, stay_date,
                                  price, extra_adult, extra_child, min_stay)
    SELECT o_id, p_id, plan_id, t.type_id, d::date,
           CASE WHEN extract(isodow FROM d) IN (5, 6) THEN t.weekend ELSE t.weekday END,
           t.extra_adult, t.extra_child, 1
      FROM generate_series(today - 7, today + 365, interval '1 day') AS d
     CROSS JOIN (VALUES (t_deluxe, 5000.00, 6000.00, 1200.00, 600.00),
                        (t_std,    3500.00, 4200.00, 1000.00, 500.00),
                        (t_dorm,    800.00,  950.00,    0.00,   0.00))
                 AS t(type_id, weekday, weekend, extra_adult, extra_child);

    INSERT INTO packages (organization_id, property_id, accommodation_type_id, code, name, description,
                          nights, adults, children, price, sort_order)
    VALUES (o_id, p_id, t_deluxe, 'COUPLE', 'Couple getaway',
            'Two nights in a Deluxe room with breakfast, a candle-light dinner, airport pick-up and a couples'' spa session.',
            2, 2, 0, 14999.00, 10)
    RETURNING id INTO pkg_id;
    INSERT INTO package_components (organization_id, property_id, package_id, category, description, share_percent, amount, sort_order) VALUES
        (o_id, p_id, pkg_id, 'ROOM',      '2 nights in a Deluxe room',              70.00, 10499.30, 0),
        (o_id, p_id, pkg_id, 'FOOD',      'Breakfast and a candle-light dinner',    15.00,  2249.85, 1),
        (o_id, p_id, pkg_id, 'TRANSPORT', 'Airport pick-up',                         5.00,   749.95, 2),
        (o_id, p_id, pkg_id, 'ACTIVITY',  'Couples'' spa session',                   10.00,  1499.90, 3);

    INSERT INTO packages (organization_id, property_id, accommodation_type_id, code, name, description,
                          nights, adults, children, price, sort_order)
    VALUES (o_id, p_id, t_std, 'ROOM_BREAKFAST', 'Room + breakfast', 'One night in a Standard room with breakfast for two.',
            1, 2, 0, 4100.00, 20)
    RETURNING id INTO pkg_id;
    INSERT INTO package_components (organization_id, property_id, package_id, category, description, share_percent, amount, sort_order) VALUES
        (o_id, p_id, pkg_id, 'ROOM', '1 night in a Standard room', 85.00, 3485.00, 0),
        (o_id, p_id, pkg_id, 'FOOD', 'Breakfast for two',          15.00,  615.00, 1);

    RAISE NOTICE 'dev seed S003: rate plan BAR (% nights × 3 room types) and 2 packages created', (today + 365) - (today - 7) + 1;
END $$;
