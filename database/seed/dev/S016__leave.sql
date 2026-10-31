-- =====================================================================
-- DEV SEED S016 — leave, holidays and payroll (Sprint 19)
--   Leave types Casual (CL, 12 a year, earned monthly, at most 12), Sick (SL, 6 a year on 1 January), Earned (EL, 15 a
--   year monthly, at most 45, whole days) and Leave without pay (LOP); seasonal staff get 6 days of CL a year.
--   Holidays: the national days and Goa Liberation Day for this year and next (Diwali and other festivals move each
--   year; add them on the Leave page from the accountant's list).
--   Balances: an opening balance for everyone (brought forward from the paper register) and this month's accrual, so
--   the accrual job adds nothing until next month.
--   Requests: Sunil (E00004) asks for a day of sick leave on his next working day from tomorrow (waiting; the approval
--   list shows what it does to the roster); Maria (E00003) asks for one and a half days of casual leave in ten days (waiting); Neha (E00008)
--   has two days approved two weeks ahead; Ravi (E00007) was refused a day last month.
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when the property has leave types).
-- =====================================================================
DO $$
DECLARE
    p_id      uuid;
    o_id      uuid;
    tz        text;
    owner_id  uuid;
    today     date;
    y         int;
    month1    date;
    cl        uuid;
    sl        uuid;
    el        uuid;
    lop       uuid;
    e         record;
    r         record;
    d         date;
    dates     date[];
    q_id      uuid;
    n         int := 0;
