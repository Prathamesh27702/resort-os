-- =====================================================================
-- V014  GST invoices, credit and debit notes, receipts (Sprint 12, brief §24, §25 and §91 L)
--
-- Model
--   invoice_series        one running number per property, document type and financial year (April–March). The next
--                         number is taken by an upsert that locks the row until the transaction ends, so two invoices
--                         issued at the same moment wait for each other: numbers are gap-free (a rolled-back invoice gives
--                         its number back) and never repeat. Format {prefix}{number}: BEACH/2627/00001 (invoice),
--                         BEACH/B2627/0001 (bill of supply), BEACH/C2627/0001 (credit note), BEACH/D2627/0001 (debit
--                         note), BEACH/R2627/00001 (receipt). GST documents stay within 16 characters (rule 46(b)).
--   invoices              a GST tax invoice (or a bill of supply under the composition scheme) for one folio. The supplier
--                         (the GSTIN on the invoice day) and the customer (B2B with a GSTIN, or B2C) are copied onto it,
--                         with the place of supply, so it never changes when a profile does. Never updated or deleted.
--   invoice_items         one line per folio charge. The amounts, and every tax row with its rule id and rate, are copied
--                         from the folio (folio_item_taxes) — never recalculated (brief §86). A charge that a credit note
--                         already reduced is invoiced net: discount = what was credited.
--   credit_notes          the only way to cancel or reduce an invoice. CANCEL credits the whole invoice (and its debit
--                         notes) and leaves the bill as it is, so the charges can be invoiced again with corrected
--                         details. REDUCE credits chosen lines in full or in part and puts a CREDIT line on the folio, so
--                         the guest owes less. A reason is required. Taxes are credited at the rates of the invoice.
--   debit_notes           charges posted to the folio after its invoice was issued (minibar found after check-out).
--   receipts              acknowledgement of a payment: one per payment, numbered in its own series.
--   einvoice_records      IRN, acknowledgement number and date and the signed QR of a document registered with the
--                         invoice registration portal. Filled by the e-invoicing integration (Sprint 25).
--
-- A folio charge is "billed" while it is on an invoice (or debit note) whose invoice has no CANCEL credit note. Billed
-- charges can't be reversed or moved on the folio: they are corrected by a credit note. All of this is checked under the
-- reservation row lock that every folio change takes.
--
-- Integrity
--   * header totals = the sum of the lines, and line tax = the sum of its tax rows (deferred constraint triggers)
--   * credited ≤ billed for every line (trigger)
--   * invoices, notes, receipts and their lines are never updated or deleted; series numbers only ever go up by one
-- =====================================================================

-- ---------------------------------------------------------------------
-- invoice_series
-- ---------------------------------------------------------------------
CREATE TABLE invoice_series (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    document_type    text        NOT NULL,
    financial_year   text        NOT NULL,
    prefix           text        NOT NULL,
    width            smallint    NOT NULL,
    last_number      integer     NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_invoice_series_type   CHECK (document_type IN ('INVOICE','BILL_OF_SUPPLY','CREDIT_NOTE','DEBIT_NOTE','RECEIPT')),
    CONSTRAINT ck_invoice_series_fy     CHECK (financial_year ~ '^[0-9]{4}-[0-9]{2}$'),
    CONSTRAINT ck_invoice_series_prefix CHECK (prefix ~ '^[A-Z0-9/-]{1,14}$'),
    CONSTRAINT ck_invoice_series_width  CHECK (width BETWEEN 3 AND 8),
    CONSTRAINT ck_invoice_series_number CHECK (last_number >= 1),
    CONSTRAINT ux_invoice_series        UNIQUE (property_id, document_type, financial_year)
);

