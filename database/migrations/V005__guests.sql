-- =====================================================================
-- V005  Guests: profiles, addresses, ID documents, preferences, consents
-- Sprint 3 (brief §17, §59)
--
-- Model
--   guests              one row per person, shared by every property of the organisation.
--                       status ACTIVE | MERGED (folded into another profile) | ANONYMISED (personal data removed).
--   guest_addresses     at most one address per type (HOME now; BILLING later for GST invoices).
--   guest_documents     ID documents. The number is stored ENCRYPTED by the API (AES-256-GCM, key outside the
--                       database); only the last 4 characters are kept readable for display. Aadhaar: only the
--                       last 4 digits are ever stored (masked Aadhaar), never the full number. The scan, if any,
--                       is an encrypted file in blob storage; only its key is here.
--   guest_preferences   free-text preferences by category (room, food, dietary…).
--   guest_consents      append-only history of consents (WhatsApp/email marketing, data processing); the latest
--                       row per purpose is the current state.
--
-- Data classification (brief §59) is recorded on every column with COMMENT ON COLUMN … 'class: …':
--   public · internal · confidential (contact details, address) · sensitive (ID data, date of birth)
--
-- Search: pg_trgm GIN indexes on a generated search_text (name, email, company) and on phone digits, so
-- "contains" searches stay fast as the guest list grows.
-- =====================================================================

CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- ---------------------------------------------------------------------
-- guests
-- ---------------------------------------------------------------------
CREATE TABLE guests (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    first_name       text        NOT NULL,
    last_name        text        NULL,
    phone            text        NULL,          -- E.164, e.g. +919820012345 (normalised by the API)
    email            text        NULL,          -- lower-case
    date_of_birth    date        NULL,
    nationality      char(2)     NULL,          -- ISO 3166-1 alpha-2; drives Form-C for foreign nationals
    company_name     text        NULL,
    gstin            text        NULL,          -- for B2B GST invoices (Sprint 12)
    notes            text        NULL,
    is_vip           boolean     NOT NULL DEFAULT false,
    status           text        NOT NULL DEFAULT 'ACTIVE',
    merged_into_id   uuid        NULL REFERENCES guests (id),
    merged_at        timestamptz NULL,
    anonymised_at    timestamptz NULL,
    anonymised_by    uuid        NULL REFERENCES users (id),
    search_text      text        GENERATED ALWAYS AS (
                         lower(first_name || ' ' || coalesce(last_name, '') || ' ' || coalesce(email, '') || ' ' || coalesce(company_name, ''))
                     ) STORED,
    phone_digits     text        GENERATED ALWAYS AS (regexp_replace(coalesce(phone, ''), '[^0-9]', '', 'g')) STORED,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_guests_first_name  CHECK (length(btrim(first_name)) BETWEEN 1 AND 100),
    CONSTRAINT ck_guests_last_name   CHECK (last_name IS NULL OR length(btrim(last_name)) BETWEEN 1 AND 100),
    CONSTRAINT ck_guests_phone       CHECK (phone IS NULL OR phone ~ '^\+[1-9][0-9]{6,14}$'),
    CONSTRAINT ck_guests_email       CHECK (email IS NULL OR (length(email) <= 254 AND email = lower(email) AND email LIKE '%_@_%')),
    CONSTRAINT ck_guests_dob         CHECK (date_of_birth IS NULL OR date_of_birth >= DATE '1900-01-01'),
    CONSTRAINT ck_guests_nationality CHECK (nationality IS NULL OR nationality ~ '^[A-Z]{2}$'),
    CONSTRAINT ck_guests_company     CHECK (company_name IS NULL OR length(company_name) <= 200),
    CONSTRAINT ck_guests_gstin       CHECK (gstin IS NULL OR gstin ~ '^[0-9]{2}[A-Z0-9]{13}$'),
    CONSTRAINT ck_guests_notes       CHECK (notes IS NULL OR length(notes) <= 2000),
    CONSTRAINT ck_guests_status      CHECK (status IN ('ACTIVE','MERGED','ANONYMISED')),
    CONSTRAINT ck_guests_merged      CHECK ((status = 'MERGED') = (merged_into_id IS NOT NULL AND merged_at IS NOT NULL)),
    CONSTRAINT ck_guests_not_self    CHECK (merged_into_id IS NULL OR merged_into_id <> id),
    CONSTRAINT ck_guests_anonymised  CHECK ((status = 'ANONYMISED') = (anonymised_at IS NOT NULL)),
    -- an anonymised profile keeps no contact or identity data
    CONSTRAINT ck_guests_anon_empty  CHECK (status <> 'ANONYMISED' OR (last_name IS NULL AND phone IS NULL AND email IS NULL
                                           AND date_of_birth IS NULL AND notes IS NULL AND gstin IS NULL AND company_name IS NULL))
);
CREATE INDEX ix_guests_org_status   ON guests (organization_id, status);
CREATE INDEX ix_guests_phone        ON guests (organization_id, phone) WHERE phone IS NOT NULL;
CREATE INDEX ix_guests_email        ON guests (organization_id, email) WHERE email IS NOT NULL;
CREATE INDEX ix_guests_search_trgm  ON guests USING gin (search_text gin_trgm_ops);
CREATE INDEX ix_guests_phone_trgm   ON guests USING gin (phone_digits gin_trgm_ops);
CREATE INDEX ix_guests_merged_into  ON guests (merged_into_id) WHERE merged_into_id IS NOT NULL;

