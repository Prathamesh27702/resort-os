-- =====================================================================
-- V012  Payments, refunds and the cash drawer (Sprint 10, brief §21 and §39)
--
-- Model
--   payments                money received for a booking: cash, UPI, card (on the property's own terminal) or bank
--                           transfer. Numbered P00001, P00002 … per property. A payment taken at the desk is CAPTURED
--                           at once (the money is in hand); the gateway states (PENDING, AUTHORIZED, FAILED) are for
--                           Razorpay (Sprint 11). Status graph in payment_status_transitions. Never deleted; only its
--                           status (and the void fields) ever change, and a trigger refuses any other change.
--   payment_allocations     which bill a payment settles: a payment can settle several folios of its booking, a folio can
--                           have several payments. Money not allocated yet is "on account" for the booking (an advance).
--                           Immutable.
--   refunds                 money given back from a payment, with a reason: from one of the folios it settled (folio_id)
--                           or from what is still on account (folio_id NULL). Refund ≤ captured − already refunded,
--                           checked under a lock on the payment row, so two refunds at once can't both pass.
--   cash_drawers            the cash box at a desk ("Front desk"). One per property is created here and on first use.
--   cash_drawer_sessions    one shift of a drawer: opened with a counted float, closed with the counted cash. Expected
--                           cash = the sum of the session's movements; variance = counted − expected. A drawer that closes
--                           short or over waits in VARIANCE_PENDING until someone with cash_drawer.approve_variance
--                           approves it with a note.
--   cash_drawer_movements   the drawer's ledger: the opening float, every cash payment (+), cash refund (−) and voided cash
--                           payment (−). Immutable; rows can only be added while the session is OPEN.
--
-- What a folio has been paid = its allocations from payments that count (CAPTURED, PARTIALLY_REFUNDED, REFUNDED; a
-- voided payment is CANCELLED and counts for nothing) − the refunds taken from it. Nothing is stored twice.
--
-- No card data, ever (brief §3): there is no column for a card number, expiry or CVV, and a card payment's reference (the
-- terminal's approval code or RRN) can't hold 13 or more digits in a row.
-- =====================================================================

-- ---------------------------------------------------------------------
-- payment_status_transitions : the status graph (docs/RAZORPAY.md, Payment states)
-- from_status NULL = the statuses a payment may be created in.
-- ---------------------------------------------------------------------
CREATE TABLE payment_status_transitions (
    from_status  text NULL,
    to_status    text NOT NULL,
    CONSTRAINT ux_payment_status_transitions UNIQUE NULLS NOT DISTINCT (from_status, to_status)
);
INSERT INTO payment_status_transitions (from_status, to_status) VALUES
(NULL,                 'PENDING'),
(NULL,                 'CAPTURED'),
('PENDING',            'AUTHORIZED'),
('PENDING',            'CAPTURED'),
('PENDING',            'FAILED'),
('PENDING',            'CANCELLED'),
('AUTHORIZED',         'CAPTURED'),
('AUTHORIZED',         'FAILED'),
('AUTHORIZED',         'CANCELLED'),
('CAPTURED',           'PARTIALLY_REFUNDED'),
('CAPTURED',           'REFUNDED'),
('CAPTURED',           'CANCELLED'),          -- void: entered by mistake, nothing refunded
('PARTIALLY_REFUNDED', 'REFUNDED');

-- ---------------------------------------------------------------------
-- cash_drawers
-- ---------------------------------------------------------------------
CREATE TABLE cash_drawers (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    name             text        NOT NULL,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_cash_drawers_name    CHECK (length(btrim(name)) BETWEEN 1 AND 60),
    CONSTRAINT ux_cash_drawers_name    UNIQUE (property_id, name),
    CONSTRAINT ux_cash_drawers_id_prop UNIQUE (id, property_id)
);
CREATE TRIGGER trg_cash_drawers_no_delete BEFORE DELETE ON cash_drawers
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

INSERT INTO cash_drawers (organization_id, property_id, name)
SELECT organization_id, id, 'Front desk' FROM properties WHERE deleted_at IS NULL;

-- ---------------------------------------------------------------------
-- cash_drawer_sessions : one shift of a drawer
-- ---------------------------------------------------------------------
CREATE TABLE cash_drawer_sessions (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    cash_drawer_id   uuid          NOT NULL,
    status           text          NOT NULL DEFAULT 'OPEN',
    opened_at        timestamptz   NOT NULL,
    opened_by        uuid          NULL,
    opening_float    numeric(12,2) NOT NULL,
    open_note        text          NULL,
    closed_at        timestamptz   NULL,
    closed_by        uuid          NULL,
    expected_amount  numeric(12,2) NULL,
    counted_amount   numeric(12,2) NULL,
    variance         numeric(12,2) NULL,
    close_note       text          NULL,
    approved_at      timestamptz   NULL,
    approved_by      uuid          NULL,
    approval_note    text          NULL,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    updated_at       timestamptz   NOT NULL DEFAULT now(),
    created_by       uuid          NULL,
    updated_by       uuid          NULL,
    CONSTRAINT ck_cash_drawer_sessions_status  CHECK (status IN ('OPEN','VARIANCE_PENDING','CLOSED')),
    CONSTRAINT ck_cash_drawer_sessions_float   CHECK (opening_float >= 0 AND opening_float <= 10000000),
    CONSTRAINT ck_cash_drawer_sessions_notes   CHECK ((open_note IS NULL OR length(open_note) <= 500)
                                                  AND (close_note IS NULL OR length(close_note) <= 500)
                                                  AND (approval_note IS NULL OR length(btrim(approval_note)) BETWEEN 3 AND 500)),
    -- an open session has nothing of the close; a closed one has all of it, and the variance is counted − expected
    CONSTRAINT ck_cash_drawer_sessions_close   CHECK (
        (status = 'OPEN' AND closed_at IS NULL AND counted_amount IS NULL AND expected_amount IS NULL AND variance IS NULL)
     OR (status <> 'OPEN' AND closed_at IS NOT NULL AND counted_amount >= 0 AND expected_amount IS NOT NULL
         AND variance = counted_amount - expected_amount)),
    -- a variance waits for approval (with a reason from whoever closed); a CLOSED session balanced or was approved
    CONSTRAINT ck_cash_drawer_sessions_approve CHECK (
        (status = 'OPEN' AND approved_at IS NULL)
     OR (status = 'VARIANCE_PENDING' AND variance <> 0 AND close_note IS NOT NULL AND approved_at IS NULL)
     OR (status = 'CLOSED' AND (variance = 0) = (approved_at IS NULL) AND (approved_at IS NULL) = (approval_note IS NULL))),
    CONSTRAINT fk_cash_drawer_sessions_drawer  FOREIGN KEY (cash_drawer_id, property_id) REFERENCES cash_drawers (id, property_id),
    CONSTRAINT ux_cash_drawer_sessions_id_prop UNIQUE (id, property_id)
);
CREATE INDEX ix_cash_drawer_sessions_drawer ON cash_drawer_sessions (cash_drawer_id, opened_at DESC);
-- one open shift per drawer
CREATE UNIQUE INDEX ux_cash_drawer_sessions_open ON cash_drawer_sessions (cash_drawer_id) WHERE status = 'OPEN';

-- A session moves OPEN → CLOSED or VARIANCE_PENDING → CLOSED; what was counted at the open and the close never changes.
CREATE FUNCTION fn_cash_drawer_sessions_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.cash_drawer_id <> OLD.cash_drawer_id OR NEW.opened_at <> OLD.opened_at OR NEW.opening_float <> OLD.opening_float
       OR NEW.opened_by IS DISTINCT FROM OLD.opened_by OR NEW.organization_id <> OLD.organization_id OR NEW.property_id <> OLD.property_id THEN
        RAISE EXCEPTION 'The opening of a cash drawer session can''t be changed'
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_cash_drawer_sessions_immutable';
    END IF;
    IF OLD.status <> 'OPEN' AND (NEW.closed_at IS DISTINCT FROM OLD.closed_at OR NEW.counted_amount IS DISTINCT FROM OLD.counted_amount
       OR NEW.expected_amount IS DISTINCT FROM OLD.expected_amount OR NEW.close_note IS DISTINCT FROM OLD.close_note
       OR NEW.closed_by IS DISTINCT FROM OLD.closed_by) THEN
        RAISE EXCEPTION 'The close of a cash drawer session can''t be changed'
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_cash_drawer_sessions_immutable';
    END IF;
    IF NEW.status <> OLD.status AND NOT ((OLD.status = 'OPEN' AND NEW.status IN ('VARIANCE_PENDING','CLOSED'))
                                       OR (OLD.status = 'VARIANCE_PENDING' AND NEW.status = 'CLOSED')) THEN
        RAISE EXCEPTION 'A cash drawer session can''t move from % to %', OLD.status, NEW.status
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_cash_drawer_sessions_transition';
    END IF;
    IF OLD.status = 'CLOSED' AND NEW IS DISTINCT FROM OLD THEN
        RAISE EXCEPTION 'A closed cash drawer session can''t be changed'
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_cash_drawer_sessions_immutable';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_cash_drawer_sessions_guard BEFORE UPDATE ON cash_drawer_sessions
    FOR EACH ROW EXECUTE FUNCTION fn_cash_drawer_sessions_guard();
CREATE TRIGGER trg_cash_drawer_sessions_no_delete BEFORE DELETE ON cash_drawer_sessions
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- payments
-- ---------------------------------------------------------------------
CREATE TABLE payments (
    id                       uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id          uuid          NOT NULL REFERENCES organizations (id),
    property_id              uuid          NOT NULL REFERENCES properties (id),
    number                   text          NOT NULL,
    reservation_id           uuid          NOT NULL,
    guest_id                 uuid          NULL REFERENCES guests (id),
    method                   text          NOT NULL,
    status                   text          NOT NULL,
    amount                   numeric(12,2) NOT NULL,
    currency                 char(3)       NOT NULL,
    reference                text          NULL,
    note                     text          NULL,
    cash_drawer_session_id   uuid          NULL,
    received_at              timestamptz   NOT NULL,
    received_by              uuid          NULL,
    captured_at              timestamptz   NULL,
    voided_at                timestamptz   NULL,
    voided_by                uuid          NULL,
    void_reason              text          NULL,
    created_at               timestamptz   NOT NULL DEFAULT now(),
    updated_at               timestamptz   NOT NULL DEFAULT now(),
    created_by               uuid          NULL,
    updated_by               uuid          NULL,
    CONSTRAINT ck_payments_number     CHECK (number ~ '^P[0-9]{5,}$'),
    CONSTRAINT ck_payments_method     CHECK (method IN ('CASH','UPI','CARD','BANK_TRANSFER')),
    CONSTRAINT ck_payments_status     CHECK (status IN ('PENDING','AUTHORIZED','CAPTURED','FAILED','REFUNDED','PARTIALLY_REFUNDED','CANCELLED')),
    CONSTRAINT ck_payments_amount     CHECK (amount > 0 AND amount <= 10000000),
    CONSTRAINT ck_payments_reference  CHECK (reference IS NULL OR length(btrim(reference)) BETWEEN 1 AND 100),
    -- no card numbers: a card terminal's approval code or RRN is at most 12 digits
    CONSTRAINT ck_payments_no_card_number CHECK (method <> 'CARD' OR reference IS NULL
                                                 OR regexp_replace(reference, '[\s-]', '', 'g') !~ '[0-9]{13,}'),
    CONSTRAINT ck_payments_note       CHECK (note IS NULL OR length(note) <= 500),
    -- cash goes into a drawer session, nothing else does
    CONSTRAINT ck_payments_drawer     CHECK ((method = 'CASH') = (cash_drawer_session_id IS NOT NULL)),
    CONSTRAINT ck_payments_void       CHECK ((voided_at IS NULL) = (void_reason IS NULL)
                                             AND (void_reason IS NULL OR (status = 'CANCELLED' AND length(btrim(void_reason)) BETWEEN 3 AND 500))),
    CONSTRAINT fk_payments_reservation FOREIGN KEY (reservation_id, property_id) REFERENCES reservations (id, property_id),
    CONSTRAINT fk_payments_session     FOREIGN KEY (cash_drawer_session_id, property_id) REFERENCES cash_drawer_sessions (id, property_id),
    CONSTRAINT ux_payments_number      UNIQUE (property_id, number),
    CONSTRAINT ux_payments_id_prop     UNIQUE (id, property_id)
);
CREATE INDEX ix_payments_reservation ON payments (reservation_id);
CREATE INDEX ix_payments_property    ON payments (property_id, received_at DESC);
CREATE INDEX ix_payments_session     ON payments (cash_drawer_session_id) WHERE cash_drawer_session_id IS NOT NULL;
CREATE INDEX ix_payments_org         ON payments (organization_id);

-- Only the moves in payment_status_transitions; and nothing but the status and the void ever changes.
CREATE FUNCTION fn_payments_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (NEW.organization_id <> OLD.organization_id OR NEW.property_id <> OLD.property_id
       OR NEW.number <> OLD.number OR NEW.reservation_id <> OLD.reservation_id OR NEW.guest_id IS DISTINCT FROM OLD.guest_id
       OR NEW.method <> OLD.method OR NEW.amount <> OLD.amount OR NEW.currency <> OLD.currency
       OR NEW.reference IS DISTINCT FROM OLD.reference OR NEW.note IS DISTINCT FROM OLD.note
       OR NEW.cash_drawer_session_id IS DISTINCT FROM OLD.cash_drawer_session_id
       OR NEW.received_at <> OLD.received_at OR NEW.received_by IS DISTINCT FROM OLD.received_by) THEN
        RAISE EXCEPTION 'Payment % can''t be changed; void or refund it instead', OLD.number
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.status = OLD.status THEN
        RETURN NEW;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM payment_status_transitions t
                    WHERE t.from_status IS NOT DISTINCT FROM (CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE OLD.status END)
                      AND t.to_status = NEW.status) THEN
        RAISE EXCEPTION 'Payment % can''t move from % to %', NEW.number,
            CASE WHEN TG_OP = 'INSERT' THEN '(new)' ELSE OLD.status END, NEW.status
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_payments_status_transition';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_payments_guard BEFORE INSERT OR UPDATE ON payments
    FOR EACH ROW EXECUTE FUNCTION fn_payments_guard();
