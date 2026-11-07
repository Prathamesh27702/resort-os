-- =====================================================================
-- DEV SEED S021 — messages (Sprint 24)
--   The Messages page starts with some history: the WhatsApp booking confirmation of the five most recent demo bookings
--   whose guest has a mobile number, as WhatsApp reported them (two read, one delivered, one sent, and one that failed
--   because the number isn't on WhatsApp, so Send again can be tried). The owner has a line in the bell.
--   Templates and scheduled jobs are not seeded: the app creates them with their defaults the first time it needs them,
--   and the scheduler then sends tomorrow's reminders through the fake WhatsApp and email (no keys in development).
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when the property has messages).
-- =====================================================================
DO $$
DECLARE
    p_id      uuid;
    o_id      uuid;
    pname     text;
    owner_id  uuid;
    n         int := 0;
    st        text;
    mid       uuid;
    x         record;
BEGIN
    SELECT p.id, p.organization_id, p.name INTO p_id, o_id, pname
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S021: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM outbox_messages WHERE property_id = p_id) THEN
        RAISE NOTICE 'dev seed S021: messages already there, skipped';
        RETURN;
    END IF;
    SELECT id INTO owner_id FROM users WHERE organization_id = o_id ORDER BY created_at LIMIT 1;

    FOR x IN
        SELECT r.id, r.reference, r.check_in, r.check_out, r.created_at, g.id AS guest_id, g.first_name, g.last_name, g.phone
          FROM reservations r JOIN guests g ON g.id = r.primary_guest_id
         WHERE r.property_id = p_id AND r.status IN ('CONFIRMED','CHECKED_IN','CHECKED_OUT') AND g.phone IS NOT NULL
         ORDER BY r.created_at DESC, r.reference DESC
         LIMIT 5
    LOOP
        n := n + 1;
        st := (ARRAY['READ','READ','DELIVERED','SENT','FAILED'])[n];
        mid := gen_random_uuid();
        INSERT INTO outbox_messages (id, organization_id, property_id, dedupe_key, template_code, channel, recipient_type, guest_id, reservation_id,
                                     recipient_name, to_address, provider_template, language, body, params, source, status, attempts,
                                     last_error, provider_message_id, created_at, sent_at, delivered_at, read_at, failed_at)
        VALUES (mid, o_id, p_id, 'booking_confirmation:' || replace(x.id::text, '-', '') || ':WHATSAPP', 'booking_confirmation', 'WHATSAPP', 'GUEST',
                x.guest_id, x.id, btrim(x.first_name || ' ' || coalesce(x.last_name, '')), x.phone, 'booking_confirmation', 'en',
                format('Hello %s, your booking %s at %s is confirmed: %s to %s (%s nights). Check-in is from 2:00 pm.',
                       x.first_name, x.reference, pname, to_char(x.check_in, 'Dy FMDD Mon YYYY'), to_char(x.check_out, 'Dy FMDD Mon YYYY'),
                       x.check_out - x.check_in),
                jsonb_build_object('guest_name', x.first_name, 'reference', x.reference, 'property_name', pname),
                'EVENT', st, 1,
                CASE WHEN st = 'FAILED' THEN 'WhatsApp couldn''t deliver it: Message undeliverable: the number isn''t on WhatsApp (error 131026).' END,
                'wamid.DEMO' || upper(replace(mid::text, '-', '')),
                x.created_at, x.created_at + interval '20 seconds',
                CASE WHEN st IN ('READ','DELIVERED') THEN x.created_at + interval '1 minute' END,
                CASE WHEN st = 'READ' THEN x.created_at + interval '25 minutes' END,
                CASE WHEN st = 'FAILED' THEN x.created_at + interval '2 minutes' END);
        INSERT INTO whatsapp_messages (organization_id, property_id, outbox_message_id, wamid, template_name, language, status, sent_at,
                                       delivered_at, read_at, failed_at, error_code, error_title, pricing_category)
        SELECT o_id, p_id, m.id, m.provider_message_id, 'booking_confirmation', 'en', st, m.sent_at, m.delivered_at, m.read_at, m.failed_at,
               CASE WHEN st = 'FAILED' THEN '131026' END, CASE WHEN st = 'FAILED' THEN 'Message undeliverable' END, 'utility'
          FROM outbox_messages m WHERE m.id = mid;
    END LOOP;

    IF owner_id IS NOT NULL THEN
        INSERT INTO notifications (organization_id, property_id, user_id, title, body, link)
        VALUES (o_id, p_id, owner_id, 'Messages are on',
                'Booking confirmations and reminders now go to guests on WhatsApp and by email. See what was sent, and why anything wasn''t, on the Messages page.',
                '/messages');
    END IF;
END $$;
