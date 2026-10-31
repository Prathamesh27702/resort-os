-- Database checks for Sprint 18 (attendance and timesheets). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 18. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 18).

-- 1. Migration V020 applied. Expect V001 … V020.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new tables exist. Expect 5 rows.
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('geofences', 'attendance_events', 'timesheets', 'timesheet_lines', 'timesheet_corrections')
ORDER BY table_name;

-- 3. The guard triggers are there. Expect 6 rows: clock events and corrections append-only, geofences and timesheets
--    never deleted, and a locked timesheet (and its lines) never changed.
SELECT tgname, tgrelid::regclass AS on_table FROM pg_trigger
WHERE tgname IN ('trg_attendance_events_append_only', 'trg_timesheet_corrections_append_only', 'trg_geofences_no_delete',
                 'trg_timesheets_no_delete', 'trg_timesheets_locked', 'trg_timesheet_lines_locked')
ORDER BY tgname;

-- 4. Geofences. (demo) Main gate, 300 m, active.
SELECT name, latitude, longitude, radius_m, is_active FROM geofences ORDER BY name;

-- 5. Every manager entry has a reason, and every phone entry inside an area says which one. Expect NO rows.
SELECT id, employee_id, event_type, source, location_status, reason, geofence_id FROM attendance_events
WHERE (source = 'MANAGER' AND (reason IS NULL OR location_status <> 'OVERRIDE'))
   OR (location_status = 'INSIDE' AND (geofence_id IS NULL OR distance_m IS NULL));

-- 6. Today's clock events, in the property's time zone. (demo) after the review: Gauri's in, break, back and out, and a
--    manager entry with "Phone had no signal at the beach".
SELECT e.code, a.event_type, to_char(a.occurred_at AT TIME ZONE p.timezone, 'HH24:MI') AS at, a.location_status, a.distance_m,
       a.source, a.reason
FROM attendance_events a JOIN employees e ON e.id = a.employee_id JOIN properties p ON p.id = a.property_id
WHERE a.work_date = (now() AT TIME ZONE p.timezone)::date
ORDER BY a.occurred_at;

-- 7. Timesheets by week and status. (demo) last week: approved and locked after the review; this week: drafts and Gauri's approved.
SELECT t.week_start, t.status, count(*) AS timesheets, sum(t.worked_minutes) / 60 AS hours
FROM timesheets t GROUP BY t.week_start, t.status ORDER BY t.week_start DESC, t.status;

-- 8. A timesheet's totals are the sum of its lines. Expect NO rows.
SELECT t.id, t.worked_minutes, sum(l.worked_minutes) AS lines_worked, t.overtime_minutes, sum(l.overtime_minutes) AS lines_overtime
FROM timesheets t JOIN timesheet_lines l ON l.timesheet_id = t.id
GROUP BY t.id HAVING t.worked_minutes <> sum(l.worked_minutes) OR t.overtime_minutes <> sum(l.overtime_minutes);

-- 9. No approved or locked timesheet has a day without a clock-out. Expect NO rows.
SELECT t.id, t.status, l.work_date, l.status FROM timesheets t JOIN timesheet_lines l ON l.timesheet_id = t.id
WHERE t.status IN ('APPROVED', 'LOCKED') AND l.status IN ('ON_SHIFT', 'MISSING_OUT');

-- 10. Corrections with their reasons and old and new times. (demo) Ravi Kamat's Tuesday, 14:01 with no clock-out → 14:01–22:05.
SELECT e.code, c.work_date, c.timesheet_status, c.old_clock_in_at, c.old_clock_out_at, c.new_clock_in_at, c.new_clock_out_at,
       c.old_worked_minutes, c.new_worked_minutes, c.reason
FROM timesheet_corrections c JOIN timesheets t ON t.id = c.timesheet_id JOIN employees e ON e.id = t.employee_id
ORDER BY c.created_at;

-- 11. Nobody approved their own timesheet. Expect NO rows.
SELECT t.id, e.code, t.decided_by FROM timesheets t JOIN employees e ON e.id = t.employee_id
WHERE t.decided_by IS NOT NULL AND t.decided_by = e.user_id;
