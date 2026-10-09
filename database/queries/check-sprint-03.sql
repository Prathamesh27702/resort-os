-- Database checks for Sprint 03 (guests). Read-only: every statement is a SELECT.
-- How to run: docs/testing/VERIFYING.md → "Database checks" (VS Code PostgreSQL extension, or one docker command).
-- Each block says what you should see. Numbers marked (demo) assume a freshly reset database plus the review script.

-- 1. Migration V005 applied. Expect V001 … V005.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. Guests by status. (demo) 20 ACTIVE from the seed, plus what you added; MERGED and ANONYMISED after the review.
SELECT status, count(*) FROM guests GROUP BY status ORDER BY status;

-- 3. The demo guests: phone numbers stored in one format (E.164, +91…), emails lower-case.
SELECT first_name, last_name, phone, email, nationality, is_vip, status
FROM guests ORDER BY first_name, last_name LIMIT 30;

-- 4. Every phone number is E.164. Expect NO rows.
SELECT id, phone FROM guests WHERE phone IS NOT NULL AND phone !~ '^\+[1-9][0-9]{6,14}$';

-- 5. ID documents as the database sees them: the number is ciphertext (bytea), only the last 4 are readable.
--    number_bytes > 0 and a key_id for passports etc.; Aadhaar has NO encrypted number, only last 4.
SELECT d.document_type, d.number_last4, length(d.number_encrypted) AS number_bytes, encode(substring(d.number_encrypted from 1 for 8), 'hex') AS first_bytes_hex,
       d.key_id, d.content_type, d.size_bytes, d.storage_key IS NOT NULL AS has_scan, d.status, g.first_name
FROM guest_documents d JOIN guests g ON g.id = d.guest_id
ORDER BY d.created_at DESC;

-- 6. Aadhaar numbers are never stored in full. Expect NO rows.
SELECT id FROM guest_documents WHERE document_type = 'AADHAAR' AND number_encrypted IS NOT NULL;

-- 7. Anonymised profiles hold no personal data, and their documents are purged. Expect NO rows.
SELECT g.id, g.first_name FROM guests g
WHERE g.status = 'ANONYMISED'
  AND (g.last_name IS NOT NULL OR g.phone IS NOT NULL OR g.email IS NOT NULL OR g.date_of_birth IS NOT NULL OR g.notes IS NOT NULL
       OR EXISTS (SELECT 1 FROM guest_addresses a WHERE a.guest_id = g.id)
       OR EXISTS (SELECT 1 FROM guest_documents d WHERE d.guest_id = g.id AND d.status <> 'PURGED'));

-- 8. Merged profiles and the profile they were folded into. The survivor should be ACTIVE (or ANONYMISED later).
SELECT dup.first_name || ' ' || coalesce(dup.last_name, '') AS merged_profile, dup.merged_at,
       s.first_name || ' ' || coalesce(s.last_name, '') AS survivor, s.status AS survivor_status
FROM guests dup JOIN guests s ON s.id = dup.merged_into_id
ORDER BY dup.merged_at DESC;

-- 9. Current consent per guest and purpose (latest answer wins; the full history is kept).
SELECT DISTINCT ON (c.guest_id, c.purpose) g.first_name, g.last_name, c.purpose, c.granted, c.source, c.recorded_at
FROM guest_consents c JOIN guests g ON g.id = c.guest_id
ORDER BY c.guest_id, c.purpose, c.recorded_at DESC;

-- 10. Who opened ID documents, and when (every "Show number" and "View scan" is a SENSITIVE_READ row).
SELECT a.occurred_at, u.full_name AS who, a.changes ->> 'viewed' AS what, a.entity_id AS document_id, a.ip_address
FROM audit_logs a LEFT JOIN users u ON u.id = a.user_id
WHERE a.action = 'SENSITIVE_READ'
ORDER BY a.id DESC LIMIT 20;

-- 11. The audit trail never stores guests' personal data: changed fields are named, values show ***.
--     Expect NO rows (no email address or phone number inside guest audit entries).
SELECT id, entity_type, changes FROM audit_logs
WHERE entity_type IN ('Guest', 'GuestAddress', 'GuestDocument', 'GuestPreference')
  AND (changes::text ~ '[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}' OR changes::text ~ '\+[1-9][0-9]{6,14}');

-- 12. Anonymise and merge reasons in the audit trail.
SELECT a.occurred_at, u.full_name AS who, a.action, a.entity_type, a.reason
FROM audit_logs a LEFT JOIN users u ON u.id = a.user_id
WHERE a.entity_type = 'Guest' AND a.reason IS NOT NULL
ORDER BY a.id DESC LIMIT 20;

-- 13. Every column of the guest tables has a data classification. Expect NO rows.
SELECT c.table_name, c.column_name
FROM information_schema.columns c
WHERE c.table_schema = 'public' AND c.table_name IN ('guests', 'guest_addresses', 'guest_documents', 'guest_preferences', 'guest_consents')
  AND c.column_name NOT IN ('organization_id', 'guest_id', 'created_at', 'updated_at', 'created_by', 'updated_by', 'id')
  AND col_description(format('%I.%I', c.table_schema, c.table_name)::regclass, c.ordinal_position) IS NULL;

-- 14. What the retention job would purge today (documents older than 365 days). Normally empty in development.
SELECT d.id, d.document_type, d.created_at, g.first_name FROM guest_documents d JOIN guests g ON g.id = d.guest_id
WHERE d.status = 'ACTIVE' AND d.created_at < now() - interval '365 days';
