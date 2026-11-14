-- Database checks for Sprint 30 (India readiness: FSSAI and UPI on bills, Form C). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 30. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database (dev seed S022).

-- 1. Migration V027 applied. Expect V001 … V027.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The property's food licence number and UPI ID. (demo) the made-up 10020099000123 and resort.demo@exampleupi.
SELECT code, name, fssai_licence_no, upi_id, upi_payee_name FROM properties WHERE deleted_at IS NULL ORDER BY code;

-- 3. A wrong value can't be stored whatever the app does. Expect 3 rows (the three CHECKs).
SELECT conname FROM pg_constraint WHERE conrelid = 'properties'::regclass AND conname IN ('ck_properties_fssai','ck_properties_upi_id','ck_properties_upi_payee')
ORDER BY conname;

-- 4. Form C sheets by status, with how many are past their 24 hours.
SELECT p.code, f.status, count(*) AS sheets, count(*) FILTER (WHERE f.status = 'PENDING' AND f.due_at <= now()) AS overdue
FROM form_c_records f JOIN properties p ON p.id = f.property_id GROUP BY p.code, f.status ORDER BY p.code, f.status;

-- 5. Every sheet gives exactly 24 hours, a filed one says when, one not needed says why. Expect no rows.
SELECT id, status, arrived_at, due_at, submitted_at, not_required_reason FROM form_c_records
WHERE due_at <> arrived_at + interval '24 hours'
   OR (status = 'SUBMITTED') <> (submitted_at IS NOT NULL)
   OR (status = 'NOT_REQUIRED') <> (not_required_reason IS NOT NULL);

-- 6. No guest has two sheets for one stay, and no sheet is for an Indian guest who was never anything else. Expect no rows.
SELECT 'two sheets' AS problem, reservation_id::text, guest_id::text FROM form_c_records GROUP BY reservation_id, guest_id HAVING count(*) > 1
UNION ALL
SELECT 'indian guest', f.reservation_id::text, f.guest_id::text FROM form_c_records f JOIN guests g ON g.id = f.guest_id
WHERE g.nationality = 'IN' AND f.status <> 'NOT_REQUIRED';

-- 7. Foreign guests in house without a sheet. Expect no rows once the Form C page has been opened (sheets for guests
--    checked in before Sprint 30 are made then).
SELECT r.reference, g.first_name, g.last_name, g.nationality
FROM reservations r
JOIN reservation_guests rg ON rg.reservation_id = r.id
JOIN guests g ON g.id = rg.guest_id
WHERE r.status = 'CHECKED_IN' AND g.status = 'ACTIVE' AND g.nationality IS NOT NULL AND g.nationality <> 'IN'
  AND NOT EXISTS (SELECT 1 FROM form_c_records f WHERE f.reservation_id = r.id AND f.guest_id = g.id);

-- 8. What is typed on a sheet is stored encrypted: the column is bytes, not readable text. Shows the size only.
SELECT id, status, octet_length(details_encrypted) AS encrypted_bytes, key_id FROM form_c_records WHERE details_encrypted IS NOT NULL LIMIT 10;

-- 9. The Form C reminder is one of the daily jobs (09:00, property time), and did each day once. Second query: expect no rows.
SELECT p.code, j.job, j.enabled, j.run_at, j.last_outcome FROM scheduled_jobs j JOIN properties p ON p.id = j.property_id WHERE j.job = 'form_c_reminder';
SELECT property_id, business_key, count(*) FROM job_runs WHERE job = 'form_c_reminder' GROUP BY 1, 2 HAVING count(*) > 1;
