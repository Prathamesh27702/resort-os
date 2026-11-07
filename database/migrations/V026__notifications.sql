-- =====================================================================
-- V026  Notifications: WhatsApp, email, in-app messages and scheduled jobs (Sprint 24, brief §32, §69, §70, §76)
--
-- Something happens (a booking is confirmed) or a scheduled job finds work (guests arriving tomorrow). In the same
-- transaction as that business change, a row is written to outbox_messages: who it is for, on which channel, and the
-- message already filled in from its template. Nothing is sent inside the business transaction. A background worker
-- (the dispatcher) picks queued rows one at a time with FOR UPDATE SKIP LOCKED, sends them through WhatsApp's Cloud API,
-- the email provider or the in-app bell, and records the result. A failed send that may work later (the provider is
-- down, a timeout) is tried again later; one that can't (the number isn't on WhatsApp) fails at once with the reason.
--
-- Model
--   message_templates   per property, one per message (booking_confirmation, reservation_reminder…) and channel
--                       (WHATSAPP, EMAIL, IN_APP): the wording with {{variables}}, the name of the template approved in
--                       WhatsApp Manager and its language, the subject (email and in-app). Created with default wording on
--                       first use; edited on the Messages page without a release.
--   outbox_messages     one row per message to one person on one channel. dedupe_key is UNIQUE per property
--                       (booking_confirmation:<reservation>:WHATSAPP, reservation_reminder:<reservation>:<arrival>:EMAIL…),
--                       so writing the same message twice (a job that runs twice, two API instances) inserts it once.
--                       QUEUED → SENT → DELIVERED → READ, or FAILED (with the reason); SKIPPED when it couldn't be sent at
--                       all (the guest has no WhatsApp number, opted out, the template is off), so staff see why.
--   whatsapp_messages   what WhatsApp accepted: its message id (wamid) and the statuses its webhooks reported (sent,
--                       delivered, read, failed with the error), forward only, and the pricing category.
--   email_messages      what the email provider accepted: its message id.
--   notifications       the staff bell: one row per user and message; read_at when they open it.
--   scheduled_jobs      the PostgreSQL-backed scheduler (no Redis, brief §76): per property and job, on/off, the local time
--                       it runs and next_run_at. An instance claims a due job by moving next_run_at forward with a
--                       compare-and-set UPDATE, so one instance runs it.
--   job_runs            what each job did, once per business key: UNIQUE (property, job, business key), e.g.
--                       reservation_reminder / <reservation>:<arrival date>. A job that runs again (by hand, after a crash,
--                       on two instances) finds the key taken and sends nothing twice. Append-only.
--
-- Also: webhook_events takes WhatsApp's status webhooks (provider WHATSAPP, event id <wamid>:<status>), verified with
-- X-Hub-Signature-256 and processed once each; guest_consents gains SERVICE_WHATSAPP and SERVICE_EMAIL, the guest's
-- choice about booking messages (no row = allowed; the latest row with granted = false = opted out).
--
-- Settings read (property_settings; defaults in code)
--   notifications.pre_arrival_days   3: the pre-arrival message goes this many days before arrival.
-- No sensitive data in messages (brief §32): no ID numbers, no payment details beyond the amount and a pay link.
-- =====================================================================

