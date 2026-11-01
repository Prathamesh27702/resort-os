-- Database checks for Sprint 19 (leave, holidays and the payroll export). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 19. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 19).

-- 1. Migration V021 applied. Expect V001 … V021.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new tables exist. Expect 7 rows.
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('leave_types', 'leave_policies', 'leave_requests', 'leave_ledger', 'leave_balances', 'holidays', 'payroll_exports')
ORDER BY table_name;

-- 3. The guards are there. Expect 4 rows: the ledger and payroll files append-only, the balance kept by the ledger, and
--    the overlap guard on leave requests.
SELECT tgname AS name, tgrelid::regclass AS on_table FROM pg_trigger
WHERE tgname IN ('trg_leave_ledger_append_only', 'trg_payroll_exports_append_only', 'trg_leave_ledger_balance')
UNION ALL
SELECT conname, conrelid::regclass FROM pg_constraint WHERE conname = 'ex_leave_requests_overlap'
ORDER BY 1;

-- 4. Kinds of leave and their policies. (demo) CL 12 a year monthly (6 for seasonal staff), SL 6 yearly, EL 15 monthly
--    up to 45, LOP unpaid with no balance.
SELECT t.code, t.name, t.is_paid, t.needs_balance, t.allow_half_day, coalesce(p.employment_type, 'everyone') AS who,
       p.days_per_year, p.accrual, p.max_balance
FROM leave_types t LEFT JOIN leave_policies p ON p.leave_type_id = t.id
ORDER BY t.sort_order, p.employment_type NULLS FIRST;

-- 5. Every balance equals the sum of its ledger. Expect NO rows.
SELECT b.employee_id, b.leave_type_id, b.balance, coalesce(sum(l.days), 0) AS ledger
FROM leave_balances b LEFT JOIN leave_ledger l ON l.employee_id = b.employee_id AND l.leave_type_id = b.leave_type_id
GROUP BY b.employee_id, b.leave_type_id, b.balance
HAVING b.balance <> coalesce(sum(l.days), 0);

-- 6. No balance below zero. Expect NO rows (a CHECK constraint refuses it anyway).
SELECT employee_id, leave_type_id, balance FROM leave_balances WHERE balance < 0;

-- 7. No two waiting or approved requests of one person share a day. Expect NO rows.
SELECT a.code, b.code, a.employee_id FROM leave_requests a JOIN leave_requests b
  ON a.employee_id = b.employee_id AND a.id < b.id AND a.period && b.period
WHERE a.status IN ('PENDING', 'APPROVED') AND b.status IN ('PENDING', 'APPROVED');

-- 8. Every approved request was taken off the balance once, and every one cancelled after approval was given back.
--    Expect NO rows.
SELECT q.code, q.status, q.days,
       sum(l.days) FILTER (WHERE l.entry_type = 'TAKEN') AS taken, sum(l.days) FILTER (WHERE l.entry_type = 'RETURNED') AS returned
FROM leave_requests q JOIN leave_types t ON t.id = q.leave_type_id AND t.needs_balance
LEFT JOIN leave_ledger l ON l.leave_request_id = q.id
WHERE q.status = 'APPROVED' OR q.decided_at IS NOT NULL AND q.status = 'CANCELLED'
GROUP BY q.id, q.code, q.status, q.days
HAVING (q.status = 'APPROVED' AND coalesce(sum(l.days) FILTER (WHERE l.entry_type = 'TAKEN'), 0) <> -q.days)
    OR (q.status = 'CANCELLED' AND coalesce(sum(l.days), 0) <> 0);

-- 9. Nobody has a shift still scheduled on a full day of approved leave. Expect NO rows.
SELECT q.code, e.code AS employee, s.work_date, s.status
FROM leave_requests q JOIN employees e ON e.id = q.employee_id
JOIN roster_shifts s ON s.employee_id = q.employee_id AND s.work_date = ANY (q.counted_dates) AND s.status = 'SCHEDULED'
WHERE q.status = 'APPROVED'
  AND NOT (q.half_day_start AND s.work_date = q.from_date) AND NOT (q.half_day_end AND s.work_date = q.to_date);

-- 10. Nobody approved their own leave. Expect NO rows.
SELECT q.code, e.code, q.decided_by FROM leave_requests q JOIN employees e ON e.id = q.employee_id
WHERE q.status IN ('APPROVED', 'REJECTED') AND q.decided_by = e.user_id;

-- 11. Leave requests. (demo) LV00001–LV00004 from the seed; after the review Gauri's 1.5 days of CL approved, with one
--     shift taken off the roster.
SELECT q.code, e.code AS employee, t.code AS type, q.from_date, q.to_date, q.half_day_start, q.half_day_end, q.days,
       q.status, q.shifts_cancelled, q.decision_note
FROM leave_requests q JOIN employees e ON e.id = q.employee_id JOIN leave_types t ON t.id = q.leave_type_id
ORDER BY q.code;

-- 12. The accrual ran once per person, kind and period. Expect NO rows (a unique index refuses it anyway).
SELECT employee_id, leave_type_id, period, count(*) FROM leave_ledger WHERE entry_type = 'ACCRUAL'
GROUP BY employee_id, leave_type_id, period HAVING count(*) > 1;

-- 13. Adjustments by hand, with their reasons. (demo) after the review: +1 CL for Gauri, "Worked on the holiday in August".
SELECT e.code, t.code AS type, l.entry_type, l.days, l.reason, l.created_at
FROM leave_ledger l JOIN employees e ON e.id = l.employee_id JOIN leave_types t ON t.id = l.leave_type_id
WHERE l.entry_type IN ('ADJUSTMENT', 'OPENING') AND l.reason NOT LIKE 'Brought forward from the paper register%'
ORDER BY l.created_at;

-- 14. Holidays this year and next. (demo) Republic Day, Independence Day, Gandhi Jayanti, Goa Liberation Day and
--     Christmas each year, plus any added in the review. No two active holidays share a date.
SELECT holiday_date, name, is_active FROM holidays ORDER BY holiday_date;

-- 15. Payroll files made, with their SHA-256 matching what is stored. Expect is_intact true on every row.
SELECT period_start, file_name, employees, timesheets_locked, cardinality(warnings) AS left_out, sha256,
       encode(sha256(content), 'hex') = sha256 AS is_intact, created_at
FROM payroll_exports ORDER BY created_at;

-- 16. Every approved timesheet with hours in a month that has a payroll file was locked by it. Expect NO rows.
SELECT t.id, t.week_start, t.status, x.file_name
FROM payroll_exports x JOIN timesheets t ON t.status = 'APPROVED' AND t.property_id = x.property_id
WHERE EXISTS (SELECT 1 FROM timesheet_lines l WHERE l.timesheet_id = t.id
                AND l.work_date BETWEEN x.period_start AND x.period_end
                AND t.decided_at < x.created_at);