CREATE TRIGGER trg_payments_no_delete BEFORE DELETE ON payments
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- payment_allocations : which bill a payment settles
-- ---------------------------------------------------------------------
CREATE TABLE payment_allocations (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    payment_id       uuid          NOT NULL,
    folio_id         uuid          NOT NULL,
    amount           numeric(12,2) NOT NULL,
    allocated_at     timestamptz   NOT NULL,
    allocated_by     uuid          NULL,
    CONSTRAINT ck_payment_allocations_amount CHECK (amount > 0),
    CONSTRAINT fk_payment_allocations_payment FOREIGN KEY (payment_id, property_id) REFERENCES payments (id, property_id),
    CONSTRAINT fk_payment_allocations_folio   FOREIGN KEY (folio_id, property_id) REFERENCES folios (id, property_id)
);
CREATE INDEX ix_payment_allocations_payment ON payment_allocations (payment_id);
CREATE INDEX ix_payment_allocations_folio   ON payment_allocations (folio_id);

-- ---------------------------------------------------------------------
-- refunds
-- ---------------------------------------------------------------------
CREATE TABLE refunds (
    id                       uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id          uuid          NOT NULL REFERENCES organizations (id),
    property_id              uuid          NOT NULL REFERENCES properties (id),
    payment_id               uuid          NOT NULL,
    folio_id                 uuid          NULL,
    amount                   numeric(12,2) NOT NULL,
    method                   text          NOT NULL,
    status                   text          NOT NULL DEFAULT 'PROCESSED',
    reason                   text          NOT NULL,
    reference                text          NULL,
    cash_drawer_session_id   uuid          NULL,
    refunded_at              timestamptz   NOT NULL,
    refunded_by              uuid          NULL,
    created_at               timestamptz   NOT NULL DEFAULT now(),
    updated_at               timestamptz   NOT NULL DEFAULT now(),
    created_by               uuid          NULL,
    updated_by               uuid          NULL,
    CONSTRAINT ck_refunds_amount    CHECK (amount > 0),
    CONSTRAINT ck_refunds_method    CHECK (method IN ('CASH','UPI','CARD','BANK_TRANSFER')),
    -- PENDING and FAILED are for gateway refunds (Sprint 11); a refund made at the desk is PROCESSED
    CONSTRAINT ck_refunds_status    CHECK (status IN ('PENDING','PROCESSED','FAILED')),
    CONSTRAINT ck_refunds_reason    CHECK (length(btrim(reason)) BETWEEN 3 AND 500),
    CONSTRAINT ck_refunds_reference CHECK (reference IS NULL OR (length(btrim(reference)) BETWEEN 1 AND 100
                                           AND (method <> 'CARD' OR regexp_replace(reference, '[\s-]', '', 'g') !~ '[0-9]{13,}'))),
    CONSTRAINT ck_refunds_drawer    CHECK ((method = 'CASH') = (cash_drawer_session_id IS NOT NULL)),
    CONSTRAINT fk_refunds_payment   FOREIGN KEY (payment_id, property_id) REFERENCES payments (id, property_id),
    CONSTRAINT fk_refunds_folio     FOREIGN KEY (folio_id, property_id) REFERENCES folios (id, property_id),
    CONSTRAINT fk_refunds_session   FOREIGN KEY (cash_drawer_session_id, property_id) REFERENCES cash_drawer_sessions (id, property_id)
);
CREATE INDEX ix_refunds_payment ON refunds (payment_id);
CREATE INDEX ix_refunds_folio   ON refunds (folio_id) WHERE folio_id IS NOT NULL;

