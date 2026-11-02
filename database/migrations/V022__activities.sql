-- =====================================================================
-- V022  Activities and equipment (Sprint 20, brief §48, §49)
--
-- Guests are booked on activities (kayaking at 7am, sunrise yoga, a snorkelling trip) within each session's seats, the
-- price goes on their room bill, and a session the weather stops is cancelled with every booking refunded on the bill or
-- moved to another session. Equipment (life jackets, kayaks, snorkel sets, bicycles) is issued and returned with a
-- record, and an item that comes back damaged or is lost is out of use until someone puts it back.
--
-- Model
--   activities             what the resort offers: name, length, seats per session, price per person, where to meet,
--                          whether the weather can stop it, and the equipment each person needs (e.g. a life jacket).
--   activity_schedules     one session of an activity: local date, start and end, seats (capacity), price per person.
--                          seats_booked is kept by a trigger from the bookings and can never pass capacity (CHECK): the
--                          final guard against overbooking (the service locks the session row first and says why).
--                          SCHEDULED → CANCELLED (the weather, with the reason).
--   activity_bookings      AB00001…: seats on a session for a staying guest (the price goes on the stay's main folio, one
--                          line, category ACTIVITY, source ACTIVITY), or for anyone when it is free or complimentary (with
--                          the reason). BOOKED → CANCELLED: by the guest (the charge is reversed) or by the weather (the
--                          charge is reversed, or the booking moves to another session of the same activity and the
--                          charge goes with it: moved_to_booking_id / moved_from_booking_id).
--   equipment_types        kinds of equipment: Life jacket, Kayak, Snorkel set, Bicycle.
--   equipment              one item with its tag (LJ-01). AVAILABLE → RESERVED → ISSUED → back to AVAILABLE, or DAMAGED,
--                          or LOST; DAMAGED → MAINTENANCE → AVAILABLE; RETIRED is the end. Moves are checked by a trigger.
--   equipment_assignments  one item reserved for or issued to someone (a booking, a stay, or a name): RESERVED → ISSUED →
--                          RETURNED (good or damaged) or LOST; RESERVED → RELEASED. An item has at most one open
--                          assignment (unique index): two people can't be handed the same kayak.
-- =====================================================================

