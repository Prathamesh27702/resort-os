-- =====================================================================
-- V011  GST configuration and the tax engine (Sprint 9, brief §23 and §86)
--
-- No GST rate, threshold or SAC code lives in the code: they are rows here, with the dates they apply.
--
-- Model
--   gst_registrations  the GSTIN a property bills under, from/to a date. A property with no registration on a day charges
--                      no GST that day (an unregistered supplier can't); a COMPOSITION registration charges none either
--                      (it issues a bill of supply). The GSTIN's first two digits are its state; periods don't overlap.
--   tax_categories     what is taxed: accommodation (SAC 996311), restaurant service (SAC 996331) … Per organisation.
--                      charge_category links it to the folio's charge categories (ROOM, FOOD …): a charge in a
--                      category with no tax category carries no GST.
--   tax_rules          a category's rule from effective_from to effective_to (both days included; NULL = until
--                      further notice), with the notification it comes from (source). FLAT = one rate; SLAB = the rate
--                      depends on the value of one unit (room or dorm bed) for one night — for any other charge, the
--                      price of one item. A category's rules never overlap. A rule is never deleted and only its end
--                      date and "verified by" can change, so a rule a charge used stays as it was.
--   tax_rule_slabs     the rates of a rule: up_to (included; NULL = no upper limit) → rate, and whether input tax credit
--                      is available. A FLAT rule has one slab with up_to NULL. Never updated or deleted.
--   folio_item_taxes   (V010) now filled when a charge is posted: one row per tax (CGST + SGST inside the state, IGST
--                      across states) with the rule id and the rate used, so a later rule never changes a posted charge.
--
-- Seed data (fn_seed_tax_defaults): the current accommodation and restaurant rules, AS DATA, with their source. They
-- were entered from public CBIC notifications and are marked unverified: the owner's accountant must check each rule and
-- press Verify in Settings → GST before go-live. Every new organisation gets them (trigger on organizations).
-- =====================================================================

-- ---------------------------------------------------------------------
-- gst_registrations
-- ---------------------------------------------------------------------
CREATE TABLE gst_registrations (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid        NOT NULL REFERENCES organizations (id),
    property_id        uuid        NOT NULL REFERENCES properties (id),
    gstin              char(15)    NOT NULL,
    legal_name         text        NOT NULL,
    trade_name         text        NULL,
    state_code         char(2)     NOT NULL,
    registration_type  text        NOT NULL DEFAULT 'REGULAR',
    effective_from     date        NOT NULL,
    effective_to       date        NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    created_by         uuid        NULL,
    updated_by         uuid        NULL,
    CONSTRAINT ck_gst_registrations_gstin  CHECK (gstin ~ '^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$'),
    CONSTRAINT ck_gst_registrations_state  CHECK (state_code = left(gstin, 2)),
    CONSTRAINT ck_gst_registrations_type   CHECK (registration_type IN ('REGULAR','COMPOSITION')),
    CONSTRAINT ck_gst_registrations_name   CHECK (length(btrim(legal_name)) BETWEEN 1 AND 200),
    CONSTRAINT ck_gst_registrations_dates  CHECK (effective_to IS NULL OR effective_to >= effective_from),
    -- one registration per property on any day
    CONSTRAINT ex_gst_registrations_overlap EXCLUDE USING gist
        (property_id WITH =, daterange(effective_from, effective_to, '[]') WITH &&)
);
CREATE INDEX ix_gst_registrations_org ON gst_registrations (organization_id);

CREATE TRIGGER trg_gst_registrations_no_delete BEFORE DELETE ON gst_registrations
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- tax_categories
-- ---------------------------------------------------------------------
CREATE TABLE tax_categories (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    code             text        NOT NULL,
    name             text        NOT NULL,
    hsn_sac          text        NOT NULL,
    charge_category  text        NULL,
    description      text        NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_tax_categories_code     CHECK (code ~ '^[A-Z][A-Z0-9_]{1,29}$'),
    CONSTRAINT ck_tax_categories_name     CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    CONSTRAINT ck_tax_categories_hsn_sac  CHECK (hsn_sac ~ '^([0-9]{4}|[0-9]{6}|[0-9]{8})$'),
    CONSTRAINT ck_tax_categories_charge   CHECK (charge_category IS NULL OR charge_category IN
        ('ROOM','FOOD','BEVERAGE','ACTIVITY','TRANSPORT','EQUIPMENT','LAUNDRY','OTHER')),
    CONSTRAINT ux_tax_categories_code     UNIQUE (organization_id, code)
);
-- a folio charge category is taxed under at most one tax category
CREATE UNIQUE INDEX ux_tax_categories_charge ON tax_categories (organization_id, charge_category) WHERE charge_category IS NOT NULL;

