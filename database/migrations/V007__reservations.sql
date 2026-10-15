-- =====================================================================
-- V007  Reservations, availability guards, status history, idempotency keys
-- Sprint 5 (brief §14–16). The exact booking algorithm is in docs/architecture/booking-algorithm.md.
--
-- Model
--   reservations                 one booking: who, when (property-local dates, check_out exclusive), status, total.
--   reservation_units            what it holds: one row per room, or per dorm bed (bed_id set). stay = [start, end).
--                                holds_inventory is true while the reservation is PENDING / CONFIRMED / CHECKED_IN and
--                                the row is current; a change of dates or rooms supersedes the old rows (kept as
--                                history) and inserts new ones.
--   reservation_guests           everyone staying (the primary guest included).
--   reservation_rate_breakdowns  the price of every night of every reservation unit, copied from the rate grid when
--                                the booking was made or changed (later price changes don't touch existing bookings).
--   reservation_notes            free-text notes; append-only.
--   reservation_status_history   every status change with who, when and why; append-only.
--   reservation_status_transitions  the allowed status graph. A trigger rejects any change not listed here.
--   idempotency_keys             the stored response of a create request, so a retry with the same Idempotency-Key
--                                returns the same reservation instead of booking twice.
--   property_counters            per-property running numbers (reservation references R00001, R00002 …).
--
-- Integrity — the final guards against double booking (the API checks the same rules first, under row locks)
--   * EXCLUDE (unit_id =, stay &&) for room rows and EXCLUDE (bed_id =, stay &&) for bed rows, while holds_inventory
--   * a trigger rejects a held row that overlaps an active block on its room (or bed), and the reverse trigger on
--     unit_blocks rejects a block over a held reservation; both lock the room row first, so they serialise
--   * reservations, their units, breakdowns, notes and history are never deleted (brief §87)
-- =====================================================================

-- ---------------------------------------------------------------------
-- property_counters
-- ---------------------------------------------------------------------
CREATE TABLE property_counters (
    property_id  uuid    NOT NULL REFERENCES properties (id),
    name         text    NOT NULL,
    last_value   bigint  NOT NULL DEFAULT 0,
    PRIMARY KEY (property_id, name),
    CONSTRAINT ck_property_counters_name  CHECK (name ~ '^[a-z_]{2,40}$'),
    CONSTRAINT ck_property_counters_value CHECK (last_value >= 0)
);

-- ---------------------------------------------------------------------
-- reservation_status_transitions : the status graph (docs/architecture/lifecycles.md, I. Booking)
-- from_status NULL = the statuses a reservation may be created in.
-- ---------------------------------------------------------------------
CREATE TABLE reservation_status_transitions (
    from_status  text NULL,
    to_status    text NOT NULL,
    CONSTRAINT ux_reservation_status_transitions UNIQUE NULLS NOT DISTINCT (from_status, to_status)
);
INSERT INTO reservation_status_transitions (from_status, to_status) VALUES
(NULL,         'INQUIRY'),
(NULL,         'PENDING'),
(NULL,         'CONFIRMED'),
('INQUIRY',    'PENDING'),
('INQUIRY',    'CONFIRMED'),
('INQUIRY',    'CANCELLED'),
('PENDING',    'CONFIRMED'),
('PENDING',    'CANCELLED'),
('CONFIRMED',  'CHECKED_IN'),
('CONFIRMED',  'CANCELLED'),
('CONFIRMED',  'NO_SHOW'),
('CHECKED_IN', 'CHECKED_OUT'),
('CANCELLED',  'REFUNDED'),
('NO_SHOW',    'REFUNDED');

-- ---------------------------------------------------------------------
-- reservations
-- ---------------------------------------------------------------------
CREATE TABLE reservations (
    id                 uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid          NOT NULL REFERENCES organizations (id),
    property_id        uuid          NOT NULL REFERENCES properties (id),
    reference          text          NOT NULL,
    status             text          NOT NULL,
    source_id          uuid          NOT NULL REFERENCES reservation_sources (id),
    primary_guest_id   uuid          NOT NULL REFERENCES guests (id),
    rate_plan_id       uuid          NOT NULL,
    check_in           date          NOT NULL,
    check_out          date          NOT NULL,
    adults             smallint      NOT NULL,
    children           smallint      NOT NULL DEFAULT 0,
    total_amount       numeric(12,2) NOT NULL,
    currency           char(3)       NOT NULL,
    special_requests   text          NULL,
    status_changed_at  timestamptz   NOT NULL DEFAULT now(),
    created_at         timestamptz   NOT NULL DEFAULT now(),
    updated_at         timestamptz   NOT NULL DEFAULT now(),
    created_by         uuid          NULL,
    updated_by         uuid          NULL,
    CONSTRAINT ck_reservations_reference CHECK (reference ~ '^R[0-9]{5,}$'),
    CONSTRAINT ck_reservations_status    CHECK (status IN ('INQUIRY','PENDING','CONFIRMED','CHECKED_IN','CHECKED_OUT',
                                                          'CANCELLED','NO_SHOW','REFUNDED')),
    CONSTRAINT ck_reservations_dates     CHECK (check_out > check_in AND check_out - check_in <= 180),
    CONSTRAINT ck_reservations_guests    CHECK (adults BETWEEN 1 AND 500 AND children BETWEEN 0 AND 500),
    CONSTRAINT ck_reservations_total     CHECK (total_amount >= 0),
    CONSTRAINT ck_reservations_requests  CHECK (special_requests IS NULL OR length(special_requests) <= 2000),
    CONSTRAINT fk_reservations_rate_plan FOREIGN KEY (rate_plan_id, property_id) REFERENCES rate_plans (id, property_id),
    CONSTRAINT ux_reservations_reference UNIQUE (property_id, reference),
    CONSTRAINT ux_reservations_id_prop   UNIQUE (id, property_id)
);
CREATE INDEX ix_reservations_property_dates  ON reservations (property_id, check_in, check_out);
CREATE INDEX ix_reservations_property_status ON reservations (property_id, status);
CREATE INDEX ix_reservations_primary_guest   ON reservations (primary_guest_id);
CREATE INDEX ix_reservations_org             ON reservations (organization_id);

-- Only the moves listed in reservation_status_transitions are allowed (the API checks the same table-driven graph).
CREATE FUNCTION fn_reservations_status_transition() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.status = OLD.status THEN
        RETURN NEW;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM reservation_status_transitions t
                    WHERE t.from_status IS NOT DISTINCT FROM (CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE OLD.status END)
                      AND t.to_status = NEW.status) THEN
        RAISE EXCEPTION 'Reservation % can''t move from % to %', NEW.reference,
            CASE WHEN TG_OP = 'INSERT' THEN '(new)' ELSE OLD.status END, NEW.status
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_reservations_status_transition';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_reservations_status_transition BEFORE INSERT OR UPDATE OF status ON reservations
    FOR EACH ROW EXECUTE FUNCTION fn_reservations_status_transition();