-- Allocations and refunds are checked against their payment under a lock on its row (FOR UPDATE), so two at the same time
-- wait for each other and the second sees the first:
--   allocated + refunded from what was on account  ≤  amount
--   refunded                                       ≤  amount (captured − already refunded)
--   refunded from a folio                          ≤  what the payment put on that folio − what was refunded from it
CREATE FUNCTION fn_payment_money_check() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p          record;
    allocated  numeric;
    refunded   numeric;
    on_account numeric;
    on_folio   numeric;
BEGIN
    SELECT * INTO p FROM payments WHERE id = NEW.payment_id FOR UPDATE;
    IF p.status NOT IN ('CAPTURED','PARTIALLY_REFUNDED') THEN
        RAISE EXCEPTION 'Payment % is %; only captured money can be allocated or refunded', p.number, p.status
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_payment_money_status';
    END IF;
    IF NEW.folio_id IS NOT NULL AND (SELECT reservation_id FROM folios WHERE id = NEW.folio_id) IS DISTINCT FROM p.reservation_id THEN
        RAISE EXCEPTION 'Payment % belongs to another booking than this folio', p.number
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_payment_money_folio';
    END IF;
    SELECT coalesce(sum(amount), 0) INTO allocated FROM payment_allocations WHERE payment_id = p.id;
    SELECT coalesce(sum(amount), 0) INTO refunded FROM refunds WHERE payment_id = p.id AND status <> 'FAILED';
    SELECT coalesce(sum(amount), 0) INTO on_account FROM refunds WHERE payment_id = p.id AND status <> 'FAILED' AND folio_id IS NULL;

    IF TG_TABLE_NAME = 'payment_allocations' THEN
        IF allocated + on_account + NEW.amount > p.amount THEN
            RAISE EXCEPTION 'Payment % has only % left to allocate', p.number, p.amount - allocated - on_account
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_payment_allocations_limit';
        END IF;
    ELSE
        IF refunded + NEW.amount > p.amount THEN
            RAISE EXCEPTION 'Payment % has only % left to refund', p.number, p.amount - refunded
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_refunds_limit';
        END IF;
        IF NEW.folio_id IS NULL AND allocated + on_account + NEW.amount > p.amount THEN
            RAISE EXCEPTION 'Payment % has only % on account', p.number, p.amount - allocated - on_account
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_refunds_limit';
        END IF;
        IF NEW.folio_id IS NOT NULL THEN
            SELECT coalesce((SELECT sum(amount) FROM payment_allocations WHERE payment_id = p.id AND folio_id = NEW.folio_id), 0)
                 - coalesce((SELECT sum(amount) FROM refunds WHERE payment_id = p.id AND folio_id = NEW.folio_id AND status <> 'FAILED'), 0)
              INTO on_folio;
            IF NEW.amount > on_folio THEN
                RAISE EXCEPTION 'Payment % has only % on this folio', p.number, on_folio
                    USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_refunds_limit';
            END IF;
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_payment_allocations_check BEFORE INSERT ON payment_allocations
    FOR EACH ROW EXECUTE FUNCTION fn_payment_money_check();