COMMENT ON TABLE guests IS 'Guest profiles, one per person per organisation. Never deleted: merged or anonymised instead.';
COMMENT ON COLUMN guests.id              IS 'class: internal';
COMMENT ON COLUMN guests.organization_id IS 'class: internal';
COMMENT ON COLUMN guests.first_name      IS 'class: confidential';
COMMENT ON COLUMN guests.last_name       IS 'class: confidential';
COMMENT ON COLUMN guests.phone           IS 'class: confidential';
COMMENT ON COLUMN guests.email           IS 'class: confidential';
COMMENT ON COLUMN guests.date_of_birth   IS 'class: sensitive';
COMMENT ON COLUMN guests.nationality     IS 'class: confidential';
COMMENT ON COLUMN guests.company_name    IS 'class: internal';
COMMENT ON COLUMN guests.gstin           IS 'class: internal';
COMMENT ON COLUMN guests.notes           IS 'class: confidential';
COMMENT ON COLUMN guests.is_vip          IS 'class: internal';
COMMENT ON COLUMN guests.status          IS 'class: internal';
COMMENT ON COLUMN guests.merged_into_id  IS 'class: internal';
COMMENT ON COLUMN guests.merged_at       IS 'class: internal';
COMMENT ON COLUMN guests.anonymised_at   IS 'class: internal';
COMMENT ON COLUMN guests.anonymised_by   IS 'class: internal';
COMMENT ON COLUMN guests.search_text     IS 'class: confidential (derived from name, email, company)';
COMMENT ON COLUMN guests.phone_digits    IS 'class: confidential (derived from phone)';
COMMENT ON COLUMN guests.created_at      IS 'class: internal';
COMMENT ON COLUMN guests.updated_at      IS 'class: internal';
COMMENT ON COLUMN guests.created_by      IS 'class: internal';
COMMENT ON COLUMN guests.updated_by      IS 'class: internal';

-- ---------------------------------------------------------------------
-- guest_addresses
-- ---------------------------------------------------------------------
CREATE TABLE guest_addresses (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    guest_id         uuid        NOT NULL REFERENCES guests (id),
    address_type     text        NOT NULL DEFAULT 'HOME',
    line1            text        NULL,
    line2            text        NULL,
    city             text        NULL,
    state            text        NULL,
    state_code       char(2)     NULL,          -- GST state code (e.g. 30 = Goa) for Indian addresses
    postal_code      text        NULL,
    country_code     char(2)     NOT NULL DEFAULT 'IN',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_guest_addresses_type    CHECK (address_type IN ('HOME','BILLING')),
    CONSTRAINT ck_guest_addresses_lengths CHECK (coalesce(length(line1), 0) <= 200 AND coalesce(length(line2), 0) <= 200
                                                AND coalesce(length(city), 0) <= 100 AND coalesce(length(state), 0) <= 100
                                                AND coalesce(length(postal_code), 0) <= 20),
    CONSTRAINT ck_guest_addresses_state   CHECK (state_code IS NULL OR state_code ~ '^[0-9]{2}$'),
    CONSTRAINT ck_guest_addresses_country CHECK (country_code ~ '^[A-Z]{2}$'),
    CONSTRAINT ux_guest_addresses_type    UNIQUE (guest_id, address_type)
);
CREATE INDEX ix_guest_addresses_org ON guest_addresses (organization_id);

