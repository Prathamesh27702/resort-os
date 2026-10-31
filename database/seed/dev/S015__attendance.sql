-- =====================================================================
-- DEV SEED S015 — attendance and timesheets (Sprint 18)
--   A 300 m geofence "Main gate" at the property (its own coordinates, or Calangute, Goa, if it has none).
--   Last week: the same roster as this week, everyone clocked in and out at the gate (a few minutes late some days,
--   staying a little longer others), with their breaks. Their timesheets are SUBMITTED, waiting for the manager, except
--   Joel's (still a DRAFT). Two things for the demo: Ravi (E00007) never clocked out on Tuesday (approval is refused
--   until a manager corrects the day) and Pooja (E00005) didn't come on Tuesday (ABSENT).
--   This week: clock-ins and outs for every shift before today; their timesheets are worked out by the API as DRAFTs.
-- Runs ONLY in Development (DevSeed:Enabled) and only once (skipped when the property has a geofence).
-- =====================================================================
DO $$
DECLARE
    p_id      uuid;
    o_id      uuid;
    tz        text;
    owner_id  uuid;
    fence_id  uuid;
    lat       numeric(9,6);
    lng       numeric(9,6);
    monday    date;
    today     date;
    d         date;
    e         record;
    s         record;
    t_id      uuid;
    late      integer;
    extra     integer;
    in_at     timestamptz;
    out_at    timestamptz;
    brk_at    timestamptz;
    worked    integer;
    paid      integer;
