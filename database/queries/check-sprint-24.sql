-- Database checks for Sprint 24 (notifications, WhatsApp, email, background jobs). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 24. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database (dev seed S021) and that the API has run for a minute (the app
-- makes the templates and the daily jobs the first time it needs them).

-- 1. Migration V026 applied. Expect V001 … V026.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new tables. Expect 7 rows.
SELECT table_name FROM information_schema.tables
WHERE table_schema = 'public' AND table_name IN ('message_templates','outbox_messages','whatsapp_messages','email_messages','notifications',
                                                 'scheduled_jobs','job_runs')
ORDER BY table_name;

-- 3. Templates per property: 12 (six guest messages on WhatsApp and by email; low stock and leave in the app and by email).
SELECT p.code, count(*) AS templates, count(*) FILTER (WHERE t.is_active) AS turned_on
FROM message_templates t JOIN properties p ON p.id = t.property_id GROUP BY p.code;

-- 4. The daily jobs: 6 per property, each with its time and when it next runs (property time). (demo) all on, the next run
--    in the next 24 hours.
SELECT p.code, j.job, j.enabled, j.run_at, j.next_run_at AT TIME ZONE p.timezone AS next_run_local, j.last_outcome
FROM scheduled_jobs j JOIN properties p ON p.id = j.property_id ORDER BY p.code, j.run_at, j.job;

-- 5. What became of the messages, by template, channel and status. (demo) booking_confirmation on WhatsApp: 2 READ,
--    1 DELIVERED, 1 SENT, 1 FAILED, plus whatever the jobs and new bookings added.
SELECT template_code, channel, status, count(*) FROM outbox_messages GROUP BY 1, 2, 3 ORDER BY 1, 2, 3;

-- 6. Sent nothing twice: no business key done twice by a job, and no message twice for the same key. Expect no rows.
SELECT 'job run twice' AS problem, property_id, job, business_key, count(*) FROM job_runs GROUP BY property_id, job, business_key HAVING count(*) > 1
UNION ALL
SELECT 'message twice', property_id, template_code, dedupe_key, count(*) FROM outbox_messages GROUP BY property_id, template_code, dedupe_key HAVING count(*) > 1;

-- 7. Every WhatsApp message WhatsApp accepted has its WhatsApp row with the same status, and a failed one says why.
--    Expect no rows.
SELECT m.id, m.status, w.status AS whatsapp_status, m.last_error
FROM outbox_messages m LEFT JOIN whatsapp_messages w ON w.outbox_message_id = m.id
WHERE m.channel = 'WHATSAPP' AND m.provider_message_id IS NOT NULL
  AND (w.id IS NULL OR w.status <> m.status OR (m.status = 'FAILED' AND m.last_error IS NULL));

-- 8. Not sent, and why (no mobile number, no email, opted out, template off). Each reason is in words.
SELECT template_code, channel, recipient_name, last_error FROM outbox_messages WHERE status = 'SKIPPED' ORDER BY created_at DESC LIMIT 20;

-- 9. Waiting to be sent and when the next try is. Expect few rows, each due within the hour.
SELECT template_code, channel, attempts, next_attempt_at, last_error FROM outbox_messages WHERE status = 'QUEUED' ORDER BY next_attempt_at;

-- 10. WhatsApp webhooks received: each status once (the event id is the message id and the status). Expect no duplicates.
SELECT provider, status, count(*) FROM webhook_events WHERE provider = 'WHATSAPP' GROUP BY provider, status;

-- 11. No phone number or message text kept from a webhook (guest replies are counted, not stored). Expect no rows.
SELECT id FROM webhook_events WHERE provider = 'WHATSAPP' AND payload::text ~ '\+?91[0-9]{10}';

-- 12. The bell: unread notifications per person. (demo) the owner has "Messages are on".
SELECT u.email, count(*) FILTER (WHERE n.read_at IS NULL) AS unread, count(*) AS total
FROM notifications n JOIN users u ON u.id = n.user_id GROUP BY u.email ORDER BY unread DESC;
