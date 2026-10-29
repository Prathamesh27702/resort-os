-- =====================================================================
-- V019  Employees and roster (Sprint 17, brief §42, §46)
--
-- Who works at the property, what they are paid (kept apart and readable only with employee_salary.read), their
-- documents (encrypted), and the weekly shift roster with clash detection and a staffing shortage view.
--
-- Model
--   departments            Front office, Housekeeping, Kitchen … per property, with a short code.
--   designations           job titles inside a department (Receptionist, Commis chef …).
--   employees              one person: numbered E00001 … per property, department and designation, employment type,
--                          joining date, weekly day off, emergency contact. Optionally linked to one staff account
--                          (users) so they can later see their own shifts and clock in. ACTIVE until they leave
--                          (LEFT with the last working day and a reason).
--   employee_documents     Aadhaar (last 4 digits only, never the full number), PAN, bank account, passport,
--                          driving licence, offer letter … The number is encrypted by the API (AES-256-GCM, key
--                          outside the database) and only its last 4 characters are readable; a scan is an encrypted
--                          file in storage. Opening a document is audited (SENSITIVE_READ). Readable only with
--                          employee_document.read.
--   employee_compensation  what an employee is paid from a date: monthly or per day, basic and allowances, PF and
--                          ESI. A separate table behind a separate endpoint (employee_salary.read): profiles, rosters
--                          and audit entries never show an amount. Never changed or deleted: a raise or a correction
--                          is a new row; the latest row on or before a date is the pay for that date (for one date,
--                          the latest recorded wins).
--   shift_templates        the shifts a department works (Morning 07:00–15:00 …), with the number of people it needs
--                          every day (min_staff). end_time ≤ start_time means the shift ends the next day.
--   roster_shifts          one person on one shift on one day: the department they work in, the local work date and
--                          the exact start and end (timestamptz, from the property's time zone). Removing a shift
--                          sets CANCELLED with a reason; the row stays.
--
-- Guards
--   * EXCLUDE (employee_id =, period &&) on scheduled shifts: one person can't be on two overlapping shifts, even
--     when two managers save at the same moment. A manager can allow a deliberate overlap (a handover) with a
--     reason; those rows are left out of the constraint.
--   * one employee per staff account; a leaving date on or after the joining date; shifts at most 16 hours.
-- =====================================================================

-- ---------------------------------------------------------------------
-- departments / designations
-- ---------------------------------------------------------------------
CREATE TABLE departments (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    code             text        NOT NULL,
    name             text        NOT NULL,
    sort_order       integer     NOT NULL DEFAULT 0,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_departments_code    CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,19}$'),
    CONSTRAINT ck_departments_name    CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT ux_departments_code    UNIQUE (property_id, code),
    CONSTRAINT ux_departments_id_prop UNIQUE (id, property_id)
);
CREATE UNIQUE INDEX ux_departments_name ON departments (property_id, lower(name));
CREATE TRIGGER trg_departments_no_delete BEFORE DELETE ON departments
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