-- ---------------------------------------------------------------------
-- message_templates
-- ---------------------------------------------------------------------
CREATE TABLE message_templates (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid        NOT NULL REFERENCES organizations (id),
    property_id        uuid        NOT NULL REFERENCES properties (id),
    code               text        NOT NULL,
    channel            text        NOT NULL,
    name               text        NOT NULL,
    provider_template  text        NULL,               -- WhatsApp: the template's name in WhatsApp Manager
    language           text        NOT NULL DEFAULT 'en',
    subject            text        NULL,               -- email subject / in-app title
    body               text        NOT NULL,
    is_active          boolean     NOT NULL DEFAULT true,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    created_by         uuid        NULL,
    updated_by         uuid        NULL,
    CONSTRAINT ck_message_templates_code     CHECK (code ~ '^[a-z][a-z0-9_]{2,40}$'),
    CONSTRAINT ck_message_templates_channel  CHECK (channel IN ('WHATSAPP','EMAIL','IN_APP')),
    CONSTRAINT ck_message_templates_name     CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT ck_message_templates_provider CHECK ((channel = 'WHATSAPP') = (provider_template IS NOT NULL)
                                                    AND (provider_template IS NULL OR (provider_template ~ '^[a-z0-9_]+$' AND length(provider_template) <= 512))),
    CONSTRAINT ck_message_templates_language CHECK (language ~ '^[a-z]{2,3}(_[A-Z]{2})?$'),
    CONSTRAINT ck_message_templates_subject  CHECK ((channel = 'WHATSAPP') = (subject IS NULL)
                                                    AND (subject IS NULL OR length(btrim(subject)) BETWEEN 2 AND 150)),
    CONSTRAINT ck_message_templates_body     CHECK (length(btrim(body)) BETWEEN 3 AND 4000
                                                    AND (channel <> 'WHATSAPP' OR length(body) <= 1024)),
    CONSTRAINT ux_message_templates          UNIQUE (property_id, code, channel)
);
CREATE TRIGGER trg_message_templates_no_delete BEFORE DELETE ON message_templates
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- outbox_messages
-- ---------------------------------------------------------------------
CREATE TABLE outbox_messages (
    id                   uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id      uuid        NOT NULL REFERENCES organizations (id),
    property_id          uuid        NOT NULL REFERENCES properties (id),
    dedupe_key           text        NOT NULL,
    template_code        text        NOT NULL,
    channel              text        NOT NULL,
    recipient_type       text        NOT NULL,
    guest_id             uuid        NULL REFERENCES guests (id),
    user_id              uuid        NULL REFERENCES users (id),
    reservation_id       uuid        NULL REFERENCES reservations (id),
    recipient_name       text        NOT NULL,
    to_address           text        NULL,               -- +919820012345 or someone@example.com; NULL when skipped or anonymised
    provider_template    text        NULL,
    language             text        NULL,
    subject              text        NULL,
    body                 text        NOT NULL,           -- as filled in when it was queued
    params               jsonb       NOT NULL DEFAULT '{}'::jsonb,  -- WhatsApp's named body parameters
    link                 text        NULL,               -- in-app: where the bell takes the user
    source               text        NOT NULL,
    job_run_id           uuid        NULL,
    status               text        NOT NULL DEFAULT 'QUEUED',
    attempts             integer     NOT NULL DEFAULT 0,
    next_attempt_at      timestamptz NOT NULL DEFAULT now(),
    last_error           text        NULL,
    provider_message_id  text        NULL,
    created_at           timestamptz NOT NULL DEFAULT now(),
    sent_at              timestamptz NULL,
    delivered_at         timestamptz NULL,
    read_at              timestamptz NULL,
    failed_at            timestamptz NULL,
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_outbox_messages_key       CHECK (length(dedupe_key) BETWEEN 3 AND 200),
    CONSTRAINT ck_outbox_messages_channel   CHECK (channel IN ('WHATSAPP','EMAIL','IN_APP')),
    CONSTRAINT ck_outbox_messages_recipient CHECK ((recipient_type = 'GUEST' AND guest_id IS NOT NULL AND user_id IS NULL)
                                                   OR (recipient_type = 'USER' AND user_id IS NOT NULL AND guest_id IS NULL)),
    CONSTRAINT ck_outbox_messages_in_app    CHECK (channel <> 'IN_APP' OR recipient_type = 'USER'),
    CONSTRAINT ck_outbox_messages_source    CHECK (source IN ('EVENT','JOB','MANUAL')),
    CONSTRAINT ck_outbox_messages_status    CHECK (status IN ('QUEUED','SENT','DELIVERED','READ','FAILED','SKIPPED')),
    CONSTRAINT ck_outbox_messages_attempts  CHECK (attempts BETWEEN 0 AND 20),
    CONSTRAINT ck_outbox_messages_error     CHECK (last_error IS NULL OR length(last_error) <= 1000),
    CONSTRAINT ck_outbox_messages_why       CHECK (status NOT IN ('FAILED','SKIPPED') OR last_error IS NOT NULL),
    CONSTRAINT ck_outbox_messages_sent      CHECK (status NOT IN ('SENT','DELIVERED','READ') OR sent_at IS NOT NULL),
    CONSTRAINT ux_outbox_messages_key       UNIQUE (property_id, dedupe_key)
);
-- What the dispatcher picks next.
CREATE INDEX ix_outbox_messages_due ON outbox_messages (next_attempt_at) WHERE status = 'QUEUED';
CREATE INDEX ix_outbox_messages_list ON outbox_messages (property_id, created_at DESC);
CREATE INDEX ix_outbox_messages_reservation ON outbox_messages (reservation_id, created_at) WHERE reservation_id IS NOT NULL;
CREATE INDEX ix_outbox_messages_guest ON outbox_messages (guest_id) WHERE guest_id IS NOT NULL;
CREATE TRIGGER trg_outbox_messages_no_delete BEFORE DELETE ON outbox_messages
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- A message keeps who it was for and what it said; only its delivery moves (and anonymising a guest blanks them).
CREATE FUNCTION fn_outbox_messages_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.property_id <> OLD.property_id OR NEW.dedupe_key <> OLD.dedupe_key OR NEW.channel <> OLD.channel
       OR NEW.template_code <> OLD.template_code OR NEW.guest_id IS DISTINCT FROM OLD.guest_id
       OR NEW.user_id IS DISTINCT FROM OLD.user_id OR NEW.created_at <> OLD.created_at THEN
        RAISE EXCEPTION 'outbox message % keeps its recipient, channel and key', OLD.id
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_outbox_messages_frozen';
    END IF;
    IF OLD.status = 'SKIPPED' AND NEW.status <> 'SKIPPED' THEN
        RAISE EXCEPTION 'outbox message % was skipped and is never sent', OLD.id
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_outbox_messages_frozen';
    END IF;
    NEW.updated_at := now();
    RETURN NEW;
