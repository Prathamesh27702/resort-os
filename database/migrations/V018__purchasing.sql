-- =====================================================================
-- V018  Suppliers and purchasing (Sprint 16, brief §38, §58)
--
-- The purchase cycle: a purchase request (what a store needs) → a purchase order to a supplier (approved when it is over
-- the property's approval limit) → goods received against the order (stock goes into the store through the stock
-- ledger) → the supplier's invoice with its GST (the input tax) → payments until it is paid.
--
-- Model
--   suppliers                who the property buys from: code, name, GSTIN (optional: an unregistered supplier charges
--                            no GST), the state they supply from (the GSTIN's first two digits when there is one),
--                            contact details and payment terms in days.
--   purchase_requests        what a store needs, numbered PR00001 … per property. OPEN until a purchase order is raised
--                            from it (ORDERED) or it is cancelled with a reason (CANCELLED).
--   purchase_request_items   one stock item and how much, in the item's unit.
--   purchase_orders          an order to one supplier for one store, numbered PO00001 … per property, with its totals before
--                            and after GST. DRAFT → (submit) PENDING_APPROVAL → APPROVED → PARTLY_RECEIVED → RECEIVED;
--                            an order at or under the approval limit is APPROVED when it is submitted. A rejected order goes
--                            back to DRAFT with the reason; an approved order with nothing received can be CANCELLED; a
--                            partly received one can be CLOSED (the rest is not coming), each with a reason.
--   purchase_order_items     one stock item: quantity (item's unit), price of one unit before GST, GST rate, and how much
--                            has been received and billed so far (billed ≤ received ≤ ordered, checked here).
--   goods_received_notes     a delivery against an order, numbered GRN00001 … per property, into a store, with the
--                            supplier's delivery challan number. Never changed: a mistake is corrected in stock.
--   grn_items                what came in for one order line. Each is one RECEIVED stock movement at the order's price.
--   supplier_invoices        the supplier's bill against an order, numbered SI00001 … per property (the supplier's own number
--                            is kept and can be recorded once per supplier). Lines and GST are fixed once saved; amount_paid
--                            and status move with payments (UNPAID → PART_PAID → PAID). Input tax = CGST + SGST + IGST;
--                            itc_eligible: the property was a regular GST registrant on the invoice date (input tax can be
--                            claimed).
--   supplier_invoice_items   one billed order line: quantity, price, GST rate, taxable value and taxes.
--   supplier_payments        money paid against an invoice, numbered SP00001 … per property. Never changed. Cash comes out
--                            of the open cash drawer (cash_drawer_movements PAY_OUT, linked to the payment).
--
-- Settings read (property_settings; defaults in code)
--   purchasing.approval_limit  orders whose total (with GST) is over this need approval by someone with purchase.approve
--                              other than the person who raised them. Default ₹10,000.
-- =====================================================================

-- ---------------------------------------------------------------------
-- suppliers
-- ---------------------------------------------------------------------
CREATE TABLE suppliers (
    id                  uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id     uuid        NOT NULL REFERENCES organizations (id),
    property_id         uuid        NOT NULL REFERENCES properties (id),
    code                text        NOT NULL,
    name                text        NOT NULL,
    gstin               char(15)    NULL,
    state_code          char(2)     NOT NULL,
    contact_name        text        NULL,
    phone               text        NULL,
    email               text        NULL,
    address             text        NULL,
    payment_terms_days  integer     NOT NULL DEFAULT 0,
    note                text        NULL,
    is_active           boolean     NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    created_by          uuid        NULL,
    updated_by          uuid        NULL,
    CONSTRAINT ck_suppliers_code    CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,19}$'),
    CONSTRAINT ck_suppliers_name    CHECK (length(btrim(name)) BETWEEN 2 AND 120),
    CONSTRAINT ck_suppliers_gstin   CHECK (gstin IS NULL OR (gstin ~ '^[0-9]{2}[A-Z0-9]{13}$' AND left(gstin, 2) = state_code)),
    CONSTRAINT ck_suppliers_state   CHECK (state_code ~ '^[0-9]{2}$'),
    CONSTRAINT ck_suppliers_terms   CHECK (payment_terms_days BETWEEN 0 AND 180),
    CONSTRAINT ux_suppliers_code    UNIQUE (property_id, code),
    CONSTRAINT ux_suppliers_id_prop UNIQUE (id, property_id)
);
CREATE UNIQUE INDEX ux_suppliers_gstin ON suppliers (property_id, gstin) WHERE gstin IS NOT NULL;
CREATE TRIGGER trg_suppliers_no_delete BEFORE DELETE ON suppliers
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- purchase_requests / purchase_request_items
-- ---------------------------------------------------------------------
CREATE TABLE purchase_requests (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid        NOT NULL REFERENCES organizations (id),
    property_id        uuid        NOT NULL REFERENCES properties (id),
    number             text        NOT NULL,
    location_id        uuid        NOT NULL,
    needed_by          date        NULL,
    note               text        NULL,
    status             text        NOT NULL DEFAULT 'OPEN',
    purchase_order_id  uuid        NULL,
    cancel_reason      text        NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    created_by         uuid        NULL REFERENCES users (id),
    updated_by         uuid        NULL,
    CONSTRAINT ck_purchase_requests_number   CHECK (number ~ '^PR[0-9]{5,}$'),
    CONSTRAINT ck_purchase_requests_status   CHECK (status IN ('OPEN','ORDERED','CANCELLED')),
    CONSTRAINT ck_purchase_requests_note     CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 500),
    CONSTRAINT ck_purchase_requests_ordered  CHECK ((status = 'ORDERED') = (purchase_order_id IS NOT NULL)),
    CONSTRAINT ck_purchase_requests_cancel   CHECK ((status = 'CANCELLED') = (cancel_reason IS NOT NULL)
                                               AND (cancel_reason IS NULL OR length(btrim(cancel_reason)) BETWEEN 3 AND 300)),
    CONSTRAINT fk_purchase_requests_location FOREIGN KEY (location_id, property_id) REFERENCES stock_locations (id, property_id),
    CONSTRAINT ux_purchase_requests_number   UNIQUE (property_id, number),
    CONSTRAINT ux_purchase_requests_id_prop  UNIQUE (id, property_id)
);
CREATE INDEX ix_purchase_requests_status ON purchase_requests (property_id, status, created_at DESC);
CREATE TRIGGER trg_purchase_requests_no_delete BEFORE DELETE ON purchase_requests
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

