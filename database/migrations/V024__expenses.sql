-- =====================================================================
-- V024  Expenses and budgets (Sprint 22, brief §40, §41)
--
-- Staff record what they spend (the diesel for the generator, a plumber, printer paper), with a photo of the receipt
-- and, for a GST bill, the vendor's GSTIN, the bill number and the GST on it. Someone with expense.approve approves or
-- rejects it, and only approved expenses count as actuals. Each financial year (April to March) a property has a budget:
-- what each department plans to spend in each month. The budget report puts the two side by side, with the variance and a
-- simple forecast for the year.
--
-- Model
--   expense_categories   what the money was for: Electricity and water, Repairs and maintenance, Fuel… gst_claimable
--                        false when the input tax can't be claimed (Staff welfare: food for staff, section 17(5)).
--   cost_centres         the departments that spend and have a budget: Front office, Housekeeping, Food and beverage…
--   expenses             EX00001…: one payment. amount is what was paid with GST; on a GST bill gst_amount is split into
--                        CGST + SGST or IGST and taxable_value = amount − gst_amount. cost_amount is what counts against
--                        the budget: the taxable value when the tax comes back as input credit (itc_eligible), otherwise
--                        the whole amount. SUBMITTED → APPROVED | REJECTED (reason) | CANCELLED (reason). A decided row
--                        never changes again (trigger), so the actuals can't be edited after approval.
--   expense_attachments  receipts: JPEG, PNG or WebP photos (shrunk on the phone) or PDF bills, at most 2 MB each, stored
--                        encrypted through IFileStore; only the key is here. Added only while the expense waits for
--                        approval (trigger); append-only.
--   budgets              one per property and financial year (year_start = 1 April). total = the sum of its lines, kept by
--                        the service so a change to the plan moves the version and is in the audit trail.
--   budget_lines         a department's plan for one month of the year.
--
-- Settings read (property_settings; defaults in code)
--   expenses.receipt_required_over  500: an expense over ₹500 needs a receipt before it can be approved.
-- =====================================================================

