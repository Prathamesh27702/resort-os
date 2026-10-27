-- =====================================================================
-- DEV SEED S012 — inventory and recipe costing (Sprint 15)
--   Two stores: Main store (STORE) and Kitchen (KITCHEN, where served dishes take their ingredients from).
--   21 stock items in six groups with opening stock and a unit cost before GST; Fresh cream is at its reorder level (low).
--   Nine recipes for eight dishes of the S011 menu. Paneer Tikka (Full, ₹280) uses 200 g paneer, 50 g curd, 20 g tikka masala,
--   100 g onion and capsicum and 20 ml oil = ₹105, a 37.5 % food cost; the Half portion has its own recipe (₹63).
--   Six dishes have no recipe (they show under "No recipe" in the food-cost report).
--   One wastage, SA00001: 0.2 kg onion and capsicum spoilt in the kitchen.
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when the property has a KITCHEN store).
-- =====================================================================
DO $$
DECLARE
    p_id     uuid;
    o_id     uuid;
    owner_id uuid;
    store    uuid;
    kitchen  uuid;
    i        record;
    rec      uuid;
    adj      uuid;
BEGIN
    SELECT p.id, p.organization_id INTO p_id, o_id
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S012: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM stock_locations WHERE property_id = p_id AND code = 'KITCHEN') THEN
        RAISE NOTICE 'dev seed S012: inventory demo already there, skipped';
        RETURN;
    END IF;
    SELECT id INTO owner_id FROM users WHERE organization_id = o_id ORDER BY created_at LIMIT 1;

    INSERT INTO stock_locations (organization_id, property_id, code, name, is_kitchen, sort_order)
        VALUES (o_id, p_id, 'STORE', 'Main store', false, 1) RETURNING id INTO store;
    INSERT INTO stock_locations (organization_id, property_id, code, name, is_kitchen, sort_order)
        VALUES (o_id, p_id, 'KITCHEN', 'Kitchen', true, 2) RETURNING id INTO kitchen;

    INSERT INTO inventory_categories (organization_id, property_id, name, sort_order) VALUES
        (o_id, p_id, 'Dairy', 1), (o_id, p_id, 'Vegetables and fruit', 2), (o_id, p_id, 'Meat and fish', 3),
        (o_id, p_id, 'Dry goods and spices', 4), (o_id, p_id, 'Oils and fats', 5), (o_id, p_id, 'Beverages', 6);

    -- code, name, group, unit, cost per unit, reorder level, opening in the kitchen, opening in the main store
    FOR i IN SELECT * FROM (VALUES
            ('PANEER',         'Paneer',                'Dairy',                'kg', 400.00, 2.000,  5.000,  0.000),
            ('CURD',           'Curd',                  'Dairy',                'kg', 100.00, 1.000,  4.000,  0.000),
            ('BUTTER',         'Butter',                'Dairy',                'kg', 500.00, 1.000,  2.000,  3.000),
            ('CREAM',          'Fresh cream',           'Dairy',                'l',  250.00, 2.000,  2.000,  0.000),
            ('ONION-CAPSICUM', 'Onion and capsicum',    'Vegetables and fruit', 'kg',  60.00, 5.000,  8.000, 20.000),
            ('MIXED-VEG',      'Mixed vegetables',      'Vegetables and fruit', 'kg',  60.00, 3.000,  6.000,  0.000),
            ('LIME',           'Lime',                  'Vegetables and fruit', 'pcs',  5.00, 20.000, 60.000, 0.000),
            ('KINGFISH',       'Kingfish',              'Meat and fish',        'kg', 700.00, 2.000,  4.000,  0.000),
            ('PRAWNS',         'Prawns',                'Meat and fish',        'kg', 600.00, 2.000,  3.000,  0.000),
            ('CHICKEN',        'Chicken',               'Meat and fish',        'kg', 260.00, 3.000,  6.000,  0.000),
            ('TIKKA-MASALA',   'Tikka masala',          'Dry goods and spices', 'kg', 500.00, 0.500,  1.000,  2.000),
            ('GARAM-MASALA',   'Garam masala',          'Dry goods and spices', 'kg', 500.00, 0.500,  1.000,  1.000),
            ('URAD-DAL',       'Urad dal',              'Dry goods and spices', 'kg', 140.00, 2.000,  3.000, 10.000),
            ('BASMATI',        'Basmati rice',          'Dry goods and spices', 'kg', 120.00, 5.000,  8.000, 25.000),
            ('MAIDA',          'Maida',                 'Dry goods and spices', 'kg',  40.00, 5.000, 10.000, 25.000),
            ('BESAN',          'Besan',                 'Dry goods and spices', 'kg',  90.00, 1.000,  2.000,  0.000),
            ('SUGAR',          'Sugar',                 'Dry goods and spices', 'kg',  45.00, 2.000,  5.000, 10.000),
            ('OIL',            'Cooking oil',           'Oils and fats',        'l',  200.00, 5.000,  5.000, 15.000),
            ('GHEE',           'Ghee',                  'Oils and fats',        'kg', 600.00, 1.000,  2.000,  0.000),
            ('COCONUT-MILK',   'Coconut milk',          'Beverages',            'l',  180.00, 2.000,  3.000,  0.000),
            ('SODA',           'Soda (300 ml bottle)',  'Beverages',            'pcs', 20.00, 24.000, 48.000, 96.000)
        ) AS t(code, name, grp, unit, cost, reorder, at_kitchen, at_store)
    LOOP
        INSERT INTO inventory_items (organization_id, property_id, category_id, code, name, unit_id, unit_cost, reorder_level, created_by, updated_by)
        SELECT o_id, p_id, c.id, i.code, i.name, u.id, i.cost, i.reorder, owner_id, owner_id
          FROM inventory_categories c, inventory_units u WHERE c.property_id = p_id AND c.name = i.grp AND u.code = i.unit;
        IF i.at_kitchen > 0 THEN
            INSERT INTO stock_movements (organization_id, property_id, item_id, location_id, movement_type, quantity, unit_cost, value, on_hand_after, note, created_by, created_at)
            SELECT o_id, p_id, it.id, kitchen, 'OPENING', i.at_kitchen, i.cost, round(i.at_kitchen * i.cost, 2), 0, 'Opening stock', owner_id, now() - interval '2 days'
              FROM inventory_items it WHERE it.property_id = p_id AND it.code = i.code;
        END IF;
        IF i.at_store > 0 THEN
            INSERT INTO stock_movements (organization_id, property_id, item_id, location_id, movement_type, quantity, unit_cost, value, on_hand_after, note, created_by, created_at)
            SELECT o_id, p_id, it.id, store, 'OPENING', i.at_store, i.cost, round(i.at_store * i.cost, 2), 0, 'Opening stock', owner_id, now() - interval '2 days'
              FROM inventory_items it WHERE it.property_id = p_id AND it.code = i.code;
        END IF;
    END LOOP;

    -- Recipes: dish code, portion (NULL = the dish), then item code / quantity / unit per ingredient.
    FOR i IN SELECT dish, portion, array_agg(item ORDER BY n) AS items, array_agg(qty ORDER BY n) AS qtys, array_agg(unit ORDER BY n) AS units
               FROM (VALUES
                ('PANEER-TIKKA',   'Full', 1, 'PANEER',         200, 'g'),
                ('PANEER-TIKKA',   'Full', 2, 'CURD',            50, 'g'),
                ('PANEER-TIKKA',   'Full', 3, 'TIKKA-MASALA',    20, 'g'),
                ('PANEER-TIKKA',   'Full', 4, 'ONION-CAPSICUM', 100, 'g'),
                ('PANEER-TIKKA',   'Full', 5, 'OIL',             20, 'ml'),
                ('PANEER-TIKKA',   'Half', 1, 'PANEER',         120, 'g'),
                ('PANEER-TIKKA',   'Half', 2, 'CURD',            30, 'g'),
                ('PANEER-TIKKA',   'Half', 3, 'TIKKA-MASALA',    12, 'g'),
                ('PANEER-TIKKA',   'Half', 4, 'ONION-CAPSICUM',  60, 'g'),
                ('PANEER-TIKKA',   'Half', 5, 'OIL',             12, 'ml'),
                ('PRAWN-KOLIWADA', NULL,   1, 'PRAWNS',         200, 'g'),
                ('PRAWN-KOLIWADA', NULL,   2, 'BESAN',           30, 'g'),
                ('PRAWN-KOLIWADA', NULL,   3, 'OIL',             50, 'ml'),
                ('PRAWN-KOLIWADA', NULL,   4, 'TIKKA-MASALA',    10, 'g'),
                ('FISH-CURRY',     NULL,   1, 'KINGFISH',       200, 'g'),
                ('FISH-CURRY',     NULL,   2, 'COCONUT-MILK',   100, 'ml'),
                ('FISH-CURRY',     NULL,   3, 'BASMATI',        150, 'g'),
                ('FISH-CURRY',     NULL,   4, 'GARAM-MASALA',    15, 'g'),
                ('BUTTER-CHICKEN', NULL,   1, 'CHICKEN',        250, 'g'),
                ('BUTTER-CHICKEN', NULL,   2, 'BUTTER',          30, 'g'),
                ('BUTTER-CHICKEN', NULL,   3, 'CREAM',           50, 'ml'),
                ('BUTTER-CHICKEN', NULL,   4, 'GARAM-MASALA',    15, 'g'),
                ('DAL-MAKHANI',    NULL,   1, 'URAD-DAL',        80, 'g'),
                ('DAL-MAKHANI',    NULL,   2, 'BUTTER',          30, 'g'),
                ('DAL-MAKHANI',    NULL,   3, 'CREAM',           30, 'ml'),
                ('DAL-MAKHANI',    NULL,   4, 'GARAM-MASALA',    10, 'g'),
                ('VEG-BIRYANI',    NULL,   1, 'BASMATI',        150, 'g'),
                ('VEG-BIRYANI',    NULL,   2, 'MIXED-VEG',      150, 'g'),
                ('VEG-BIRYANI',    NULL,   3, 'CURD',            50, 'g'),
                ('VEG-BIRYANI',    NULL,   4, 'GHEE',            20, 'g'),
                ('VEG-BIRYANI',    NULL,   5, 'GARAM-MASALA',    15, 'g'),
                ('BUTTER-NAAN',    NULL,   1, 'MAIDA',          100, 'g'),
                ('BUTTER-NAAN',    NULL,   2, 'BUTTER',          10, 'g'),
                ('LIME-SODA',      NULL,   1, 'LIME',             2, 'pcs'),
                ('LIME-SODA',      NULL,   2, 'SODA',             1, 'pcs'),
                ('LIME-SODA',      NULL,   3, 'SUGAR',           20, 'g')
               ) AS t(dish, portion, n, item, qty, unit)
              GROUP BY dish, portion
    LOOP
        rec := gen_random_uuid();
        INSERT INTO recipes (id, organization_id, property_id, menu_item_id, variant_id, created_by, updated_by)
        SELECT rec, o_id, p_id, mi.id, v.id, owner_id, owner_id
          FROM menu_items mi LEFT JOIN menu_item_variants v ON v.menu_item_id = mi.id AND v.name = i.portion AND v.is_active
         WHERE mi.property_id = p_id AND mi.code = i.dish;
        INSERT INTO recipe_ingredients (organization_id, recipe_id, item_id, quantity, unit_id, sort_order)
        SELECT o_id, rec, it.id, x.qty, u.id, x.n
          FROM unnest(i.items, i.qtys, i.units) WITH ORDINALITY AS x(code, qty, unit, n)
          JOIN inventory_items it ON it.property_id = p_id AND it.code = x.code
          JOIN inventory_units u ON u.code = x.unit;
    END LOOP;

    -- SA00001: 0.2 kg onion and capsicum spoilt in the kitchen.
    INSERT INTO property_counters (property_id, name, last_value) VALUES (p_id, 'stock_adjustment', 1)
        ON CONFLICT (property_id, name) DO NOTHING;
    adj := gen_random_uuid();
    INSERT INTO stock_adjustments (id, organization_id, property_id, number, kind, item_id, location_id, quantity, unit_cost, reason, created_by, created_at)
    SELECT adj, o_id, p_id, 'SA00001', 'WASTAGE', it.id, kitchen, -0.200, it.unit_cost, 'Spoilt in the cold room', owner_id, now() - interval '1 day'
      FROM inventory_items it WHERE it.property_id = p_id AND it.code = 'ONION-CAPSICUM';
    INSERT INTO stock_movements (organization_id, property_id, item_id, location_id, movement_type, quantity, unit_cost, value, on_hand_after, note, adjustment_id, created_by, created_at)
    SELECT o_id, p_id, it.id, kitchen, 'WASTAGE', -0.200, it.unit_cost, round(-0.200 * it.unit_cost, 2), 0, 'Spoilt in the cold room', adj, owner_id, now() - interval '1 day'
      FROM inventory_items it WHERE it.property_id = p_id AND it.code = 'ONION-CAPSICUM';
END $$;