CREATE TABLE purchase_request_items (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    request_id       uuid          NOT NULL REFERENCES purchase_requests (id),
    item_id          uuid          NOT NULL REFERENCES inventory_items (id),
    quantity         numeric(14,3) NOT NULL,
    note             text          NULL,
    sort_order       integer       NOT NULL DEFAULT 0,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_purchase_request_items_quantity CHECK (quantity > 0),
    CONSTRAINT ck_purchase_request_items_note     CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 200),
    CONSTRAINT ux_purchase_request_items_item     UNIQUE (request_id, item_id)
);
CREATE TRIGGER trg_purchase_request_items_append_only BEFORE UPDATE OR DELETE ON purchase_request_items
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- ---------------------------------------------------------------------
-- purchase_orders / purchase_order_items
-- ---------------------------------------------------------------------
CREATE TABLE purchase_orders (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    number           text          NOT NULL,
    supplier_id      uuid          NOT NULL,
    request_id       uuid          NULL,
    location_id      uuid          NOT NULL,
    order_date       date          NOT NULL,
    expected_date    date          NULL,
    status           text          NOT NULL DEFAULT 'DRAFT',
    intra_state      boolean       NOT NULL,
    note             text          NULL,
    subtotal         numeric(14,2) NOT NULL DEFAULT 0,
    tax_total        numeric(14,2) NOT NULL DEFAULT 0,
    total            numeric(14,2) NOT NULL DEFAULT 0,
    approval_limit   numeric(14,2) NULL,
    submitted_at     timestamptz   NULL,
    submitted_by     uuid          NULL REFERENCES users (id),
    approved_at      timestamptz   NULL,
    approved_by      uuid          NULL REFERENCES users (id),
    approval_note    text          NULL,
    rejected_reason  text          NULL,
    end_reason       text          NULL,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    updated_at       timestamptz   NOT NULL DEFAULT now(),
    created_by       uuid          NULL REFERENCES users (id),
    updated_by       uuid          NULL,
    CONSTRAINT ck_purchase_orders_number   CHECK (number ~ '^PO[0-9]{5,}$'),
    CONSTRAINT ck_purchase_orders_status   CHECK (status IN ('DRAFT','PENDING_APPROVAL','APPROVED','PARTLY_RECEIVED','RECEIVED','CLOSED','CANCELLED')),
    CONSTRAINT ck_purchase_orders_totals   CHECK (subtotal >= 0 AND tax_total >= 0 AND total = subtotal + tax_total),
    CONSTRAINT ck_purchase_orders_dates    CHECK (expected_date IS NULL OR expected_date >= order_date),
    CONSTRAINT ck_purchase_orders_note     CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 500),
    -- Submitted once it left DRAFT; approved once it can be received; a cancelled or closed order says why.
    CONSTRAINT ck_purchase_orders_submit   CHECK (status = 'DRAFT' OR status = 'CANCELLED' OR submitted_at IS NOT NULL),
    CONSTRAINT ck_purchase_orders_approved CHECK ((status IN ('APPROVED','PARTLY_RECEIVED','RECEIVED','CLOSED')) <= (approved_at IS NOT NULL)),
    CONSTRAINT ck_purchase_orders_end      CHECK ((status IN ('CANCELLED','CLOSED')) = (end_reason IS NOT NULL)
                                             AND (end_reason IS NULL OR length(btrim(end_reason)) BETWEEN 3 AND 300)),
    CONSTRAINT fk_purchase_orders_supplier FOREIGN KEY (supplier_id, property_id) REFERENCES suppliers (id, property_id),
    CONSTRAINT fk_purchase_orders_request  FOREIGN KEY (request_id, property_id) REFERENCES purchase_requests (id, property_id),
    CONSTRAINT fk_purchase_orders_location FOREIGN KEY (location_id, property_id) REFERENCES stock_locations (id, property_id),
    CONSTRAINT ux_purchase_orders_number   UNIQUE (property_id, number),
    CONSTRAINT ux_purchase_orders_id_prop  UNIQUE (id, property_id)
);
CREATE INDEX ix_purchase_orders_status ON purchase_orders (property_id, status, created_at DESC);
CREATE INDEX ix_purchase_orders_supplier ON purchase_orders (supplier_id, created_at DESC);
-- A request becomes one order.
CREATE UNIQUE INDEX ux_purchase_orders_request ON purchase_orders (request_id) WHERE request_id IS NOT NULL AND status <> 'CANCELLED';
CREATE TRIGGER trg_purchase_orders_no_delete BEFORE DELETE ON purchase_orders
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