END $$;
CREATE TRIGGER trg_outbox_messages_guard BEFORE UPDATE ON outbox_messages
    FOR EACH ROW EXECUTE FUNCTION fn_outbox_messages_guard();

-- ---------------------------------------------------------------------
-- whatsapp_messages : what WhatsApp accepted, and what its status webhooks said
-- ---------------------------------------------------------------------
CREATE TABLE whatsapp_messages (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid        NOT NULL REFERENCES organizations (id),
    property_id        uuid        NOT NULL REFERENCES properties (id),
    outbox_message_id  uuid        NOT NULL REFERENCES outbox_messages (id),
    wamid              text        NOT NULL,
    template_name      text        NOT NULL,
    language           text        NOT NULL,
    status             text        NOT NULL DEFAULT 'SENT',
    sent_at            timestamptz NOT NULL,
    delivered_at       timestamptz NULL,
    read_at            timestamptz NULL,
    failed_at          timestamptz NULL,
    error_code         text        NULL,
    error_title        text        NULL,
    pricing_category   text        NULL,               -- utility, marketing, service… (from the status webhook)
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_whatsapp_messages_wamid  CHECK (length(wamid) BETWEEN 1 AND 200),
    CONSTRAINT ck_whatsapp_messages_status CHECK (status IN ('SENT','DELIVERED','READ','FAILED')),
    CONSTRAINT ck_whatsapp_messages_error  CHECK ((status = 'FAILED') = (failed_at IS NOT NULL)
                                                  AND (error_title IS NULL OR length(error_title) <= 500)),
    CONSTRAINT ux_whatsapp_messages_wamid  UNIQUE (wamid),
    CONSTRAINT ux_whatsapp_messages_outbox UNIQUE (outbox_message_id)
);
CREATE TRIGGER trg_whatsapp_messages_no_delete BEFORE DELETE ON whatsapp_messages
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- email_messages : what the email provider accepted
-- ---------------------------------------------------------------------
CREATE TABLE email_messages (
    id                   uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id      uuid        NOT NULL REFERENCES organizations (id),
    property_id          uuid        NOT NULL REFERENCES properties (id),
    outbox_message_id    uuid        NOT NULL REFERENCES outbox_messages (id),
    provider             text        NOT NULL,
    provider_message_id  text        NOT NULL,
    status               text        NOT NULL DEFAULT 'SENT',
    sent_at              timestamptz NOT NULL,
    CONSTRAINT ck_email_messages_provider CHECK (provider IN ('RESEND','FAKE')),
    CONSTRAINT ck_email_messages_id       CHECK (length(provider_message_id) BETWEEN 1 AND 200),
    CONSTRAINT ck_email_messages_status   CHECK (status IN ('SENT')),
    CONSTRAINT ux_email_messages_outbox   UNIQUE (outbox_message_id)
);
CREATE TRIGGER trg_email_messages_append_only BEFORE UPDATE OR DELETE ON email_messages
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- ---------------------------------------------------------------------
-- notifications : the staff bell
-- ---------------------------------------------------------------------
CREATE TABLE notifications (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid        NOT NULL REFERENCES organizations (id),
    property_id        uuid        NULL REFERENCES properties (id),
    user_id            uuid        NOT NULL REFERENCES users (id),
    outbox_message_id  uuid        NULL REFERENCES outbox_messages (id),
    title              text        NOT NULL,
    body               text        NOT NULL,
    link               text        NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    read_at            timestamptz NULL,
    CONSTRAINT ck_notifications_title CHECK (length(btrim(title)) BETWEEN 1 AND 150),
    CONSTRAINT ck_notifications_body  CHECK (length(body) BETWEEN 1 AND 4000),
    CONSTRAINT ck_notifications_link  CHECK (link IS NULL OR link ~ '^/[A-Za-z0-9/_?=&.-]{0,200}$'),
    CONSTRAINT ux_notifications_outbox UNIQUE (outbox_message_id)
);
CREATE INDEX ix_notifications_user ON notifications (user_id, created_at DESC);
CREATE INDEX ix_notifications_unread ON notifications (user_id) WHERE read_at IS NULL;
CREATE TRIGGER trg_notifications_no_delete BEFORE DELETE ON notifications
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- scheduled_jobs : the scheduler (PostgreSQL-backed; no Redis)
-- ---------------------------------------------------------------------
CREATE TABLE scheduled_jobs (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id   uuid        NOT NULL REFERENCES organizations (id),
    property_id       uuid        NOT NULL REFERENCES properties (id),
    job               text        NOT NULL,
    enabled           boolean     NOT NULL DEFAULT true,
    run_at            time        NOT NULL,           -- local time at the property, every day
    next_run_at       timestamptz NOT NULL,
    last_started_at   timestamptz NULL,
    last_finished_at  timestamptz NULL,
    last_outcome      text        NULL,
    last_error        text        NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    created_by        uuid        NULL,
    updated_by        uuid        NULL,
    CONSTRAINT ck_scheduled_jobs_job     CHECK (job IN ('reservation_reminder','pre_arrival','checkout_reminder','payment_reminder',
                                                        'inventory_alert','leave_reminder')),
    CONSTRAINT ck_scheduled_jobs_outcome CHECK (last_outcome IS NULL OR length(last_outcome) <= 1000),
    CONSTRAINT ck_scheduled_jobs_error   CHECK (last_error IS NULL OR length(last_error) <= 1000),
    CONSTRAINT ux_scheduled_jobs         UNIQUE (property_id, job)
);
CREATE INDEX ix_scheduled_jobs_due ON scheduled_jobs (next_run_at) WHERE enabled;
CREATE TRIGGER trg_scheduled_jobs_no_delete BEFORE DELETE ON scheduled_jobs
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- job_runs : once per business key
-- ---------------------------------------------------------------------
CREATE TABLE job_runs (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    job              text        NOT NULL,
    business_key     text        NOT NULL,
    reservation_id   uuid        NULL REFERENCES reservations (id),
    triggered_by     text        NOT NULL,
    run_by           uuid        NULL REFERENCES users (id),
    outcome          text        NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_job_runs_job     CHECK (job IN ('reservation_reminder','pre_arrival','checkout_reminder','payment_reminder',
                                                  'inventory_alert','leave_reminder')),
    CONSTRAINT ck_job_runs_key     CHECK (length(business_key) BETWEEN 1 AND 200),
    CONSTRAINT ck_job_runs_trigger CHECK (triggered_by IN ('SCHEDULE','MANUAL')),
    CONSTRAINT ck_job_runs_manual  CHECK ((triggered_by = 'MANUAL') = (run_by IS NOT NULL)),
    CONSTRAINT ck_job_runs_outcome CHECK (length(outcome) BETWEEN 1 AND 1000),
    CONSTRAINT ux_job_runs         UNIQUE (property_id, job, business_key)
);
CREATE INDEX ix_job_runs_list ON job_runs (property_id, created_at DESC);
CREATE TRIGGER trg_job_runs_append_only BEFORE UPDATE OR DELETE ON job_runs
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- ---------------------------------------------------------------------
-- WhatsApp's status webhooks go through webhook_events like Razorpay's; guests can opt out of booking messages
-- ---------------------------------------------------------------------
ALTER TABLE webhook_events DROP CONSTRAINT ck_webhook_events_provider;
ALTER TABLE webhook_events ADD CONSTRAINT ck_webhook_events_provider CHECK (provider IN ('RAZORPAY','WHATSAPP'));

