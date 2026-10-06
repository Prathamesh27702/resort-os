-- =====================================================================
-- V001  Foundation: organisations, properties, identity, RBAC, audit
-- Sprint 1
-- Conventions (apply to every later migration too):
--   * uuid PKs default gen_random_uuid(); append-only logs use bigint identity
--   * all timestamps are timestamptz and written in UTC by the API
--   * created_at/updated_at/created_by/updated_by on mutable tables
--     (created_by/updated_by hold users.id; no FK so system/seed rows can use NULL
--      and user rows can be hard-purged by retention without breaking history)
--   * soft delete = deleted_at timestamptz NULL on master data only;
--     financial/audit rows are never deleted
--   * every business table carries organization_id (+ property_id where it is
--     property-specific) for multi-tenant readiness
-- =====================================================================

-- ---------------------------------------------------------------------
-- organizations : the tenant. One owner group = one organization.
-- ---------------------------------------------------------------------
CREATE TABLE organizations (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    code            text        NOT NULL,
    name            text        NOT NULL,
    legal_name      text        NULL,
    base_currency   char(3)     NOT NULL DEFAULT 'INR',
    country_code    char(2)     NOT NULL DEFAULT 'IN',
    is_active       boolean     NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    created_by      uuid        NULL,
    updated_by      uuid        NULL,
    deleted_at      timestamptz NULL,
    CONSTRAINT ck_organizations_code     CHECK (code ~ '^[A-Z0-9_]{2,30}$'),
    CONSTRAINT ck_organizations_currency CHECK (base_currency ~ '^[A-Z]{3}$'),
    CONSTRAINT ck_organizations_country  CHECK (country_code ~ '^[A-Z]{2}$')
);
CREATE UNIQUE INDEX ux_organizations_code ON organizations (code) WHERE deleted_at IS NULL;

-- ---------------------------------------------------------------------
-- properties : a resort/hotel. Timezone is stored here; the server clock
-- timezone is never used for business dates.
-- ---------------------------------------------------------------------
CREATE TABLE properties (
    id               uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid         NOT NULL REFERENCES organizations (id),
    code             text         NOT NULL,
    name             text         NOT NULL,
    legal_name       text         NULL,
    property_type    text         NOT NULL DEFAULT 'RESORT',
    timezone         text         NOT NULL DEFAULT 'Asia/Kolkata',
    currency         char(3)      NOT NULL DEFAULT 'INR',
    address_line1    text         NULL,
    address_line2    text         NULL,
    city             text         NULL,
    district         text         NULL,
    state_name       text         NULL,
    gst_state_code   char(2)      NULL,      -- GST state code, e.g. '30' Goa, '32' Kerala
    postal_code      text         NULL,
    country_code     char(2)      NOT NULL DEFAULT 'IN',
    phone            text         NULL,
    email            text         NULL,
    check_in_time    time         NOT NULL DEFAULT '14:00',
    check_out_time   time         NOT NULL DEFAULT '11:00',
    latitude         numeric(9,6) NULL,
    longitude        numeric(9,6) NULL,
    is_active        boolean      NOT NULL DEFAULT true,
    created_at       timestamptz  NOT NULL DEFAULT now(),
    updated_at       timestamptz  NOT NULL DEFAULT now(),
    created_by       uuid         NULL,
    updated_by       uuid         NULL,
    deleted_at       timestamptz  NULL,
    CONSTRAINT ck_properties_code      CHECK (code ~ '^[A-Z0-9_]{2,30}$'),
    CONSTRAINT ck_properties_type      CHECK (property_type IN ('RESORT','HOTEL','HOMESTAY','HOSTEL','VILLA','OTHER')),
    CONSTRAINT ck_properties_currency  CHECK (currency ~ '^[A-Z]{3}$'),
    CONSTRAINT ck_properties_gst_state CHECK (gst_state_code IS NULL OR gst_state_code ~ '^[0-9]{2}$'),
    CONSTRAINT ck_properties_lat       CHECK (latitude  IS NULL OR latitude  BETWEEN -90  AND 90),
    CONSTRAINT ck_properties_lng       CHECK (longitude IS NULL OR longitude BETWEEN -180 AND 180),
    CONSTRAINT ck_properties_timezone  CHECK (length(timezone) BETWEEN 3 AND 64)  -- IANA name validated in API
);
CREATE UNIQUE INDEX ux_properties_org_code ON properties (organization_id, code) WHERE deleted_at IS NULL;
CREATE INDEX ix_properties_org ON properties (organization_id);