ALTER TABLE purchase_requests ADD CONSTRAINT fk_purchase_requests_order
    FOREIGN KEY (purchase_order_id, property_id) REFERENCES purchase_orders (id, property_id);

CREATE TABLE purchase_order_items (
    id                 uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid          NOT NULL REFERENCES organizations (id),
    order_id           uuid          NOT NULL REFERENCES purchase_orders (id),
    item_id            uuid          NOT NULL REFERENCES inventory_items (id),
    quantity           numeric(14,3) NOT NULL,
    unit_price         numeric(12,4) NOT NULL,
    gst_rate           numeric(5,2)  NOT NULL,
    taxable_value      numeric(14,2) NOT NULL,
    tax                numeric(14,2) NOT NULL,
    received_quantity  numeric(14,3) NOT NULL DEFAULT 0,
    billed_quantity    numeric(14,3) NOT NULL DEFAULT 0,
    sort_order         integer       NOT NULL DEFAULT 0,
    created_at         timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_purchase_order_items_quantity CHECK (quantity > 0 AND quantity <= 1000000),
    CONSTRAINT ck_purchase_order_items_price    CHECK (unit_price >= 0 AND unit_price <= 1000000),
    CONSTRAINT ck_purchase_order_items_rate     CHECK (gst_rate IN (0, 0.25, 3, 5, 12, 18, 28, 40)),
    CONSTRAINT ck_purchase_order_items_value    CHECK (taxable_value = round(quantity * unit_price, 2) AND tax >= 0),
    -- Nothing is received beyond the order, nor billed beyond what came in.
    CONSTRAINT ck_purchase_order_items_flow     CHECK (billed_quantity >= 0 AND billed_quantity <= received_quantity AND received_quantity <= quantity),
    CONSTRAINT ux_purchase_order_items_item     UNIQUE (order_id, item_id)
);
CREATE INDEX ix_purchase_order_items_item ON purchase_order_items (item_id);