COMMENT ON COLUMN guest_addresses.line1        IS 'class: confidential';
COMMENT ON COLUMN guest_addresses.line2        IS 'class: confidential';
COMMENT ON COLUMN guest_addresses.city         IS 'class: confidential';
COMMENT ON COLUMN guest_addresses.state        IS 'class: confidential';
COMMENT ON COLUMN guest_addresses.state_code   IS 'class: confidential';
COMMENT ON COLUMN guest_addresses.postal_code  IS 'class: confidential';
COMMENT ON COLUMN guest_addresses.country_code IS 'class: confidential';
COMMENT ON COLUMN guest_addresses.address_type IS 'class: internal';

-- ---------------------------------------------------------------------
-- guest_documents : ID documents. Encrypted by the API; the database never sees a plaintext ID number.
-- ---------------------------------------------------------------------
CREATE TABLE guest_documents (
    id                  uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id     uuid        NOT NULL REFERENCES organizations (id),
    guest_id            uuid        NOT NULL REFERENCES guests (id),
    document_type       text        NOT NULL,
    number_encrypted    bytea       NULL,       -- AES-256-GCM envelope; NULL for Aadhaar (masked only) and after purge
    number_last4        text        NULL,       -- for display: "•••• 1234"
    number_hash         bytea       NULL,       -- HMAC-SHA256 of the normalised number: finds the same ID on two profiles
    issuing_country     char(2)     NULL,
    expires_on          date        NULL,
    details_encrypted   bytea       NULL,       -- AES-256-GCM envelope of JSON details (visa number, place of issue…)
    storage_key         text        NULL,       -- encrypted scan in blob storage
    file_name           text        NULL,
    content_type        text        NULL,
    size_bytes          integer     NULL,
    key_id              text        NOT NULL,   -- which encryption key protects this row (key rotation)
    status              text        NOT NULL DEFAULT 'ACTIVE',
    purged_at           timestamptz NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    created_by          uuid        NULL,
    updated_by          uuid        NULL,
    CONSTRAINT ck_guest_documents_type    CHECK (document_type IN ('PASSPORT','AADHAAR','DRIVING_LICENCE','VOTER_ID','PAN','VISA','OTHER')),
    CONSTRAINT ck_guest_documents_last4   CHECK (number_last4 IS NULL OR number_last4 ~ '^[A-Z0-9]{1,4}$'),
    CONSTRAINT ck_guest_documents_aadhaar CHECK (document_type <> 'AADHAAR' OR number_encrypted IS NULL),
    CONSTRAINT ck_guest_documents_country CHECK (issuing_country IS NULL OR issuing_country ~ '^[A-Z]{2}$'),
    CONSTRAINT ck_guest_documents_ctype   CHECK (content_type IS NULL OR content_type IN ('image/jpeg','image/png','application/pdf')),
    CONSTRAINT ck_guest_documents_size    CHECK (size_bytes IS NULL OR size_bytes BETWEEN 1 AND 5242880),
    CONSTRAINT ck_guest_documents_file    CHECK ((storage_key IS NULL) = (content_type IS NULL)),
    CONSTRAINT ck_guest_documents_status  CHECK (status IN ('ACTIVE','PURGED')),
    -- a purged document keeps only its type and dates (for the audit trail); every identifying field is gone
    CONSTRAINT ck_guest_documents_purged  CHECK ((status = 'PURGED') = (purged_at IS NOT NULL)
                                                AND (status = 'ACTIVE' OR (number_encrypted IS NULL AND number_last4 IS NULL
                                                     AND number_hash IS NULL AND details_encrypted IS NULL AND storage_key IS NULL
                                                     AND file_name IS NULL)))
);
CREATE INDEX ix_guest_documents_guest ON guest_documents (guest_id);
CREATE INDEX ix_guest_documents_hash  ON guest_documents (organization_id, number_hash) WHERE number_hash IS NOT NULL;
CREATE INDEX ix_guest_documents_retention ON guest_documents (organization_id, created_at) WHERE status = 'ACTIVE';