-- ---------------------------------------------------------------------
-- expense_categories
-- ---------------------------------------------------------------------
CREATE TABLE expense_categories (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    code             text        NOT NULL,
    name             text        NOT NULL,
    gst_claimable    boolean     NOT NULL DEFAULT true,
    sort_order       integer     NOT NULL DEFAULT 0,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_expense_categories_code    CHECK (code ~ '^[A-Z][A-Z0-9]{0,9}$'),
    CONSTRAINT ck_expense_categories_name    CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT ux_expense_categories_code    UNIQUE (property_id, code),
    CONSTRAINT ux_expense_categories_id_prop UNIQUE (id, property_id)
);
CREATE UNIQUE INDEX ux_expense_categories_name ON expense_categories (property_id, lower(name));
CREATE TRIGGER trg_expense_categories_no_delete BEFORE DELETE ON expense_categories
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- cost_centres
-- ---------------------------------------------------------------------
CREATE TABLE cost_centres (
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
    CONSTRAINT ck_cost_centres_code    CHECK (code ~ '^[A-Z][A-Z0-9]{0,9}$'),
    CONSTRAINT ck_cost_centres_name    CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT ux_cost_centres_code    UNIQUE (property_id, code),
    CONSTRAINT ux_cost_centres_id_prop UNIQUE (id, property_id)
);
CREATE UNIQUE INDEX ux_cost_centres_name ON cost_centres (property_id, lower(name));
CREATE TRIGGER trg_cost_centres_no_delete BEFORE DELETE ON cost_centres
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- expenses
-- ---------------------------------------------------------------------
CREATE TABLE expenses (
    id                 uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid          NOT NULL REFERENCES organizations (id),
    property_id        uuid          NOT NULL REFERENCES properties (id),
    code               text          NOT NULL,
    expense_date       date          NOT NULL,          -- the day the money was spent (local date at the property)
    category_id        uuid          NOT NULL,
    cost_centre_id     uuid          NOT NULL,
    description        text          NOT NULL,
    payee              text          NOT NULL,          -- the shop, vendor or person paid
    payment_method     text          NOT NULL,
    payment_reference  text          NULL,              -- UPI reference, cheque number…
    amount             numeric(12,2) NOT NULL,          -- what was paid, GST included
    vendor_gstin       text          NULL,
    bill_number        text          NULL,
    gst_amount         numeric(12,2) NOT NULL DEFAULT 0,
    taxable_value      numeric(12,2) NOT NULL,
    cgst               numeric(12,2) NOT NULL DEFAULT 0,
    sgst               numeric(12,2) NOT NULL DEFAULT 0,
    igst               numeric(12,2) NOT NULL DEFAULT 0,
    itc_eligible       boolean       NOT NULL DEFAULT false,
    cost_amount        numeric(12,2) NOT NULL,          -- what counts against the budget
    note               text          NULL,
    status             text          NOT NULL DEFAULT 'SUBMITTED',
    decided_at         timestamptz   NULL,
    decided_by         uuid          NULL REFERENCES users (id),
    decision_reason    text          NULL,              -- why it was rejected or cancelled
    created_at         timestamptz   NOT NULL DEFAULT now(),
    updated_at         timestamptz   NOT NULL DEFAULT now(),
    created_by         uuid          NULL,
    updated_by         uuid          NULL,
    CONSTRAINT ck_expenses_code        CHECK (code ~ '^EX[0-9]{5,}$'),
    CONSTRAINT ck_expenses_description CHECK (length(btrim(description)) BETWEEN 3 AND 200),
    CONSTRAINT ck_expenses_payee       CHECK (length(btrim(payee)) BETWEEN 2 AND 100),
    CONSTRAINT ck_expenses_method      CHECK (payment_method IN ('CASH','UPI','CARD','BANK_TRANSFER','CHEQUE')),
    CONSTRAINT ck_expenses_reference   CHECK (payment_reference IS NULL OR length(btrim(payment_reference)) BETWEEN 1 AND 60),
    CONSTRAINT ck_expenses_amount      CHECK (amount > 0 AND amount <= 10000000),
    CONSTRAINT ck_expenses_gstin       CHECK (vendor_gstin IS NULL OR vendor_gstin ~ '^[0-9]{2}[A-Z0-9]{13}$'),
    CONSTRAINT ck_expenses_bill        CHECK (bill_number IS NULL OR length(btrim(bill_number)) BETWEEN 1 AND 30),
    -- GST is only on a GST bill, is less than what was paid, and is exactly its CGST + SGST or its IGST.
    CONSTRAINT ck_expenses_gst         CHECK (gst_amount >= 0 AND gst_amount < amount
                                              AND (gst_amount = 0 OR (vendor_gstin IS NOT NULL AND bill_number IS NOT NULL))
                                              AND taxable_value = amount - gst_amount
                                              AND cgst >= 0 AND sgst >= 0 AND igst >= 0
                                              AND cgst + sgst + igst = gst_amount
                                              AND (igst = 0 OR cgst + sgst = 0)),
    CONSTRAINT ck_expenses_itc         CHECK (NOT itc_eligible OR gst_amount > 0),
    CONSTRAINT ck_expenses_cost        CHECK (cost_amount = CASE WHEN itc_eligible THEN taxable_value ELSE amount END),
    CONSTRAINT ck_expenses_note        CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 500),
    CONSTRAINT ck_expenses_status      CHECK (status IN ('SUBMITTED','APPROVED','REJECTED','CANCELLED')),
    CONSTRAINT ck_expenses_decided     CHECK ((status = 'SUBMITTED') = (decided_at IS NULL)
                                              AND (status = 'SUBMITTED') = (decided_by IS NULL)
                                              AND (status IN ('REJECTED','CANCELLED')) = (decision_reason IS NOT NULL)),
    CONSTRAINT ck_expenses_reason      CHECK (decision_reason IS NULL OR length(btrim(decision_reason)) BETWEEN 3 AND 300),
    CONSTRAINT fk_expenses_category    FOREIGN KEY (category_id, property_id) REFERENCES expense_categories (id, property_id),
    CONSTRAINT fk_expenses_cost_centre FOREIGN KEY (cost_centre_id, property_id) REFERENCES cost_centres (id, property_id),
    CONSTRAINT ux_expenses_code        UNIQUE (property_id, code),
    CONSTRAINT ux_expenses_id_prop     UNIQUE (id, property_id)
);
-- The budget report sums approved expenses by department and date.
CREATE INDEX ix_expenses_actuals ON expenses (property_id, cost_centre_id, expense_date) WHERE status = 'APPROVED';
CREATE INDEX ix_expenses_list ON expenses (property_id, expense_date DESC);
CREATE INDEX ix_expenses_waiting ON expenses (property_id) WHERE status = 'SUBMITTED';
-- A vendor's GST bill is recorded once at a property (rejected and cancelled ones don't count).
CREATE UNIQUE INDEX ux_expenses_bill ON expenses (property_id, vendor_gstin, upper(bill_number))
    WHERE vendor_gstin IS NOT NULL AND bill_number IS NOT NULL AND status IN ('SUBMITTED','APPROVED');