-- A series only ever moves on by one; nothing else about it changes.
CREATE FUNCTION fn_invoice_series_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.last_number <> OLD.last_number + 1
       OR ROW(NEW.organization_id, NEW.property_id, NEW.document_type, NEW.financial_year, NEW.prefix, NEW.width, NEW.created_at)
          IS DISTINCT FROM ROW(OLD.organization_id, OLD.property_id, OLD.document_type, OLD.financial_year, OLD.prefix, OLD.width, OLD.created_at) THEN
        RAISE EXCEPTION 'A document series only moves on by one'
            USING ERRCODE = 'insufficient_privilege', CONSTRAINT = 'ck_invoice_series_gap_free';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_invoice_series_guard BEFORE UPDATE ON invoice_series
    FOR EACH ROW EXECUTE FUNCTION fn_invoice_series_guard();
CREATE TRIGGER trg_invoice_series_no_delete BEFORE DELETE ON invoice_series
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- invoices
-- ---------------------------------------------------------------------
CREATE TABLE invoices (
    id                    uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id       uuid          NOT NULL REFERENCES organizations (id),
    property_id           uuid          NOT NULL REFERENCES properties (id),
    series_id             uuid          NOT NULL REFERENCES invoice_series (id),
    document_type         text          NOT NULL,
    number                text          NOT NULL,
    financial_year        text          NOT NULL,
    invoice_date          date          NOT NULL,
    folio_id              uuid          NOT NULL,
    reservation_id        uuid          NOT NULL,
    gst_registration_id   uuid          NOT NULL REFERENCES gst_registrations (id),
    supplier_gstin        char(15)      NOT NULL,
    supplier_legal_name   text          NOT NULL,
    supplier_trade_name   text          NULL,
    supplier_address      text          NULL,
    supplier_state_code   char(2)       NOT NULL,
    bill_to_guest_id      uuid          NULL REFERENCES guests (id),
    customer_name         text          NOT NULL,
    customer_gstin        char(15)      NULL,
    customer_address      text          NULL,
    customer_state_code   char(2)       NULL,
    place_of_supply       char(2)       NOT NULL,
    taxable_value         numeric(12,2) NOT NULL,
    cgst                  numeric(12,2) NOT NULL,
    sgst                  numeric(12,2) NOT NULL,
    igst                  numeric(12,2) NOT NULL,
    cess                  numeric(12,2) NOT NULL,
    tax_total             numeric(12,2) NOT NULL,
    total                 numeric(12,2) NOT NULL,
    currency              char(3)       NOT NULL,
    notes                 text          NULL,
    issued_at             timestamptz   NOT NULL DEFAULT now(),
    issued_by             uuid          NULL,
    CONSTRAINT ck_invoices_type        CHECK (document_type IN ('INVOICE','BILL_OF_SUPPLY')),
    CONSTRAINT ck_invoices_number      CHECK (number ~ '^[A-Z0-9/-]{1,16}$'),
    CONSTRAINT ck_invoices_customer    CHECK (length(btrim(customer_name)) BETWEEN 1 AND 200),
    CONSTRAINT ck_invoices_gstin       CHECK (customer_gstin IS NULL OR customer_gstin ~ '^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$'),
    CONSTRAINT ck_invoices_cust_state  CHECK (customer_gstin IS NULL OR customer_state_code = left(customer_gstin, 2)),
    CONSTRAINT ck_invoices_notes       CHECK (notes IS NULL OR length(notes) <= 500),
    CONSTRAINT ck_invoices_totals      CHECK (tax_total = cgst + sgst + igst + cess AND total = taxable_value + tax_total),
    -- a bill of supply (composition scheme) carries no tax
    CONSTRAINT ck_invoices_supply      CHECK (document_type = 'INVOICE' OR tax_total = 0),
    CONSTRAINT fk_invoices_folio       FOREIGN KEY (folio_id, property_id) REFERENCES folios (id, property_id),
    CONSTRAINT fk_invoices_reservation FOREIGN KEY (reservation_id, property_id) REFERENCES reservations (id, property_id),
    CONSTRAINT ux_invoices_number      UNIQUE (property_id, number)
);
CREATE INDEX ix_invoices_folio       ON invoices (folio_id);
CREATE INDEX ix_invoices_reservation ON invoices (reservation_id);
CREATE INDEX ix_invoices_date        ON invoices (property_id, invoice_date DESC);