-- ---------------------------------------------------------------------
-- reservation_units : the rooms and dorm beds a reservation holds
-- ---------------------------------------------------------------------
CREATE TABLE reservation_units (
    id                     uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id        uuid        NOT NULL REFERENCES organizations (id),
    property_id            uuid        NOT NULL REFERENCES properties (id),
    reservation_id         uuid        NOT NULL,
    accommodation_type_id  uuid        NOT NULL,
    unit_id                uuid        NOT NULL,
    bed_id                 uuid        NULL,
    start_date             date        NOT NULL,
    end_date               date        NOT NULL,
    stay                   daterange   GENERATED ALWAYS AS (daterange(start_date, end_date, '[)')) STORED,
    adults                 smallint    NOT NULL,
    children               smallint    NOT NULL DEFAULT 0,
    holds_inventory        boolean     NOT NULL,
    superseded_at          timestamptz NULL,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now(),
    created_by             uuid        NULL,
    updated_by             uuid        NULL,
    CONSTRAINT ck_reservation_units_dates      CHECK (end_date > start_date),
    CONSTRAINT ck_reservation_units_guests     CHECK (adults >= 0 AND children >= 0 AND adults + children >= 1),
    -- a dorm bed sleeps one guest
    CONSTRAINT ck_reservation_units_bed_guests CHECK (bed_id IS NULL OR adults + children = 1),
    CONSTRAINT ck_reservation_units_superseded CHECK (superseded_at IS NULL OR NOT holds_inventory),
    CONSTRAINT fk_reservation_units_reservation FOREIGN KEY (reservation_id, property_id) REFERENCES reservations (id, property_id),
    CONSTRAINT fk_reservation_units_type       FOREIGN KEY (accommodation_type_id, property_id)
                                               REFERENCES accommodation_types (id, property_id),
    CONSTRAINT fk_reservation_units_unit       FOREIGN KEY (unit_id, property_id) REFERENCES accommodation_units (id, property_id),
    CONSTRAINT fk_reservation_units_bed        FOREIGN KEY (bed_id, unit_id) REFERENCES beds (id, unit_id),
    -- the final guard against double booking: the same room, or the same bed, can't be held twice for any night
    CONSTRAINT ex_reservation_units_unit EXCLUDE USING gist (unit_id WITH =, stay WITH &&)
        WHERE (holds_inventory AND bed_id IS NULL),
    CONSTRAINT ex_reservation_units_bed  EXCLUDE USING gist (bed_id WITH =, stay WITH &&)
        WHERE (holds_inventory AND bed_id IS NOT NULL)
);
CREATE INDEX ix_reservation_units_reservation ON reservation_units (reservation_id);
CREATE INDEX ix_reservation_units_held        ON reservation_units (property_id, start_date, end_date) WHERE holds_inventory;
CREATE INDEX ix_reservation_units_unit        ON reservation_units (unit_id);