CREATE TRIGGER trg_expenses_no_delete BEFORE DELETE ON expenses
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- Only a waiting expense changes: it can be edited, or decided once (SUBMITTED → APPROVED | REJECTED | CANCELLED, mirrored
-- in ExpenseStatuses.Moves). An approved expense is an actual and stays exactly as it was approved.
CREATE FUNCTION fn_expenses_frozen() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status <> 'SUBMITTED' THEN
        RAISE EXCEPTION 'expense % is % and can''t change', OLD.code, OLD.status
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_expenses_frozen';
    END IF;
    IF NEW.code <> OLD.code OR NEW.property_id <> OLD.property_id OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
        RAISE EXCEPTION 'an expense keeps its number, property and who recorded it'
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_expenses_frozen';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_expenses_frozen BEFORE UPDATE ON expenses
    FOR EACH ROW EXECUTE FUNCTION fn_expenses_frozen();

-- ---------------------------------------------------------------------
-- expense_attachments : receipts (encrypted in the file store; only the key here)
-- ---------------------------------------------------------------------
CREATE TABLE expense_attachments (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    expense_id       uuid        NOT NULL REFERENCES expenses (id),
    storage_key      text        NOT NULL,
    key_id           text        NOT NULL,
    file_name        text        NOT NULL,
    content_type     text        NOT NULL,
    size_bytes       integer     NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL REFERENCES users (id),
    CONSTRAINT ck_expense_attachments_type CHECK (content_type IN ('image/jpeg','image/png','image/webp','application/pdf')),
    CONSTRAINT ck_expense_attachments_size CHECK (size_bytes BETWEEN 1 AND 2097152),
    CONSTRAINT ck_expense_attachments_name CHECK (length(file_name) BETWEEN 1 AND 120),
    CONSTRAINT ux_expense_attachments_key  UNIQUE (storage_key)
);
CREATE INDEX ix_expense_attachments_expense ON expense_attachments (expense_id, created_at);
CREATE TRIGGER trg_expense_attachments_append_only BEFORE UPDATE OR DELETE ON expense_attachments
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- A receipt is added while the expense waits for approval; after that what was approved is what stays.
CREATE FUNCTION fn_expense_attachments_open() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE s text;
BEGIN
    SELECT status INTO s FROM expenses WHERE id = NEW.expense_id;
    IF s <> 'SUBMITTED' THEN
        RAISE EXCEPTION 'expense % is %; receipts can''t be added', NEW.expense_id, s
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_expense_attachments_open';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_expense_attachments_open BEFORE INSERT ON expense_attachments
    FOR EACH ROW EXECUTE FUNCTION fn_expense_attachments_open();