CREATE TABLE invoice_items (
    id                uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id   uuid          NOT NULL REFERENCES organizations (id),
    property_id       uuid          NOT NULL REFERENCES properties (id),
    invoice_id        uuid          NOT NULL REFERENCES invoices (id),
    line_no           smallint      NOT NULL,
    folio_item_id     uuid          NOT NULL REFERENCES folio_items (id),
    category          text          NOT NULL,
    description       text          NOT NULL,
    hsn_sac           text          NULL,
    service_date      date          NOT NULL,
    quantity          numeric(12,3) NOT NULL,
    unit_price        numeric(12,2) NOT NULL,
    gross_amount      numeric(12,2) NOT NULL,
    discount          numeric(12,2) NOT NULL DEFAULT 0,
    taxable_value     numeric(12,2) NOT NULL,
    tax_amount        numeric(12,2) NOT NULL,
    total             numeric(12,2) NOT NULL,
    CONSTRAINT ck_invoice_items_amounts CHECK (
        quantity > 0 AND unit_price >= 0 AND gross_amount = round(quantity * unit_price, 2) AND discount >= 0
        AND taxable_value = gross_amount - discount AND taxable_value > 0 AND tax_amount >= 0 AND total = taxable_value + tax_amount),
    CONSTRAINT ux_invoice_items_line  UNIQUE (invoice_id, line_no),
    CONSTRAINT ux_invoice_items_charge UNIQUE (invoice_id, folio_item_id)
);
CREATE INDEX ix_invoice_items_folio_item ON invoice_items (folio_item_id);

CREATE TABLE invoice_item_taxes (
    id                uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id   uuid          NOT NULL REFERENCES organizations (id),
    property_id       uuid          NOT NULL REFERENCES properties (id),
    invoice_item_id   uuid          NOT NULL REFERENCES invoice_items (id),
    tax_rule_id       uuid          NULL REFERENCES tax_rules (id),
    tax_code          text          NOT NULL,
    rate              numeric(5,2)  NOT NULL,
    taxable_value     numeric(12,2) NOT NULL,
    amount            numeric(12,2) NOT NULL,
    CONSTRAINT ck_invoice_item_taxes_code   CHECK (tax_code IN ('CGST','SGST','IGST','CESS')),
    CONSTRAINT ck_invoice_item_taxes_values CHECK (rate >= 0 AND rate <= 100 AND taxable_value >= 0 AND amount >= 0)
);
CREATE INDEX ix_invoice_item_taxes_item ON invoice_item_taxes (invoice_item_id);

-- ---------------------------------------------------------------------
-- debit_notes : charges posted after the invoice
-- ---------------------------------------------------------------------
CREATE TABLE debit_notes (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    series_id        uuid          NOT NULL REFERENCES invoice_series (id),
    number           text          NOT NULL,
    financial_year   text          NOT NULL,
    note_date        date          NOT NULL,
    invoice_id       uuid          NOT NULL REFERENCES invoices (id),
    reason           text          NOT NULL,
    taxable_value    numeric(12,2) NOT NULL,
    cgst             numeric(12,2) NOT NULL,
    sgst             numeric(12,2) NOT NULL,
    igst             numeric(12,2) NOT NULL,
    cess             numeric(12,2) NOT NULL,
    tax_total        numeric(12,2) NOT NULL,
    total            numeric(12,2) NOT NULL,
    currency         char(3)       NOT NULL,
    issued_at        timestamptz   NOT NULL DEFAULT now(),
    issued_by        uuid          NULL,
    CONSTRAINT ck_debit_notes_number CHECK (number ~ '^[A-Z0-9/-]{1,16}$'),
    CONSTRAINT ck_debit_notes_reason CHECK (length(btrim(reason)) BETWEEN 3 AND 500),
    CONSTRAINT ck_debit_notes_totals CHECK (tax_total = cgst + sgst + igst + cess AND total = taxable_value + tax_total AND taxable_value > 0),
    CONSTRAINT ux_debit_notes_number UNIQUE (property_id, number)
);
CREATE INDEX ix_debit_notes_invoice ON debit_notes (invoice_id);