CREATE TRIGGER trg_tax_categories_no_delete BEFORE DELETE ON tax_categories
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- tax_rules
-- ---------------------------------------------------------------------
CREATE TABLE tax_rules (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id   uuid        NOT NULL REFERENCES organizations (id),
    tax_category_id   uuid        NOT NULL REFERENCES tax_categories (id),
    effective_from    date        NOT NULL,
    effective_to      date        NULL,
    rule_type         text        NOT NULL,
    source            text        NOT NULL,
    notes             text        NULL,
    verified_at       timestamptz NULL,
    verified_by       uuid        NULL REFERENCES users (id),
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    created_by        uuid        NULL,
    updated_by        uuid        NULL,
    CONSTRAINT ck_tax_rules_type    CHECK (rule_type IN ('FLAT','SLAB')),
    CONSTRAINT ck_tax_rules_dates   CHECK (effective_to IS NULL OR effective_to >= effective_from),
    CONSTRAINT ck_tax_rules_source  CHECK (length(btrim(source)) BETWEEN 3 AND 500),
    CONSTRAINT ck_tax_rules_verified CHECK ((verified_at IS NULL) = (verified_by IS NULL)),
    -- one rule per category on any day
    CONSTRAINT ex_tax_rules_overlap EXCLUDE USING gist
        (tax_category_id WITH =, daterange(effective_from, effective_to, '[]') WITH &&)
);
CREATE INDEX ix_tax_rules_org ON tax_rules (organization_id);

-- Only the end date (to close a rule when the next one starts) and the verification can change.
CREATE FUNCTION fn_tax_rules_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF ROW(NEW.organization_id, NEW.tax_category_id, NEW.effective_from, NEW.rule_type, NEW.source, NEW.notes, NEW.created_at, NEW.created_by)
       IS DISTINCT FROM
       ROW(OLD.organization_id, OLD.tax_category_id, OLD.effective_from, OLD.rule_type, OLD.source, OLD.notes, OLD.created_at, OLD.created_by) THEN
        RAISE EXCEPTION 'A tax rule can''t be changed once saved: add a new rule from the date it should change'
            USING ERRCODE = 'insufficient_privilege', CONSTRAINT = 'ck_tax_rules_immutable';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_tax_rules_guard BEFORE UPDATE ON tax_rules
    FOR EACH ROW EXECUTE FUNCTION fn_tax_rules_guard();
CREATE TRIGGER trg_tax_rules_no_delete BEFORE DELETE ON tax_rules
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- tax_rule_slabs
-- ---------------------------------------------------------------------
CREATE TABLE tax_rule_slabs (
    id                 uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid          NOT NULL REFERENCES organizations (id),
    tax_rule_id        uuid          NOT NULL REFERENCES tax_rules (id),
    up_to              numeric(12,2) NULL,
    rate               numeric(5,2)  NOT NULL,
    input_tax_credit   boolean       NOT NULL DEFAULT false,
    sort_order         integer       NOT NULL DEFAULT 0,
    created_at         timestamptz   NOT NULL DEFAULT now(),
    -- the rate is split evenly into CGST and SGST, so half of it must still be a whole number of hundredths
    CONSTRAINT ck_tax_rule_slabs_rate   CHECK (rate >= 0 AND rate <= 100 AND (rate * 50) = trunc(rate * 50)),
    CONSTRAINT ck_tax_rule_slabs_up_to  CHECK (up_to IS NULL OR up_to > 0),
    CONSTRAINT ux_tax_rule_slabs_up_to  UNIQUE NULLS NOT DISTINCT (tax_rule_id, up_to)
);
CREATE INDEX ix_tax_rule_slabs_rule ON tax_rule_slabs (tax_rule_id);

CREATE TRIGGER trg_tax_rule_slabs_no_delete BEFORE DELETE ON tax_rule_slabs
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();
CREATE TRIGGER trg_tax_rule_slabs_no_update BEFORE UPDATE ON tax_rule_slabs
    FOR EACH ROW EXECUTE FUNCTION fn_reject_update();

-- ---------------------------------------------------------------------
-- folio_item_taxes (V010) points at the rule it used
-- ---------------------------------------------------------------------
ALTER TABLE folio_item_taxes
    ADD CONSTRAINT fk_folio_item_taxes_rule FOREIGN KEY (tax_rule_id) REFERENCES tax_rules (id);