ALTER TABLE guest_consents DROP CONSTRAINT ck_guest_consents_purpose;
ALTER TABLE guest_consents ADD CONSTRAINT ck_guest_consents_purpose
    CHECK (purpose IN ('MARKETING_WHATSAPP','MARKETING_EMAIL','DATA_PROCESSING','SERVICE_WHATSAPP','SERVICE_EMAIL'));

-- ---------------------------------------------------------------------
-- Data classification (brief §59)
-- ---------------------------------------------------------------------
COMMENT ON TABLE message_templates IS 'class: internal. Wording of guest and staff messages per channel; editable without a release.';
COMMENT ON TABLE outbox_messages   IS 'class: confidential. Every message queued for a guest or staff member, its delivery and why it failed; holds phone numbers and emails.';
COMMENT ON TABLE whatsapp_messages IS 'class: internal. Messages WhatsApp accepted (wamid) and their delivery statuses.';
COMMENT ON TABLE email_messages    IS 'class: internal. Emails the provider accepted.';
COMMENT ON TABLE notifications     IS 'class: internal. The staff in-app bell.';
COMMENT ON TABLE scheduled_jobs    IS 'class: internal. When each job runs at each property.';
COMMENT ON TABLE job_runs          IS 'class: internal. What each job did, once per business key; append-only.';
COMMENT ON TABLE webhook_events    IS 'class: confidential. Verified Razorpay and WhatsApp webhooks, once per event id; no card data or phone numbers.';
