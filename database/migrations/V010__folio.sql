-- =====================================================================
-- V010  Folios: the running bill of a stay (Sprint 8, brief §20 and §85)
--
-- Model
--   folios            a bill for one reservation. Every confirmed booking has one main folio (is_primary), opened when it
--                     is confirmed; more can be opened to split the bill (e.g. the company pays the room, the guest pays
--                     the bar). Numbered F00001, F00002 … per property.
--   folio_items       one line of a bill. Immutable: never updated, never deleted. A mistake is corrected by a REVERSAL
--                     line (negative quantity and amount, the original's id in reverses_item_id, a reason). Moving a line
--                     to another folio (split) is a reversal on this folio plus the same line on the other one
--                     (transferred_from_item_id), so both bills keep their history.
--                       source ROOM_NIGHT  the price of one night of the stay (all of the booking's rooms that night, as
--                                          booked in reservation_rate_breakdowns); posted at check-in for the whole stay,
--                                          or night by night (property setting folio.room_charge_posting = "NIGHTLY")
--                       source PACKAGE     one line per package component (ROOM, FOOD, TRANSPORT, ACTIVITY), the lines of
--                                          one sale sharing group_id; amounts are the component amounts × quantity, so
--                                          they add up to the package price exactly
--                       source MANUAL      anything posted by staff (restaurant, transport, laundry …)
--   folio_item_taxes  the tax on a line (CGST/SGST/IGST/cess), the rule and rate copied at posting time so a later rule
--                     change never alters history (brief §86). Filled from Sprint 9 (tax engine); empty until then, so
--                     tax_amount is 0 on every line of this sprint.
--
-- Balance = sum(folio_items.total) − payments (Sprint 10). Nothing is stored twice: totals are always summed from the lines.
--
-- Integrity
--   * a line's amount is round(quantity × unit_price, 2) and its total is amount + tax_amount (CHECK)
--   * a reversal must point to a CHARGE on the same folio and negate it exactly; a line can be reversed once (UNIQUE)
--   * one ROOM_NIGHT charge per reservation and night (partial UNIQUE), so posting twice is impossible
--   * DELETE and UPDATE on folio_items and folio_item_taxes are rejected by triggers; folios are never deleted
-- =====================================================================

-- ---------------------------------------------------------------------
-- folios
-- ---------------------------------------------------------------------
CREATE TABLE folios (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id   uuid        NOT NULL REFERENCES organizations (id),
    property_id       uuid        NOT NULL REFERENCES properties (id),
    reservation_id    uuid        NOT NULL,
    number            text        NOT NULL,
    name              text        NOT NULL,
    is_primary        boolean     NOT NULL DEFAULT false,
    bill_to_guest_id  uuid        NULL REFERENCES guests (id),
    currency          char(3)     NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    created_by        uuid        NULL,
    updated_by        uuid        NULL,
    CONSTRAINT ck_folios_number          CHECK (number ~ '^F[0-9]{5,}$'),
    CONSTRAINT ck_folios_name            CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    CONSTRAINT fk_folios_reservation     FOREIGN KEY (reservation_id, property_id) REFERENCES reservations (id, property_id),
    CONSTRAINT ux_folios_number          UNIQUE (property_id, number),
    CONSTRAINT ux_folios_id_prop         UNIQUE (id, property_id)
);
CREATE INDEX ix_folios_reservation ON folios (reservation_id);
CREATE INDEX ix_folios_org         ON folios (organization_id);
-- one main folio per reservation
CREATE UNIQUE INDEX ux_folios_primary ON folios (reservation_id) WHERE is_primary;

CREATE TRIGGER trg_folios_no_delete BEFORE DELETE ON folios
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- folio_items : immutable lines
-- ---------------------------------------------------------------------
CREATE TABLE folio_items (
    id                        uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id           uuid          NOT NULL REFERENCES organizations (id),
    property_id               uuid          NOT NULL REFERENCES properties (id),
    folio_id                  uuid          NOT NULL,
    reservation_id            uuid          NOT NULL,
    item_type                 text          NOT NULL,
    category                  text          NOT NULL,
    description               text          NOT NULL,
    service_date              date          NOT NULL,
    quantity                  numeric(12,3) NOT NULL,
    unit_price                numeric(12,2) NOT NULL,
    amount                    numeric(12,2) NOT NULL,
    tax_amount                numeric(12,2) NOT NULL DEFAULT 0,
    total                     numeric(12,2) NOT NULL,
    source                    text          NOT NULL,
    package_id                uuid          NULL,
    group_id                  uuid          NULL,
    reverses_item_id          uuid          NULL REFERENCES folio_items (id),
    transferred_from_item_id  uuid          NULL REFERENCES folio_items (id),
    reason                    text          NULL,
    posted_at                 timestamptz   NOT NULL DEFAULT now(),
    posted_by                 uuid          NULL,
    CONSTRAINT ck_folio_items_type        CHECK (item_type IN ('CHARGE','REVERSAL')),
    CONSTRAINT ck_folio_items_category    CHECK (category IN ('ROOM','FOOD','BEVERAGE','ACTIVITY','TRANSPORT','EQUIPMENT','LAUNDRY','OTHER')),
    CONSTRAINT ck_folio_items_source      CHECK (source IN ('MANUAL','ROOM_NIGHT','PACKAGE')),
    CONSTRAINT ck_folio_items_description CHECK (length(btrim(description)) BETWEEN 1 AND 200),
    CONSTRAINT ck_folio_items_reason      CHECK (reason IS NULL OR length(btrim(reason)) BETWEEN 3 AND 500),
    CONSTRAINT ck_folio_items_amounts     CHECK (
        unit_price >= 0 AND amount = round(quantity * unit_price, 2) AND total = amount + tax_amount
        AND ((item_type = 'CHARGE'   AND quantity > 0 AND tax_amount >= 0 AND reverses_item_id IS NULL)
          OR (item_type = 'REVERSAL' AND quantity < 0 AND tax_amount <= 0 AND reverses_item_id IS NOT NULL AND reason IS NOT NULL))),
    CONSTRAINT ck_folio_items_package     CHECK ((source = 'PACKAGE') = (package_id IS NOT NULL)),
    CONSTRAINT fk_folio_items_folio       FOREIGN KEY (folio_id, property_id) REFERENCES folios (id, property_id),
    CONSTRAINT fk_folio_items_reservation FOREIGN KEY (reservation_id, property_id) REFERENCES reservations (id, property_id),
    CONSTRAINT fk_folio_items_package     FOREIGN KEY (package_id, property_id) REFERENCES packages (id, property_id),
    -- a line is reversed at most once, and moved to another folio at most once
    CONSTRAINT ux_folio_items_reverses    UNIQUE (reverses_item_id),
    CONSTRAINT ux_folio_items_transferred UNIQUE (transferred_from_item_id)
);
CREATE INDEX ix_folio_items_folio       ON folio_items (folio_id, posted_at);
CREATE INDEX ix_folio_items_reservation ON folio_items (reservation_id);
-- each night of a stay is charged once (a line moved to another folio carries the night with it, so it is left out)
CREATE UNIQUE INDEX ux_folio_items_room_night ON folio_items (reservation_id, service_date)
    WHERE source = 'ROOM_NIGHT' AND item_type = 'CHARGE' AND transferred_from_item_id IS NULL;

-- A folio's reservation is the line's reservation; a reversal negates a charge on the same folio exactly.
CREATE FUNCTION fn_folio_items_check() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    f   record;
    o   record;
BEGIN
    SELECT reservation_id INTO f FROM folios WHERE id = NEW.folio_id;
    IF f.reservation_id IS DISTINCT FROM NEW.reservation_id THEN
        RAISE EXCEPTION 'Folio line reservation does not match its folio'
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_folio_items_folio_reservation';
    END IF;
    IF NEW.item_type = 'REVERSAL' THEN
        SELECT * INTO o FROM folio_items WHERE id = NEW.reverses_item_id;
        IF o.item_type IS DISTINCT FROM 'CHARGE' OR o.folio_id <> NEW.folio_id
           OR NEW.quantity <> -o.quantity OR NEW.unit_price <> o.unit_price OR NEW.amount <> -o.amount
           OR NEW.tax_amount <> -o.tax_amount OR NEW.category <> o.category THEN
            RAISE EXCEPTION 'A reversal must negate a charge on the same folio exactly'
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_folio_items_reversal';
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_folio_items_check BEFORE INSERT ON folio_items
    FOR EACH ROW EXECUTE FUNCTION fn_folio_items_check();

CREATE TRIGGER trg_folio_items_no_delete BEFORE DELETE ON folio_items
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();
CREATE TRIGGER trg_folio_items_no_update BEFORE UPDATE ON folio_items
    FOR EACH ROW EXECUTE FUNCTION fn_reject_update();

-- ---------------------------------------------------------------------
-- folio_item_taxes : the tax on a line, as calculated when posted (Sprint 9 fills it)
-- tax_rule_id gets its foreign key when tax_rules is created (Sprint 9).
-- ---------------------------------------------------------------------
CREATE TABLE folio_item_taxes (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    folio_item_id    uuid          NOT NULL REFERENCES folio_items (id),
    tax_rule_id      uuid          NULL,
    tax_code         text          NOT NULL,
    rate             numeric(5,2)  NOT NULL,
    taxable_value    numeric(12,2) NOT NULL,
    amount           numeric(12,2) NOT NULL,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_folio_item_taxes_code CHECK (tax_code IN ('CGST','SGST','IGST','CESS')),
    CONSTRAINT ck_folio_item_taxes_rate CHECK (rate >= 0 AND rate <= 100)
);
CREATE INDEX ix_folio_item_taxes_item ON folio_item_taxes (folio_item_id);

CREATE TRIGGER trg_folio_item_taxes_no_delete BEFORE DELETE ON folio_item_taxes
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();
CREATE TRIGGER trg_folio_item_taxes_no_update BEFORE UPDATE ON folio_item_taxes
    FOR EACH ROW EXECUTE FUNCTION fn_reject_update();

-- ---------------------------------------------------------------------
-- Main folios for bookings that already exist (confirmed, in house or checked out). Also called by the dev seed,
-- whose demo bookings are inserted after the migrations run. New bookings get theirs from the API.
-- ---------------------------------------------------------------------
CREATE FUNCTION fn_open_missing_folios() RETURNS integer LANGUAGE plpgsql AS $$
DECLARE
    opened integer;
BEGIN
    WITH todo AS (
        SELECT r.id, r.organization_id, r.property_id, r.currency,
               row_number() OVER (PARTITION BY r.property_id ORDER BY r.created_at, r.reference) AS n
          FROM reservations r
         WHERE r.status IN ('CONFIRMED','CHECKED_IN','CHECKED_OUT')
           AND NOT EXISTS (SELECT 1 FROM folios f WHERE f.reservation_id = r.id AND f.is_primary)
    ), ins AS (
        INSERT INTO folios (organization_id, property_id, reservation_id, number, name, is_primary, currency)
        SELECT t.organization_id, t.property_id, t.id,
               'F' || lpad((coalesce(c.last_value, 0) + t.n)::text, 5, '0'), 'Main', true, t.currency
          FROM todo t LEFT JOIN property_counters c ON c.property_id = t.property_id AND c.name = 'folio'
        RETURNING property_id
    ), counted AS (
        INSERT INTO property_counters (property_id, name, last_value)
        SELECT property_id, 'folio', count(*) FROM ins GROUP BY property_id
        ON CONFLICT (property_id, name) DO UPDATE SET last_value = property_counters.last_value + EXCLUDED.last_value
        RETURNING last_value
    )
    SELECT (SELECT count(*) FROM ins) INTO opened FROM (SELECT count(*) FROM counted) x;
    RETURN opened;
END $$;

SELECT fn_open_missing_folios();

-- Data classification (brief §59).
COMMENT ON TABLE folios           IS 'class: personal (a guest''s bill). Never deleted.';
COMMENT ON TABLE folio_items      IS 'class: confidential. Bill lines; immutable, corrected by reversal lines.';
COMMENT ON TABLE folio_item_taxes IS 'class: confidential. Tax per bill line as calculated when posted (Sprint 9).';