-- ---------------------------------------------------------------------
-- goods_received_notes / grn_items : deliveries, never changed
-- ---------------------------------------------------------------------
CREATE TABLE goods_received_notes (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    number           text          NOT NULL,
    order_id         uuid          NOT NULL,
    supplier_id      uuid          NOT NULL,
    location_id      uuid          NOT NULL,
    received_on      date          NOT NULL,
    challan_number   text          NULL,
    note             text          NULL,
    value            numeric(14,2) NOT NULL,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    created_by       uuid          NULL REFERENCES users (id),
    CONSTRAINT ck_grn_number   CHECK (number ~ '^GRN[0-9]{5,}$'),
    CONSTRAINT ck_grn_challan  CHECK (challan_number IS NULL OR length(btrim(challan_number)) BETWEEN 1 AND 40),
    CONSTRAINT ck_grn_note     CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 300),
    CONSTRAINT ck_grn_value    CHECK (value >= 0),
    CONSTRAINT fk_grn_order    FOREIGN KEY (order_id, property_id) REFERENCES purchase_orders (id, property_id),
    CONSTRAINT fk_grn_supplier FOREIGN KEY (supplier_id, property_id) REFERENCES suppliers (id, property_id),
    CONSTRAINT fk_grn_location FOREIGN KEY (location_id, property_id) REFERENCES stock_locations (id, property_id),
    CONSTRAINT ux_grn_number   UNIQUE (property_id, number)
);
CREATE INDEX ix_grn_order ON goods_received_notes (order_id, created_at);
CREATE TRIGGER trg_goods_received_notes_append_only BEFORE UPDATE OR DELETE ON goods_received_notes
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

CREATE TABLE grn_items (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    grn_id           uuid          NOT NULL REFERENCES goods_received_notes (id),
    order_item_id    uuid          NOT NULL REFERENCES purchase_order_items (id),
    item_id          uuid          NOT NULL REFERENCES inventory_items (id),
    quantity         numeric(14,3) NOT NULL,
    unit_cost        numeric(12,4) NOT NULL,
    value            numeric(14,2) NOT NULL,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_grn_items_quantity CHECK (quantity > 0),
    CONSTRAINT ck_grn_items_value    CHECK (unit_cost >= 0 AND value = round(quantity * unit_cost, 2)),
    CONSTRAINT ux_grn_items_line     UNIQUE (grn_id, order_item_id)
);
CREATE INDEX ix_grn_items_order_item ON grn_items (order_item_id);
CREATE TRIGGER trg_grn_items_append_only BEFORE UPDATE OR DELETE ON grn_items
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- stock_movements: stock received is a person's adjustment (Sprint 15) or a goods received line.
ALTER TABLE stock_movements ADD COLUMN grn_item_id uuid NULL REFERENCES grn_items (id);
ALTER TABLE stock_movements DROP CONSTRAINT ck_stock_movements_source;
ALTER TABLE stock_movements ADD CONSTRAINT ck_stock_movements_source CHECK (
        (movement_type = 'CONSUMPTION') = (order_item_id IS NOT NULL)
    AND (movement_type <> 'COUNT' OR count_id IS NOT NULL)
    AND (movement_type <> 'RECEIVED' OR (adjustment_id IS NOT NULL) <> (grn_item_id IS NOT NULL))
    AND (grn_item_id IS NULL OR movement_type = 'RECEIVED')
    AND (movement_type NOT IN ('WASTAGE','ADJUSTMENT','TRANSFER_OUT','TRANSFER_IN') OR adjustment_id IS NOT NULL));