CREATE TABLE debit_note_items (
    id                uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id   uuid          NOT NULL REFERENCES organizations (id),
    property_id       uuid          NOT NULL REFERENCES properties (id),
    debit_note_id     uuid          NOT NULL REFERENCES debit_notes (id),
    line_no           smallint      NOT NULL,
    folio_item_id     uuid          NOT NULL REFERENCES folio_items (id),
    category          text          NOT NULL,
    description       text          NOT NULL,
    hsn_sac           text          NULL,
    service_date      date          NOT NULL,
    quantity          numeric(12,3) NOT NULL,
    unit_price        numeric(12,2) NOT NULL,
    gross_amount      numeric(12,2) NOT NULL,
    discount          numeric(12,2) NOT NULL DEFAULT 0,
    taxable_value     numeric(12,2) NOT NULL,
    tax_amount        numeric(12,2) NOT NULL,
    total             numeric(12,2) NOT NULL,
    CONSTRAINT ck_debit_note_items_amounts CHECK (
        quantity > 0 AND unit_price >= 0 AND gross_amount = round(quantity * unit_price, 2) AND discount >= 0
        AND taxable_value = gross_amount - discount AND taxable_value > 0 AND tax_amount >= 0 AND total = taxable_value + tax_amount),
    CONSTRAINT ux_debit_note_items_line UNIQUE (debit_note_id, line_no)
);
CREATE INDEX ix_debit_note_items_folio_item ON debit_note_items (folio_item_id);

CREATE TABLE debit_note_item_taxes (
    id                  uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id     uuid          NOT NULL REFERENCES organizations (id),
    property_id         uuid          NOT NULL REFERENCES properties (id),
    debit_note_item_id  uuid          NOT NULL REFERENCES debit_note_items (id),
    tax_rule_id         uuid          NULL REFERENCES tax_rules (id),
    tax_code            text          NOT NULL,
    rate                numeric(5,2)  NOT NULL,
    taxable_value       numeric(12,2) NOT NULL,
    amount              numeric(12,2) NOT NULL,
    CONSTRAINT ck_debit_note_item_taxes_code   CHECK (tax_code IN ('CGST','SGST','IGST','CESS')),
    CONSTRAINT ck_debit_note_item_taxes_values CHECK (rate >= 0 AND rate <= 100 AND taxable_value >= 0 AND amount >= 0)
);
CREATE INDEX ix_debit_note_item_taxes_item ON debit_note_item_taxes (debit_note_item_id);

-- ---------------------------------------------------------------------
-- credit_notes : the only way to cancel or reduce an invoice
-- ---------------------------------------------------------------------
CREATE TABLE credit_notes (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    series_id        uuid          NOT NULL REFERENCES invoice_series (id),
    number           text          NOT NULL,
    financial_year   text          NOT NULL,
    note_date        date          NOT NULL,
    invoice_id       uuid          NOT NULL REFERENCES invoices (id),
    note_type        text          NOT NULL,
    reason           text          NOT NULL,
    taxable_value    numeric(12,2) NOT NULL,
    cgst             numeric(12,2) NOT NULL,
    sgst             numeric(12,2) NOT NULL,
    igst             numeric(12,2) NOT NULL,
    cess             numeric(12,2) NOT NULL,
    tax_total        numeric(12,2) NOT NULL,
    total            numeric(12,2) NOT NULL,
    currency         char(3)       NOT NULL,
    issued_at        timestamptz   NOT NULL DEFAULT now(),
    issued_by        uuid          NULL,
    CONSTRAINT ck_credit_notes_type   CHECK (note_type IN ('CANCEL','REDUCE')),
    CONSTRAINT ck_credit_notes_number CHECK (number ~ '^[A-Z0-9/-]{1,16}$'),
    CONSTRAINT ck_credit_notes_reason CHECK (length(btrim(reason)) BETWEEN 3 AND 500),
    CONSTRAINT ck_credit_notes_totals CHECK (tax_total = cgst + sgst + igst + cess AND total = taxable_value + tax_total AND taxable_value > 0),
    CONSTRAINT ux_credit_notes_number UNIQUE (property_id, number)
);
CREATE INDEX ix_credit_notes_invoice ON credit_notes (invoice_id);
-- an invoice is cancelled once
CREATE UNIQUE INDEX ux_credit_notes_cancel ON credit_notes (invoice_id) WHERE note_type = 'CANCEL';

