-- =====================================================================
-- V027  India readiness (Sprint 30): FSSAI licence and UPI on bills, Form C for foreign guests
--
-- What an Indian property is expected to print and file, found in the market comparison of 13 Nov 2026:
--   * a restaurant bill carries the FSSAI licence number (FSSAI order, from 1 Oct 2021) and a UPI QR the guest scans;
--   * every foreign guest is reported to the FRRO on Form C within 24 hours of arrival (Foreigners Act 1946, s. 7;
--     Registration of Foreigners Rules 1992, rule 14). The FRRO portal has no public API, so the app keeps a sheet per
--     guest and stay with every field the portal asks for, a deadline, and who filed it under which number.
--
-- Model
--   properties.fssai_licence_no   the 14-digit licence or registration number printed on food bills and invoices.
--   properties.upi_id             the UPI ID (VPA) money is collected on, e.g. palmgrove@okhdfcbank. The app only builds
--                                 a "upi://pay" link and its QR from it: no money moves through the app, and staff
--                                 record the payment with its UPI reference as before.
--   properties.upi_payee_name     the name the guest's UPI app shows (default: the property's name).
--   form_c_records                one per foreign guest and stay, made at check-in. arrived_at is the check-in moment,
--                                 due_at 24 hours later. details_encrypted is the sheet (passport, visa, arrival in India…)
--                                 as AES-256-GCM JSON, like guest_documents.details_encrypted. PENDING → SUBMITTED (with
--                                 the FRRO acknowledgement number when there is one) → back to PENDING by reopening,
--                                 or NOT_REQUIRED with a reason (an OCI holder entered as a foreign national by mistake).
--                                 Never deleted; anonymising the guest empties details_encrypted.
--
-- Jobs: form_c_reminder joins the daily jobs (staff who check guests in get the sheets still to file).
-- =====================================================================

ALTER TABLE properties
    ADD COLUMN fssai_licence_no text NULL,
    ADD COLUMN upi_id           text NULL,
    ADD COLUMN upi_payee_name   text NULL,
    ADD CONSTRAINT ck_properties_fssai     CHECK (fssai_licence_no IS NULL OR fssai_licence_no ~ '^[0-9]{14}$'),
    ADD CONSTRAINT ck_properties_upi_id    CHECK (upi_id IS NULL OR (upi_id ~ '^[A-Za-z0-9._-]{2,255}@[A-Za-z][A-Za-z0-9.-]{1,63}$' AND length(upi_id) <= 100)),
    ADD CONSTRAINT ck_properties_upi_payee CHECK (upi_payee_name IS NULL OR length(btrim(upi_payee_name)) BETWEEN 2 AND 60);

COMMENT ON COLUMN properties.fssai_licence_no IS 'class: public. Printed on food bills.';
COMMENT ON COLUMN properties.upi_id           IS 'class: public. Printed as a QR on bills.';
COMMENT ON COLUMN properties.upi_payee_name   IS 'class: public';

-- ---------------------------------------------------------------------
-- form_c_records
-- ---------------------------------------------------------------------
CREATE TABLE form_c_records (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid        NOT NULL REFERENCES organizations (id),
    property_id        uuid        NOT NULL REFERENCES properties (id),
    reservation_id     uuid        NOT NULL REFERENCES reservations (id),
    guest_id           uuid        NOT NULL REFERENCES guests (id),
    status             text        NOT NULL DEFAULT 'PENDING',
    arrived_at         timestamptz NOT NULL,
    due_at             timestamptz NOT NULL,
    details_encrypted  bytea       NULL,
    key_id             text        NULL,
    frro_reference     text        NULL,
    submitted_at       timestamptz NULL,
    submitted_by       uuid        NULL REFERENCES users (id),
    not_required_reason text       NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    created_by         uuid        NULL,
    updated_by         uuid        NULL,
    CONSTRAINT ck_form_c_status     CHECK (status IN ('PENDING','SUBMITTED','NOT_REQUIRED')),
    CONSTRAINT ck_form_c_due        CHECK (due_at > arrived_at),
    CONSTRAINT ck_form_c_details    CHECK ((details_encrypted IS NULL) = (key_id IS NULL)),
    CONSTRAINT ck_form_c_reference  CHECK (frro_reference IS NULL OR length(btrim(frro_reference)) BETWEEN 3 AND 40),
    CONSTRAINT ck_form_c_submitted  CHECK ((status = 'SUBMITTED') = (submitted_at IS NOT NULL)),
    CONSTRAINT ck_form_c_not_needed CHECK ((status = 'NOT_REQUIRED') = (not_required_reason IS NOT NULL)
                                           AND (not_required_reason IS NULL OR length(btrim(not_required_reason)) BETWEEN 3 AND 300)),
    CONSTRAINT ux_form_c_stay_guest UNIQUE (reservation_id, guest_id)
);
CREATE INDEX ix_form_c_open ON form_c_records (property_id, due_at) WHERE status = 'PENDING';
CREATE INDEX ix_form_c_list ON form_c_records (property_id, arrived_at DESC);
CREATE INDEX ix_form_c_guest ON form_c_records (guest_id);
CREATE TRIGGER trg_form_c_records_no_delete BEFORE DELETE ON form_c_records
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

COMMENT ON TABLE  form_c_records                   IS 'class: sensitive. Form C (foreign guest arrival report) per guest and stay; the sheet is encrypted.';
COMMENT ON COLUMN form_c_records.details_encrypted IS 'class: sensitive. AES-256-GCM JSON: passport, visa, arrival in India, address abroad.';
COMMENT ON COLUMN form_c_records.frro_reference    IS 'class: confidential. The acknowledgement number the FRRO portal gave.';

-- ---------------------------------------------------------------------
-- The Form C reminder joins the scheduled jobs
-- ---------------------------------------------------------------------
ALTER TABLE scheduled_jobs DROP CONSTRAINT ck_scheduled_jobs_job;
ALTER TABLE scheduled_jobs ADD CONSTRAINT ck_scheduled_jobs_job
    CHECK (job IN ('reservation_reminder','pre_arrival','checkout_reminder','payment_reminder','inventory_alert','leave_reminder','form_c_reminder'));
ALTER TABLE job_runs DROP CONSTRAINT ck_job_runs_job;
ALTER TABLE job_runs ADD CONSTRAINT ck_job_runs_job
    CHECK (job IN ('reservation_reminder','pre_arrival','checkout_reminder','payment_reminder','inventory_alert','leave_reminder','form_c_reminder'));
