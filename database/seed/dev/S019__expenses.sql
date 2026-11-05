-- =====================================================================
-- DEV SEED S019 — expenses and budgets (Sprint 22)
--   Categories and departments: the defaults the app makes for every property (Electricity and water … Other; Front
--   office … Administration). Staff welfare (food) can't claim back its GST.
--   Budget for this financial year (April to March), the same every month: Front office ₹15,000, Housekeeping ₹40,000,
--   Food and beverage ₹60,000, Maintenance ₹25,000, Activities ₹10,000, Transport ₹20,000, Sales and marketing ₹15,000,
--   Administration ₹20,000 (₹2,05,000 a month, ₹24,60,000 a year).
--   Expenses, all recorded and approved by the owner unless said: electricity, diesel, repairs, laundry soap, ads, staff
--   food and printer paper across this year so far, so the budget report has actuals; Maintenance spends most against its plan.
--   One GST bill (Malvan Fuels, a made-up GSTIN in Goa): its CGST + SGST is claimable, so only the taxable value counts.
--   Two wait for approval: ₹1,200 of housekeeping linen with no receipt yet (it needs one: over ₹500), and ₹350 of tea.
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when the property has a budget).
-- =====================================================================
DO $$
DECLARE
    p_id      uuid;
    o_id      uuid;
    tz        text;
    owner_id  uuid;
    today     date;
    fy        date;
    m         date;
    n         int := 0;
    regular   boolean;
    x         record;
