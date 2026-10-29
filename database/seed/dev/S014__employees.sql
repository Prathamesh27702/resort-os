-- =====================================================================
-- DEV SEED S014 — employees and roster (Sprint 17)
--   Five departments (Front office, Housekeeping, Kitchen, Restaurant, Maintenance) with their job titles, and ten
--   employees E00001 … E00010, each with a weekly day off and their pay (monthly; one seasonal housekeeper per day).
--   Shift templates for every department, with the people each needs every day.
--   This week's roster: everyone on their usual shift except on their day off, so the shortage view shows the gaps
--   (e.g. Front office · Morning on Asha's day off). Next week is empty: the demo builds it.
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when the property has the FRONT department).
-- =====================================================================
DO $$
DECLARE
    p_id     uuid;
    o_id     uuid;
    tz       text;
    owner_id uuid;
    monday   date;
    d        date;
    e        record;
BEGIN
    SELECT p.id, p.organization_id, p.timezone INTO p_id, o_id, tz
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S014: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM departments WHERE property_id = p_id AND code = 'FRONT') THEN
        RAISE NOTICE 'dev seed S014: employees demo already there, skipped';
        RETURN;
    END IF;
    SELECT id INTO owner_id FROM users WHERE organization_id = o_id ORDER BY created_at LIMIT 1;

    INSERT INTO departments (organization_id, property_id, code, name, sort_order, created_by, updated_by) VALUES
        (o_id, p_id, 'FRONT',   'Front office', 1, owner_id, owner_id),
        (o_id, p_id, 'HK',      'Housekeeping', 2, owner_id, owner_id),
        (o_id, p_id, 'KITCHEN', 'Kitchen',      3, owner_id, owner_id),
        (o_id, p_id, 'REST',    'Restaurant',   4, owner_id, owner_id),
        (o_id, p_id, 'MAINT',   'Maintenance',  5, owner_id, owner_id);

    INSERT INTO designations (organization_id, property_id, department_id, name, created_by, updated_by)
    SELECT o_id, p_id, dp.id, x.name, owner_id, owner_id
      FROM (VALUES ('FRONT', 'Front office manager'), ('FRONT', 'Receptionist'),
                   ('HK', 'Housekeeping supervisor'), ('HK', 'Housekeeper'),
                   ('KITCHEN', 'Head chef'), ('KITCHEN', 'Commis chef'),
                   ('REST', 'Steward'), ('MAINT', 'Technician')) AS x(dept, name)
      JOIN departments dp ON dp.property_id = p_id AND dp.code = x.dept;

    -- Ten employees: code, names, department, job title, type, joined, weekly day off (1 Mon … 7 Sun), phone.
    INSERT INTO employees (organization_id, property_id, code, first_name, last_name, phone, department_id, designation_id, employment_type,
                           joined_on, weekly_off, emergency_contact_name, emergency_contact_phone, created_by, updated_by)
    SELECT o_id, p_id, x.code, x.first, x.last, x.phone, dp.id, ds.id, x.type, x.joined::date, x.off, x.ec, x.ecp, owner_id, owner_id
      FROM (VALUES
        ('E00001', 'Asha',    'Naik',      '+91 98230 10001', 'FRONT',   'Receptionist',            'FULL_TIME', '2023-11-01', 2, 'Vinay Naik',     '+91 98230 20001'),
        ('E00002', 'Rohan',   'Desai',     '+91 98230 10002', 'FRONT',   'Front office manager',    'FULL_TIME', '2022-10-15', 1, 'Meera Desai',    '+91 98230 20002'),
        ('E00003', 'Maria',   'Fernandes', '+91 98230 10003', 'HK',      'Housekeeping supervisor', 'FULL_TIME', '2021-09-01', 3, 'Peter Fernandes','+91 98230 20003'),
        ('E00004', 'Sunil',   'Gawas',     '+91 98230 10004', 'HK',      'Housekeeper',             'FULL_TIME', '2024-01-10', 4, 'Kavita Gawas',   '+91 98230 20004'),
        ('E00005', 'Pooja',   'Shetye',    '+91 98230 10005', 'HK',      'Housekeeper',             'SEASONAL',  '2025-10-01', 5, 'Ramesh Shetye',  '+91 98230 20005'),
        ('E00006', 'Anthony', 'D''Souza',  '+91 98230 10006', 'KITCHEN', 'Head chef',               'FULL_TIME', '2020-11-20', 1, 'Lydia D''Souza', '+91 98230 20006'),
        ('E00007', 'Ravi',    'Kamat',     '+91 98230 10007', 'KITCHEN', 'Commis chef',             'FULL_TIME', '2024-06-01', 3, 'Suman Kamat',    '+91 98230 20007'),
        ('E00008', 'Neha',    'Parab',     '+91 98230 10008', 'REST',    'Steward',                 'FULL_TIME', '2023-12-01', 4, 'Ajay Parab',     '+91 98230 20008'),
        ('E00009', 'Joel',    'Rodrigues', '+91 98230 10009', 'REST',    'Steward',                 'PART_TIME', '2025-11-01', 7, 'Rita Rodrigues', '+91 98230 20009'),
        ('E00010', 'Santosh', 'Velip',     '+91 98230 10010', 'MAINT',   'Technician',              'FULL_TIME', '2022-03-01', 7, 'Lata Velip',     '+91 98230 20010')
      ) AS x(code, first, last, phone, dept, title, type, joined, off, ec, ecp)
      JOIN departments dp ON dp.property_id = p_id AND dp.code = x.dept
      JOIN designations ds ON ds.department_id = dp.id AND ds.name = x.title;
    INSERT INTO property_counters (property_id, name, last_value) VALUES (p_id, 'employee', 10)
        ON CONFLICT (property_id, name) DO UPDATE SET last_value = GREATEST(property_counters.last_value, 10);

    -- Pay from the joining date (restricted: employee_salary.read).
    INSERT INTO employee_compensation (organization_id, employee_id, effective_from, pay_basis, basic, allowances, pf_applicable, esi_applicable, created_by)
    SELECT o_id, em.id, em.joined_on, x.basis, x.basic, x.allow, x.pf, x.esi, owner_id
      FROM (VALUES ('E00001', 'MONTHLY', 18000, 3000, true, true),  ('E00002', 'MONTHLY', 32000, 6000, true, false),
                   ('E00003', 'MONTHLY', 22000, 3000, true, true),  ('E00004', 'MONTHLY', 14000, 2000, true, true),
                   ('E00005', 'DAILY',     700,    0, false, false), ('E00006', 'MONTHLY', 45000, 8000, true, false),
                   ('E00007', 'MONTHLY', 16000, 2500, true, true),  ('E00008', 'MONTHLY', 15000, 2500, true, true),
                   ('E00009', 'DAILY',     800,    0, false, false), ('E00010', 'MONTHLY', 20000, 2000, true, true)
           ) AS x(code, basis, basic, allow, pf, esi)
      JOIN employees em ON em.property_id = p_id AND em.code = x.code;

    -- Shift templates and the people each needs every day.
    INSERT INTO shift_templates (organization_id, property_id, department_id, code, name, start_time, end_time, break_minutes, min_staff, created_by, updated_by)
    SELECT o_id, p_id, dp.id, x.code, x.name, x.st::time, x.en::time, x.brk, x.need, owner_id, owner_id
      FROM (VALUES ('FRONT',   'FO-AM',      'Morning',          '07:00', '15:00', 30, 1),
                   ('FRONT',   'FO-PM',      'Evening',          '15:00', '23:00', 30, 1),
                   ('FRONT',   'FO-NIGHT',   'Night',            '23:00', '07:00', 30, 0),
                   ('HK',      'HK-DAY',     'Day',              '08:00', '17:00', 60, 2),
                   ('KITCHEN', 'KIT-AM',     'Breakfast & lunch','06:00', '14:00', 30, 1),
                   ('KITCHEN', 'KIT-PM',     'Dinner',           '14:00', '22:00', 30, 1),
                   ('REST',    'RST-LUNCH',  'Lunch',            '11:00', '17:00', 30, 1),
                   ('REST',    'RST-DINNER', 'Dinner',           '17:00', '23:00', 30, 1),
                   ('MAINT',   'MNT-DAY',    'Day',              '09:00', '18:00', 60, 1)
           ) AS x(dept, code, name, st, en, brk, need)
      JOIN departments dp ON dp.property_id = p_id AND dp.code = x.dept;

    -- This week: everyone on their usual shift except on their day off (Joel, part time, Friday to Sunday only).
    monday := date_trunc('week', (now() AT TIME ZONE tz)::date)::date;
    FOR e IN
        SELECT em.id, em.department_id, em.weekly_off, t.id AS template_id, t.start_time, t.end_time, t.break_minutes, em.code
          FROM (VALUES ('E00001', 'FO-AM'), ('E00002', 'FO-PM'), ('E00003', 'HK-DAY'), ('E00004', 'HK-DAY'), ('E00005', 'HK-DAY'),
                       ('E00006', 'KIT-AM'), ('E00007', 'KIT-PM'), ('E00008', 'RST-LUNCH'), ('E00009', 'RST-DINNER'), ('E00010', 'MNT-DAY')) AS x(code, tpl)
          JOIN employees em ON em.property_id = p_id AND em.code = x.code
          JOIN shift_templates t ON t.property_id = p_id AND t.code = x.tpl
    LOOP
        FOR i IN 0..6 LOOP
            d := monday + i;
            CONTINUE WHEN extract(isodow FROM d) = e.weekly_off;
            CONTINUE WHEN e.code = 'E00009' AND extract(isodow FROM d) < 5;
            INSERT INTO roster_shifts (organization_id, property_id, employee_id, department_id, template_id, work_date, starts_at, ends_at, break_minutes,
                                       created_by, updated_by)
            VALUES (o_id, p_id, e.id, e.department_id, e.template_id, d, (d + e.start_time) AT TIME ZONE tz,
                    ((CASE WHEN e.end_time <= e.start_time THEN d + 1 ELSE d END) + e.end_time) AT TIME ZONE tz, e.break_minutes, owner_id, owner_id);
        END LOOP;
    END LOOP;
END $$;