BEGIN
    SELECT p.id, p.organization_id, p.timezone INTO p_id, o_id, tz
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S016: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM leave_types WHERE property_id = p_id) THEN
        RAISE NOTICE 'dev seed S016: leave demo already there, skipped';
        RETURN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM departments WHERE property_id = p_id AND code = 'FRONT') THEN
        RAISE NOTICE 'dev seed S016: no employees (S014) yet, skipped';
        RETURN;
    END IF;
    SELECT id INTO owner_id FROM users WHERE organization_id = o_id ORDER BY created_at LIMIT 1;
    today  := (now() AT TIME ZONE tz)::date;
    y      := extract(year FROM today)::int;
    month1 := date_trunc('month', today)::date;

    INSERT INTO leave_types (organization_id, property_id, code, name, is_paid, needs_balance, allow_half_day, sort_order, created_by, updated_by) VALUES
        (o_id, p_id, 'CL',  'Casual leave',      true,  true,  true,  10, owner_id, owner_id),
        (o_id, p_id, 'SL',  'Sick leave',        true,  true,  true,  20, owner_id, owner_id),
        (o_id, p_id, 'EL',  'Earned leave',      true,  true,  false, 30, owner_id, owner_id),
        (o_id, p_id, 'LOP', 'Leave without pay', false, false, true,  40, owner_id, owner_id);
    SELECT id INTO cl  FROM leave_types WHERE property_id = p_id AND code = 'CL';
    SELECT id INTO sl  FROM leave_types WHERE property_id = p_id AND code = 'SL';
    SELECT id INTO el  FROM leave_types WHERE property_id = p_id AND code = 'EL';
    SELECT id INTO lop FROM leave_types WHERE property_id = p_id AND code = 'LOP';

    INSERT INTO leave_policies (organization_id, property_id, leave_type_id, employment_type, days_per_year, accrual, max_balance, created_by, updated_by) VALUES
        (o_id, p_id, cl,  NULL,       12, 'MONTHLY', 12,   owner_id, owner_id),
        (o_id, p_id, cl,  'SEASONAL',  6, 'MONTHLY', 6,    owner_id, owner_id),
        (o_id, p_id, sl,  NULL,        6, 'YEARLY',  12,   owner_id, owner_id),
        (o_id, p_id, el,  NULL,       15, 'MONTHLY', 45,   owner_id, owner_id),
        (o_id, p_id, lop, NULL,        0, 'NONE',    NULL, owner_id, owner_id);

    INSERT INTO holidays (organization_id, property_id, holiday_date, name, created_by, updated_by)
    SELECT o_id, p_id, make_date(yr, x.m, x.d), x.name, owner_id, owner_id
      FROM (VALUES (1, 26, 'Republic Day'), (8, 15, 'Independence Day'), (10, 2, 'Gandhi Jayanti'), (12, 19, 'Goa Liberation Day'),
                   (12, 25, 'Christmas')) AS x(m, d, name)
     CROSS JOIN (VALUES (y), (y + 1)) AS yy(yr);

    -- Opening balances (brought forward) and this month's accrual for everyone working here on the 1st.
    FOR e IN SELECT * FROM employees WHERE property_id = p_id AND status = 'ACTIVE' ORDER BY code LOOP
        n := n + 1;
        INSERT INTO leave_ledger (organization_id, property_id, employee_id, leave_type_id, entry_type, days, entry_date, reason, created_at, created_by)
        VALUES (o_id, p_id, e.id, cl, 'OPENING', 2 + n % 4,       make_date(y, 1, 1), 'Brought forward from the paper register', now() - interval '2 days', owner_id),
               (o_id, p_id, e.id, el, 'OPENING', 4 + (n * 3) % 9, make_date(y, 1, 1), 'Brought forward from the paper register', now() - interval '2 days', owner_id);
        CONTINUE WHEN e.joined_on > month1;
        INSERT INTO leave_ledger (organization_id, property_id, employee_id, leave_type_id, entry_type, days, entry_date, period, created_at)
        VALUES (o_id, p_id, e.id, cl, 'ACCRUAL', CASE WHEN e.employment_type = 'SEASONAL' THEN 0.5 ELSE 1 END, month1, to_char(month1, 'YYYY-MM'), now() - interval '1 day'),
               (o_id, p_id, e.id, el, 'ACCRUAL', 1.25, month1, to_char(month1, 'YYYY-MM'), now() - interval '1 day'),
               (o_id, p_id, e.id, sl, 'ACCRUAL', 6,    make_date(y, 1, 1), y::text, now() - interval '1 day');
    END LOOP;

    -- The requests, on days that count for each person (not a holiday, not their day off).
    FOR r IN
        SELECT * FROM (VALUES
            ('E00004', 'SL', 1,   1, false, false, 'PENDING',  'Fever since last night'),
            ('E00003', 'CL', 10,  2, false, true,  'PENDING',  'Sister''s wedding'),
            ('E00008', 'CL', 14,  2, false, false, 'APPROVED', 'Trip home'),
            ('E00007', 'CL', -20, 1, false, false, 'REJECTED', 'Football match')
        ) AS x(code, type, after, len, hs, he, status, reason)
    LOOP
        SELECT * INTO e FROM employees WHERE property_id = p_id AND code = r.code;
        CONTINUE WHEN e.id IS NULL;
        dates := '{}';
        d := today + r.after;
        WHILE cardinality(dates) < r.len LOOP
            IF (e.weekly_off IS NULL OR extract(isodow FROM d)::int <> e.weekly_off)
               AND NOT EXISTS (SELECT 1 FROM holidays WHERE property_id = p_id AND holiday_date = d AND is_active) THEN
                dates := dates || d;
            END IF;   -- a day off in the middle stays inside the range, not counted
            d := d + 1;
        END LOOP;
        q_id := gen_random_uuid();
        INSERT INTO leave_requests (id, organization_id, property_id, code, employee_id, leave_type_id, from_date, to_date, half_day_start, half_day_end,
                                    days, counted_dates, reason, status, requested_by, decided_at, decided_by, decision_note, created_at, created_by, updated_by)
        SELECT q_id, o_id, p_id, 'LV' || lpad((SELECT count(*) + 1 FROM leave_requests WHERE property_id = p_id)::text, 5, '0'), e.id, lt.id,
               dates[1], dates[cardinality(dates)], r.hs, r.he, cardinality(dates) - CASE WHEN r.he THEN 0.5 ELSE 0 END, dates, r.reason, r.status,
               e.user_id,
               CASE WHEN r.status <> 'PENDING' THEN now() - interval '1 hour' END,
               CASE WHEN r.status <> 'PENDING' THEN owner_id END,
               CASE WHEN r.status = 'REJECTED' THEN 'Kitchen is short that day; please pick another.' END,
               now() - interval '3 hours', owner_id, owner_id
          FROM leave_types lt WHERE lt.id = CASE r.type WHEN 'SL' THEN sl WHEN 'EL' THEN el WHEN 'LOP' THEN lop ELSE cl END;
        IF r.status = 'APPROVED' THEN
            INSERT INTO leave_ledger (organization_id, property_id, employee_id, leave_type_id, entry_type, days, entry_date, leave_request_id, created_at, created_by)
            SELECT o_id, p_id, e.id, q.leave_type_id, 'TAKEN', -q.days, today, q.id, now() - interval '1 hour', owner_id FROM leave_requests q WHERE q.id = q_id;
        END IF;
    END LOOP;

    INSERT INTO property_counters (property_id, name, last_value)
    VALUES (p_id, 'leave_request', (SELECT count(*) FROM leave_requests WHERE property_id = p_id))
    ON CONFLICT (property_id, name) DO UPDATE SET last_value = EXCLUDED.last_value;
END $$;