-- ---------------------------------------------------------------------
-- equipment_types (first: activities point at the equipment each person needs)
-- ---------------------------------------------------------------------
CREATE TABLE equipment_types (
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
    CONSTRAINT ck_equipment_types_code CHECK (code ~ '^[A-Z][A-Z0-9]{0,9}$'),
    CONSTRAINT ck_equipment_types_name CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT ux_equipment_types_code UNIQUE (property_id, code),
    CONSTRAINT ux_equipment_types_id_prop UNIQUE (id, property_id)
);
CREATE UNIQUE INDEX ux_equipment_types_name ON equipment_types (property_id, lower(name));
CREATE TRIGGER trg_equipment_types_no_delete BEFORE DELETE ON equipment_types
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- activities
-- ---------------------------------------------------------------------
CREATE TABLE activities (
    id                 uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid          NOT NULL REFERENCES organizations (id),
    property_id        uuid          NOT NULL REFERENCES properties (id),
    code               text          NOT NULL,
    name               text          NOT NULL,
    description        text          NULL,
    duration_minutes   smallint      NOT NULL,
    capacity           smallint      NOT NULL,           -- seats a new session gets
    price              numeric(10,2) NOT NULL DEFAULT 0, -- per person, before GST; a new session gets it
    meeting_point      text          NULL,
    weather_dependent  boolean       NOT NULL DEFAULT true,
    equipment_type_id  uuid          NULL,               -- one of these per person (e.g. a life jacket)
    sort_order         integer       NOT NULL DEFAULT 0,
    is_active          boolean       NOT NULL DEFAULT true,
    created_at         timestamptz   NOT NULL DEFAULT now(),
    updated_at         timestamptz   NOT NULL DEFAULT now(),
    created_by         uuid          NULL,
    updated_by         uuid          NULL,
    CONSTRAINT ck_activities_code     CHECK (code ~ '^[A-Z][A-Z0-9]{0,9}$'),
    CONSTRAINT ck_activities_name     CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT ck_activities_desc     CHECK (description IS NULL OR length(btrim(description)) BETWEEN 1 AND 500),
    CONSTRAINT ck_activities_meet     CHECK (meeting_point IS NULL OR length(btrim(meeting_point)) BETWEEN 1 AND 100),
    CONSTRAINT ck_activities_duration CHECK (duration_minutes BETWEEN 15 AND 720),
    CONSTRAINT ck_activities_capacity CHECK (capacity BETWEEN 1 AND 200),
    CONSTRAINT ck_activities_price    CHECK (price BETWEEN 0 AND 1000000),
    CONSTRAINT fk_activities_equipment FOREIGN KEY (equipment_type_id, property_id) REFERENCES equipment_types (id, property_id),
    CONSTRAINT ux_activities_code     UNIQUE (property_id, code),
    CONSTRAINT ux_activities_id_prop  UNIQUE (id, property_id)
);
CREATE UNIQUE INDEX ux_activities_name ON activities (property_id, lower(name));
CREATE TRIGGER trg_activities_no_delete BEFORE DELETE ON activities
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- activity_schedules (sessions)
-- ---------------------------------------------------------------------
CREATE TABLE activity_schedules (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    activity_id      uuid          NOT NULL,
    session_date     date          NOT NULL,           -- the local date at the property
    starts_at        timestamptz   NOT NULL,
    ends_at          timestamptz   NOT NULL,
    capacity         smallint      NOT NULL,
    price            numeric(10,2) NOT NULL,
    seats_booked     smallint      NOT NULL DEFAULT 0, -- kept by trg_activity_bookings_seats; never set by hand
    status           text          NOT NULL DEFAULT 'SCHEDULED',
    note             text          NULL,
    cancel_reason    text          NULL,
    cancelled_at     timestamptz   NULL,
    cancelled_by     uuid          NULL REFERENCES users (id),
    created_at       timestamptz   NOT NULL DEFAULT now(),
    updated_at       timestamptz   NOT NULL DEFAULT now(),
    created_by       uuid          NULL,
    updated_by       uuid          NULL,
    CONSTRAINT ck_activity_schedules_times    CHECK (ends_at > starts_at AND ends_at - starts_at <= interval '12 hours'),
    CONSTRAINT ck_activity_schedules_price    CHECK (price BETWEEN 0 AND 1000000),
    CONSTRAINT ck_activity_schedules_seats    CHECK (capacity BETWEEN 1 AND 200),
    -- The overbooking guard: the seats taken can never pass the seats there are.
    CONSTRAINT ck_activity_schedules_capacity CHECK (seats_booked BETWEEN 0 AND capacity),
    CONSTRAINT ck_activity_schedules_status   CHECK (status IN ('SCHEDULED','CANCELLED')),
    CONSTRAINT ck_activity_schedules_cancel   CHECK ((status = 'CANCELLED') = (cancelled_at IS NOT NULL)
                                                     AND (status <> 'CANCELLED' OR cancel_reason IS NOT NULL)),
    CONSTRAINT ck_activity_schedules_reason   CHECK (cancel_reason IS NULL OR length(btrim(cancel_reason)) BETWEEN 3 AND 300),
    CONSTRAINT ck_activity_schedules_note     CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 200),
    CONSTRAINT fk_activity_schedules_activity FOREIGN KEY (activity_id, property_id) REFERENCES activities (id, property_id),
    CONSTRAINT ux_activity_schedules_id_prop  UNIQUE (id, property_id)
);
-- One session of an activity per start time (a cancelled one can be put on again).
CREATE UNIQUE INDEX ux_activity_schedules_start ON activity_schedules (activity_id, starts_at) WHERE status = 'SCHEDULED';
CREATE INDEX ix_activity_schedules_day ON activity_schedules (property_id, session_date, starts_at);
CREATE TRIGGER trg_activity_schedules_no_delete BEFORE DELETE ON activity_schedules
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- activity_bookings
-- ---------------------------------------------------------------------
CREATE TABLE activity_bookings (
    id                    uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id       uuid          NOT NULL REFERENCES organizations (id),
    property_id           uuid          NOT NULL REFERENCES properties (id),
    code                  text          NOT NULL,
    schedule_id           uuid          NOT NULL,
    activity_id           uuid          NOT NULL,
    reservation_id        uuid          NULL REFERENCES reservations (id),
    guest_id              uuid          NULL REFERENCES guests (id),
    guest_name            text          NOT NULL,
    seats                 smallint      NOT NULL,
    unit_price            numeric(10,2) NOT NULL,
    amount                numeric(12,2) NOT NULL,
    settlement            text          NOT NULL,       -- ROOM_CHARGE | FREE | COMPLIMENTARY
    complimentary_reason  text          NULL,
    folio_id              uuid          NULL REFERENCES folios (id),
    folio_item_id         uuid          NULL REFERENCES folio_items (id),
    note                  text          NULL,
    status                text          NOT NULL DEFAULT 'BOOKED',
    cancel_kind           text          NULL,           -- GUEST | WEATHER
    cancel_reason         text          NULL,
    cancelled_at          timestamptz   NULL,
    cancelled_by          uuid          NULL REFERENCES users (id),
    refund                text          NULL,           -- REVERSED (the charge is off the bill) | MOVED (it went with the booking) | NONE
    reversal_item_id      uuid          NULL REFERENCES folio_items (id),
    moved_to_booking_id   uuid          NULL,
    moved_from_booking_id uuid          NULL REFERENCES activity_bookings (id),
    created_at            timestamptz   NOT NULL DEFAULT now(),
    updated_at            timestamptz   NOT NULL DEFAULT now(),
    created_by            uuid          NULL,
    updated_by            uuid          NULL,
    CONSTRAINT ck_activity_bookings_code       CHECK (code ~ '^AB[0-9]{5,}$'),
    CONSTRAINT ck_activity_bookings_name       CHECK (length(btrim(guest_name)) BETWEEN 2 AND 100),
    CONSTRAINT ck_activity_bookings_seats      CHECK (seats BETWEEN 1 AND 50),
    CONSTRAINT ck_activity_bookings_amount     CHECK (unit_price >= 0 AND amount = seats * unit_price),
    CONSTRAINT ck_activity_bookings_settlement CHECK (CASE settlement
                                                         WHEN 'ROOM_CHARGE'   THEN reservation_id IS NOT NULL AND folio_item_id IS NOT NULL AND amount > 0
                                                         WHEN 'FREE'          THEN amount = 0 AND folio_item_id IS NULL
                                                         WHEN 'COMPLIMENTARY' THEN folio_item_id IS NULL AND complimentary_reason IS NOT NULL
                                                         ELSE false END),
    CONSTRAINT ck_activity_bookings_comp       CHECK (complimentary_reason IS NULL OR length(btrim(complimentary_reason)) BETWEEN 3 AND 200),
    CONSTRAINT ck_activity_bookings_note       CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 200),
    CONSTRAINT ck_activity_bookings_status     CHECK (status IN ('BOOKED','CANCELLED')),
    CONSTRAINT ck_activity_bookings_cancel     CHECK ((status = 'CANCELLED') = (cancelled_at IS NOT NULL)
                                                      AND (status = 'CANCELLED') = (cancel_kind IS NOT NULL)
                                                      AND (status = 'CANCELLED') = (cancel_reason IS NOT NULL)
                                                      AND (status = 'CANCELLED') = (refund IS NOT NULL)),
    CONSTRAINT ck_activity_bookings_kind       CHECK (cancel_kind IS NULL OR cancel_kind IN ('GUEST','WEATHER')),
    CONSTRAINT ck_activity_bookings_reason     CHECK (cancel_reason IS NULL OR length(btrim(cancel_reason)) BETWEEN 3 AND 300),
    CONSTRAINT ck_activity_bookings_refund     CHECK (CASE refund
                                                         WHEN 'REVERSED' THEN reversal_item_id IS NOT NULL AND moved_to_booking_id IS NULL
                                                         WHEN 'MOVED'    THEN moved_to_booking_id IS NOT NULL AND reversal_item_id IS NULL
                                                         WHEN 'NONE'     THEN reversal_item_id IS NULL AND moved_to_booking_id IS NULL
                                                         ELSE refund IS NULL END),
    CONSTRAINT fk_activity_bookings_schedule   FOREIGN KEY (schedule_id, property_id) REFERENCES activity_schedules (id, property_id),
    CONSTRAINT fk_activity_bookings_activity   FOREIGN KEY (activity_id, property_id) REFERENCES activities (id, property_id),
    CONSTRAINT ux_activity_bookings_code       UNIQUE (property_id, code),
    -- Checked at commit: a move cancels the old booking (pointing at the new one) before the new one is inserted, so the
    -- charge is never on two live bookings at once (ux_activity_bookings_charge).
    CONSTRAINT fk_activity_bookings_moved_to   FOREIGN KEY (moved_to_booking_id) REFERENCES activity_bookings (id) DEFERRABLE INITIALLY DEFERRED
);
-- A charge belongs to one live booking (a moved booking takes its charge along; the old one is cancelled).
CREATE UNIQUE INDEX ux_activity_bookings_charge ON activity_bookings (folio_item_id) WHERE status = 'BOOKED' AND folio_item_id IS NOT NULL;
CREATE INDEX ix_activity_bookings_schedule ON activity_bookings (schedule_id);
CREATE INDEX ix_activity_bookings_reservation ON activity_bookings (reservation_id) WHERE reservation_id IS NOT NULL;
CREATE TRIGGER trg_activity_bookings_no_delete BEFORE DELETE ON activity_bookings
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- seats_booked = the seats of the session's BOOKED bookings, moved in the same statement as the booking. The UPDATE
-- takes the session's row lock, so two bookings queue here even if something skipped the service's lock, and the CHECK
-- ck_activity_schedules_capacity refuses the one that doesn't fit.
CREATE FUNCTION fn_activity_bookings_seats() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    delta int := 0;
    s     text;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.status = 'BOOKED' THEN
            SELECT status INTO s FROM activity_schedules WHERE id = NEW.schedule_id;
            IF s <> 'SCHEDULED' THEN
                RAISE EXCEPTION 'activity session % is %', NEW.schedule_id, s
                    USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_activity_bookings_session_open';
            END IF;
            delta := NEW.seats;
        END IF;
    ELSE
        IF NEW.schedule_id <> OLD.schedule_id OR NEW.seats <> OLD.seats THEN
            RAISE EXCEPTION 'an activity booking keeps its session and seats; cancel it and book again'
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_activity_bookings_fixed';
        END IF;
        IF OLD.status = 'CANCELLED' AND NEW.status = 'BOOKED' THEN
            RAISE EXCEPTION 'a cancelled activity booking stays cancelled'
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_activity_bookings_fixed';
        END IF;
        IF OLD.status = 'BOOKED' AND NEW.status = 'CANCELLED' THEN
            delta := -OLD.seats;
        END IF;
    END IF;
    IF delta <> 0 THEN
        UPDATE activity_schedules SET seats_booked = seats_booked + delta WHERE id = NEW.schedule_id;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_activity_bookings_seats AFTER INSERT OR UPDATE ON activity_bookings
    FOR EACH ROW EXECUTE FUNCTION fn_activity_bookings_seats();