-- ---------------------------------------------------------------------
-- budgets
-- ---------------------------------------------------------------------
CREATE TABLE budgets (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    year_start       date          NOT NULL,          -- 1 April: 2026-04-01 is the financial year 2026-27
    name             text          NOT NULL,
    note             text          NULL,
    total            numeric(14,2) NOT NULL DEFAULT 0, -- the sum of the lines, kept by the service
    created_at       timestamptz   NOT NULL DEFAULT now(),
    updated_at       timestamptz   NOT NULL DEFAULT now(),
    created_by       uuid          NULL,
    updated_by       uuid          NULL,
    CONSTRAINT ck_budgets_year    CHECK (extract(month FROM year_start) = 4 AND extract(day FROM year_start) = 1),
    CONSTRAINT ck_budgets_name    CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT ck_budgets_note    CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 500),
    CONSTRAINT ck_budgets_total   CHECK (total >= 0),
    CONSTRAINT ux_budgets_year    UNIQUE (property_id, year_start),
    CONSTRAINT ux_budgets_id_prop UNIQUE (id, property_id)
);
CREATE TRIGGER trg_budgets_no_delete BEFORE DELETE ON budgets
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- budget_lines
-- ---------------------------------------------------------------------
CREATE TABLE budget_lines (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    budget_id        uuid          NOT NULL,
    cost_centre_id   uuid          NOT NULL,
    month            date          NOT NULL,          -- the first of the month
    amount           numeric(12,2) NOT NULL,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    updated_at       timestamptz   NOT NULL DEFAULT now(),
    created_by       uuid          NULL,
    updated_by       uuid          NULL,
    CONSTRAINT ck_budget_lines_month  CHECK (extract(day FROM month) = 1),
    CONSTRAINT ck_budget_lines_amount CHECK (amount >= 0 AND amount <= 10000000),
    CONSTRAINT fk_budget_lines_budget FOREIGN KEY (budget_id, property_id) REFERENCES budgets (id, property_id),
    CONSTRAINT fk_budget_lines_centre FOREIGN KEY (cost_centre_id, property_id) REFERENCES cost_centres (id, property_id),
    CONSTRAINT ux_budget_lines_month  UNIQUE (budget_id, cost_centre_id, month)
);
CREATE TRIGGER trg_budget_lines_no_delete BEFORE DELETE ON budget_lines
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- A line's month is inside its budget's year.
CREATE FUNCTION fn_budget_lines_month() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE y date;
BEGIN
    SELECT year_start INTO y FROM budgets WHERE id = NEW.budget_id;
    IF NEW.month < y OR NEW.month >= (y + interval '1 year') THEN
        RAISE EXCEPTION 'month % is outside the budget year starting %', NEW.month, y
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_budget_lines_in_year';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_budget_lines_month BEFORE INSERT OR UPDATE ON budget_lines
    FOR EACH ROW EXECUTE FUNCTION fn_budget_lines_month();

-- ---------------------------------------------------------------------
-- Data classification (brief §59)
-- ---------------------------------------------------------------------
COMMENT ON TABLE expense_categories  IS 'class: internal. What money is spent on; whether its input GST can be claimed.';
COMMENT ON TABLE cost_centres        IS 'class: internal. Departments that spend and have a budget.';
COMMENT ON TABLE expenses            IS 'class: confidential. Money spent, with GST details and approval; approved rows are frozen.';
COMMENT ON TABLE expense_attachments IS 'class: confidential. Receipts, encrypted in the file store; append-only.';
COMMENT ON TABLE budgets             IS 'class: confidential. A property''s budget for a financial year.';
COMMENT ON TABLE budget_lines        IS 'class: confidential. A department''s planned spend for one month.';
