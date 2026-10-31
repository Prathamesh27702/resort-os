-- =====================================================================
-- V020  Attendance and timesheets (Sprint 18, brief §43, §44, §74)
--
-- Staff clock in and out on their phones at the resort; each week becomes a timesheet that the employee submits and a
-- manager approves, then locks for payroll.
--
-- Model
--   geofences              where clocking in counts as "at work": a point (latitude, longitude) and a radius in metres.
--                          A property can have several (the main campus, the beach shack). None set up: clock-ins are
--                          accepted and marked NO_GEOFENCE.
--   attendance_events      what happened, never changed: CLOCK_IN, BREAK_START, BREAK_END, CLOCK_OUT for one employee at
--                          one instant, with the work date it belongs to (the rostered shift's date, else the local date
--                          of the clock-in), the phone's coordinates and accuracy, how far it was from the nearest
--                          geofence, and whether it was INSIDE, NO_GEOFENCE or a manager OVERRIDE (source MANAGER, with
--                          the reason: e.g. the phone had no signal, or the person forgot to clock out).
--   timesheets             one employee × one week (Monday to Sunday) with the week's totals. DRAFT → SUBMITTED
--                          (employee) → APPROVED or REJECTED (manager; rejected goes back to the employee) → LOCKED (for
--                          payroll; nothing changes after that).
--   timesheet_lines        one per work date with a shift or a clock-in: the rostered shift, first in, last out, breaks,
--                          worked, overtime, late and early minutes, and the day's status (WORKED, ABSENT, ON_SHIFT,
--                          MISSING_OUT). Worked out by the API in the property's time zone from the events while the
--                          timesheet is DRAFT or REJECTED; frozen once submitted. A corrected line is never recalculated
--                          from events again.
--   timesheet_corrections  a manager changing a day's times (in, out, break) with the reason: old and new values. Never
--                          changed or deleted. Refused once the timesheet is LOCKED.
--
-- Guards
--   * one timesheet per employee and week; week_start is a Monday.
--   * events and corrections are append-only (triggers); a manager event always carries its reason.
--   * a LOCKED timesheet can't change: the trigger refuses any update to it or its lines.
-- =====================================================================