-- ---------------------------------------------------------------------
-- equipment (items)
-- ---------------------------------------------------------------------
CREATE TABLE equipment (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid        NOT NULL REFERENCES organizations (id),
    property_id        uuid        NOT NULL REFERENCES properties (id),
    equipment_type_id  uuid        NOT NULL,
    tag                text        NOT NULL,
    note               text        NULL,
    status             text        NOT NULL DEFAULT 'AVAILABLE',
    status_note        text        NULL,
    status_changed_at  timestamptz NOT NULL DEFAULT now(),
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    created_by         uuid        NULL,
    updated_by         uuid        NULL,
    CONSTRAINT ck_equipment_tag    CHECK (tag ~ '^[A-Z0-9][A-Z0-9-]{0,19}$'),
    CONSTRAINT ck_equipment_status CHECK (status IN ('AVAILABLE','RESERVED','ISSUED','DAMAGED','MAINTENANCE','LOST','RETIRED')),
    CONSTRAINT ck_equipment_note   CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 200),
    CONSTRAINT ck_equipment_snote  CHECK (status_note IS NULL OR length(btrim(status_note)) BETWEEN 1 AND 300),
    CONSTRAINT fk_equipment_type   FOREIGN KEY (equipment_type_id, property_id) REFERENCES equipment_types (id, property_id),
    CONSTRAINT ux_equipment_tag    UNIQUE (property_id, tag)
);
CREATE INDEX ix_equipment_type ON equipment (equipment_type_id, status);
CREATE TRIGGER trg_equipment_no_delete BEFORE DELETE ON equipment
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- The allowed moves of an item (mirrored in EquipmentStatuses.Transitions, which answers first with the reason).
CREATE FUNCTION fn_equipment_status_transition() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status = OLD.status THEN RETURN NEW; END IF;
    IF NOT (OLD.status, NEW.status) IN (
        ('AVAILABLE','RESERVED'), ('AVAILABLE','ISSUED'), ('AVAILABLE','DAMAGED'), ('AVAILABLE','MAINTENANCE'), ('AVAILABLE','LOST'), ('AVAILABLE','RETIRED'),
        ('RESERVED','ISSUED'), ('RESERVED','AVAILABLE'),
        ('ISSUED','AVAILABLE'), ('ISSUED','DAMAGED'), ('ISSUED','LOST'),
        ('DAMAGED','MAINTENANCE'), ('DAMAGED','AVAILABLE'), ('DAMAGED','RETIRED'), ('DAMAGED','LOST'),
        ('MAINTENANCE','AVAILABLE'), ('MAINTENANCE','DAMAGED'), ('MAINTENANCE','RETIRED'),
        ('LOST','AVAILABLE'), ('LOST','RETIRED')) THEN
        RAISE EXCEPTION 'equipment % can''t go from % to %', NEW.tag, OLD.status, NEW.status
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_equipment_status_transition';
    END IF;
    NEW.status_changed_at := now();
    RETURN NEW;