CREATE TRIGGER trg_refunds_check BEFORE INSERT ON refunds
    FOR EACH ROW EXECUTE FUNCTION fn_payment_money_check();

CREATE TRIGGER trg_payment_allocations_no_delete BEFORE DELETE ON payment_allocations
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();
CREATE TRIGGER trg_payment_allocations_no_update BEFORE UPDATE ON payment_allocations
    FOR EACH ROW EXECUTE FUNCTION fn_reject_update();
CREATE TRIGGER trg_refunds_no_delete BEFORE DELETE ON refunds
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- A refund keeps its amount, payment and reason; only a gateway refund's status moves (PENDING → PROCESSED or FAILED).
CREATE FUNCTION fn_refunds_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.payment_id <> OLD.payment_id OR NEW.folio_id IS DISTINCT FROM OLD.folio_id OR NEW.amount <> OLD.amount
       OR NEW.method <> OLD.method OR NEW.reason <> OLD.reason OR NEW.reference IS DISTINCT FROM OLD.reference
       OR NEW.cash_drawer_session_id IS DISTINCT FROM OLD.cash_drawer_session_id OR NEW.refunded_at <> OLD.refunded_at
       OR NEW.refunded_by IS DISTINCT FROM OLD.refunded_by
       OR (NEW.status <> OLD.status AND NOT (OLD.status = 'PENDING' AND NEW.status IN ('PROCESSED','FAILED'))) THEN
        RAISE EXCEPTION 'Refunds can''t be changed' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_refunds_guard BEFORE UPDATE ON refunds
    FOR EACH ROW EXECUTE FUNCTION fn_refunds_guard();