-- ---------------------------------------------------------------------
-- geofences
-- ---------------------------------------------------------------------
CREATE TABLE geofences (
    id               uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid         NOT NULL REFERENCES organizations (id),
    property_id      uuid         NOT NULL REFERENCES properties (id),
    name             text         NOT NULL,
    latitude         numeric(9,6) NOT NULL,
    longitude        numeric(9,6) NOT NULL,
    radius_m         integer      NOT NULL,
    is_active        boolean      NOT NULL DEFAULT true,
    created_at       timestamptz  NOT NULL DEFAULT now(),
    updated_at       timestamptz  NOT NULL DEFAULT now(),
    created_by       uuid         NULL,
    updated_by       uuid         NULL,
    CONSTRAINT ck_geofences_name   CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT ck_geofences_lat    CHECK (latitude  BETWEEN -90  AND 90),
    CONSTRAINT ck_geofences_lng    CHECK (longitude BETWEEN -180 AND 180),
    CONSTRAINT ck_geofences_radius CHECK (radius_m BETWEEN 20 AND 2000)
);
CREATE UNIQUE INDEX ux_geofences_name ON geofences (property_id, lower(name));
CREATE TRIGGER trg_geofences_no_delete BEFORE DELETE ON geofences
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- attendance_events (append-only)
-- ---------------------------------------------------------------------
CREATE TABLE attendance_events (
    id               uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid         NOT NULL REFERENCES organizations (id),
    property_id      uuid         NOT NULL REFERENCES properties (id),
    employee_id      uuid         NOT NULL,
    event_type       text         NOT NULL,
    occurred_at      timestamptz  NOT NULL,
    work_date        date         NOT NULL,
    roster_shift_id  uuid         NULL REFERENCES roster_shifts (id),
    latitude         numeric(9,6) NULL,
    longitude        numeric(9,6) NULL,
    accuracy_m       integer      NULL,
    distance_m       integer      NULL,       -- metres from the nearest active geofence's centre
    geofence_id      uuid         NULL REFERENCES geofences (id),
    location_status  text         NOT NULL,
    source           text         NOT NULL DEFAULT 'DEVICE',
    reason           text         NULL,
    device           text         NULL,
    created_at       timestamptz  NOT NULL DEFAULT now(),
    created_by       uuid         NULL REFERENCES users (id),
    CONSTRAINT ck_attendance_events_type     CHECK (event_type IN ('CLOCK_IN','BREAK_START','BREAK_END','CLOCK_OUT')),
    CONSTRAINT ck_attendance_events_location CHECK (location_status IN ('INSIDE','NO_GEOFENCE','OVERRIDE')),
    CONSTRAINT ck_attendance_events_source   CHECK (source IN ('DEVICE','MANAGER')),
    CONSTRAINT ck_attendance_events_reason   CHECK ((source = 'MANAGER') = (reason IS NOT NULL)
                                                AND (reason IS NULL OR length(btrim(reason)) BETWEEN 3 AND 300)),
    CONSTRAINT ck_attendance_events_override CHECK ((location_status = 'OVERRIDE') = (source = 'MANAGER')),
    CONSTRAINT ck_attendance_events_inside   CHECK (location_status <> 'INSIDE' OR (geofence_id IS NOT NULL AND latitude IS NOT NULL)),
    CONSTRAINT ck_attendance_events_coords   CHECK ((latitude IS NULL) = (longitude IS NULL)
                                                AND (latitude IS NULL OR latitude BETWEEN -90 AND 90)
                                                AND (longitude IS NULL OR longitude BETWEEN -180 AND 180)
                                                AND (accuracy_m IS NULL OR accuracy_m >= 0)),
    CONSTRAINT fk_attendance_events_employee FOREIGN KEY (employee_id, property_id) REFERENCES employees (id, property_id)
);
CREATE INDEX ix_attendance_events_employee ON attendance_events (employee_id, occurred_at);
CREATE INDEX ix_attendance_events_day ON attendance_events (property_id, work_date);
CREATE TRIGGER trg_attendance_events_append_only BEFORE UPDATE OR DELETE ON attendance_events
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- ---------------------------------------------------------------------
-- timesheets
-- ---------------------------------------------------------------------
CREATE TABLE timesheets (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id   uuid        NOT NULL REFERENCES organizations (id),
    property_id       uuid        NOT NULL REFERENCES properties (id),
    employee_id       uuid        NOT NULL,
    week_start        date        NOT NULL,
    status            text        NOT NULL DEFAULT 'DRAFT',
    worked_minutes    integer     NOT NULL DEFAULT 0,
    break_minutes     integer     NOT NULL DEFAULT 0,
    overtime_minutes  integer     NOT NULL DEFAULT 0,
    late_minutes      integer     NOT NULL DEFAULT 0,
    early_minutes     integer     NOT NULL DEFAULT 0,
    days_worked       smallint    NOT NULL DEFAULT 0,
    note              text        NULL,       -- the employee's note when submitting
    submitted_at      timestamptz NULL,
    submitted_by      uuid        NULL REFERENCES users (id),
    decided_at        timestamptz NULL,
    decided_by        uuid        NULL REFERENCES users (id),
    rejection_reason  text        NULL,
    locked_at         timestamptz NULL,
    locked_by         uuid        NULL REFERENCES users (id),
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    created_by        uuid        NULL,
    updated_by        uuid        NULL,
    CONSTRAINT ck_timesheets_week     CHECK (extract(isodow FROM week_start) = 1),
    CONSTRAINT ck_timesheets_status   CHECK (status IN ('DRAFT','SUBMITTED','APPROVED','REJECTED','LOCKED')),
    CONSTRAINT ck_timesheets_minutes  CHECK (worked_minutes >= 0 AND break_minutes >= 0 AND overtime_minutes >= 0
                                         AND late_minutes >= 0 AND early_minutes >= 0 AND days_worked BETWEEN 0 AND 7),
    CONSTRAINT ck_timesheets_rejected CHECK ((status = 'REJECTED') = (rejection_reason IS NOT NULL)),
    CONSTRAINT ck_timesheets_decided  CHECK (status NOT IN ('APPROVED','REJECTED','LOCKED') OR decided_at IS NOT NULL),
    CONSTRAINT ck_timesheets_locked   CHECK ((status = 'LOCKED') = (locked_at IS NOT NULL)),
    CONSTRAINT ck_timesheets_note     CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 300),
    CONSTRAINT fk_timesheets_employee FOREIGN KEY (employee_id, property_id) REFERENCES employees (id, property_id),
    CONSTRAINT ux_timesheets_week     UNIQUE (employee_id, week_start)
);
CREATE INDEX ix_timesheets_week ON timesheets (property_id, week_start, status);
CREATE TRIGGER trg_timesheets_no_delete BEFORE DELETE ON timesheets
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- A locked timesheet is final (payroll has used it).
CREATE FUNCTION fn_timesheet_locked() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status = 'LOCKED' THEN
        RAISE EXCEPTION 'timesheet % is locked for payroll and cannot change', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_timesheets_locked BEFORE UPDATE ON timesheets
    FOR EACH ROW EXECUTE FUNCTION fn_timesheet_locked();

