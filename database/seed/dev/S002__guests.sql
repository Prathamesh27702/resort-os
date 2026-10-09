-- =====================================================================
-- DEV SEED S002 — 20 demo guests for the bootstrap organisation (brief §71)
--   14 Indian guests from different states, 6 international guests.
--   All names are invented. Phone numbers are fictional (Indian 90000 000xx block; UK/US/Australia/Germany
--   numbers come from the ranges those countries reserve for drama and testing). Emails use example.com/.in.
--   No ID documents: their numbers are encrypted by the API, so add them through the app.
-- Runs ONLY in Development (DevSeed:Enabled) and ONLY if the organisation has no guests yet.
-- =====================================================================
DO $$
DECLARE
    o_id uuid;
    g    record;
    gid  uuid;
BEGIN
    SELECT id INTO o_id FROM organizations WHERE code = '{{ORGANIZATION_CODE}}' AND deleted_at IS NULL;
    IF o_id IS NULL THEN
        RAISE NOTICE 'dev seed S002: bootstrap organisation not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM guests WHERE organization_id = o_id) THEN
        RAISE NOTICE 'dev seed S002: organisation already has guests, skipped';
        RETURN;
    END IF;

    FOR g IN
        SELECT * FROM (VALUES
        -- first,      last,          phone,            email,                              nat,  city,          state,             st,   pin,      country, vip,   pref_cat,  pref
        ('Aarav',     'Sharma',      '+919000000101', 'aarav.sharma@example.in',          'IN', 'New Delhi',   'Delhi',           '07', '110017', 'IN',    false, 'ROOM',    'Ground floor, near the garden'),
        ('Diya',      'Patel',       '+919000000102', 'diya.patel@example.in',            'IN', 'Ahmedabad',   'Gujarat',         '24', '380015', 'IN',    false, 'DIETARY', 'Vegetarian, no onion or garlic'),
        ('Vihaan',    'Reddy',       '+919000000103', 'vihaan.reddy@example.in',          'IN', 'Hyderabad',   'Telangana',       '36', '500034', 'IN',    true,  'ROOM',    'Late check-out when possible'),
        ('Ananya',    'Iyer',        '+919000000104', 'ananya.iyer@example.in',           'IN', 'Chennai',     'Tamil Nadu',      '33', '600020', 'IN',    false, 'FOOD',    'Filter coffee at breakfast'),
        ('Kabir',     'Malhotra',    '+919000000105', NULL,                               'IN', 'Mumbai',      'Maharashtra',     '27', '400050', 'IN',    false, 'ACTIVITY','Kayaking at sunrise'),
        ('Saanvi',    'Deshpande',   '+919000000106', 'saanvi.d@example.in',              'IN', 'Pune',        'Maharashtra',     '27', '411004', 'IN',    false, NULL,      NULL),
        ('Arjun',     'Nair',        '+919000000107', 'arjun.nair@example.in',            'IN', 'Kochi',       'Kerala',          '32', '682016', 'IN',    false, 'DIETARY', 'Allergic to peanuts - tell the kitchen'),
        ('Ishita',    'Banerjee',    '+919000000108', 'ishita.banerjee@example.in',       'IN', 'Kolkata',     'West Bengal',     '19', '700019', 'IN',    false, 'FOOD',    'Loves fish curry'),
        ('Rohan',     'Fernandes',   '+919000000109', 'rohan.fernandes@example.in',       'IN', 'Margao',      'Goa',             '30', '403601', 'IN',    true,  'ROOM',    'Sea-facing room'),
        ('Meera',     'Kulkarni',    '+919000000110', NULL,                               'IN', 'Bengaluru',   'Karnataka',       '29', '560038', 'IN',    false, NULL,      NULL),
        ('Aditya',    'Singh',       '+919000000111', 'aditya.singh@example.in',          'IN', 'Jaipur',      'Rajasthan',       '08', '302001', 'IN',    false, 'ACTIVITY','Beach volleyball'),
        ('Nisha',     'Gupta',       '+919000000112', 'nisha.gupta@example.in',           'IN', 'Lucknow',     'Uttar Pradesh',   '09', '226001', 'IN',    false, 'DIETARY', 'Jain food'),
        ('Tenzin',    'Dorjee',      '+919000000113', 'tenzin.dorjee@example.in',         'IN', 'Gangtok',     'Sikkim',          '11', '737101', 'IN',    false, NULL,      NULL),
        ('Priya',     'Menon',       '+919000000114', 'priya.menon@example.in',           'IN', 'Thiruvananthapuram', 'Kerala',   '32', '695001', 'IN',    false, 'ROOM',    'Extra pillows'),
        ('Oliver',    'Bennett',     '+447700900123', 'oliver.bennett@example.com',       'GB', 'Manchester',  NULL,              NULL, 'M1 1AE', 'GB',    false, 'ACTIVITY','Surf lessons'),
        ('Emily',     'Clarke',      '+447700900456', 'emily.clarke@example.com',         'GB', 'London',      NULL,              NULL, 'SW1A 1AA','GB',   false, 'DIETARY', 'Vegan'),
        ('Lukas',     'Schneider',   '+493023125111', 'lukas.schneider@example.com',      'DE', 'Berlin',      NULL,              NULL, '10115',  'DE',    false, 'ROOM',    'Quiet room, away from the bar'),
        ('Sophie',    'Martin',      NULL,            'sophie.martin@example.com',        'FR', 'Lyon',        NULL,              NULL, '69002',  'FR',    false, NULL,      NULL),
        ('Jack',      'Wilson',      '+61491570156',  'jack.wilson@example.com',          'AU', 'Sydney',      'New South Wales', NULL, '2000',   'AU',    false, 'ACTIVITY','Snorkelling trip'),
        ('Ava',       'Johnson',     '+12025550123',  'ava.johnson@example.com',          'US', 'Washington',  'DC',              NULL, '20001',  'US',    true,  'FOOD',    'Gluten-free breakfast')
        ) AS t(first, last, phone, email, nat, city, state, st, pin, country, vip, pref_cat, pref)
    LOOP
        INSERT INTO guests (organization_id, first_name, last_name, phone, email, nationality, is_vip)
        VALUES (o_id, g.first, g.last, g.phone, g.email, g.nat, g.vip)
        RETURNING id INTO gid;

        INSERT INTO guest_addresses (organization_id, guest_id, address_type, city, state, state_code, postal_code, country_code)
        VALUES (o_id, gid, 'HOME', g.city, g.state, g.st, g.pin, g.country);

        IF g.pref_cat IS NOT NULL THEN
            INSERT INTO guest_preferences (organization_id, guest_id, category, value) VALUES (o_id, gid, g.pref_cat, g.pref);
        END IF;

        INSERT INTO guest_consents (organization_id, guest_id, purpose, granted, source)
        VALUES (o_id, gid, 'DATA_PROCESSING', true, 'FRONT_DESK');
        IF g.phone IS NOT NULL AND g.nat = 'IN' THEN
            INSERT INTO guest_consents (organization_id, guest_id, purpose, granted, source)
            VALUES (o_id, gid, 'MARKETING_WHATSAPP', g.vip OR g.pref_cat IS NOT NULL, 'FRONT_DESK');
        END IF;
    END LOOP;

    RAISE NOTICE 'dev seed S002: 20 demo guests created';
END $$;