-- ---------------------------------------------------------------------
-- cash_drawer_movements : the drawer's ledger
-- ---------------------------------------------------------------------
CREATE TABLE cash_drawer_movements (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    session_id       uuid          NOT NULL,
    movement_type    text          NOT NULL,
    amount           numeric(12,2) NOT NULL,
    payment_id       uuid          NULL,
    refund_id        uuid          NULL REFERENCES refunds (id),
    description      text          NOT NULL,
    moved_at         timestamptz   NOT NULL,
    moved_by         uuid          NULL,
    -- PAY_IN and PAY_OUT (petty cash) are kept for a later sprint; nothing posts them yet
    CONSTRAINT ck_cash_drawer_movements_type   CHECK (movement_type IN ('OPENING_FLOAT','PAYMENT','REFUND','VOID','PAY_IN','PAY_OUT')),
    CONSTRAINT ck_cash_drawer_movements_amount CHECK (
        (movement_type IN ('OPENING_FLOAT') AND amount >= 0)
     OR (movement_type IN ('PAYMENT','PAY_IN') AND amount > 0)
     OR (movement_type IN ('REFUND','VOID','PAY_OUT') AND amount < 0)),
    CONSTRAINT ck_cash_drawer_movements_links  CHECK (
        (movement_type IN ('PAYMENT','VOID')) = (payment_id IS NOT NULL) AND (movement_type = 'REFUND') = (refund_id IS NOT NULL)),
    CONSTRAINT ck_cash_drawer_movements_desc   CHECK (length(btrim(description)) BETWEEN 1 AND 200),
    CONSTRAINT fk_cash_drawer_movements_session FOREIGN KEY (session_id, property_id) REFERENCES cash_drawer_sessions (id, property_id),
    CONSTRAINT fk_cash_drawer_movements_payment FOREIGN KEY (payment_id, property_id) REFERENCES payments (id, property_id)
);
CREATE INDEX ix_cash_drawer_movements_session ON cash_drawer_movements (session_id, moved_at);
-- a payment comes into the drawer once and is voided out of it at most once
CREATE UNIQUE INDEX ux_cash_drawer_movements_payment ON cash_drawer_movements (payment_id, movement_type) WHERE payment_id IS NOT NULL;

