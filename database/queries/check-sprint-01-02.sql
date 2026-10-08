-- Database checks for Sprints 01–02. Read-only: every statement is a SELECT.
-- How to run: docs/testing/VERIFYING.md → "Database checks" (VS Code PostgreSQL extension, or one docker command).
-- Each block says what you should see. Numbers marked (demo) assume a freshly reset database plus the review scripts.

-- ============================================================ Sprint 01
-- 1. Migrations applied, in order, each with a checksum. Expect V001, V002, V003, V004.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The organisation and its property. Expect DEMO / BEACH01, timezone Asia/Kolkata, GST state 30 (Goa).
SELECT o.code AS org, p.code AS property, p.name, p.timezone, p.gst_state_code, p.address_line1, p.postal_code, p.phone, p.email
FROM properties p JOIN organizations o ON o.id = p.organization_id
WHERE p.deleted_at IS NULL;

-- 3. Staff accounts and their roles. Passwords are stored only as hashes: the hash_preview column shows the
--    first characters of the hash, never a password. must_change_password = true until they pick their own.
SELECT u.email, u.full_name, u.is_active, u.must_change_password, u.failed_login_count, u.lockout_until, u.last_login_at,
       left(u.password_hash, 12) || '…' AS hash_preview,
       string_agg(r.code || ' @ ' || coalesce(p.code, 'ALL'), ', ' ORDER BY r.code) AS roles
FROM users u
LEFT JOIN user_roles ur ON ur.user_id = u.id
LEFT JOIN roles r ON r.id = ur.role_id
LEFT JOIN properties p ON p.id = ur.property_id
WHERE u.deleted_at IS NULL
GROUP BY u.id ORDER BY u.created_at;

-- 4. Roles and how many permissions each has. Expect 17 roles; OWNER has all 117.
SELECT r.code, r.name, count(rp.permission_code) AS permissions
FROM roles r LEFT JOIN role_permissions rp ON rp.role_id = r.id
GROUP BY r.id ORDER BY permissions DESC, r.code;

-- 5. Who can see salaries. Expect exactly two roles: OWNER and HR.
SELECT r.code FROM role_permissions rp JOIN roles r ON r.id = rp.role_id WHERE rp.permission_code = 'employee_salary.read';

-- 6. Sign-in history: successes and failures with the reason (BAD_PASSWORD, LOCKED_OUT, INACTIVE …).
SELECT occurred_at, email_attempted, success, failure_reason, ip_address
FROM login_events ORDER BY occurred_at DESC LIMIT 20;

-- 7. Sessions: one row per refresh token. A deactivated user's tokens have revoked_at + revoked_reason.
SELECT u.email, t.created_at, t.expires_at, t.revoked_at, t.revoked_reason
FROM refresh_tokens t JOIN users u ON u.id = t.user_id
ORDER BY t.created_at DESC LIMIT 20;

-- 8. Audit trail: the latest changes with old → new values and the reason.
SELECT a.occurred_at, coalesce(u.full_name, 'System') AS who, a.action, a.entity_type, a.reason, a.changes
FROM audit_logs a LEFT JOIN users u ON u.id = a.user_id
ORDER BY a.occurred_at DESC LIMIT 20;

-- 9. The audit trail can't be edited. Run this on its own: it must FAIL with
--    ERROR:  audit_logs is append-only (Commented out so the rest of the file runs; remove the "-- " to try it.)
-- UPDATE audit_logs SET reason = 'tamper' WHERE id = (SELECT max(id) FROM audit_logs);

-- ============================================================ Sprint 02
-- 10. Room types. (demo) DELUXE_ROOM UNIT 2/3, STANDARD_ROOM UNIT 2/2, DORMITORY BED 1/1.
SELECT code, name, inventory_mode, base_occupancy, max_occupancy, size_sqm, is_active
FROM accommodation_types ORDER BY sort_order, code;

-- 11. Rooms with their type and number of beds. (demo) 101–102 deluxe, 103–106 standard, DORM with 10 beds.
SELECT un.code, un.name, t.code AS type, t.inventory_mode, un.floor, un.is_active,
       count(b.id) AS beds, count(b.id) FILTER (WHERE b.is_active) AS beds_in_use
FROM accommodation_units un
JOIN accommodation_types t ON t.id = un.accommodation_type_id
LEFT JOIN beds b ON b.unit_id = un.id
GROUP BY un.id, t.id ORDER BY un.sort_order, un.code;

-- 12. Sellable capacity per type: rooms for UNIT types, beds for BED types (only active ones count).
SELECT t.code, t.inventory_mode,
       CASE WHEN t.inventory_mode = 'UNIT'
            THEN count(DISTINCT un.id) FILTER (WHERE un.is_active)
            ELSE count(b.id) FILTER (WHERE un.is_active AND b.is_active) END AS sellable
FROM accommodation_types t
LEFT JOIN accommodation_units un ON un.accommodation_type_id = t.id
LEFT JOIN beds b ON b.unit_id = un.id
WHERE t.is_active
GROUP BY t.id ORDER BY t.sort_order;

-- 13. Blocks, active and released. end_date is NOT included (like a check-out date): nights = end - start.
SELECT un.code AS room, b2.code AS bed, bl.block_type, bl.start_date, bl.end_date, bl.end_date - bl.start_date AS nights,
       bl.status, bl.reason, bl.release_reason
FROM unit_blocks bl
JOIN accommodation_units un ON un.id = bl.unit_id
LEFT JOIN beds b2 ON b2.id = bl.bed_id
ORDER BY bl.created_at DESC;

-- 14. Proof there are no overlapping ACTIVE blocks on the same room (whole-room blocks). Expect 0 rows.
SELECT a.id, b.id FROM unit_blocks a JOIN unit_blocks b
  ON a.unit_id = b.unit_id AND a.id < b.id AND a.period && b.period
WHERE a.status = 'ACTIVE' AND b.status = 'ACTIVE' AND a.bed_id IS NULL AND b.bed_id IS NULL;

-- 15. Beds only exist under per-bed (BED) types. Expect 0 rows.
SELECT b.code, un.code AS room, t.inventory_mode FROM beds b
JOIN accommodation_units un ON un.id = b.unit_id JOIN accommodation_types t ON t.id = un.accommodation_type_id
WHERE t.inventory_mode <> 'BED';

-- 16. Sprint 02 changes in the audit trail, with reasons. Demo rooms from the seed are not audited (they come
--     from database/seed/dev, not from a user); rooms, types, beds and blocks you change in the app are.
SELECT a.occurred_at, coalesce(u.full_name, 'System') AS who, a.action, a.entity_type, a.reason
FROM audit_logs a LEFT JOIN users u ON u.id = a.user_id
WHERE a.entity_type IN ('AccommodationType', 'AccommodationUnit', 'Bed', 'UnitBlock')
ORDER BY a.occurred_at DESC LIMIT 20;
