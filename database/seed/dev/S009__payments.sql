-- =====================================================================
-- DEV SEED S009 — payments and yesterday's cash drawer shift (Sprint 10)
--   * yesterday's Front desk shift: opened at 08:00 with a ₹5,000 float, took ₹500 cash from R00007 (on its bill),
--     counted ₹5,500 at 20:00 and closed with no variance
--   * R00002 (arrives tomorrow) paid a ₹2,000 advance by UPI yesterday; it is on account until the room nights are on
--     the bill (check-in), then reception allocates it
--   * no shift is left open: opening today's drawer is part of the demo (docs/sprints/REVIEWING.md, Sprint 10)
-- Runs ONLY in Development (DevSeed:Enabled). Idempotent: skipped when the property already has a payment.
-- =====================================================================
DO $$
DECLARE
    p_id     uuid;
    o_id     uuid;
    today    date;
    drawer   uuid;
    sess     uuid := gen_random_uuid();
    pay      uuid := gen_random_uuid();
    adv      uuid := gen_random_uuid();
    r7       record;
    r2       record;
    opened   timestamptz;
    closed   timestamptz;
BEGIN
    SELECT p.id, p.organization_id, (now() AT TIME ZONE p.timezone)::date INTO p_id, o_id, today
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S009: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM payments WHERE property_id = p_id) THEN
        RAISE NOTICE 'dev seed S009: the demo property already has payments, skipped';
        RETURN;
    END IF;

    SELECT id INTO drawer FROM cash_drawers WHERE property_id = p_id ORDER BY created_at LIMIT 1;
    IF drawer IS NULL THEN
        INSERT INTO cash_drawers (organization_id, property_id, name) VALUES (o_id, p_id, 'Front desk') RETURNING id INTO drawer;
    END IF;
    SELECT x.id, x.reference, x.currency, x.primary_guest_id, f.id AS folio_id INTO r7
      FROM reservations x JOIN folios f ON f.reservation_id = x.id AND f.is_primary
     WHERE x.property_id = p_id AND x.reference = 'R00007';
    SELECT x.id, x.reference, x.currency, x.primary_guest_id INTO r2
      FROM reservations x WHERE x.property_id = p_id AND x.reference = 'R00002' AND x.status = 'CONFIRMED';
    opened := ((today - 1) + time '08:00') AT TIME ZONE 'Asia/Kolkata';
    closed := ((today - 1) + time '20:00') AT TIME ZONE 'Asia/Kolkata';

    -- yesterday's shift: open, the float, R00007's cash, then the close (cash moves only while a shift is open)
    INSERT INTO cash_drawer_sessions (id, organization_id, property_id, cash_drawer_id, status, opened_at, opening_float, open_note)
    VALUES (sess, o_id, p_id, drawer, 'OPEN', opened, 5000, 'Demo shift');
    INSERT INTO cash_drawer_movements (organization_id, property_id, session_id, movement_type, amount, description, moved_at)
    VALUES (o_id, p_id, sess, 'OPENING_FLOAT', 5000, 'Opening float', opened);
    IF r7.id IS NOT NULL THEN
        INSERT INTO payments (id, organization_id, property_id, number, reservation_id, guest_id, method, status, amount, currency,
                              note, cash_drawer_session_id, received_at, captured_at)
        VALUES (pay, o_id, p_id, 'P00001', r7.id, r7.primary_guest_id, 'CASH', 'CAPTURED', 500, r7.currency,
                'Part payment at check-in', sess, opened + interval '9 hours', opened + interval '9 hours');
        INSERT INTO payment_allocations (organization_id, property_id, payment_id, folio_id, amount, allocated_at)
        VALUES (o_id, p_id, pay, r7.folio_id, 500, opened + interval '9 hours');
        INSERT INTO cash_drawer_movements (organization_id, property_id, session_id, movement_type, amount, payment_id, description, moved_at)
        VALUES (o_id, p_id, sess, 'PAYMENT', 500, pay, 'P00001 · ' || r7.reference, opened + interval '9 hours');
    END IF;
    UPDATE cash_drawer_sessions
       SET status = 'CLOSED', closed_at = closed,
           expected_amount = (SELECT sum(amount) FROM cash_drawer_movements WHERE session_id = sess),
           counted_amount  = (SELECT sum(amount) FROM cash_drawer_movements WHERE session_id = sess),
           variance = 0
     WHERE id = sess;

    -- R00002's advance by UPI, on account
    IF r2.id IS NOT NULL THEN
        INSERT INTO payments (id, organization_id, property_id, number, reservation_id, guest_id, method, status, amount, currency,
                              reference, note, received_at, captured_at)
        VALUES (adv, o_id, p_id, CASE WHEN r7.id IS NULL THEN 'P00001' ELSE 'P00002' END, r2.id, r2.primary_guest_id, 'UPI', 'CAPTURED',
                2000, r2.currency, '412345678901', 'Advance', opened + interval '3 hours', opened + interval '3 hours');
    END IF;

    INSERT INTO property_counters (property_id, name, last_value)
    SELECT p_id, 'payment', count(*) FROM payments WHERE property_id = p_id
    ON CONFLICT (property_id, name) DO UPDATE SET last_value = EXCLUDED.last_value;

    RAISE NOTICE 'dev seed S009: yesterday''s drawer shift (₹500 cash from R00007) closed; R00002 ₹2,000 UPI advance on account';
END $$;