-- ---------------------------------------------------------------------
-- property_settings : typed-by-convention key/value settings per property
-- (e.g. 'frontdesk.allow_checkout_with_balance' => false)
-- ---------------------------------------------------------------------
CREATE TABLE property_settings (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    key              text        NOT NULL,
    value            jsonb       NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_property_settings_key CHECK (key ~ '^[a-z][a-z0-9_.]{1,99}$')
);
CREATE UNIQUE INDEX ux_property_settings_key ON property_settings (property_id, key);

-- ---------------------------------------------------------------------
-- system_settings : platform-wide settings (not tenant data)
-- ---------------------------------------------------------------------
CREATE TABLE system_settings (
    key          text        PRIMARY KEY,
    value        jsonb       NOT NULL,
    description  text        NULL,
    updated_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   uuid        NULL,
    CONSTRAINT ck_system_settings_key CHECK (key ~ '^[a-z][a-z0-9_.]{1,99}$')
);

-- ---------------------------------------------------------------------
-- users : sign-in identities. Email is unique across the platform so
-- sign-in needs no organisation picker.
-- ---------------------------------------------------------------------
CREATE TABLE users (
    id                    uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id       uuid        NOT NULL REFERENCES organizations (id),
    email                 text        NOT NULL,
    normalized_email      text        NOT NULL,
    phone                 text        NULL,
    full_name             text        NOT NULL,
    password_hash         text        NOT NULL,
    security_stamp        uuid        NOT NULL DEFAULT gen_random_uuid(),  -- rotated => all sessions invalid
    is_active             boolean     NOT NULL DEFAULT true,
    must_change_password  boolean     NOT NULL DEFAULT false,
    mfa_enabled           boolean     NOT NULL DEFAULT false,              -- enforced in Sprint 26
    failed_login_count    integer     NOT NULL DEFAULT 0,
    lockout_until         timestamptz NULL,
    last_login_at         timestamptz NULL,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now(),
    created_by            uuid        NULL,
    updated_by            uuid        NULL,
    deleted_at            timestamptz NULL,
    CONSTRAINT ck_users_normalized_email CHECK (normalized_email = lower(email)),
    CONSTRAINT ck_users_email_format     CHECK (email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'),
    CONSTRAINT ck_users_failed_count     CHECK (failed_login_count >= 0)
);
CREATE UNIQUE INDEX ux_users_normalized_email ON users (normalized_email) WHERE deleted_at IS NULL;
CREATE INDEX ix_users_org ON users (organization_id);

-- ---------------------------------------------------------------------
-- permissions : catalogue of granular permission codes (module.action)
-- ---------------------------------------------------------------------
CREATE TABLE permissions (
    code          text     PRIMARY KEY,
    module        text     NOT NULL,
    description   text     NOT NULL,
    is_sensitive  boolean  NOT NULL DEFAULT false,   -- shown with a warning in role editor
    CONSTRAINT ck_permissions_code CHECK (code ~ '^[a-z_]+(\.[a-z_]+)+$')
);
CREATE INDEX ix_permissions_module ON permissions (module);

-- ---------------------------------------------------------------------
-- roles : organization_id NULL = built-in system role (shared template);
-- organisations may later add their own custom roles.
-- ---------------------------------------------------------------------
CREATE TABLE roles (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NULL REFERENCES organizations (id),
    code             text        NOT NULL,
    name             text        NOT NULL,
    description      text        NULL,
    is_system        boolean     NOT NULL DEFAULT false,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_roles_code   CHECK (code ~ '^[A-Z][A-Z0-9_]{1,49}$'),
    CONSTRAINT ck_roles_system CHECK (NOT is_system OR organization_id IS NULL)
);
CREATE UNIQUE INDEX ux_roles_system_code ON roles (code) WHERE organization_id IS NULL;
CREATE UNIQUE INDEX ux_roles_org_code    ON roles (organization_id, code) WHERE organization_id IS NOT NULL;

CREATE TABLE role_permissions (
    role_id          uuid NOT NULL REFERENCES roles (id) ON DELETE CASCADE,
    permission_code  text NOT NULL REFERENCES permissions (code),
    PRIMARY KEY (role_id, permission_code)
);
CREATE INDEX ix_role_permissions_permission ON role_permissions (permission_code);

-- ---------------------------------------------------------------------
-- user_roles : a role grant, optionally limited to one property.
-- property_id NULL = applies to every property in the user's organisation.
-- ---------------------------------------------------------------------
CREATE TABLE user_roles (
    id           uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id      uuid        NOT NULL REFERENCES users (id),
    role_id      uuid        NOT NULL REFERENCES roles (id),
    property_id  uuid        NULL REFERENCES properties (id),
    created_at   timestamptz NOT NULL DEFAULT now(),
    created_by   uuid        NULL,
    CONSTRAINT ux_user_roles UNIQUE NULLS NOT DISTINCT (user_id, role_id, property_id)
);
CREATE INDEX ix_user_roles_user ON user_roles (user_id);

-- ---------------------------------------------------------------------
-- refresh_tokens : only a SHA-256 hash of the token is stored.
-- family_id groups a rotation chain; reuse of a rotated token revokes the family.
-- ---------------------------------------------------------------------
CREATE TABLE refresh_tokens (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id         uuid        NOT NULL REFERENCES users (id),
    family_id       uuid        NOT NULL,
    token_hash      bytea       NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,
    revoked_at      timestamptz NULL,
    revoked_reason  text        NULL,
    replaced_by_id  uuid        NULL REFERENCES refresh_tokens (id),
    created_ip      inet        NULL,
    user_agent      text        NULL,
    CONSTRAINT ck_refresh_tokens_hash_len CHECK (octet_length(token_hash) = 32),
    CONSTRAINT ck_refresh_tokens_expiry   CHECK (expires_at > created_at),
    CONSTRAINT ck_refresh_tokens_reason   CHECK (revoked_reason IS NULL OR revoked_reason IN
        ('ROTATED','LOGOUT','REUSE_DETECTED','USER_DEACTIVATED','PASSWORD_CHANGED','ADMIN'))
);
CREATE UNIQUE INDEX ux_refresh_tokens_hash ON refresh_tokens (token_hash);
CREATE INDEX ix_refresh_tokens_user   ON refresh_tokens (user_id);
CREATE INDEX ix_refresh_tokens_family ON refresh_tokens (family_id);

-- ---------------------------------------------------------------------
-- login_events : append-only sign-in history (success + failure)
-- ---------------------------------------------------------------------
CREATE TABLE login_events (
    id               bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id          uuid        NULL REFERENCES users (id),
    email_attempted  text        NOT NULL,
    success          boolean     NOT NULL,
    failure_reason   text        NULL,
    ip_address       inet        NULL,
    user_agent       text        NULL,
    occurred_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_login_events_reason CHECK (success OR failure_reason IS NOT NULL)
);
CREATE INDEX ix_login_events_user_time ON login_events (user_id, occurred_at DESC);
CREATE INDEX ix_login_events_time      ON login_events (occurred_at DESC);

-- ---------------------------------------------------------------------
-- audit_logs : append-only change history written by the API for every
-- insert/update/delete of audited entities, plus security events.
-- ---------------------------------------------------------------------
CREATE TABLE audit_logs (
    id               bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    organization_id  uuid        NULL,
    property_id      uuid        NULL,
    user_id          uuid        NULL,
    action           text        NOT NULL,
    entity_type      text        NOT NULL,
    entity_id        text        NULL,
    changes          jsonb       NULL,      -- {"col": {"old": .., "new": ..}}; secrets are masked
    correlation_id   text        NULL,
    ip_address       inet        NULL,
    occurred_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_audit_logs_action CHECK (action ~ '^[A-Z_]{3,40}$')
);
CREATE INDEX ix_audit_logs_org_time ON audit_logs (organization_id, occurred_at DESC);
CREATE INDEX ix_audit_logs_entity   ON audit_logs (entity_type, entity_id);
CREATE INDEX ix_audit_logs_user     ON audit_logs (user_id, occurred_at DESC);

-- Append-only enforcement for log tables (defence in depth: the app never
-- updates them, and this stops anyone else doing it through the app's DB user).
CREATE FUNCTION fn_reject_modification() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END $$;

CREATE TRIGGER trg_audit_logs_append_only   BEFORE UPDATE OR DELETE ON audit_logs
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();
CREATE TRIGGER trg_login_events_append_only BEFORE UPDATE OR DELETE ON login_events
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();