-- A goods received line is one movement.
CREATE UNIQUE INDEX ux_stock_movements_grn_item ON stock_movements (grn_item_id) WHERE grn_item_id IS NOT NULL;

-- ---------------------------------------------------------------------
-- supplier_invoices / supplier_invoice_items
-- ---------------------------------------------------------------------
CREATE TABLE supplier_invoices (
    id                   uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id      uuid          NOT NULL REFERENCES organizations (id),
    property_id          uuid          NOT NULL REFERENCES properties (id),
    number               text          NOT NULL,
    supplier_id          uuid          NOT NULL,
    order_id             uuid          NOT NULL,
    supplier_number      text          NOT NULL,
    invoice_date         date          NOT NULL,
    due_date             date          NOT NULL,
    supplier_gstin       char(15)      NULL,
    supplier_state_code  char(2)       NOT NULL,
    buyer_state_code     char(2)       NULL,
    intra_state          boolean       NOT NULL,
    itc_eligible         boolean       NOT NULL,
    taxable_value        numeric(14,2) NOT NULL,
    cgst                 numeric(14,2) NOT NULL DEFAULT 0,
    sgst                 numeric(14,2) NOT NULL DEFAULT 0,
    igst                 numeric(14,2) NOT NULL DEFAULT 0,
    tax_total            numeric(14,2) NOT NULL,
    total                numeric(14,2) NOT NULL,
    amount_paid          numeric(14,2) NOT NULL DEFAULT 0,
    status               text          NOT NULL DEFAULT 'UNPAID',
    note                 text          NULL,
    created_at           timestamptz   NOT NULL DEFAULT now(),
    updated_at           timestamptz   NOT NULL DEFAULT now(),
    created_by           uuid          NULL REFERENCES users (id),
    updated_by           uuid          NULL,
    CONSTRAINT ck_supplier_invoices_number   CHECK (number ~ '^SI[0-9]{5,}$'),
    CONSTRAINT ck_supplier_invoices_sup_no   CHECK (length(btrim(supplier_number)) BETWEEN 1 AND 40),
    CONSTRAINT ck_supplier_invoices_due      CHECK (due_date >= invoice_date),
    CONSTRAINT ck_supplier_invoices_tax      CHECK (tax_total = cgst + sgst + igst AND total = taxable_value + tax_total
                                               AND (intra_state OR (cgst = 0 AND sgst = 0)) AND (NOT intra_state OR igst = 0)),
    -- An unregistered supplier can't charge GST.
    CONSTRAINT ck_supplier_invoices_unreg    CHECK (supplier_gstin IS NOT NULL OR tax_total = 0),
    CONSTRAINT ck_supplier_invoices_paid     CHECK (amount_paid >= 0 AND amount_paid <= total),
    CONSTRAINT ck_supplier_invoices_status   CHECK (status IN ('UNPAID','PART_PAID','PAID')
                                               AND (status = 'PAID') = (amount_paid = total)
                                               AND (status = 'UNPAID') = (amount_paid = 0 AND total > 0)),
    CONSTRAINT ck_supplier_invoices_note     CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 300),
    CONSTRAINT fk_supplier_invoices_supplier FOREIGN KEY (supplier_id, property_id) REFERENCES suppliers (id, property_id),
    CONSTRAINT fk_supplier_invoices_order    FOREIGN KEY (order_id, property_id) REFERENCES purchase_orders (id, property_id),
    CONSTRAINT ux_supplier_invoices_number   UNIQUE (property_id, number),
    CONSTRAINT ux_supplier_invoices_id_prop  UNIQUE (id, property_id)
);
-- The same bill can't be recorded twice.
CREATE UNIQUE INDEX ux_supplier_invoices_supplier_number ON supplier_invoices (supplier_id, lower(btrim(supplier_number)));
CREATE INDEX ix_supplier_invoices_status ON supplier_invoices (property_id, status, due_date);
CREATE TRIGGER trg_supplier_invoices_no_delete BEFORE DELETE ON supplier_invoices
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- Once saved only what has been paid moves.
CREATE FUNCTION fn_supplier_invoices_fixed() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.supplier_id, NEW.order_id, NEW.number, NEW.supplier_number, NEW.invoice_date, NEW.due_date, NEW.supplier_gstin,
        NEW.supplier_state_code, NEW.intra_state, NEW.itc_eligible, NEW.taxable_value, NEW.cgst, NEW.sgst, NEW.igst,
        NEW.tax_total, NEW.total)
       IS DISTINCT FROM
       (OLD.supplier_id, OLD.order_id, OLD.number, OLD.supplier_number, OLD.invoice_date, OLD.due_date, OLD.supplier_gstin,
        OLD.supplier_state_code, OLD.intra_state, OLD.itc_eligible, OLD.taxable_value, OLD.cgst, OLD.sgst, OLD.igst,
        OLD.tax_total, OLD.total) THEN
        RAISE EXCEPTION 'A supplier invoice can''t be changed once it is recorded'
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_supplier_invoices_fixed';
    END IF;
    IF NEW.amount_paid < OLD.amount_paid THEN
        RAISE EXCEPTION 'What has been paid on a supplier invoice only grows'
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_supplier_invoices_fixed';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_supplier_invoices_fixed BEFORE UPDATE ON supplier_invoices
    FOR EACH ROW EXECUTE FUNCTION fn_supplier_invoices_fixed();