-- ---------------------------------------------------------------------
-- folio_items (V010): a REDUCE credit note puts a CREDIT line on the folio (negative, pointing at the charge it reduces)
-- ---------------------------------------------------------------------
ALTER TABLE folio_items ADD COLUMN adjusts_item_id uuid NULL REFERENCES folio_items (id);
ALTER TABLE folio_items ADD COLUMN credit_note_id  uuid NULL REFERENCES credit_notes (id);
ALTER TABLE folio_items DROP CONSTRAINT ck_folio_items_type;
ALTER TABLE folio_items ADD CONSTRAINT ck_folio_items_type CHECK (item_type IN ('CHARGE','REVERSAL','CREDIT'));
ALTER TABLE folio_items DROP CONSTRAINT ck_folio_items_amounts;
ALTER TABLE folio_items ADD CONSTRAINT ck_folio_items_amounts CHECK (
    unit_price >= 0 AND amount = round(quantity * unit_price, 2) AND total = amount + tax_amount
    AND ((item_type = 'CHARGE'   AND quantity > 0 AND tax_amount >= 0 AND reverses_item_id IS NULL AND adjusts_item_id IS NULL)
      OR (item_type = 'REVERSAL' AND quantity < 0 AND tax_amount <= 0 AND reverses_item_id IS NOT NULL AND reason IS NOT NULL
                                 AND adjusts_item_id IS NULL)
      OR (item_type = 'CREDIT'   AND quantity = -1 AND tax_amount <= 0 AND reverses_item_id IS NULL AND reason IS NOT NULL
                                 AND adjusts_item_id IS NOT NULL AND credit_note_id IS NOT NULL)));
CREATE INDEX ix_folio_items_adjusts ON folio_items (adjusts_item_id) WHERE adjusts_item_id IS NOT NULL;

CREATE OR REPLACE FUNCTION fn_folio_items_check() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    f        record;
    o        record;
    credited numeric;
    tax_back numeric;
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
        IF EXISTS (SELECT 1 FROM folio_items WHERE adjusts_item_id = o.id) THEN
            RAISE EXCEPTION 'A charge reduced by a credit note can''t be reversed'
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_folio_items_reversal_credited';
        END IF;
    ELSIF NEW.item_type = 'CREDIT' THEN
        SELECT * INTO o FROM folio_items WHERE id = NEW.adjusts_item_id;
        SELECT coalesce(sum(amount), 0), coalesce(sum(tax_amount), 0) INTO credited, tax_back
          FROM folio_items WHERE adjusts_item_id = NEW.adjusts_item_id;
        IF o.item_type IS DISTINCT FROM 'CHARGE' OR o.folio_id <> NEW.folio_id OR NEW.category <> o.category
           OR o.amount + credited + NEW.amount < 0 OR o.tax_amount + tax_back + NEW.tax_amount < 0 THEN
            RAISE EXCEPTION 'A credit line must reduce a charge on the same folio by no more than it is'
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_folio_items_credit';
        END IF;
    END IF;
    RETURN NEW;
END $$;

