-- Database checks for Sprint 17 (employees and roster). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 17. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 17).

-- 1. Migration V019 applied. Expect V001 … V019.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new tables exist. Expect 7 rows.
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('departments', 'designations', 'employees', 'employee_documents', 'employee_compensation',
                     'shift_templates', 'roster_shifts')
ORDER BY table_name;

-- 3. The clash constraint is there. Expect 1 row: ex_roster_shifts_clash, EXCLUDE USING gist (employee_id WITH =, period WITH &&)
--    WHERE (status = 'SCHEDULED' AND NOT overlap_allowed).
SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ex_roster_shifts_clash';

-- 4. Employees by department. (demo) E00001 … E00010 from the seed, plus E00011 Kavya added in the review.
SELECT e.code, e.first_name || coalesce(' ' || e.last_name, '') AS name, d.name AS department, g.name AS job_title,
       e.employment_type, e.weekly_off, e.status, e.user_id IS NOT NULL AS has_account
FROM employees e JOIN departments d ON d.id = e.department_id LEFT JOIN designations g ON g.id = e.designation_id
ORDER BY e.code;

-- 5. One person never on two overlapping scheduled shifts unless a manager allowed it. Expect NO rows.
SELECT a.id, b.id AS other, a.employee_id, a.starts_at, a.ends_at, b.starts_at AS other_starts, b.ends_at AS other_ends
FROM roster_shifts a JOIN roster_shifts b ON b.employee_id = a.employee_id AND b.id > a.id AND a.period && b.period
WHERE a.status = 'SCHEDULED' AND b.status = 'SCHEDULED' AND NOT a.overlap_allowed AND NOT b.overlap_allowed;

-- 6. Allowed overlaps, each with its reason. (demo) after the review: Asha Naik 14:00–22:00 next Monday, "Handover to the evening desk".
SELECT e.code, s.work_date, s.starts_at, s.ends_at, s.overlap_reason
FROM roster_shifts s JOIN employees e ON e.id = s.employee_id WHERE s.overlap_allowed ORDER BY s.work_date;

-- 7. Shifts only while the person works here, at most 16 hours, the break shorter than the shift. Expect NO rows.
SELECT s.id, e.code, s.work_date, e.joined_on, e.left_on, s.starts_at, s.ends_at, s.break_minutes
FROM roster_shifts s JOIN employees e ON e.id = s.employee_id
WHERE s.status = 'SCHEDULED'
  AND (s.work_date < e.joined_on OR (e.left_on IS NOT NULL AND s.work_date > e.left_on)
       OR s.ends_at - s.starts_at > interval '16 hours' OR s.break_minutes * interval '1 minute' >= s.ends_at - s.starts_at);

-- 8. Shortages next week: template × day with fewer scheduled people than it needs. (demo) after the review Tuesday's
--    Front office · Morning is filled by Kavya; the rest of the copied week's gaps remain.
WITH days AS (
    SELECT (date_trunc('week', (now() AT TIME ZONE 'Asia/Kolkata')::date + 7))::date + g AS day FROM generate_series(0, 6) g
)
SELECT d.day, dep.name AS department, t.name AS shift, t.start_time, t.end_time, t.min_staff AS needed,
       count(s.id) AS rostered
FROM days d CROSS JOIN shift_templates t JOIN departments dep ON dep.id = t.department_id
LEFT JOIN roster_shifts s ON s.template_id = t.id AND s.work_date = d.day AND s.status = 'SCHEDULED'
WHERE t.is_active AND t.min_staff > 0
GROUP BY d.day, dep.name, t.name, t.start_time, t.end_time, t.min_staff
HAVING count(s.id) < t.min_staff
ORDER BY d.day, t.start_time;

-- 9. Pay is append-only and apart from the profile: the newest entry per person. Run as the owner only — this is salary data.
SELECT DISTINCT ON (e.code) e.code, c.effective_from, c.pay_basis, c.basic, c.allowances, c.basic + c.allowances AS gross
FROM employee_compensation c JOIN employees e ON e.id = c.employee_id
WHERE c.effective_from <= current_date
ORDER BY e.code, c.effective_from DESC, c.created_at DESC;

-- 10. The audit trail never shows a pay amount. Expect NO rows.
SELECT id, entity_type, action, changes FROM audit_logs
WHERE entity_type = 'EmployeeCompensation' AND changes::text ~ '"(Basic|Allowances)": \{[^}]*[0-9]';

-- 11. Documents: Aadhaar never has an encrypted number, a removed one has neither number nor scan. Expect NO rows.
SELECT id, document_type, status FROM employee_documents
WHERE (document_type = 'AADHAAR' AND number_encrypted IS NOT NULL)
   OR (status = 'REMOVED' AND (number_encrypted IS NOT NULL OR storage_key IS NOT NULL));

-- 12. Opening a document is audited. (demo) after the review: one SENSITIVE_READ for Kavya's PAN.
SELECT a.occurred_at, a.action, a.entity_type, a.entity_id, u.email AS by_whom
FROM audit_logs a LEFT JOIN users u ON u.id = a.user_id
WHERE a.action = 'SENSITIVE_READ' AND a.entity_type = 'EmployeeDocument' ORDER BY a.occurred_at DESC LIMIT 10;

-- 13. One employee per staff account. Expect NO rows.
SELECT user_id, count(*) FROM employees WHERE user_id IS NOT NULL GROUP BY user_id HAVING count(*) > 1;