BEGIN
    SELECT p.id, p.organization_id, p.timezone, p.latitude, p.longitude INTO p_id, o_id, tz, lat, lng
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S015: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM geofences WHERE property_id = p_id) THEN
        RAISE NOTICE 'dev seed S015: attendance demo already there, skipped';
        RETURN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM departments WHERE property_id = p_id AND code = 'FRONT') THEN
        RAISE NOTICE 'dev seed S015: no employees (S014) yet, skipped';
        RETURN;
    END IF;
    SELECT id INTO owner_id FROM users WHERE organization_id = o_id ORDER BY created_at LIMIT 1;
    lat := COALESCE(lat, 15.544000);
    lng := COALESCE(lng, 73.755300);

    INSERT INTO geofences (organization_id, property_id, name, latitude, longitude, radius_m, created_by, updated_by)
    VALUES (o_id, p_id, 'Main gate', lat, lng, 300, owner_id, owner_id)
    RETURNING id INTO fence_id;

    today  := (now() AT TIME ZONE tz)::date;
    monday := date_trunc('week', today)::date;

    -- Last week's roster: the same as this week's, a week earlier.
    INSERT INTO roster_shifts (organization_id, property_id, employee_id, department_id, template_id, work_date, starts_at, ends_at, break_minutes,
                               created_by, updated_by)
    SELECT organization_id, property_id, employee_id, department_id, template_id, work_date - 7, starts_at - interval '7 days',
           ends_at - interval '7 days', break_minutes, owner_id, owner_id
      FROM roster_shifts
     WHERE property_id = p_id AND status = 'SCHEDULED' AND work_date BETWEEN monday AND monday + 6;

    -- Last week's timesheets (one per person on the roster), filled in below.
    INSERT INTO timesheets (organization_id, property_id, employee_id, week_start, status, note, submitted_at, submitted_by, created_by, updated_by)
    SELECT DISTINCT o_id, p_id, rs.employee_id, monday - 7,
           CASE WHEN em.code = 'E00009' THEN 'DRAFT' ELSE 'SUBMITTED' END,
           CASE WHEN em.code = 'E00007' THEN 'Forgot to clock out on Tuesday, left at 22:05.' END,
           CASE WHEN em.code = 'E00009' THEN NULL ELSE (monday + time '10:00') AT TIME ZONE tz END,
           CASE WHEN em.code = 'E00009' THEN NULL ELSE em.user_id END,
           owner_id, owner_id
      FROM roster_shifts rs JOIN employees em ON em.id = rs.employee_id
     WHERE rs.property_id = p_id AND rs.status = 'SCHEDULED' AND rs.work_date BETWEEN monday - 7 AND monday - 1;

    -- Every shift from last Monday up to yesterday: in (0–7 minutes late some days), a break 4 hours in, out (0–15
    -- minutes after the end some days), all at the gate.
    FOR s IN
        SELECT rs.*, em.code, substring(em.code FROM 2)::int AS n
          FROM roster_shifts rs JOIN employees em ON em.id = rs.employee_id
         WHERE rs.property_id = p_id AND rs.status = 'SCHEDULED' AND rs.work_date BETWEEN monday - 7 AND today - 1
         ORDER BY rs.work_date, em.code
    LOOP
        d := s.work_date;
        late  := CASE WHEN (s.n + extract(isodow FROM d)::int) % 3 = 0 THEN (s.n * 3 + extract(day FROM d)::int) % 8 ELSE 0 END;
        extra := ((s.n + extract(isodow FROM d)::int * 2) % 4) * 5;
        in_at  := s.starts_at + make_interval(mins => late);
        out_at := s.ends_at + make_interval(mins => extra);
        brk_at := s.starts_at + interval '4 hours';
        paid   := (extract(epoch FROM s.ends_at - s.starts_at) / 60)::int - s.break_minutes;
        worked := (extract(epoch FROM out_at - in_at) / 60)::int - s.break_minutes;

        -- Pooja didn't come on last Tuesday.
        IF s.code = 'E00005' AND d = monday - 6 THEN
            INSERT INTO timesheet_lines (organization_id, timesheet_id, work_date, roster_shift_id, scheduled_start, scheduled_end, scheduled_minutes,
                                         status, created_by, updated_by)
            SELECT o_id, t.id, d, s.id, s.starts_at, s.ends_at, paid, 'ABSENT', owner_id, owner_id
              FROM timesheets t WHERE t.employee_id = s.employee_id AND t.week_start = monday - 7;
            CONTINUE;
        END IF;

        INSERT INTO attendance_events (organization_id, property_id, employee_id, event_type, occurred_at, work_date, roster_shift_id, latitude, longitude,
                                       accuracy_m, distance_m, geofence_id, location_status, created_at, created_by)
        SELECT o_id, p_id, s.employee_id, x.t, x.at, d, s.id, lat + 0.0002, lng + 0.0001, 12, 24, fence_id, 'INSIDE', x.at, em.user_id
          FROM (VALUES ('CLOCK_IN', in_at), ('BREAK_START', brk_at), ('BREAK_END', brk_at + make_interval(mins => s.break_minutes)),
                       ('CLOCK_OUT', out_at)) AS x(t, at)
          JOIN employees em ON em.id = s.employee_id
         WHERE (s.break_minutes > 0 OR x.t IN ('CLOCK_IN', 'CLOCK_OUT'))
           AND NOT (s.code = 'E00007' AND d = monday - 6 AND x.t = 'CLOCK_OUT');   -- Ravi's Tuesday: never clocked out

        CONTINUE WHEN d >= monday;   -- this week's timesheets are worked out by the API
        SELECT id INTO t_id FROM timesheets WHERE employee_id = s.employee_id AND week_start = monday - 7;
        IF s.code = 'E00007' AND d = monday - 6 THEN
            INSERT INTO timesheet_lines (organization_id, timesheet_id, work_date, roster_shift_id, scheduled_start, scheduled_end, scheduled_minutes,
                                         clock_in_at, break_minutes, late_minutes, sessions, status, created_by, updated_by)
            VALUES (o_id, t_id, d, s.id, s.starts_at, s.ends_at, paid, in_at, 0, late, 0, 'MISSING_OUT', owner_id, owner_id);
        ELSE
            INSERT INTO timesheet_lines (organization_id, timesheet_id, work_date, roster_shift_id, scheduled_start, scheduled_end, scheduled_minutes,
                                         clock_in_at, clock_out_at, break_minutes, worked_minutes, overtime_minutes, late_minutes, early_minutes,
                                         sessions, status, created_by, updated_by)
            VALUES (o_id, t_id, d, s.id, s.starts_at, s.ends_at, paid, in_at, out_at, s.break_minutes, worked, GREATEST(0, worked - paid), late, 0,
                    1, 'WORKED', owner_id, owner_id);
        END IF;
    END LOOP;

    -- Last week's totals from their lines.
    UPDATE timesheets t
       SET worked_minutes = x.worked, break_minutes = x.brk, overtime_minutes = x.ot, late_minutes = x.late, early_minutes = x.early,
           days_worked = x.days
      FROM (SELECT timesheet_id, sum(worked_minutes) AS worked, sum(CASE WHEN status = 'WORKED' THEN break_minutes ELSE 0 END) AS brk,
                   sum(overtime_minutes) AS ot, sum(late_minutes) AS late, sum(early_minutes) AS early,
                   count(*) FILTER (WHERE status = 'WORKED' AND worked_minutes > 0) AS days
              FROM timesheet_lines GROUP BY timesheet_id) x
     WHERE x.timesheet_id = t.id AND t.property_id = p_id AND t.week_start = monday - 7;
END $$;