CREATE TABLE designations (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    department_id    uuid        NOT NULL,
    name             text        NOT NULL,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_designations_name       CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT fk_designations_department FOREIGN KEY (department_id, property_id) REFERENCES departments (id, property_id),
    CONSTRAINT ux_designations_id_dept    UNIQUE (id, department_id)
);
CREATE UNIQUE INDEX ux_designations_name ON designations (department_id, lower(name));
CREATE TRIGGER trg_designations_no_delete BEFORE DELETE ON designations
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- employees
-- ---------------------------------------------------------------------
CREATE TABLE employees (
    id                       uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id          uuid        NOT NULL REFERENCES organizations (id),
    property_id              uuid        NOT NULL REFERENCES properties (id),
    code                     text        NOT NULL,
    first_name               text        NOT NULL,
    last_name                text        NULL,
    phone                    text        NULL,
    email                    text        NULL,
    date_of_birth            date        NULL,
    department_id            uuid        NOT NULL,
    designation_id           uuid        NULL,
    employment_type          text        NOT NULL DEFAULT 'FULL_TIME',
    joined_on                date        NOT NULL,
    weekly_off               smallint    NULL,
    user_id                  uuid        NULL REFERENCES users (id),
    emergency_contact_name   text        NULL,
    emergency_contact_phone  text        NULL,
    address                  text        NULL,
    note                     text        NULL,
    status                   text        NOT NULL DEFAULT 'ACTIVE',
    left_on                  date        NULL,
    leaving_reason           text        NULL,
    created_at               timestamptz NOT NULL DEFAULT now(),
    updated_at               timestamptz NOT NULL DEFAULT now(),
    created_by               uuid        NULL,
    updated_by               uuid        NULL,
    CONSTRAINT ck_employees_code        CHECK (code ~ '^E[0-9]{5,}$'),
    CONSTRAINT ck_employees_first_name  CHECK (length(btrim(first_name)) BETWEEN 1 AND 60),
    CONSTRAINT ck_employees_type        CHECK (employment_type IN ('FULL_TIME','PART_TIME','CONTRACT','SEASONAL')),
    CONSTRAINT ck_employees_weekly_off  CHECK (weekly_off IS NULL OR weekly_off BETWEEN 1 AND 7),   -- ISO: 1 Monday … 7 Sunday
    CONSTRAINT ck_employees_status      CHECK (status IN ('ACTIVE','LEFT')),
    CONSTRAINT ck_employees_left        CHECK ((status = 'LEFT') = (left_on IS NOT NULL)
                                            AND (left_on IS NULL OR left_on >= joined_on)
                                            AND ((status = 'LEFT') = (leaving_reason IS NOT NULL))),
    CONSTRAINT fk_employees_department  FOREIGN KEY (department_id, property_id) REFERENCES departments (id, property_id),
    CONSTRAINT fk_employees_designation FOREIGN KEY (designation_id, department_id) REFERENCES designations (id, department_id),
    CONSTRAINT ux_employees_code        UNIQUE (property_id, code),
    CONSTRAINT ux_employees_id_prop     UNIQUE (id, property_id)
);
-- One employee record per staff account (organisation-wide: a user is one person).
CREATE UNIQUE INDEX ux_employees_user ON employees (user_id) WHERE user_id IS NOT NULL;
CREATE INDEX ix_employees_department ON employees (property_id, department_id, status);
CREATE TRIGGER trg_employees_no_delete BEFORE DELETE ON employees
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- employee_documents (sensitive: encrypted by the API)
-- ---------------------------------------------------------------------
CREATE TABLE employee_documents (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id   uuid        NOT NULL REFERENCES organizations (id),
    employee_id       uuid        NOT NULL REFERENCES employees (id),
    document_type     text        NOT NULL,
    number_encrypted  bytea       NULL,       -- AES-256-GCM envelope; NULL for Aadhaar (last 4 only) and once removed
    number_last4      text        NULL,       -- for display: "•••• 1234"
    expires_on        date        NULL,
    note              text        NULL,
    storage_key       text        NULL,       -- encrypted scan in file storage
    file_name         text        NULL,
    content_type      text        NULL,
    size_bytes        integer     NULL,
    key_id            text        NOT NULL,
    status            text        NOT NULL DEFAULT 'ACTIVE',
    removed_at        timestamptz NULL,
    removed_reason    text        NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    created_by        uuid        NULL REFERENCES users (id),
    updated_by        uuid        NULL,
    CONSTRAINT ck_employee_documents_type    CHECK (document_type IN ('AADHAAR','PAN','BANK_ACCOUNT','PASSPORT','DRIVING_LICENCE','OFFER_LETTER','OTHER')),
    CONSTRAINT ck_employee_documents_status  CHECK (status IN ('ACTIVE','REMOVED')),
    CONSTRAINT ck_employee_documents_aadhaar CHECK (document_type <> 'AADHAAR' OR number_encrypted IS NULL),
    CONSTRAINT ck_employee_documents_removed CHECK ((status = 'REMOVED') = (removed_at IS NOT NULL)
                                                AND (status = 'ACTIVE' OR (number_encrypted IS NULL AND storage_key IS NULL AND removed_reason IS NOT NULL))),
    CONSTRAINT ck_employee_documents_note    CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 200),
    CONSTRAINT ck_employee_documents_size    CHECK (size_bytes IS NULL OR size_bytes BETWEEN 1 AND 5242880)
);
CREATE INDEX ix_employee_documents_employee ON employee_documents (employee_id, created_at DESC);
CREATE TRIGGER trg_employee_documents_no_delete BEFORE DELETE ON employee_documents
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- employee_compensation (restricted: employee_salary.read; never changed)
-- ---------------------------------------------------------------------
CREATE TABLE employee_compensation (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    employee_id      uuid          NOT NULL REFERENCES employees (id),
    effective_from   date          NOT NULL,
    pay_basis        text          NOT NULL,
    basic            numeric(12,2) NOT NULL,
    allowances       numeric(12,2) NOT NULL DEFAULT 0,
    pf_applicable    boolean       NOT NULL DEFAULT false,
    esi_applicable   boolean       NOT NULL DEFAULT false,
    note             text          NULL,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    created_by       uuid          NULL REFERENCES users (id),
    CONSTRAINT ck_employee_compensation_basis      CHECK (pay_basis IN ('MONTHLY','DAILY')),
    CONSTRAINT ck_employee_compensation_basic      CHECK (basic > 0 AND basic <= 10000000),
    CONSTRAINT ck_employee_compensation_allowances CHECK (allowances >= 0 AND allowances <= 10000000),
    CONSTRAINT ck_employee_compensation_note       CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 300)
);
CREATE INDEX ix_employee_compensation_employee ON employee_compensation (employee_id, effective_from DESC, created_at DESC);
CREATE TRIGGER trg_employee_compensation_append_only BEFORE UPDATE OR DELETE ON employee_compensation
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- ---------------------------------------------------------------------
-- shift_templates
-- ---------------------------------------------------------------------
CREATE TABLE shift_templates (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    department_id    uuid        NOT NULL,
    code             text        NOT NULL,
    name             text        NOT NULL,
    start_time       time        NOT NULL,
    end_time         time        NOT NULL,   -- ≤ start_time: ends the next day
    break_minutes    smallint    NOT NULL DEFAULT 0,
    min_staff        smallint    NOT NULL DEFAULT 0,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_shift_templates_code       CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,19}$'),
    CONSTRAINT ck_shift_templates_name       CHECK (length(btrim(name)) BETWEEN 2 AND 40),
    CONSTRAINT ck_shift_templates_break      CHECK (break_minutes BETWEEN 0 AND 180),
    CONSTRAINT ck_shift_templates_min_staff  CHECK (min_staff BETWEEN 0 AND 50),
    CONSTRAINT fk_shift_templates_department FOREIGN KEY (department_id, property_id) REFERENCES departments (id, property_id),
    CONSTRAINT ux_shift_templates_code       UNIQUE (property_id, code)
);
CREATE TRIGGER trg_shift_templates_no_delete BEFORE DELETE ON shift_templates
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- roster_shifts
-- ---------------------------------------------------------------------
CREATE TABLE roster_shifts (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id   uuid        NOT NULL REFERENCES organizations (id),
    property_id       uuid        NOT NULL REFERENCES properties (id),
    employee_id       uuid        NOT NULL,
    department_id     uuid        NOT NULL,
    template_id       uuid        NULL REFERENCES shift_templates (id),
    work_date         date        NOT NULL,
    starts_at         timestamptz NOT NULL,
    ends_at           timestamptz NOT NULL,
    break_minutes     smallint    NOT NULL DEFAULT 0,
    period            tstzrange   GENERATED ALWAYS AS (tstzrange(starts_at, ends_at, '[)')) STORED,
    status            text        NOT NULL DEFAULT 'SCHEDULED',
    overlap_allowed   boolean     NOT NULL DEFAULT false,
    overlap_reason    text        NULL,
    note              text        NULL,
    cancel_reason     text        NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    created_by        uuid        NULL,
    updated_by        uuid        NULL,
    CONSTRAINT ck_roster_shifts_times    CHECK (ends_at > starts_at AND ends_at - starts_at <= interval '16 hours'),
    CONSTRAINT ck_roster_shifts_break    CHECK (break_minutes BETWEEN 0 AND 180),
    CONSTRAINT ck_roster_shifts_status   CHECK (status IN ('SCHEDULED','CANCELLED')),
    CONSTRAINT ck_roster_shifts_overlap  CHECK (overlap_allowed = (overlap_reason IS NOT NULL)
                                             AND (overlap_reason IS NULL OR length(btrim(overlap_reason)) BETWEEN 3 AND 200)),
    CONSTRAINT ck_roster_shifts_cancel   CHECK ((status = 'CANCELLED') = (cancel_reason IS NOT NULL)),
    CONSTRAINT ck_roster_shifts_note     CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 200),
    CONSTRAINT fk_roster_shifts_employee   FOREIGN KEY (employee_id, property_id) REFERENCES employees (id, property_id),
    CONSTRAINT fk_roster_shifts_department FOREIGN KEY (department_id, property_id) REFERENCES departments (id, property_id),
    -- The clash guard: one person, no two overlapping scheduled shifts (unless a manager allowed this one).
    CONSTRAINT ex_roster_shifts_clash EXCLUDE USING gist (employee_id WITH =, period WITH &&)
        WHERE (status = 'SCHEDULED' AND NOT overlap_allowed)
);
CREATE INDEX ix_roster_shifts_week ON roster_shifts (property_id, work_date) WHERE status = 'SCHEDULED';
CREATE INDEX ix_roster_shifts_employee ON roster_shifts (employee_id, work_date);
CREATE TRIGGER trg_roster_shifts_no_delete BEFORE DELETE ON roster_shifts
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- Data classification (brief §59)
-- ---------------------------------------------------------------------
COMMENT ON TABLE departments           IS 'class: internal. Departments of a property (Front office, Kitchen …).';
COMMENT ON TABLE designations          IS 'class: internal. Job titles inside a department.';
COMMENT ON TABLE employees             IS 'class: confidential. Employee profiles; optionally linked to one staff account.';
COMMENT ON COLUMN employees.phone                   IS 'class: confidential';
COMMENT ON COLUMN employees.email                   IS 'class: confidential';
COMMENT ON COLUMN employees.address                 IS 'class: confidential';
COMMENT ON COLUMN employees.date_of_birth           IS 'class: sensitive';
COMMENT ON COLUMN employees.emergency_contact_phone IS 'class: confidential';
COMMENT ON TABLE employee_documents    IS 'class: sensitive. Encrypted by the API; Aadhaar keeps the last 4 digits only. Needs employee_document.read.';
COMMENT ON TABLE employee_compensation IS 'class: sensitive. Pay history, never changed. Needs employee_salary.read; amounts are masked in audit_logs.';
COMMENT ON TABLE shift_templates       IS 'class: internal. Shifts a department works and how many people each needs.';
COMMENT ON TABLE roster_shifts         IS 'class: internal. The roster; an EXCLUDE constraint stops one person being on two overlapping shifts.';