BEGIN
    SELECT p.id, p.organization_id, p.timezone INTO p_id, o_id, tz
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S019: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM budgets WHERE property_id = p_id) THEN
        RAISE NOTICE 'dev seed S019: expenses demo already there, skipped';
        RETURN;
    END IF;
    SELECT id INTO owner_id FROM users WHERE organization_id = o_id ORDER BY created_at LIMIT 1;
    today := (now() AT TIME ZONE tz)::date;
    fy := make_date(CASE WHEN extract(month FROM today) >= 4 THEN extract(year FROM today)::int ELSE extract(year FROM today)::int - 1 END, 4, 1);
    regular := EXISTS (SELECT 1 FROM gst_registrations WHERE property_id = p_id AND registration_type = 'REGULAR' AND effective_from <= today
                                                         AND (effective_to IS NULL OR effective_to >= today));

    INSERT INTO expense_categories (organization_id, property_id, code, name, gst_claimable, sort_order, created_by, updated_by)
    SELECT o_id, p_id, c.code, c.name, c.claimable, c.ord * 10, owner_id, owner_id
      FROM (VALUES (1, 'UTIL', 'Electricity and water', true), (2, 'REPAIR', 'Repairs and maintenance', true), (3, 'FUEL', 'Fuel', true),
                   (4, 'SUPPLY', 'Supplies and consumables', true), (5, 'MKT', 'Marketing and commission', true),
                   (6, 'OFFICE', 'Office and stationery', true), (7, 'TRAVEL', 'Travel and local transport', true),
                   (8, 'STAFFW', 'Staff welfare (food)', false), (9, 'BANK', 'Bank and card charges', true),
                   (10, 'LICENCE', 'Licences and fees', true), (11, 'OTHER', 'Other', true)) AS c(ord, code, name, claimable)
    ON CONFLICT (property_id, code) DO NOTHING;
    INSERT INTO cost_centres (organization_id, property_id, code, name, sort_order, created_by, updated_by)
    SELECT o_id, p_id, c.code, c.name, c.ord * 10, owner_id, owner_id
      FROM (VALUES (1, 'FO', 'Front office'), (2, 'HK', 'Housekeeping'), (3, 'FB', 'Food and beverage'), (4, 'MNT', 'Maintenance'),
                   (5, 'ACT', 'Activities'), (6, 'TRN', 'Transport'), (7, 'SALES', 'Sales and marketing'), (8, 'ADMIN', 'Administration'))
           AS c(ord, code, name)
    ON CONFLICT (property_id, code) DO NOTHING;

    -- The budget: the same amount every month for each department.
    INSERT INTO budgets (organization_id, property_id, year_start, name, note, total, created_by, updated_by)
    VALUES (o_id, p_id, fy, 'Budget ' || extract(year FROM fy) || '-' || lpad(((extract(year FROM fy)::int + 1) % 100)::text, 2, '0'),
            'Demo budget: the same every month', 2460000, owner_id, owner_id);
    FOR i IN 0..11 LOOP
        m := (fy + make_interval(months => i))::date;
        INSERT INTO budget_lines (organization_id, property_id, budget_id, cost_centre_id, month, amount, created_by, updated_by)
        SELECT o_id, p_id, b.id, c.id, m, v.amount, owner_id, owner_id
          FROM budgets b
          JOIN (VALUES ('FO', 15000), ('HK', 40000), ('FB', 60000), ('MNT', 25000), ('ACT', 10000), ('TRN', 20000), ('SALES', 15000),
                       ('ADMIN', 20000)) AS v(centre, amount) ON true
          JOIN cost_centres c ON c.property_id = p_id AND c.code = v.centre
         WHERE b.property_id = p_id AND b.year_start = fy;
    END LOOP;

    -- Expenses this year so far, approved (a day before today at the latest, never before 1 April).
    FOR x IN SELECT * FROM (VALUES
            -- days back, category, centre, what, payee, method, amount, gstin, bill, gst
            (150, 'UTIL',   'ADMIN', 'Electricity bill (MSEDCL)',               'MSEDCL',            'BANK_TRANSFER', 38500.00, NULL, NULL, 0.00),
            (120, 'REPAIR', 'MNT',   'Pump motor rewinding',                    'Sai Electricals',   'CASH',          18500.00, NULL, NULL, 0.00),
            (90,  'REPAIR', 'MNT',   'Roof tiles for cottages 3 and 4',         'Kudal Hardware',    'UPI',           64000.00, NULL, NULL, 0.00),
            (75,  'SUPPLY', 'HK',    'Laundry soap and softener (bulk)',        'Malvan Traders',    'UPI',           22400.00, NULL, NULL, 0.00),
            (60,  'MKT',    'SALES', 'Online ads for the season',               'Ad agency',         'CARD',          18000.00, NULL, NULL, 0.00),
            (45,  'FUEL',   'TRN',   'Diesel for the van and the generator',    'Malvan Fuels',      'UPI',           11800.00, '30MALVF5521K1ZB', 'MF/2627/0412', 1800.00),
            (30,  'STAFFW', 'FB',    'Staff lunch on the festival day',         'Hotel Sagar',       'CASH',           4200.00, NULL, NULL, 0.00),
            (20,  'UTIL',   'ADMIN', 'Electricity bill (MSEDCL)',               'MSEDCL',            'BANK_TRANSFER', 41200.00, NULL, NULL, 0.00),
            (10,  'REPAIR', 'MNT',   'Plumber: pool filter',                    'Ganesh Plumbing',   'CASH',           9800.00, NULL, NULL, 0.00),
            (3,   'OFFICE', 'FO',    'Printer paper and registers',             'Sindhu Stationers', 'CASH',           1450.00, NULL, NULL, 0.00)
        ) AS v(back, category, centre, what, payee, method, amount, gstin, bill, gst)
    LOOP
        n := n + 1;
        INSERT INTO expenses (organization_id, property_id, code, expense_date, category_id, cost_centre_id, description, payee, payment_method,
                              amount, vendor_gstin, bill_number, gst_amount, taxable_value, cgst, sgst, itc_eligible, cost_amount, status,
                              decided_at, decided_by, created_by, updated_by)
        SELECT o_id, p_id, 'EX' || lpad(n::text, 5, '0'), GREATEST(fy, today - x.back), c.id, cc.id, x.what, x.payee, x.method,
               x.amount, x.gstin, x.bill, x.gst, x.amount - x.gst, round(x.gst / 2, 2), x.gst - round(x.gst / 2, 2),
               x.gst > 0 AND c.gst_claimable AND regular,
               CASE WHEN x.gst > 0 AND c.gst_claimable AND regular THEN x.amount - x.gst ELSE x.amount END,
               'APPROVED', now(), owner_id, owner_id, owner_id
          FROM expense_categories c JOIN cost_centres cc ON cc.property_id = p_id AND cc.code = x.centre
         WHERE c.property_id = p_id AND c.code = x.category;
    END LOOP;

    -- Two waiting for approval.
    INSERT INTO expenses (organization_id, property_id, code, expense_date, category_id, cost_centre_id, description, payee, payment_method,
                          amount, taxable_value, cost_amount, status, created_by, updated_by)
    SELECT o_id, p_id, 'EX' || lpad((n + v.k)::text, 5, '0'), today - v.back, c.id, cc.id, v.what, v.payee, v.method, v.amount, v.amount, v.amount,
           'SUBMITTED', owner_id, owner_id
      FROM (VALUES (1, 1, 'SUPPLY', 'HK', 'Bed sheets and pillow covers', 'Kudal Textiles', 'UPI', 1200.00),
                   (2, 0, 'STAFFW', 'FB', 'Tea and snacks for the staff meeting', 'Hotel Sagar', 'CASH', 350.00))
           AS v(k, back, category, centre, what, payee, method, amount)
      JOIN expense_categories c ON c.property_id = p_id AND c.code = v.category
      JOIN cost_centres cc ON cc.property_id = p_id AND cc.code = v.centre;

    INSERT INTO property_counters (property_id, name, last_value) VALUES (p_id, 'expense', n + 2)
    ON CONFLICT (property_id, name) DO UPDATE SET last_value = EXCLUDED.last_value;
END $$;
