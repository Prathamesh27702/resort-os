-- =====================================================================
-- DEV SEED S011 — the restaurant (Sprint 14)
--   A sample menu in five sections (prices before GST): Paneer Tikka with Half / Full portions, a spice level and add-ons;
--   Masala Chai sold out (to show the sold-out mark).
--   Tables T1–T4 in the Garden and B1–B2 on the Beach.
--   One open order: O00001 at table T2 for two, sent to the kitchen as K00001 (still NEW), so the kitchen display has
--   something on it. The review demo opens its own order and charges it to R00008 (Room 104, in house).
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when the menu has a Starters section).
-- =====================================================================
DO $$
DECLARE
    p_id     uuid;
    o_id     uuid;
    today    date;
    c_start  uuid;
    c_main   uuid;
    c_bread  uuid;
    c_dess   uuid;
    c_drink  uuid;
    tikka    uuid;
    g_spice  uuid;
    g_add    uuid;
    v_full   uuid;
    m_less   uuid;
    owner_id uuid;
    t2       uuid;
    ord      uuid;
    kot      uuid;
    i        record;
    line     uuid;
    sub      numeric(12,2) := 0;
    tax      numeric(12,2);
BEGIN
    SELECT p.id, p.organization_id, (now() AT TIME ZONE p.timezone)::date INTO p_id, o_id, today
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S011: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM menu_categories WHERE property_id = p_id AND name = 'Starters') THEN
        RAISE NOTICE 'dev seed S011: restaurant demo already there, skipped';
        RETURN;
    END IF;
    SELECT id INTO owner_id FROM users WHERE organization_id = o_id ORDER BY created_at LIMIT 1;

    INSERT INTO menu_categories (organization_id, property_id, name, sort_order) VALUES (o_id, p_id, 'Starters', 1) RETURNING id INTO c_start;
    INSERT INTO menu_categories (organization_id, property_id, name, sort_order) VALUES (o_id, p_id, 'Mains', 2) RETURNING id INTO c_main;
    INSERT INTO menu_categories (organization_id, property_id, name, sort_order) VALUES (o_id, p_id, 'Breads and rice', 3) RETURNING id INTO c_bread;
    INSERT INTO menu_categories (organization_id, property_id, name, sort_order) VALUES (o_id, p_id, 'Desserts', 4) RETURNING id INTO c_dess;
    INSERT INTO menu_categories (organization_id, property_id, name, sort_order) VALUES (o_id, p_id, 'Drinks', 5) RETURNING id INTO c_drink;

    INSERT INTO menu_items (organization_id, property_id, category_id, code, name, description, food_type, price, is_available, sort_order)
    SELECT o_id, p_id, x.cat, x.code, x.name, x.descr, x.ft, x.price, x.avail, x.ord FROM (VALUES
        (c_start, 'PANEER-TIKKA',   'Paneer Tikka',         'Cottage cheese from the tandoor, mint chutney', 'VEG',     280.00, true,  1),
        (c_start, 'PRAWN-KOLIWADA', 'Prawn Koliwada',       'Crisp-fried prawns, Konkan style',              'NON_VEG', 420.00, true,  2),
        (c_start, 'MASALA-OMELETTE','Masala Omelette',      NULL,                                            'EGG',     160.00, true,  3),
        (c_main,  'FISH-CURRY',     'Goan Fish Curry',      'Kingfish in coconut curry, with rice',          'NON_VEG', 450.00, true,  1),
        (c_main,  'BUTTER-CHICKEN', 'Butter Chicken',       NULL,                                            'NON_VEG', 420.00, true,  2),
        (c_main,  'DAL-MAKHANI',    'Dal Makhani',          NULL,                                            'VEG',     240.00, true,  3),
        (c_main,  'VEG-BIRYANI',    'Veg Biryani',          'With raita',                                    'VEG',     330.00, true,  4),
        (c_bread, 'BUTTER-NAAN',    'Butter Naan',          NULL,                                            'VEG',      60.00, true,  1),
        (c_bread, 'JEERA-RICE',     'Jeera Rice',           NULL,                                            'VEG',     180.00, true,  2),
        (c_dess,  'BEBINCA',        'Bebinca',              'Goan layered coconut cake',                     'EGG',     190.00, true,  1),
        (c_dess,  'GULAB-JAMUN',    'Gulab Jamun',          NULL,                                            'VEG',     120.00, true,  2),
        (c_drink, 'LIME-SODA',      'Fresh Lime Soda',      'Sweet or salted',                               'VEG',     120.00, true,  1),
        (c_drink, 'KOKUM',          'Kokum Sherbet',        NULL,                                            'VEG',     110.00, true,  2),
        (c_drink, 'MASALA-CHAI',    'Masala Chai',          NULL,                                            'VEG',      60.00, false, 3)
    ) AS x(cat, code, name, descr, ft, price, avail, ord);

    SELECT id INTO tikka FROM menu_items WHERE property_id = p_id AND code = 'PANEER-TIKKA';
    INSERT INTO menu_item_variants (organization_id, menu_item_id, name, price, sort_order) VALUES
        (o_id, tikka, 'Half', 160.00, 0);
    INSERT INTO menu_item_variants (organization_id, menu_item_id, name, price, sort_order) VALUES
        (o_id, tikka, 'Full', 280.00, 1) RETURNING id INTO v_full;
    INSERT INTO modifier_groups (organization_id, menu_item_id, name, min_select, max_select, sort_order)
        VALUES (o_id, tikka, 'Spice level', 1, 1, 0) RETURNING id INTO g_spice;
    INSERT INTO modifiers (organization_id, group_id, name, price_delta, sort_order) VALUES (o_id, g_spice, 'Less spicy', 0, 0) RETURNING id INTO m_less;
    INSERT INTO modifiers (organization_id, group_id, name, price_delta, sort_order) VALUES (o_id, g_spice, 'Medium', 0, 1), (o_id, g_spice, 'Extra spicy', 0, 2);
    INSERT INTO modifier_groups (organization_id, menu_item_id, name, min_select, max_select, sort_order)
        VALUES (o_id, tikka, 'Add-ons', 0, 2, 1) RETURNING id INTO g_add;
    INSERT INTO modifiers (organization_id, group_id, name, price_delta, sort_order) VALUES
        (o_id, g_add, 'Extra cheese', 40.00, 0), (o_id, g_add, 'Mint chutney', 20.00, 1);

    INSERT INTO restaurant_tables (organization_id, property_id, code, seats, area, sort_order) VALUES
        (o_id, p_id, 'T1', 2, 'Garden', 1), (o_id, p_id, 'T2', 4, 'Garden', 2), (o_id, p_id, 'T3', 4, 'Garden', 3),
        (o_id, p_id, 'T4', 6, 'Garden', 4), (o_id, p_id, 'B1', 4, 'Beach', 5), (o_id, p_id, 'B2', 8, 'Beach', 6);
    SELECT id INTO t2 FROM restaurant_tables WHERE property_id = p_id AND code = 'T2';

    -- O00001 at T2: 1 Paneer Tikka (Full, less spicy), 1 Goan Fish Curry, 2 Butter Naan, sent as K00001.
    INSERT INTO property_counters (property_id, name, last_value) VALUES (p_id, 'restaurant_order', 1), (p_id, 'restaurant_kot', 1)
        ON CONFLICT (property_id, name) DO NOTHING;
    ord := gen_random_uuid();
    kot := gen_random_uuid();
    INSERT INTO orders (id, organization_id, property_id, number, order_type, status, table_id, covers, business_date, opened_by, created_by)
        VALUES (ord, o_id, p_id, 'O00001', 'DINE_IN', 'OPEN', t2, 2, today, owner_id, owner_id);
    INSERT INTO kots (id, organization_id, property_id, order_id, number, status, sent_at, sent_by, created_by)
        VALUES (kot, o_id, p_id, ord, 'K00001', 'NEW', now() - interval '4 minutes', owner_id, owner_id);
    FOR i IN SELECT * FROM (VALUES
            ('PANEER-TIKKA', 1, 1, 'Paneer Tikka (Full)', 'Less spicy'),
            ('FISH-CURRY',   1, 2, 'Goan Fish Curry',     NULL),
            ('BUTTER-NAAN',  2, 3, 'Butter Naan',         NULL)) AS t(code, qty, ord_no, kot_name, mods)
    LOOP
        line := gen_random_uuid();
        INSERT INTO order_items (id, organization_id, order_id, menu_item_id, variant_id, name, variant_name, food_type, modifiers,
                                 quantity, unit_price, amount, status, kot_id, added_by, created_at)
        SELECT line, o_id, ord, mi.id, CASE WHEN i.code = 'PANEER-TIKKA' THEN v_full END, mi.name,
               CASE WHEN i.code = 'PANEER-TIKKA' THEN 'Full' END, mi.food_type,
               CASE WHEN i.code = 'PANEER-TIKKA'
                    THEN jsonb_build_array(jsonb_build_object('id', m_less, 'group', 'Spice level', 'name', 'Less spicy', 'price', 0))
                    ELSE '[]'::jsonb END,
               i.qty, mi.price, mi.price * i.qty, 'SENT', kot, owner_id, now() - interval '5 minutes' + make_interval(secs => i.ord_no)
          FROM menu_items mi WHERE mi.property_id = p_id AND mi.code = i.code;
        INSERT INTO kot_items (organization_id, kot_id, order_item_id, name, quantity, modifiers, created_at)
            VALUES (o_id, kot, line, i.kot_name, i.qty, i.mods, now() - interval '4 minutes' + make_interval(secs => i.ord_no));
        sub := sub + (SELECT price * i.qty FROM menu_items WHERE property_id = p_id AND code = i.code);
    END LOOP;
    -- 5% restaurant GST when the property is registered (S008): CGST 2.5% + SGST 2.5%, each rounded to the paisa.
    tax := CASE WHEN EXISTS (SELECT 1 FROM gst_registrations WHERE property_id = p_id AND registration_type = 'REGULAR')
                THEN 2 * round(sub * 0.025, 2) ELSE 0 END;
    UPDATE orders SET subtotal = sub, tax_amount = tax, total = sub + tax WHERE id = ord;
END $$;