COMMENT ON TABLE guest_documents IS 'Guest ID documents. Sensitive: read only with guest_document.read; numbers and scans encrypted by the API.';
COMMENT ON COLUMN guest_documents.document_type     IS 'class: confidential';
COMMENT ON COLUMN guest_documents.number_encrypted  IS 'class: sensitive (AES-256-GCM ciphertext)';
COMMENT ON COLUMN guest_documents.number_last4      IS 'class: sensitive (masked)';
COMMENT ON COLUMN guest_documents.number_hash       IS 'class: sensitive (keyed hash, not reversible)';
COMMENT ON COLUMN guest_documents.issuing_country   IS 'class: sensitive';
COMMENT ON COLUMN guest_documents.expires_on        IS 'class: sensitive';
COMMENT ON COLUMN guest_documents.details_encrypted IS 'class: sensitive (AES-256-GCM ciphertext)';
COMMENT ON COLUMN guest_documents.storage_key       IS 'class: sensitive (points to an encrypted scan)';
COMMENT ON COLUMN guest_documents.file_name         IS 'class: sensitive';
COMMENT ON COLUMN guest_documents.content_type      IS 'class: internal';
COMMENT ON COLUMN guest_documents.size_bytes        IS 'class: internal';
COMMENT ON COLUMN guest_documents.key_id            IS 'class: internal';
COMMENT ON COLUMN guest_documents.status            IS 'class: internal';
COMMENT ON COLUMN guest_documents.purged_at         IS 'class: internal';

-- ---------------------------------------------------------------------
-- guest_preferences
-- ---------------------------------------------------------------------
CREATE TABLE guest_preferences (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    guest_id         uuid        NOT NULL REFERENCES guests (id),
    category         text        NOT NULL,
    value            text        NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_guest_preferences_category CHECK (category IN ('ROOM','FOOD','DIETARY','ACTIVITY','OTHER')),
    CONSTRAINT ck_guest_preferences_value    CHECK (length(btrim(value)) BETWEEN 1 AND 500)
);
CREATE INDEX ix_guest_preferences_guest ON guest_preferences (guest_id);
COMMENT ON COLUMN guest_preferences.category IS 'class: internal';
COMMENT ON COLUMN guest_preferences.value    IS 'class: confidential (may reveal dietary or religious practice)';

-- ---------------------------------------------------------------------
-- guest_consents : append-only. Latest row per (guest, purpose) is current.
-- ---------------------------------------------------------------------
CREATE TABLE guest_consents (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    guest_id         uuid        NOT NULL REFERENCES guests (id),
    purpose          text        NOT NULL,
    granted          boolean     NOT NULL,
    source           text        NOT NULL,
    recorded_at      timestamptz NOT NULL DEFAULT now(),
    recorded_by      uuid        NULL REFERENCES users (id),
    CONSTRAINT ck_guest_consents_purpose CHECK (purpose IN ('MARKETING_WHATSAPP','MARKETING_EMAIL','DATA_PROCESSING')),
    CONSTRAINT ck_guest_consents_source  CHECK (source IN ('FRONT_DESK','PHONE','WEBSITE','OTA','EMAIL'))
);
CREATE INDEX ix_guest_consents_guest ON guest_consents (guest_id, purpose, recorded_at DESC);
COMMENT ON TABLE guest_consents IS 'Consent history (DPDP Act 2023). Append-only: UPDATE/DELETE are rejected; moved to the surviving profile on merge.';
COMMENT ON COLUMN guest_consents.purpose     IS 'class: internal';
COMMENT ON COLUMN guest_consents.granted     IS 'class: internal';
COMMENT ON COLUMN guest_consents.source      IS 'class: internal';
COMMENT ON COLUMN guest_consents.recorded_at IS 'class: internal';
COMMENT ON COLUMN guest_consents.recorded_by IS 'class: internal';

-- Consent history is evidence: rows can only be added, or re-pointed to the surviving profile when two are merged.
CREATE FUNCTION fn_guest_consents_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'guest_consents is append-only' USING ERRCODE = 'check_violation';
    END IF;
    IF (NEW.purpose, NEW.granted, NEW.source, NEW.recorded_at, NEW.recorded_by, NEW.organization_id)
       IS DISTINCT FROM (OLD.purpose, OLD.granted, OLD.source, OLD.recorded_at, OLD.recorded_by, OLD.organization_id) THEN
        RAISE EXCEPTION 'guest_consents is append-only (only guest_id may change, on merge)' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_guest_consents_append_only BEFORE UPDATE OR DELETE ON guest_consents
    FOR EACH ROW EXECUTE FUNCTION fn_guest_consents_append_only();

-- Profiles are never deleted (stays, invoices and the audit trail refer to them).
CREATE FUNCTION fn_guests_no_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'guests are anonymised or merged, never deleted' USING ERRCODE = 'check_violation';
END $$;
CREATE TRIGGER trg_guests_no_delete BEFORE DELETE ON guests FOR EACH ROW EXECUTE FUNCTION fn_guests_no_delete();