CREATE TABLE credit_note_items (
    id                   uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id      uuid          NOT NULL REFERENCES organizations (id),
    property_id          uuid          NOT NULL REFERENCES properties (id),
    credit_note_id       uuid          NOT NULL REFERENCES credit_notes (id),
    line_no              smallint      NOT NULL,
    invoice_item_id      uuid          NULL REFERENCES invoice_items (id),
    debit_note_item_id   uuid          NULL REFERENCES debit_note_items (id),
    folio_item_id        uuid          NOT NULL REFERENCES folio_items (id),
    folio_credit_item_id uuid          NULL REFERENCES folio_items (id),
    category             text          NOT NULL,
    description          text          NOT NULL,
    hsn_sac              text          NULL,
    taxable_value        numeric(12,2) NOT NULL,
    tax_amount           numeric(12,2) NOT NULL,
    total                numeric(12,2) NOT NULL,
    CONSTRAINT ck_credit_note_items_source  CHECK ((invoice_item_id IS NULL) <> (debit_note_item_id IS NULL)),
    CONSTRAINT ck_credit_note_items_amounts CHECK (taxable_value > 0 AND tax_amount >= 0 AND total = taxable_value + tax_amount),
    CONSTRAINT ux_credit_note_items_line    UNIQUE (credit_note_id, line_no)
);
CREATE INDEX ix_credit_note_items_invoice_item ON credit_note_items (invoice_item_id);
CREATE INDEX ix_credit_note_items_debit_item   ON credit_note_items (debit_note_item_id);

CREATE TABLE credit_note_item_taxes (
    id                   uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id      uuid          NOT NULL REFERENCES organizations (id),
    property_id          uuid          NOT NULL REFERENCES properties (id),
    credit_note_item_id  uuid          NOT NULL REFERENCES credit_note_items (id),
    tax_rule_id          uuid          NULL REFERENCES tax_rules (id),
    tax_code             text          NOT NULL,
    rate                 numeric(5,2)  NOT NULL,
    taxable_value        numeric(12,2) NOT NULL,
    amount               numeric(12,2) NOT NULL,
    CONSTRAINT ck_credit_note_item_taxes_code   CHECK (tax_code IN ('CGST','SGST','IGST','CESS')),
    CONSTRAINT ck_credit_note_item_taxes_values CHECK (rate >= 0 AND rate <= 100 AND taxable_value >= 0 AND amount >= 0)
);
CREATE INDEX ix_credit_note_item_taxes_item ON credit_note_item_taxes (credit_note_item_id);

-- Never more credited on a line than it billed (value and tax).
CREATE FUNCTION fn_credit_note_items_limit() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    billed_value numeric;
    billed_tax   numeric;
    done_value   numeric;
    done_tax     numeric;
BEGIN
    IF NEW.invoice_item_id IS NOT NULL THEN
        SELECT taxable_value, tax_amount INTO billed_value, billed_tax FROM invoice_items WHERE id = NEW.invoice_item_id;
        SELECT coalesce(sum(taxable_value), 0), coalesce(sum(tax_amount), 0) INTO done_value, done_tax
          FROM credit_note_items WHERE invoice_item_id = NEW.invoice_item_id;
    ELSE
        SELECT taxable_value, tax_amount INTO billed_value, billed_tax FROM debit_note_items WHERE id = NEW.debit_note_item_id;
        SELECT coalesce(sum(taxable_value), 0), coalesce(sum(tax_amount), 0) INTO done_value, done_tax
          FROM credit_note_items WHERE debit_note_item_id = NEW.debit_note_item_id;
    END IF;
    IF done_value + NEW.taxable_value > billed_value OR done_tax + NEW.tax_amount > billed_tax THEN
        RAISE EXCEPTION 'A credit note can''t credit more than the line billed'
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_credit_note_items_limit';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_credit_note_items_limit BEFORE INSERT ON credit_note_items
    FOR EACH ROW EXECUTE FUNCTION fn_credit_note_items_limit();

-- ---------------------------------------------------------------------
-- Header totals add up to the lines, and each line's tax to its tax rows (checked when the transaction commits).
-- ---------------------------------------------------------------------
CREATE FUNCTION fn_document_totals_add_up() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    items   text := TG_ARGV[0];   -- invoice_items | credit_note_items | debit_note_items
    taxes   text := TG_ARGV[1];   -- invoice_item_taxes | …
    fk      text := TG_ARGV[2];   -- invoice_id | credit_note_id | debit_note_id
    tax_fk  text := TG_ARGV[3];   -- invoice_item_id | …
    lines   record;
    sums    record;
    bad     integer;
