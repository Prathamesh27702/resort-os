-- =====================================================================
-- DEV SEED S013 — suppliers and purchasing (Sprint 16)
--   Four suppliers: Mapusa Fresh Vegetables and Goa Dairy Co-operative (Goa GSTINs: CGST + SGST), Konkan Spice Traders
--   (a Maharashtra GSTIN: IGST) and Anjuna Fishermen (unregistered: no GST).
--   One open purchase request from the kitchen, PR00001: 15 kg onion and capsicum, 10 kg mixed vegetables and 100 limes
--   for the main store — the start of the demo's purchase cycle for vegetables.
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when the property has the FRESH-VEG supplier).
-- =====================================================================
DO $$
DECLARE
    p_id     uuid;
    o_id     uuid;
    owner_id uuid;
    store    uuid;
    req      uuid;
BEGIN
    SELECT p.id, p.organization_id INTO p_id, o_id
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S013: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM suppliers WHERE property_id = p_id AND code = 'FRESH-VEG') THEN
        RAISE NOTICE 'dev seed S013: purchasing demo already there, skipped';
        RETURN;
    END IF;
    SELECT id INTO store FROM stock_locations WHERE property_id = p_id AND code = 'STORE';
    IF store IS NULL THEN
        RAISE NOTICE 'dev seed S013: no main store (S012 not run), skipped';
        RETURN;
    END IF;
    SELECT id INTO owner_id FROM users WHERE organization_id = o_id ORDER BY created_at LIMIT 1;

    INSERT INTO suppliers (organization_id, property_id, code, name, gstin, state_code, contact_name, phone, email, address, payment_terms_days, note, created_by, updated_by) VALUES
        (o_id, p_id, 'FRESH-VEG', 'Mapusa Fresh Vegetables',  '30AAACF1234A1ZS', '30', 'Ravi Naik',     '+91 98220 11111', 'orders@mapusafresh.example', 'Shop 12, Mapusa market, Bardez, Goa', 7,
            'Delivers before 9 am, Monday to Saturday', owner_id, owner_id),
        (o_id, p_id, 'DAIRY',     'Goa Dairy Co-operative',   '30AAATG4321B1ZO', '30', 'Sunita Kamat',  '+91 98220 22222', 'sales@goadairy.example',     'Curti, Ponda, Goa',                   15, NULL, owner_id, owner_id),
        (o_id, p_id, 'SPICES',    'Konkan Spice Traders',     '27AABCK7788C1ZA', '27', 'Imran Shaikh',  '+91 98220 33333', 'accounts@konkanspice.example','APMC market, Vashi, Navi Mumbai',    30, NULL, owner_id, owner_id),
        (o_id, p_id, 'FISH',      'Anjuna Fishermen',         NULL,              '30', 'Joaquim Fernandes', '+91 98220 44444', NULL,                     'Anjuna beach, Goa',                   0,
            'Unregistered: no GST. Pay in cash on delivery', owner_id, owner_id);

    -- PR00001: the kitchen's vegetables for the weekend.
    INSERT INTO property_counters (property_id, name, last_value) VALUES (p_id, 'purchase_request', 1)
        ON CONFLICT (property_id, name) DO UPDATE SET last_value = GREATEST(property_counters.last_value, 1);
    INSERT INTO purchase_requests (organization_id, property_id, number, location_id, needed_by, note, created_by, updated_by, created_at)
        VALUES (o_id, p_id, 'PR00001', store, (now() AT TIME ZONE 'Asia/Kolkata')::date + 2, 'Vegetables for the weekend', owner_id, owner_id, now() - interval '3 hours')
        RETURNING id INTO req;
    INSERT INTO purchase_request_items (organization_id, request_id, item_id, quantity, sort_order)
    SELECT o_id, req, it.id, x.qty, x.n
      FROM (VALUES ('ONION-CAPSICUM', 15.000, 1), ('MIXED-VEG', 10.000, 2), ('LIME', 100.000, 3)) AS x(code, qty, n)
      JOIN inventory_items it ON it.property_id = p_id AND it.code = x.code;
END $$;