-- A new row must match its room (type, and a bed exactly when the room is sold by the bed). A held row must not
-- overlap an active block on its room (a whole-room block, or a block on the same bed). Locks the room row first, the
-- same lock block changes take, so a booking and a block for the same room can't both slip in.
CREATE FUNCTION fn_reservation_units_check() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    type_id uuid;
    mode    text;
BEGIN
    IF TG_OP = 'INSERT' OR NEW.unit_id IS DISTINCT FROM OLD.unit_id OR NEW.bed_id IS DISTINCT FROM OLD.bed_id THEN
        SELECT u.accommodation_type_id, t.inventory_mode INTO type_id, mode
          FROM accommodation_units u JOIN accommodation_types t ON t.id = u.accommodation_type_id
         WHERE u.id = NEW.unit_id;
        IF type_id IS DISTINCT FROM NEW.accommodation_type_id THEN
            RAISE EXCEPTION 'Reservation unit type does not match the room''s type'
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_reservation_units_type';
        END IF;
        IF (mode = 'BED') <> (NEW.bed_id IS NOT NULL) THEN
            RAISE EXCEPTION 'Dormitories are booked by the bed and rooms as a whole'
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_reservation_units_mode';
        END IF;
    END IF;
    IF NEW.holds_inventory THEN
        PERFORM 1 FROM accommodation_units WHERE id = NEW.unit_id FOR UPDATE;
        IF EXISTS (
            SELECT 1 FROM unit_blocks b
             WHERE b.unit_id = NEW.unit_id
               AND b.status = 'ACTIVE'
               AND b.period && daterange(NEW.start_date, NEW.end_date, '[)')
               AND (b.bed_id IS NULL OR b.bed_id = NEW.bed_id)) THEN
            RAISE EXCEPTION 'The room is blocked for some of these nights'
                USING ERRCODE = 'exclusion_violation', CONSTRAINT = 'ex_reservation_units_blocked';
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_reservation_units_check BEFORE INSERT OR UPDATE ON reservation_units
    FOR EACH ROW EXECUTE FUNCTION fn_reservation_units_check();

-- The reverse rule: an active block can't cover a night a reservation holds on that room (or bed).
CREATE FUNCTION fn_unit_blocks_not_reserved() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status <> 'ACTIVE' THEN
        RETURN NEW;
    END IF;
    PERFORM 1 FROM accommodation_units WHERE id = NEW.unit_id FOR UPDATE;
    IF EXISTS (
        SELECT 1 FROM reservation_units r
         WHERE r.unit_id = NEW.unit_id
           AND r.holds_inventory
           AND r.stay && daterange(NEW.start_date, NEW.end_date, '[)')
           AND (NEW.bed_id IS NULL OR r.bed_id = NEW.bed_id)) THEN
        RAISE EXCEPTION 'The room is reserved for some of these nights'
            USING ERRCODE = 'exclusion_violation', CONSTRAINT = 'ex_unit_blocks_reserved';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_unit_blocks_not_reserved BEFORE INSERT OR UPDATE ON unit_blocks
    FOR EACH ROW EXECUTE FUNCTION fn_unit_blocks_not_reserved();