END $$;
CREATE TRIGGER trg_equipment_status_transition BEFORE UPDATE OF status ON equipment
    FOR EACH ROW EXECUTE FUNCTION fn_equipment_status_transition();

-- ---------------------------------------------------------------------
-- equipment_assignments
-- ---------------------------------------------------------------------
CREATE TABLE equipment_assignments (
    id                   uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id      uuid        NOT NULL REFERENCES organizations (id),
    property_id          uuid        NOT NULL REFERENCES properties (id),
    equipment_id         uuid        NOT NULL REFERENCES equipment (id),
    activity_booking_id  uuid        NULL REFERENCES activity_bookings (id),
    reservation_id       uuid        NULL REFERENCES reservations (id),
    issued_to            text        NOT NULL,
    status               text        NOT NULL,
    reserved_at          timestamptz NULL,
    reserved_by          uuid        NULL REFERENCES users (id),
    issued_at            timestamptz NULL,
    issued_by            uuid        NULL REFERENCES users (id),
    due_back_at          timestamptz NULL,
    closed_at            timestamptz NULL,
    closed_by            uuid        NULL REFERENCES users (id),
    return_condition     text        NULL,          -- GOOD | DAMAGED | LOST
    return_note          text        NULL,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    created_by           uuid        NULL,
    updated_by           uuid        NULL,
    CONSTRAINT ck_equipment_assignments_status CHECK (status IN ('RESERVED','ISSUED','RETURNED','LOST','RELEASED')),
    CONSTRAINT ck_equipment_assignments_to     CHECK (length(btrim(issued_to)) BETWEEN 2 AND 100),
    CONSTRAINT ck_equipment_assignments_steps  CHECK ((status = 'RESERVED') <= (reserved_at IS NOT NULL)
                                                     AND (status IN ('ISSUED','RETURNED','LOST')) = (issued_at IS NOT NULL)
                                                     AND (status IN ('RETURNED','LOST','RELEASED')) = (closed_at IS NOT NULL)),
    CONSTRAINT ck_equipment_assignments_return CHECK (CASE status
                                                         WHEN 'RETURNED' THEN return_condition IN ('GOOD','DAMAGED')
                                                         WHEN 'LOST'     THEN return_condition = 'LOST'
                                                         ELSE return_condition IS NULL END),
    CONSTRAINT ck_equipment_assignments_note   CHECK ((return_condition IN ('DAMAGED','LOST')) <= (return_note IS NOT NULL)
                                                     AND (return_note IS NULL OR length(btrim(return_note)) BETWEEN 3 AND 300))
);
-- An item is with one person at a time: at most one reserved or issued assignment.
CREATE UNIQUE INDEX ux_equipment_assignments_open ON equipment_assignments (equipment_id) WHERE status IN ('RESERVED','ISSUED');
CREATE INDEX ix_equipment_assignments_open ON equipment_assignments (property_id, status) WHERE status IN ('RESERVED','ISSUED');
CREATE INDEX ix_equipment_assignments_booking ON equipment_assignments (activity_booking_id) WHERE activity_booking_id IS NOT NULL;
CREATE INDEX ix_equipment_assignments_item ON equipment_assignments (equipment_id, created_at DESC);
CREATE TRIGGER trg_equipment_assignments_no_delete BEFORE DELETE ON equipment_assignments
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- folio_items: an activity charged to a room is one line with source ACTIVITY
-- ---------------------------------------------------------------------
ALTER TABLE folio_items DROP CONSTRAINT ck_folio_items_source;
ALTER TABLE folio_items ADD CONSTRAINT ck_folio_items_source CHECK (source IN ('MANUAL','ROOM_NIGHT','PACKAGE','RESTAURANT','ACTIVITY'));

-- ---------------------------------------------------------------------
-- Data classification (brief §59)
-- ---------------------------------------------------------------------
COMMENT ON TABLE activities            IS 'class: internal. Activities the resort offers, with seats and price per person.';
COMMENT ON TABLE activity_schedules    IS 'class: internal. Sessions of an activity; seats_booked kept by a trigger, never above capacity.';
COMMENT ON TABLE activity_bookings     IS 'class: personal. Guests booked on activity sessions; the price goes on the room bill.';
COMMENT ON TABLE equipment_types       IS 'class: internal. Kinds of equipment (life jackets, kayaks, snorkel sets, bicycles).';
COMMENT ON TABLE equipment             IS 'class: internal. One equipment item with its tag and status.';
COMMENT ON TABLE equipment_assignments IS 'class: personal. Equipment reserved for or issued to someone, and how it came back.';