CREATE TABLE supplier_invoice_items (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    invoice_id       uuid          NOT NULL REFERENCES supplier_invoices (id),
    order_item_id    uuid          NOT NULL REFERENCES purchase_order_items (id),
    item_id          uuid          NOT NULL REFERENCES inventory_items (id),
    quantity         numeric(14,3) NOT NULL,
    unit_price       numeric(12,4) NOT NULL,
    gst_rate         numeric(5,2)  NOT NULL,
    taxable_value    numeric(14,2) NOT NULL,
    cgst             numeric(14,2) NOT NULL DEFAULT 0,
    sgst             numeric(14,2) NOT NULL DEFAULT 0,
    igst             numeric(14,2) NOT NULL DEFAULT 0,
    total            numeric(14,2) NOT NULL,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_supplier_invoice_items_quantity CHECK (quantity > 0),
    CONSTRAINT ck_supplier_invoice_items_rate     CHECK (gst_rate IN (0, 0.25, 3, 5, 12, 18, 28, 40)),
    CONSTRAINT ck_supplier_invoice_items_value    CHECK (unit_price >= 0 AND taxable_value = round(quantity * unit_price, 2)
                                                    AND total = taxable_value + cgst + sgst + igst),
    CONSTRAINT ux_supplier_invoice_items_line     UNIQUE (invoice_id, order_item_id)
);
CREATE TRIGGER trg_supplier_invoice_items_append_only BEFORE UPDATE OR DELETE ON supplier_invoice_items
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- ---------------------------------------------------------------------
-- supplier_payments : never changed
-- ---------------------------------------------------------------------
CREATE TABLE supplier_payments (
    id                      uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id         uuid          NOT NULL REFERENCES organizations (id),
    property_id             uuid          NOT NULL REFERENCES properties (id),
    number                  text          NOT NULL,
    supplier_id             uuid          NOT NULL,
    invoice_id              uuid          NOT NULL,
    paid_on                 date          NOT NULL,
    amount                  numeric(14,2) NOT NULL,
    method                  text          NOT NULL,
    reference               text          NULL,
    note                    text          NULL,
    cash_drawer_session_id  uuid          NULL,
    created_at              timestamptz   NOT NULL DEFAULT now(),
    created_by              uuid          NULL REFERENCES users (id),
    CONSTRAINT ck_supplier_payments_number    CHECK (number ~ '^SP[0-9]{5,}$'),
    CONSTRAINT ck_supplier_payments_amount    CHECK (amount > 0),
    CONSTRAINT ck_supplier_payments_method    CHECK (method IN ('CASH','BANK_TRANSFER','UPI','CHEQUE')),
    -- A bank transfer, UPI payment or cheque carries its UTR or cheque number; cash comes out of the drawer.
    CONSTRAINT ck_supplier_payments_reference CHECK ((method = 'CASH' OR length(btrim(coalesce(reference, ''))) BETWEEN 3 AND 60)
                                                AND (reference IS NULL OR length(btrim(reference)) BETWEEN 1 AND 60)),
    CONSTRAINT ck_supplier_payments_cash      CHECK ((method = 'CASH') = (cash_drawer_session_id IS NOT NULL)),
    CONSTRAINT ck_supplier_payments_note      CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 300),
    CONSTRAINT fk_supplier_payments_supplier  FOREIGN KEY (supplier_id, property_id) REFERENCES suppliers (id, property_id),
    CONSTRAINT fk_supplier_payments_invoice   FOREIGN KEY (invoice_id, property_id) REFERENCES supplier_invoices (id, property_id),
    CONSTRAINT fk_supplier_payments_session   FOREIGN KEY (cash_drawer_session_id, property_id) REFERENCES cash_drawer_sessions (id, property_id),
    CONSTRAINT ux_supplier_payments_number    UNIQUE (property_id, number)
);
CREATE INDEX ix_supplier_payments_invoice ON supplier_payments (invoice_id, created_at);
CREATE TRIGGER trg_supplier_payments_append_only BEFORE UPDATE OR DELETE ON supplier_payments
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- cash_drawer_movements: cash paid to a supplier leaves the drawer (PAY_OUT, linked to the payment).
ALTER TABLE cash_drawer_movements ADD COLUMN supplier_payment_id uuid NULL REFERENCES supplier_payments (id);
ALTER TABLE cash_drawer_movements ADD CONSTRAINT ck_cash_drawer_movements_supplier
    CHECK (supplier_payment_id IS NULL OR movement_type = 'PAY_OUT');