-- ---------------------------------------------------------------------
-- reservation_guests : everyone staying, the primary guest included
-- ---------------------------------------------------------------------
CREATE TABLE reservation_guests (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    reservation_id   uuid        NOT NULL,
    guest_id         uuid        NOT NULL REFERENCES guests (id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT fk_reservation_guests_reservation FOREIGN KEY (reservation_id, property_id) REFERENCES reservations (id, property_id),
    CONSTRAINT ux_reservation_guests UNIQUE (reservation_id, guest_id)
);
CREATE INDEX ix_reservation_guests_guest ON reservation_guests (guest_id);

-- ---------------------------------------------------------------------
-- reservation_rate_breakdowns : the price of every night, as booked
-- ---------------------------------------------------------------------
CREATE TABLE reservation_rate_breakdowns (
    id                   uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id      uuid          NOT NULL REFERENCES organizations (id),
    property_id          uuid          NOT NULL REFERENCES properties (id),
    reservation_id       uuid          NOT NULL,
    reservation_unit_id  uuid          NOT NULL REFERENCES reservation_units (id),
    rate_plan_id         uuid          NOT NULL,
    stay_date            date          NOT NULL,
    units                smallint      NOT NULL,
    unit_price           numeric(12,2) NOT NULL,
    base_amount          numeric(12,2) NOT NULL,
    extra_adults         smallint      NOT NULL DEFAULT 0,
    extra_adult_rate     numeric(12,2) NOT NULL DEFAULT 0,
    extra_adult_amount   numeric(12,2) NOT NULL DEFAULT 0,
    extra_children       smallint      NOT NULL DEFAULT 0,
    extra_child_rate     numeric(12,2) NOT NULL DEFAULT 0,
    extra_child_amount   numeric(12,2) NOT NULL DEFAULT 0,
    total                numeric(12,2) NOT NULL,
    created_at           timestamptz   NOT NULL DEFAULT now(),
    updated_at           timestamptz   NOT NULL DEFAULT now(),
    created_by           uuid          NULL,
    updated_by           uuid          NULL,
    CONSTRAINT ck_reservation_rate_breakdowns_amounts CHECK (
        units >= 1 AND unit_price >= 0 AND base_amount = unit_price * units
        AND extra_adults >= 0 AND extra_adult_amount = extra_adult_rate * extra_adults
        AND extra_children >= 0 AND extra_child_amount = extra_child_rate * extra_children
        AND total = base_amount + extra_adult_amount + extra_child_amount),
    CONSTRAINT fk_reservation_rate_breakdowns_reservation FOREIGN KEY (reservation_id, property_id)
                                                          REFERENCES reservations (id, property_id),
    CONSTRAINT fk_reservation_rate_breakdowns_plan        FOREIGN KEY (rate_plan_id, property_id)
                                                          REFERENCES rate_plans (id, property_id),
    CONSTRAINT ux_reservation_rate_breakdowns_night       UNIQUE (reservation_unit_id, stay_date)
);
CREATE INDEX ix_reservation_rate_breakdowns_reservation ON reservation_rate_breakdowns (reservation_id);

-- ---------------------------------------------------------------------
-- reservation_notes : append-only
-- ---------------------------------------------------------------------
CREATE TABLE reservation_notes (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    reservation_id   uuid        NOT NULL,
    note             text        NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    CONSTRAINT ck_reservation_notes_note        CHECK (length(btrim(note)) BETWEEN 1 AND 2000),
    CONSTRAINT fk_reservation_notes_reservation FOREIGN KEY (reservation_id, property_id) REFERENCES reservations (id, property_id)
);
CREATE INDEX ix_reservation_notes_reservation ON reservation_notes (reservation_id);

-- ---------------------------------------------------------------------
-- reservation_status_history : append-only
-- ---------------------------------------------------------------------
CREATE TABLE reservation_status_history (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    reservation_id   uuid        NOT NULL,
    from_status      text        NULL,
    to_status        text        NOT NULL,
    reason           text        NULL,
    changed_at       timestamptz NOT NULL DEFAULT now(),
    changed_by       uuid        NULL,
    CONSTRAINT ck_reservation_status_history_reason CHECK (reason IS NULL OR length(btrim(reason)) BETWEEN 1 AND 500),
    CONSTRAINT fk_reservation_status_history_reservation FOREIGN KEY (reservation_id, property_id)
                                                         REFERENCES reservations (id, property_id),
    CONSTRAINT fk_reservation_status_history_transition  FOREIGN KEY (from_status, to_status)
                                                         REFERENCES reservation_status_transitions (from_status, to_status)
);
CREATE INDEX ix_reservation_status_history_reservation ON reservation_status_history (reservation_id, changed_at);

-- ---------------------------------------------------------------------
-- idempotency_keys : a create request's stored response, per organisation + endpoint + key
-- ---------------------------------------------------------------------
CREATE TABLE idempotency_keys (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    scope            text        NOT NULL,
    key              text        NOT NULL,
    request_hash     bytea       NOT NULL,
    user_id          uuid        NULL,
    status_code      smallint    NULL,
    response         jsonb       NULL,
    resource_id      uuid        NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    completed_at     timestamptz NULL,
    CONSTRAINT ck_idempotency_keys_key      CHECK (length(key) BETWEEN 8 AND 100),
    CONSTRAINT ck_idempotency_keys_scope    CHECK (length(scope) BETWEEN 1 AND 200),
    CONSTRAINT ck_idempotency_keys_hash     CHECK (octet_length(request_hash) = 32),
    CONSTRAINT ck_idempotency_keys_complete CHECK ((completed_at IS NULL) = (response IS NULL)
                                                   AND (completed_at IS NULL) = (status_code IS NULL)),
    CONSTRAINT ux_idempotency_keys UNIQUE (organization_id, scope, key)
);
CREATE INDEX ix_idempotency_keys_created ON idempotency_keys (created_at);

-- ---------------------------------------------------------------------
-- History is kept: reservations, what they held, their prices, notes and status changes are never deleted;
-- notes and status history are never edited either.
-- ---------------------------------------------------------------------
CREATE FUNCTION fn_reject_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Rows in % cannot be changed', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END $$;

CREATE TRIGGER trg_reservations_no_delete BEFORE DELETE ON reservations
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();
CREATE TRIGGER trg_reservation_units_no_delete BEFORE DELETE ON reservation_units
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();
CREATE TRIGGER trg_reservation_rate_breakdowns_no_delete BEFORE DELETE ON reservation_rate_breakdowns
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();
CREATE TRIGGER trg_reservation_notes_no_delete BEFORE DELETE ON reservation_notes
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();
CREATE TRIGGER trg_reservation_notes_no_update BEFORE UPDATE ON reservation_notes
    FOR EACH ROW EXECUTE FUNCTION fn_reject_update();
CREATE TRIGGER trg_reservation_status_history_no_delete BEFORE DELETE ON reservation_status_history
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();
CREATE TRIGGER trg_reservation_status_history_no_update BEFORE UPDATE ON reservation_status_history
    FOR EACH ROW EXECUTE FUNCTION fn_reject_update();

-- Data classification (brief §59).
COMMENT ON TABLE reservations                   IS 'class: personal (links a guest to dates and a property). Never deleted.';
COMMENT ON TABLE reservation_units              IS 'class: internal. Rooms and dorm beds a reservation holds; EXCLUDE constraints stop double booking.';
COMMENT ON TABLE reservation_guests             IS 'class: personal. Guests staying on a reservation.';
COMMENT ON TABLE reservation_rate_breakdowns    IS 'class: confidential. Nightly price as booked.';
COMMENT ON TABLE reservation_notes              IS 'class: personal (free text may name guests). Append-only.';
COMMENT ON TABLE reservation_status_history     IS 'class: internal. Every status change with who, when and why. Append-only.';
COMMENT ON TABLE reservation_status_transitions IS 'class: internal. Allowed status graph; from_status NULL = allowed initial statuses.';
COMMENT ON TABLE idempotency_keys               IS 'class: personal (stored responses name guests). Retried creates replay the stored response.';
COMMENT ON TABLE property_counters              IS 'class: internal. Per-property running numbers.';