-- ---------------------------------------------------------------------
-- timesheet_lines (worked out by the API; frozen once submitted)
-- ---------------------------------------------------------------------
CREATE TABLE timesheet_lines (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid        NOT NULL REFERENCES organizations (id),
    timesheet_id       uuid        NOT NULL REFERENCES timesheets (id),
    work_date          date        NOT NULL,
    roster_shift_id    uuid        NULL REFERENCES roster_shifts (id),
    scheduled_start    timestamptz NULL,
    scheduled_end      timestamptz NULL,
    scheduled_minutes  integer     NULL,      -- the shift's length minus its break
    clock_in_at        timestamptz NULL,
    clock_out_at       timestamptz NULL,
    break_minutes      integer     NOT NULL DEFAULT 0,
    worked_minutes     integer     NOT NULL DEFAULT 0,
    overtime_minutes   integer     NOT NULL DEFAULT 0,
    late_minutes       integer     NOT NULL DEFAULT 0,
    early_minutes      integer     NOT NULL DEFAULT 0,
    sessions           smallint    NOT NULL DEFAULT 0,
    status             text        NOT NULL,
    overridden         boolean     NOT NULL DEFAULT false,   -- a manager clocked them in or out
    corrected          boolean     NOT NULL DEFAULT false,   -- changed by a correction; never recalculated from events
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    created_by         uuid        NULL,
    updated_by         uuid        NULL,
    CONSTRAINT ck_timesheet_lines_status  CHECK (status IN ('WORKED','ABSENT','ON_SHIFT','MISSING_OUT')),
    CONSTRAINT ck_timesheet_lines_minutes CHECK (break_minutes >= 0 AND worked_minutes >= 0 AND overtime_minutes >= 0
                                              AND late_minutes >= 0 AND early_minutes >= 0 AND worked_minutes <= 24 * 60),
    CONSTRAINT ck_timesheet_lines_times   CHECK (clock_out_at IS NULL OR (clock_in_at IS NOT NULL AND clock_out_at > clock_in_at)),
    CONSTRAINT ck_timesheet_lines_worked  CHECK (status <> 'WORKED' OR (clock_in_at IS NOT NULL AND clock_out_at IS NOT NULL)),
    CONSTRAINT ux_timesheet_lines_day     UNIQUE (timesheet_id, work_date)
);

CREATE FUNCTION fn_timesheet_line_locked() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (SELECT status FROM timesheets WHERE id = COALESCE(OLD.timesheet_id, NEW.timesheet_id)) = 'LOCKED' THEN
        RAISE EXCEPTION 'timesheet % is locked for payroll and cannot change', COALESCE(OLD.timesheet_id, NEW.timesheet_id)
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;
CREATE TRIGGER trg_timesheet_lines_locked BEFORE INSERT OR UPDATE OR DELETE ON timesheet_lines
    FOR EACH ROW EXECUTE FUNCTION fn_timesheet_line_locked();

-- ---------------------------------------------------------------------
-- timesheet_corrections (append-only)
-- ---------------------------------------------------------------------
CREATE TABLE timesheet_corrections (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid        NOT NULL REFERENCES organizations (id),
    timesheet_id       uuid        NOT NULL REFERENCES timesheets (id),
    work_date          date        NOT NULL,
    timesheet_status   text        NOT NULL,  -- the timesheet's status when it was corrected
    old_clock_in_at    timestamptz NULL,
    old_clock_out_at   timestamptz NULL,
    old_break_minutes  integer     NULL,
    old_worked_minutes integer     NULL,
    new_clock_in_at    timestamptz NULL,
    new_clock_out_at   timestamptz NULL,
    new_break_minutes  integer     NOT NULL,
    new_worked_minutes integer     NOT NULL,
    reason             text        NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    created_by         uuid        NULL REFERENCES users (id),
    CONSTRAINT ck_timesheet_corrections_reason CHECK (length(btrim(reason)) BETWEEN 3 AND 300),
    CONSTRAINT ck_timesheet_corrections_status CHECK (timesheet_status IN ('DRAFT','SUBMITTED','APPROVED','REJECTED')),
    CONSTRAINT ck_timesheet_corrections_times  CHECK (new_clock_out_at IS NULL OR (new_clock_in_at IS NOT NULL AND new_clock_out_at > new_clock_in_at))
);
CREATE INDEX ix_timesheet_corrections_sheet ON timesheet_corrections (timesheet_id, created_at);
CREATE TRIGGER trg_timesheet_corrections_append_only BEFORE UPDATE OR DELETE ON timesheet_corrections
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- ---------------------------------------------------------------------
-- Data classification (brief §59)
-- ---------------------------------------------------------------------
COMMENT ON TABLE geofences             IS 'class: internal. Where clocking in counts as at work: a point and a radius.';
COMMENT ON TABLE attendance_events     IS 'class: confidential. Clock-ins, breaks and clock-outs with the phone''s location; never changed.';
COMMENT ON COLUMN attendance_events.latitude  IS 'class: confidential';
COMMENT ON COLUMN attendance_events.longitude IS 'class: confidential';
COMMENT ON TABLE timesheets            IS 'class: confidential. One employee''s week: DRAFT → SUBMITTED → APPROVED/REJECTED → LOCKED.';
COMMENT ON TABLE timesheet_lines       IS 'class: confidential. One work day of a timesheet, worked out in the property''s time zone.';
COMMENT ON TABLE timesheet_corrections IS 'class: confidential. Changes to a day''s times with the reason; never changed.';
