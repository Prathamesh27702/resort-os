-- =====================================================================
-- V021  Leave, holidays and payroll export (Sprint 19, brief §45, §74)
--
-- Employees ask for leave on their phones; the request is checked against their balance, their other leave and the
-- roster's cover; a manager approves or refuses it. Approval takes the days off the balance and the person off the
-- roster. Holidays and approved hours go into a monthly payroll file.
--
-- Model
--   leave_types       the kinds of leave a property gives: Casual (CL), Sick (SL), Earned (EL), Leave without pay (LOP).
--                     is_paid: counts as paid in payroll. needs_balance: can only be taken from a balance (LOP doesn't).
--                     allow_half_day: half a day can be asked for.
--   leave_policies    how many days a year a leave type gives and how they are earned: MONTHLY (a twelfth on the 1st of
--                     each month), YEARLY (all on 1 January, a part for someone who joins in the year) or NONE (only by
--                     hand). max_balance stops accruing above it. employment_type NULL: everyone; a row for one
--                     employment type (e.g. SEASONAL) wins over it.
--   leave_ledger      every change to a balance, never changed: OPENING (balance brought in), ACCRUAL (earned, one per
--                     employee, type and period, so running the accrual twice adds nothing), TAKEN (approved leave,
--                     negative), RETURNED (approved leave cancelled), ADJUSTMENT (HR, with the reason).
--   leave_balances    the balance per employee and leave type: always the sum of the ledger (a trigger adds each new
--                     entry). It can never go below zero, the final guard against two approvals spending the same day.
--   leave_requests    LV00001…: one employee, one leave type, from–to with half days at either end. days: the days that
--                     count (holidays and the weekly day off inside the range don't); counted_dates: those dates.
--                     PENDING → APPROVED or REJECTED (with the reason) → CANCELLED (with the reason; approved leave only
--                     before it starts, and the days go back). One person's pending or approved leave can't overlap
--                     (EXCLUDE constraint).
--   holidays          the property's public holidays, one per date. Switching one off keeps it in history.
--   payroll_exports   each monthly payroll file made: the month, who made it, the CSV itself and its SHA-256, the
--                     warnings that were accepted. Never changed.
-- =====================================================================

-- ---------------------------------------------------------------------
-- leave_types
-- ---------------------------------------------------------------------
CREATE TABLE leave_types (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    code             text        NOT NULL,
    name             text        NOT NULL,
    is_paid          boolean     NOT NULL DEFAULT true,
    needs_balance    boolean     NOT NULL DEFAULT true,
    allow_half_day   boolean     NOT NULL DEFAULT true,
    sort_order       integer     NOT NULL DEFAULT 0,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_leave_types_code    CHECK (code ~ '^[A-Z][A-Z0-9]{0,9}$'),
    CONSTRAINT ck_leave_types_name    CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT ck_leave_types_balance CHECK (is_paid OR NOT needs_balance),
    CONSTRAINT ux_leave_types_code    UNIQUE (property_id, code),
    CONSTRAINT ux_leave_types_id_prop UNIQUE (id, property_id)
);
CREATE UNIQUE INDEX ux_leave_types_name ON leave_types (property_id, lower(name));
CREATE TRIGGER trg_leave_types_no_delete BEFORE DELETE ON leave_types
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- leave_policies
-- ---------------------------------------------------------------------
CREATE TABLE leave_policies (
    id               uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid         NOT NULL REFERENCES organizations (id),
    property_id      uuid         NOT NULL REFERENCES properties (id),
    leave_type_id    uuid         NOT NULL,
    employment_type  text         NULL,       -- NULL: everyone
    days_per_year    numeric(5,2) NOT NULL,
    accrual          text         NOT NULL DEFAULT 'MONTHLY',
    max_balance      numeric(6,2) NULL,
    is_active        boolean      NOT NULL DEFAULT true,
    created_at       timestamptz  NOT NULL DEFAULT now(),
    updated_at       timestamptz  NOT NULL DEFAULT now(),
    created_by       uuid         NULL,
    updated_by       uuid         NULL,
    CONSTRAINT ck_leave_policies_days    CHECK (days_per_year BETWEEN 0 AND 365),
    CONSTRAINT ck_leave_policies_accrual CHECK (accrual IN ('MONTHLY','YEARLY','NONE')),
    CONSTRAINT ck_leave_policies_max     CHECK (max_balance IS NULL OR max_balance BETWEEN 0 AND 999),
    CONSTRAINT ck_leave_policies_type    CHECK (employment_type IS NULL OR employment_type IN ('FULL_TIME','PART_TIME','CONTRACT','SEASONAL')),
    CONSTRAINT fk_leave_policies_type    FOREIGN KEY (leave_type_id, property_id) REFERENCES leave_types (id, property_id)
);
CREATE UNIQUE INDEX ux_leave_policies_type ON leave_policies (leave_type_id, COALESCE(employment_type, '*'));
CREATE TRIGGER trg_leave_policies_no_delete BEFORE DELETE ON leave_policies
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- leave_requests
-- ---------------------------------------------------------------------
CREATE TABLE leave_requests (
    id               uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid         NOT NULL REFERENCES organizations (id),
    property_id      uuid         NOT NULL REFERENCES properties (id),
    code             text         NOT NULL,
    employee_id      uuid         NOT NULL,
    leave_type_id    uuid         NOT NULL,
    from_date        date         NOT NULL,
    to_date          date         NOT NULL,
    half_day_start   boolean      NOT NULL DEFAULT false,   -- the first day starts after lunch
    half_day_end     boolean      NOT NULL DEFAULT false,   -- the last day ends at lunch
    days             numeric(5,1) NOT NULL,
    counted_dates    date[]       NOT NULL,
    period           daterange    GENERATED ALWAYS AS (daterange(from_date, to_date, '[]')) STORED,
    reason           text         NULL,
    status           text         NOT NULL DEFAULT 'PENDING',
    requested_by     uuid         NULL REFERENCES users (id),
    decided_at       timestamptz  NULL,
    decided_by       uuid         NULL REFERENCES users (id),
    decision_note    text         NULL,
    shifts_cancelled smallint     NOT NULL DEFAULT 0,
    cancelled_at     timestamptz  NULL,
    cancelled_by     uuid         NULL REFERENCES users (id),
    cancel_reason    text         NULL,
    created_at       timestamptz  NOT NULL DEFAULT now(),
    updated_at       timestamptz  NOT NULL DEFAULT now(),
    created_by       uuid         NULL,
    updated_by       uuid         NULL,
    CONSTRAINT ck_leave_requests_code     CHECK (code ~ '^LV[0-9]{5,}$'),
    CONSTRAINT ck_leave_requests_dates    CHECK (to_date >= from_date AND to_date - from_date <= 90),
    CONSTRAINT ck_leave_requests_half     CHECK (from_date <> to_date OR NOT (half_day_start AND half_day_end)),
    CONSTRAINT ck_leave_requests_days     CHECK (days > 0 AND days = cardinality(counted_dates)
                                                 - (CASE WHEN half_day_start AND counted_dates[1] = from_date THEN 0.5 ELSE 0 END)
                                                 - (CASE WHEN half_day_end AND counted_dates[cardinality(counted_dates)] = to_date THEN 0.5 ELSE 0 END)),
    CONSTRAINT ck_leave_requests_status   CHECK (status IN ('PENDING','APPROVED','REJECTED','CANCELLED')),
    CONSTRAINT ck_leave_requests_reason   CHECK (reason IS NULL OR length(btrim(reason)) BETWEEN 1 AND 300),
    CONSTRAINT ck_leave_requests_decided  CHECK (status = 'PENDING' OR status = 'CANCELLED' OR decided_at IS NOT NULL),
    CONSTRAINT ck_leave_requests_rejected CHECK (status <> 'REJECTED' OR decision_note IS NOT NULL),
    CONSTRAINT ck_leave_requests_cancel   CHECK ((status = 'CANCELLED') = (cancelled_at IS NOT NULL)
                                                 AND (status <> 'CANCELLED' OR cancel_reason IS NOT NULL)),
    CONSTRAINT fk_leave_requests_employee FOREIGN KEY (employee_id, property_id) REFERENCES employees (id, property_id),
    CONSTRAINT fk_leave_requests_type     FOREIGN KEY (leave_type_id, property_id) REFERENCES leave_types (id, property_id),
    CONSTRAINT ux_leave_requests_code     UNIQUE (property_id, code),
    -- One person's leave can't overlap: two pending or approved requests never share a day.
    CONSTRAINT ex_leave_requests_overlap  EXCLUDE USING gist (employee_id WITH =, period WITH &&)
        WHERE (status IN ('PENDING','APPROVED'))
);
CREATE INDEX ix_leave_requests_dates ON leave_requests (property_id, from_date, to_date) WHERE status IN ('PENDING','APPROVED');
CREATE INDEX ix_leave_requests_employee ON leave_requests (employee_id, from_date DESC);
CREATE TRIGGER trg_leave_requests_no_delete BEFORE DELETE ON leave_requests
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- leave_ledger (append-only) and leave_balances (= the ledger's sum)
-- ---------------------------------------------------------------------
CREATE TABLE leave_ledger (
    id               uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid         NOT NULL REFERENCES organizations (id),
    property_id      uuid         NOT NULL REFERENCES properties (id),
    employee_id      uuid         NOT NULL,
    leave_type_id    uuid         NOT NULL,
    entry_type       text         NOT NULL,
    days             numeric(6,2) NOT NULL,
    entry_date       date         NOT NULL,
    period           text         NULL,       -- ACCRUAL: '2026-11' (monthly) or '2026' (yearly)
    leave_request_id uuid         NULL REFERENCES leave_requests (id),
    reason           text         NULL,
    created_at       timestamptz  NOT NULL DEFAULT now(),
    created_by       uuid         NULL REFERENCES users (id),
    CONSTRAINT ck_leave_ledger_type    CHECK (entry_type IN ('OPENING','ACCRUAL','TAKEN','RETURNED','ADJUSTMENT')),
    CONSTRAINT ck_leave_ledger_sign    CHECK (CASE entry_type
                                                WHEN 'TAKEN'      THEN days < 0
                                                WHEN 'ACCRUAL'    THEN days >= 0
                                                WHEN 'ADJUSTMENT' THEN days <> 0
                                                ELSE days > 0 END),
    CONSTRAINT ck_leave_ledger_period  CHECK ((entry_type = 'ACCRUAL') = (period IS NOT NULL)
                                              AND (period IS NULL OR period ~ '^[0-9]{4}(-(0[1-9]|1[0-2]))?$')),
    CONSTRAINT ck_leave_ledger_request CHECK ((entry_type IN ('TAKEN','RETURNED')) = (leave_request_id IS NOT NULL)),
    CONSTRAINT ck_leave_ledger_reason  CHECK ((entry_type IN ('ADJUSTMENT','OPENING')) = (reason IS NOT NULL)
                                              AND (reason IS NULL OR length(btrim(reason)) BETWEEN 3 AND 300)),
    CONSTRAINT fk_leave_ledger_employee FOREIGN KEY (employee_id, property_id) REFERENCES employees (id, property_id),
    CONSTRAINT fk_leave_ledger_type     FOREIGN KEY (leave_type_id, property_id) REFERENCES leave_types (id, property_id)
);
-- Running the accrual twice for a month adds nothing; a request is taken and given back at most once.
CREATE UNIQUE INDEX ux_leave_ledger_accrual ON leave_ledger (employee_id, leave_type_id, period) WHERE entry_type = 'ACCRUAL';
CREATE UNIQUE INDEX ux_leave_ledger_request ON leave_ledger (leave_request_id, entry_type) WHERE leave_request_id IS NOT NULL;
CREATE INDEX ix_leave_ledger_employee ON leave_ledger (employee_id, leave_type_id, created_at);
CREATE TRIGGER trg_leave_ledger_append_only BEFORE UPDATE OR DELETE ON leave_ledger
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

CREATE TABLE leave_balances (
    id               uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid         NOT NULL REFERENCES organizations (id),
    property_id      uuid         NOT NULL REFERENCES properties (id),
    employee_id      uuid         NOT NULL,
    leave_type_id    uuid         NOT NULL,
    balance          numeric(6,2) NOT NULL DEFAULT 0,
    updated_at       timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT ck_leave_balances_not_negative CHECK (balance >= 0),
    CONSTRAINT fk_leave_balances_employee FOREIGN KEY (employee_id, property_id) REFERENCES employees (id, property_id),
    CONSTRAINT fk_leave_balances_type     FOREIGN KEY (leave_type_id, property_id) REFERENCES leave_types (id, property_id),
    CONSTRAINT ux_leave_balances          UNIQUE (employee_id, leave_type_id)
);

-- Every ledger entry moves the balance in the same transaction: the balance is always the ledger's sum.
CREATE FUNCTION fn_leave_ledger_balance() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    -- Update first: an INSERT … ON CONFLICT would check the new row (a negative TAKEN) against the CHECK before merging.
    UPDATE leave_balances SET balance = balance + NEW.days, updated_at = now()
    WHERE employee_id = NEW.employee_id AND leave_type_id = NEW.leave_type_id;
    IF NOT FOUND THEN
        INSERT INTO leave_balances (organization_id, property_id, employee_id, leave_type_id, balance, updated_at)
        VALUES (NEW.organization_id, NEW.property_id, NEW.employee_id, NEW.leave_type_id, NEW.days, now());
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_leave_ledger_balance AFTER INSERT ON leave_ledger
    FOR EACH ROW EXECUTE FUNCTION fn_leave_ledger_balance();

-- ---------------------------------------------------------------------
-- holidays
-- ---------------------------------------------------------------------
CREATE TABLE holidays (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    holiday_date     date        NOT NULL,
    name             text        NOT NULL,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_holidays_name CHECK (length(btrim(name)) BETWEEN 2 AND 60)
);
CREATE UNIQUE INDEX ux_holidays_date ON holidays (property_id, holiday_date) WHERE is_active;
CREATE TRIGGER trg_holidays_no_delete BEFORE DELETE ON holidays
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- payroll_exports (append-only)
-- ---------------------------------------------------------------------
CREATE TABLE payroll_exports (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    period_start     date        NOT NULL,
    period_end       date        NOT NULL,
    format           text        NOT NULL DEFAULT 'GENERIC_CSV_V1',
    file_name        text        NOT NULL,
    content          bytea       NOT NULL,
    sha256           text        NOT NULL,
    employees        integer     NOT NULL,
    timesheets_locked integer    NOT NULL DEFAULT 0,
    warnings         text[]      NOT NULL DEFAULT '{}',
    created_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL REFERENCES users (id),
    CONSTRAINT ck_payroll_exports_month  CHECK (extract(day FROM period_start) = 1
                                                AND period_end = (period_start + interval '1 month' - interval '1 day')::date),
    CONSTRAINT ck_payroll_exports_format CHECK (format IN ('GENERIC_CSV_V1')),
    CONSTRAINT ck_payroll_exports_sha    CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_payroll_exports_counts CHECK (employees >= 0 AND timesheets_locked >= 0)
);
CREATE INDEX ix_payroll_exports_period ON payroll_exports (property_id, period_start, created_at DESC);
CREATE TRIGGER trg_payroll_exports_append_only BEFORE UPDATE OR DELETE ON payroll_exports
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- ---------------------------------------------------------------------
-- Permission: making the payroll file (owner and HR by default)
-- ---------------------------------------------------------------------
INSERT INTO permissions (code, module, description, is_sensitive) VALUES
('payroll.export', 'payroll', 'Make the monthly payroll export file', true);

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, 'payroll.export' FROM roles r
WHERE r.organization_id IS NULL AND r.code IN ('OWNER','HR');

-- ---------------------------------------------------------------------
-- Data classification (brief §59)
-- ---------------------------------------------------------------------
COMMENT ON TABLE leave_types      IS 'class: internal. Kinds of leave a property gives (CL, SL, EL, LOP).';
COMMENT ON TABLE leave_policies   IS 'class: internal. Days a year per leave type and how they are earned.';
COMMENT ON TABLE leave_requests   IS 'class: confidential. Leave asked for: PENDING → APPROVED/REJECTED → CANCELLED.';
COMMENT ON COLUMN leave_requests.reason IS 'class: confidential (may mention health)';
COMMENT ON TABLE leave_ledger     IS 'class: confidential. Every change to a leave balance; never changed.';
COMMENT ON TABLE leave_balances   IS 'class: confidential. Leave balance per employee and type = the sum of leave_ledger.';
COMMENT ON TABLE holidays         IS 'class: internal. The property''s public holidays.';
COMMENT ON TABLE payroll_exports  IS 'class: confidential. Monthly payroll files (days and hours, no pay amounts); never changed.';