BEGIN
    EXECUTE format('SELECT count(*) AS n, coalesce(sum(taxable_value), 0) AS value, coalesce(sum(tax_amount), 0) AS tax, coalesce(sum(total), 0) AS total
                      FROM %I WHERE %I = $1', items, fk) INTO lines USING NEW.id;
    EXECUTE format('SELECT coalesce(sum(t.amount) FILTER (WHERE t.tax_code = ''CGST''), 0) AS cgst,
                           coalesce(sum(t.amount) FILTER (WHERE t.tax_code = ''SGST''), 0) AS sgst,
                           coalesce(sum(t.amount) FILTER (WHERE t.tax_code = ''IGST''), 0) AS igst,
                           coalesce(sum(t.amount) FILTER (WHERE t.tax_code = ''CESS''), 0) AS cess
                      FROM %I t JOIN %I i ON i.id = t.%I WHERE i.%I = $1', taxes, items, tax_fk, fk) INTO sums USING NEW.id;
    EXECUTE format('SELECT count(*) FROM %I i WHERE i.%I = $1
                       AND i.tax_amount <> (SELECT coalesce(sum(t.amount), 0) FROM %I t WHERE t.%I = i.id)', items, fk, taxes, tax_fk)
        INTO bad USING NEW.id;
    IF lines.n = 0 OR lines.value <> NEW.taxable_value OR lines.tax <> NEW.tax_total OR lines.total <> NEW.total
       OR sums.cgst <> NEW.cgst OR sums.sgst <> NEW.sgst OR sums.igst <> NEW.igst OR sums.cess <> NEW.cess OR bad > 0 THEN
        RAISE EXCEPTION '% % totals don''t add up to its lines', TG_TABLE_NAME, NEW.number
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_' || TG_TABLE_NAME || '_add_up';
    END IF;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER trg_invoices_add_up AFTER INSERT ON invoices DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION fn_document_totals_add_up('invoice_items', 'invoice_item_taxes', 'invoice_id', 'invoice_item_id');
CREATE CONSTRAINT TRIGGER trg_credit_notes_add_up AFTER INSERT ON credit_notes DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION fn_document_totals_add_up('credit_note_items', 'credit_note_item_taxes', 'credit_note_id', 'credit_note_item_id');
CREATE CONSTRAINT TRIGGER trg_debit_notes_add_up AFTER INSERT ON debit_notes DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION fn_document_totals_add_up('debit_note_items', 'debit_note_item_taxes', 'debit_note_id', 'debit_note_item_id');

-- ---------------------------------------------------------------------
-- receipts : one per payment
-- ---------------------------------------------------------------------
CREATE TABLE receipts (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    series_id        uuid          NOT NULL REFERENCES invoice_series (id),
    number           text          NOT NULL,
    financial_year   text          NOT NULL,
    receipt_date     date          NOT NULL,
    payment_id       uuid          NOT NULL REFERENCES payments (id),
    reservation_id   uuid          NOT NULL,
    received_from    text          NOT NULL,
    amount           numeric(12,2) NOT NULL,
    method           text          NOT NULL,
    currency         char(3)       NOT NULL,
    issued_at        timestamptz   NOT NULL DEFAULT now(),
    issued_by        uuid          NULL,
    CONSTRAINT ck_receipts_number      CHECK (number ~ '^[A-Z0-9/-]{1,20}$'),
    CONSTRAINT ck_receipts_amount      CHECK (amount > 0),
    CONSTRAINT ck_receipts_from        CHECK (length(btrim(received_from)) BETWEEN 1 AND 200),
    CONSTRAINT fk_receipts_reservation FOREIGN KEY (reservation_id, property_id) REFERENCES reservations (id, property_id),
    CONSTRAINT ux_receipts_number      UNIQUE (property_id, number),
    CONSTRAINT ux_receipts_payment     UNIQUE (payment_id)
);
CREATE INDEX ix_receipts_reservation ON receipts (reservation_id);

-- ---------------------------------------------------------------------
-- einvoice_records : IRN and signed QR from the invoice registration portal (Sprint 25 fills it)
-- ---------------------------------------------------------------------
CREATE TABLE einvoice_records (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    document_type    text        NOT NULL,
    document_id      uuid        NOT NULL,
    status           text        NOT NULL DEFAULT 'PENDING',
    irn              char(64)    NULL,
    ack_no           text        NULL,
    ack_date         timestamptz NULL,
    signed_qr        text        NULL,
    signed_invoice   text        NULL,
    provider         text        NULL,
    attempts         integer     NOT NULL DEFAULT 0,
    last_error       text        NULL,
    cancelled_at     timestamptz NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_einvoice_records_type   CHECK (document_type IN ('INVOICE','CREDIT_NOTE','DEBIT_NOTE')),
    CONSTRAINT ck_einvoice_records_status CHECK (status IN ('PENDING','GENERATED','FAILED','CANCELLED')),
    CONSTRAINT ck_einvoice_records_irn    CHECK (irn IS NULL OR irn ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_einvoice_records_done   CHECK (status <> 'GENERATED' OR (irn IS NOT NULL AND ack_no IS NOT NULL AND ack_date IS NOT NULL AND signed_qr IS NOT NULL)),
    CONSTRAINT ux_einvoice_records_doc    UNIQUE (document_type, document_id)
);
CREATE TRIGGER trg_einvoice_records_no_delete BEFORE DELETE ON einvoice_records
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- Issued documents are never changed or deleted.
-- ---------------------------------------------------------------------
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['invoices','invoice_items','invoice_item_taxes','credit_notes','credit_note_items','credit_note_item_taxes',
                             'debit_notes','debit_note_items','debit_note_item_taxes','receipts'] LOOP
        EXECUTE format('CREATE TRIGGER trg_%s_no_delete BEFORE DELETE ON %I FOR EACH ROW EXECUTE FUNCTION fn_reject_delete()', t, t);
        EXECUTE format('CREATE TRIGGER trg_%s_no_update BEFORE UPDATE ON %I FOR EACH ROW EXECUTE FUNCTION fn_reject_update()', t, t);
    END LOOP;
END $$;

-- Data classification (brief §59).
COMMENT ON TABLE invoice_series         IS 'class: internal. Running numbers per property, document type and financial year.';
COMMENT ON TABLE invoices               IS 'class: confidential. GST invoices / bills of supply; immutable, corrected by credit notes.';
COMMENT ON TABLE invoice_items          IS 'class: confidential. Invoice lines copied from folio charges; immutable.';
COMMENT ON TABLE invoice_item_taxes     IS 'class: confidential. Tax rows of invoice lines, copied from the folio; immutable.';
COMMENT ON TABLE credit_notes           IS 'class: confidential. Cancel or reduce an invoice, with a reason; immutable.';
COMMENT ON TABLE credit_note_items      IS 'class: confidential. Lines a credit note credits; immutable.';
COMMENT ON TABLE credit_note_item_taxes IS 'class: confidential. Tax credited per line, at the invoice rates; immutable.';
COMMENT ON TABLE debit_notes            IS 'class: confidential. Charges added to an invoice after it was issued; immutable.';
COMMENT ON TABLE debit_note_items       IS 'class: confidential. Lines of a debit note; immutable.';
COMMENT ON TABLE debit_note_item_taxes  IS 'class: confidential. Tax rows of debit note lines; immutable.';
COMMENT ON TABLE receipts               IS 'class: confidential. One receipt per payment; immutable.';
COMMENT ON TABLE einvoice_records       IS 'class: confidential. IRN / acknowledgement / signed QR per document (Sprint 25).';