CREATE FUNCTION fn_cash_drawer_movements_check() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (SELECT status FROM cash_drawer_sessions WHERE id = NEW.session_id) IS DISTINCT FROM 'OPEN' THEN
        RAISE EXCEPTION 'Cash can only move through an open drawer session'
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_cash_drawer_movements_open';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_cash_drawer_movements_check BEFORE INSERT ON cash_drawer_movements
    FOR EACH ROW EXECUTE FUNCTION fn_cash_drawer_movements_check();
CREATE TRIGGER trg_cash_drawer_movements_no_delete BEFORE DELETE ON cash_drawer_movements
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();
CREATE TRIGGER trg_cash_drawer_movements_no_update BEFORE UPDATE ON cash_drawer_movements
    FOR EACH ROW EXECUTE FUNCTION fn_reject_update();

-- Data classification (brief §59).
COMMENT ON TABLE payments              IS 'class: confidential. Money received; never deleted, only status and void change. No card data.';
COMMENT ON TABLE payment_allocations   IS 'class: confidential. Which folio a payment settles; immutable.';
COMMENT ON TABLE refunds               IS 'class: confidential. Money given back, with a reason; never deleted.';
COMMENT ON TABLE cash_drawers          IS 'class: internal. Cash boxes of a property.';
COMMENT ON TABLE cash_drawer_sessions  IS 'class: confidential. Shifts of a cash drawer: float, count, variance and its approval.';
COMMENT ON TABLE cash_drawer_movements IS 'class: confidential. Ledger of a drawer session; immutable.';
COMMENT ON TABLE payment_status_transitions IS 'class: public. The payment status graph.';