CREATE UNIQUE INDEX ux_cash_drawer_movements_supplier_payment ON cash_drawer_movements (supplier_payment_id) WHERE supplier_payment_id IS NOT NULL;

-- Data classification (brief §59).
COMMENT ON TABLE suppliers               IS 'class: internal. Who the property buys from; GSTIN and contact details.';
COMMENT ON TABLE purchase_requests       IS 'class: internal. What a store needs.';
COMMENT ON TABLE purchase_request_items  IS 'class: internal. Lines of a purchase request; never changed.';
COMMENT ON TABLE purchase_orders         IS 'class: confidential. Orders to suppliers with their prices and approval.';
COMMENT ON TABLE purchase_order_items    IS 'class: confidential. Lines of a purchase order with received and billed quantities.';
COMMENT ON TABLE goods_received_notes    IS 'class: internal. Deliveries against purchase orders; never changed.';
COMMENT ON TABLE grn_items               IS 'class: internal. Lines of a delivery; each is a RECEIVED stock movement.';
COMMENT ON TABLE supplier_invoices       IS 'class: confidential. Supplier bills with their input GST; fixed once recorded.';
COMMENT ON TABLE supplier_invoice_items  IS 'class: confidential. Lines of a supplier bill; never changed.';
COMMENT ON TABLE supplier_payments       IS 'class: confidential. Money paid to suppliers; never changed.';
