-- =====================================================================
-- V009  Front desk: check-in, check-out, room housekeeping state (Sprint 7)
--
-- Model
--   reservations.checked_in_at / checked_out_at   when the guest actually arrived and left (who is in
--                                                 reservation_status_history and the audit trail).
--   unit_housekeeping_status    the cleaning state of every room (bed_id NULL) and every dormitory bed (bed_id set).
--                               A room or bed with no row has never been used and counts as READY. Check-in needs
--                               READY (or an override with a reason); check-out sets DIRTY. Sprint 13 adds the
--                               housekeeping board and the CLEANING → CLEAN → INSPECTION steps on these same rows.
--   unit_housekeeping_history   every change of that state: from, to, why, which reservation caused it and from
--                               where (CHECK_OUT, ROOM_MOVE, MANUAL). Append-only. Rows from a check-out are the
--                               "room needs cleaning" events Sprint 13 turns into housekeeping tasks; it marks each one
--                               consumed (consumed_at) when it does.
--
-- Settings read by the front desk (property_settings, JSON booleans; defaults in code)
--   frontdesk.allow_checkout_with_balance   false: check-out is refused while the folio has a balance (Sprint 8)
--   frontdesk.allow_checkin_when_not_ready  true:  reception may check a guest into a room that isn't READY, with a reason
-- =====================================================================

ALTER TABLE reservations
    ADD COLUMN checked_in_at  timestamptz NULL,
    ADD COLUMN checked_out_at timestamptz NULL,
    ADD CONSTRAINT ck_reservations_checked_out CHECK (checked_out_at IS NULL OR checked_in_at IS NOT NULL);

-- The front desk lists read arrivals and departures by date.
CREATE INDEX ix_reservations_property_check_out ON reservations (property_id, check_out);

-- ---------------------------------------------------------------------
-- unit_housekeeping_status : one row per room (bed_id NULL) or dorm bed
-- ---------------------------------------------------------------------
CREATE TABLE unit_housekeeping_status (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    unit_id          uuid        NOT NULL,
    bed_id           uuid        NULL,
    status           text        NOT NULL,
    reason           text        NULL,
    changed_at       timestamptz NOT NULL DEFAULT now(),
    changed_by       uuid        NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_unit_housekeeping_status_status CHECK (status IN ('DIRTY','CLEANING','CLEAN','INSPECTION','READY','OUT_OF_ORDER')),
    CONSTRAINT ck_unit_housekeeping_status_reason CHECK (reason IS NULL OR length(btrim(reason)) BETWEEN 1 AND 500),
    CONSTRAINT fk_unit_housekeeping_status_unit   FOREIGN KEY (unit_id, property_id) REFERENCES accommodation_units (id, property_id),
    CONSTRAINT fk_unit_housekeeping_status_bed    FOREIGN KEY (bed_id, unit_id) REFERENCES beds (id, unit_id),
    CONSTRAINT ux_unit_housekeeping_status        UNIQUE NULLS NOT DISTINCT (unit_id, bed_id)
);
CREATE INDEX ix_unit_housekeeping_status_property ON unit_housekeeping_status (property_id, status);

-- ---------------------------------------------------------------------
-- unit_housekeeping_history : append-only
-- ---------------------------------------------------------------------
CREATE TABLE unit_housekeeping_history (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    unit_id          uuid        NOT NULL,
    bed_id           uuid        NULL,
    from_status      text        NOT NULL,
    to_status        text        NOT NULL,
    source           text        NOT NULL,
    reservation_id   uuid        NULL,
    reason           text        NULL,
    changed_at       timestamptz NOT NULL DEFAULT now(),
    changed_by       uuid        NULL,
    consumed_at      timestamptz NULL,
    CONSTRAINT ck_unit_housekeeping_history_status CHECK (
        from_status IN ('DIRTY','CLEANING','CLEAN','INSPECTION','READY','OUT_OF_ORDER')
        AND to_status IN ('DIRTY','CLEANING','CLEAN','INSPECTION','READY','OUT_OF_ORDER')),
    CONSTRAINT ck_unit_housekeeping_history_source CHECK (source IN ('CHECK_OUT','ROOM_MOVE','MANUAL')),
    CONSTRAINT ck_unit_housekeeping_history_reason CHECK (reason IS NULL OR length(btrim(reason)) BETWEEN 1 AND 500),
    CONSTRAINT fk_unit_housekeeping_history_unit   FOREIGN KEY (unit_id, property_id) REFERENCES accommodation_units (id, property_id),
    CONSTRAINT fk_unit_housekeeping_history_bed    FOREIGN KEY (bed_id, unit_id) REFERENCES beds (id, unit_id),
    CONSTRAINT fk_unit_housekeeping_history_reservation FOREIGN KEY (reservation_id, property_id) REFERENCES reservations (id, property_id)
);
CREATE INDEX ix_unit_housekeeping_history_unit ON unit_housekeeping_history (unit_id, changed_at);
-- Sprint 13 reads the check-out events it hasn't turned into tasks yet.
CREATE INDEX ix_unit_housekeeping_history_pending ON unit_housekeeping_history (property_id, changed_at)
    WHERE consumed_at IS NULL AND to_status = 'DIRTY';

CREATE TRIGGER trg_unit_housekeeping_history_no_delete BEFORE DELETE ON unit_housekeeping_history
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- Data classification (brief §59).
COMMENT ON TABLE unit_housekeeping_status  IS 'class: internal. Cleaning state per room or dorm bed; no row = READY (never used).';
COMMENT ON TABLE unit_housekeeping_history IS 'class: internal. Every housekeeping state change; check-out rows are the events Sprint 13 consumes.';