-- ---------------------------------------------------------------------
-- Seed: the current accommodation and restaurant rules as data (unverified until the accountant checks them).
-- Idempotent: a category that already exists is left alone, and so are its rules.
-- ---------------------------------------------------------------------
CREATE FUNCTION fn_seed_tax_defaults(org uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    cat   uuid;
    rule  uuid;
BEGIN
    -- Accommodation: the rate depends on the value of one room (or bed) for one night.
    INSERT INTO tax_categories (organization_id, code, name, hsn_sac, charge_category, description)
    VALUES (org, 'ACCOMMODATION', 'Accommodation', '996311', 'ROOM',
            'Room or dorm-bed nights and other room charges. Slab on the value of one unit for one night.')
    ON CONFLICT (organization_id, code) DO NOTHING
    RETURNING id INTO cat;
    IF cat IS NOT NULL THEN
        INSERT INTO tax_rules (organization_id, tax_category_id, effective_from, effective_to, rule_type, source, notes)
        VALUES (org, cat, DATE '2022-07-18', DATE '2025-09-21', 'SLAB',
                'Notification 03/2022-Central Tax (Rate) of 13 Jul 2022 (GST Council, 47th meeting), amending 11/2017-Central Tax (Rate)',
                'Up to ₹7,500 per unit per day 12%; above ₹7,500 18%. Seeded by Resort OS — verify before go-live.')
        RETURNING id INTO rule;
        INSERT INTO tax_rule_slabs (organization_id, tax_rule_id, up_to, rate, input_tax_credit, sort_order) VALUES
            (org, rule, 7500.00, 12.00, true, 0),
            (org, rule, NULL,    18.00, true, 1);

        INSERT INTO tax_rules (organization_id, tax_category_id, effective_from, effective_to, rule_type, source, notes)
        VALUES (org, cat, DATE '2025-09-22', NULL, 'SLAB',
                'Notification 05/2025-Central Tax (Rate) of 17 Sep 2025 (GST Council, 56th meeting), amending 11/2017-Central Tax (Rate)',
                'Up to ₹7,500 per unit per day 5% without input tax credit; above ₹7,500 18% with input tax credit. Seeded by Resort OS — verify before go-live.')
        RETURNING id INTO rule;
        INSERT INTO tax_rule_slabs (organization_id, tax_rule_id, up_to, rate, input_tax_credit, sort_order) VALUES
            (org, rule, 7500.00, 5.00,  false, 0),
            (org, rule, NULL,    18.00, true,  1);
    END IF;

    -- Restaurant service: one rate.
    cat := NULL;
    INSERT INTO tax_categories (organization_id, code, name, hsn_sac, charge_category, description)
    VALUES (org, 'RESTAURANT', 'Restaurant service', '996331', 'FOOD',
            'Food served at the restaurant, the beach shack or in the room.')
    ON CONFLICT (organization_id, code) DO NOTHING
    RETURNING id INTO cat;
    IF cat IS NOT NULL THEN
        INSERT INTO tax_rules (organization_id, tax_category_id, effective_from, effective_to, rule_type, source, notes)
        VALUES (org, cat, DATE '2017-11-15', NULL, 'FLAT',
                'Notification 46/2017-Central Tax (Rate) of 14 Nov 2017, amending 11/2017-Central Tax (Rate)',
                '5% without input tax credit. A restaurant in "specified premises" (a hotel that charges more than ₹7,500 a night for a room) charges 18% with input tax credit instead: if that is this property, add an 18% rule. Seeded by Resort OS — verify before go-live.')
        RETURNING id INTO rule;
        INSERT INTO tax_rule_slabs (organization_id, tax_rule_id, up_to, rate, input_tax_credit, sort_order)
        VALUES (org, rule, NULL, 5.00, false, 0);
    END IF;
END $$;

SELECT fn_seed_tax_defaults(id) FROM organizations;

CREATE FUNCTION fn_organizations_seed_tax() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM fn_seed_tax_defaults(NEW.id);
    RETURN NEW;
END $$;
CREATE TRIGGER trg_organizations_seed_tax AFTER INSERT ON organizations
    FOR EACH ROW EXECUTE FUNCTION fn_organizations_seed_tax();

-- Data classification (brief §59).
COMMENT ON TABLE gst_registrations IS 'class: internal. GSTINs a property bills under; printed on invoices. Never deleted.';
COMMENT ON TABLE tax_categories    IS 'class: internal. What is taxed (SAC/HSN) and which folio charges it covers.';
COMMENT ON TABLE tax_rules         IS 'class: internal. GST rules with the dates they apply; only the end date and verification change.';
COMMENT ON TABLE tax_rule_slabs    IS 'class: internal. Rates of a tax rule by value; immutable.';
