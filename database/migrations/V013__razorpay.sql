-- =====================================================================
-- V013  Razorpay: online payments confirmed by the webhook (Sprint 11, brief §22; docs/RAZORPAY.md)
--
-- Model
--   payment_gateway_orders  a payment link: an order created at Razorpay for a booking and an amount, and the private
--                           link (/pay/<token>) the guest opens to pay it with Razorpay Checkout (UPI, card, netbanking).
--                           CREATED until paid; PAID once the webhook confirms a captured payment; CANCELLED by staff
--                           (a payment that still arrives for a cancelled link is recorded: the money is real).
--   webhook_events          every Razorpay webhook whose signature checked out, once per Razorpay event id
--                           (UNIQUE (provider, event_id)): a second delivery of the same event changes nothing. A
--                           delivery with a bad signature is refused with 401 and stores nothing. A FAILED event (our
--                           error while processing) is processed again when Razorpay retries it.
--   payments                gains method ONLINE (paid through Razorpay): reference = Razorpay's payment id (pay_…),
--                           gateway_order_id = the payment link, gateway_method = how the guest paid (upi, card …).
--                           One payment row per Razorpay payment (unique index). Created only by the webhook.
--   refunds                 gains method ONLINE (money sent back through Razorpay): gateway_refund_id = rfnd_…; PENDING
--                           until the refund.processed webhook (or Razorpay's answer) says it went through.
--
-- The webhook is authoritative: the guest's browser only ever shows the status, it never marks anything paid.
-- No card data: Razorpay hosts Checkout; we keep its payment id, the method word and nothing about the card.
-- =====================================================================

-- ---------------------------------------------------------------------
-- payment_gateway_orders : payment links
-- ---------------------------------------------------------------------
CREATE TABLE payment_gateway_orders (
    id                uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id   uuid          NOT NULL REFERENCES organizations (id),
    property_id       uuid          NOT NULL REFERENCES properties (id),
    reservation_id    uuid          NOT NULL,
    gateway           text          NOT NULL DEFAULT 'RAZORPAY',
    gateway_order_id  text          NOT NULL,
    amount            numeric(12,2) NOT NULL,
    currency          char(3)       NOT NULL,
    status            text          NOT NULL DEFAULT 'CREATED',
    -- the guest's private link: /pay/<public_token> (32 random bytes, base64url)
    public_token      text          NOT NULL,
    note              text          NULL,
    attempts          integer       NOT NULL DEFAULT 0,
    last_error        text          NULL,
    callback_at       timestamptz   NULL,
    payment_id        uuid          NULL,
    paid_at           timestamptz   NULL,
    cancelled_at      timestamptz   NULL,
    cancelled_by      uuid          NULL,
    cancel_reason     text          NULL,
    created_at        timestamptz   NOT NULL DEFAULT now(),
    updated_at        timestamptz   NOT NULL DEFAULT now(),
    created_by        uuid          NULL,
    updated_by        uuid          NULL,
    CONSTRAINT ck_payment_gateway_orders_gateway  CHECK (gateway IN ('RAZORPAY')),
    CONSTRAINT ck_payment_gateway_orders_order_id CHECK (length(gateway_order_id) BETWEEN 1 AND 60),
    CONSTRAINT ck_payment_gateway_orders_amount   CHECK (amount > 0 AND amount <= 10000000),
    CONSTRAINT ck_payment_gateway_orders_status   CHECK (status IN ('CREATED','PAID','CANCELLED')),
    CONSTRAINT ck_payment_gateway_orders_token    CHECK (public_token ~ '^[A-Za-z0-9_-]{32,64}$'),
    CONSTRAINT ck_payment_gateway_orders_note     CHECK (note IS NULL OR length(note) <= 500),
    CONSTRAINT ck_payment_gateway_orders_paid     CHECK ((status = 'PAID') = (paid_at IS NOT NULL) AND (paid_at IS NULL) = (payment_id IS NULL)),
    -- a cancelled link keeps its reason; a link cancelled and then paid anyway keeps both
    CONSTRAINT ck_payment_gateway_orders_cancel   CHECK ((cancelled_at IS NULL) = (cancel_reason IS NULL)
                                                         AND (status <> 'CANCELLED' OR cancelled_at IS NOT NULL)
                                                         AND (cancel_reason IS NULL OR length(btrim(cancel_reason)) BETWEEN 3 AND 500)),
    CONSTRAINT fk_payment_gateway_orders_reservation FOREIGN KEY (reservation_id, property_id) REFERENCES reservations (id, property_id),
    CONSTRAINT ux_payment_gateway_orders_gateway  UNIQUE (gateway, gateway_order_id),
    CONSTRAINT ux_payment_gateway_orders_token    UNIQUE (public_token),
    CONSTRAINT ux_payment_gateway_orders_id_prop  UNIQUE (id, property_id)
);
CREATE INDEX ix_payment_gateway_orders_reservation ON payment_gateway_orders (reservation_id, created_at DESC);
CREATE INDEX ix_payment_gateway_orders_org         ON payment_gateway_orders (organization_id);

-- What a link is for (booking, amount, Razorpay order, token) never changes; its status moves CREATED → PAID or
-- CANCELLED, and CANCELLED → PAID when the guest paid at the moment it was cancelled. A paid link stays paid.
CREATE FUNCTION fn_payment_gateway_orders_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.organization_id <> OLD.organization_id OR NEW.property_id <> OLD.property_id OR NEW.reservation_id <> OLD.reservation_id
       OR NEW.gateway <> OLD.gateway OR NEW.gateway_order_id <> OLD.gateway_order_id OR NEW.amount <> OLD.amount
       OR NEW.currency <> OLD.currency OR NEW.public_token <> OLD.public_token OR NEW.note IS DISTINCT FROM OLD.note THEN
        RAISE EXCEPTION 'Payment link % can''t be changed; cancel it and send a new one', OLD.gateway_order_id
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NEW.status <> OLD.status AND NOT ((OLD.status = 'CREATED' AND NEW.status IN ('PAID','CANCELLED'))
                                       OR (OLD.status = 'CANCELLED' AND NEW.status = 'PAID')) THEN
        RAISE EXCEPTION 'Payment link % can''t move from % to %', OLD.gateway_order_id, OLD.status, NEW.status
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_payment_gateway_orders_transition';
    END IF;
    IF OLD.payment_id IS NOT NULL AND NEW.payment_id IS DISTINCT FROM OLD.payment_id THEN
        RAISE EXCEPTION 'Payment link % is already paid', OLD.gateway_order_id
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_payment_gateway_orders_transition';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_payment_gateway_orders_guard BEFORE UPDATE ON payment_gateway_orders
    FOR EACH ROW EXECUTE FUNCTION fn_payment_gateway_orders_guard();
CREATE TRIGGER trg_payment_gateway_orders_no_delete BEFORE DELETE ON payment_gateway_orders
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- payments : method ONLINE, created by the webhook
-- ---------------------------------------------------------------------
ALTER TABLE payments DROP CONSTRAINT ck_payments_method;
ALTER TABLE payments ADD CONSTRAINT ck_payments_method CHECK (method IN ('CASH','UPI','CARD','BANK_TRANSFER','ONLINE'));
ALTER TABLE payments ADD COLUMN gateway_order_id uuid NULL;
ALTER TABLE payments ADD COLUMN gateway_method   text NULL;
ALTER TABLE payments ADD CONSTRAINT fk_payments_gateway_order FOREIGN KEY (gateway_order_id, property_id)
    REFERENCES payment_gateway_orders (id, property_id);
-- an online payment has its link and Razorpay's payment id; nothing else has either
ALTER TABLE payments ADD CONSTRAINT ck_payments_gateway CHECK (
    (method = 'ONLINE') = (gateway_order_id IS NOT NULL)
    AND (method <> 'ONLINE' OR reference ~ '^pay_[A-Za-z0-9]{1,40}$')
    AND (gateway_method IS NULL OR (method = 'ONLINE' AND gateway_method ~ '^[a-z_]{1,20}$')));
-- one payment row per Razorpay payment, whichever webhook arrives first
CREATE UNIQUE INDEX ux_payments_gateway_payment ON payments (reference) WHERE method = 'ONLINE';
CREATE INDEX ix_payments_gateway_order ON payments (gateway_order_id) WHERE gateway_order_id IS NOT NULL;

ALTER TABLE payment_gateway_orders ADD CONSTRAINT fk_payment_gateway_orders_payment FOREIGN KEY (payment_id, property_id)
    REFERENCES payments (id, property_id);

-- An online payment can be created as soon as Razorpay authorises it (it counts for nothing until captured). A gateway
-- refund that fails gives the money back to the payment, so an online payment may move back from REFUNDED or
-- PARTIALLY_REFUNDED; the guard below allows those moves for ONLINE payments only.
INSERT INTO payment_status_transitions (from_status, to_status) VALUES
(NULL,                 'AUTHORIZED'),
('REFUNDED',           'PARTIALLY_REFUNDED'),
('REFUNDED',           'CAPTURED'),
('PARTIALLY_REFUNDED', 'CAPTURED');

CREATE OR REPLACE FUNCTION fn_payments_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (NEW.organization_id <> OLD.organization_id OR NEW.property_id <> OLD.property_id
       OR NEW.number <> OLD.number OR NEW.reservation_id <> OLD.reservation_id OR NEW.guest_id IS DISTINCT FROM OLD.guest_id
       OR NEW.method <> OLD.method OR NEW.amount <> OLD.amount OR NEW.currency <> OLD.currency
       OR NEW.reference IS DISTINCT FROM OLD.reference OR NEW.note IS DISTINCT FROM OLD.note
       OR NEW.cash_drawer_session_id IS DISTINCT FROM OLD.cash_drawer_session_id
       OR NEW.gateway_order_id IS DISTINCT FROM OLD.gateway_order_id OR NEW.gateway_method IS DISTINCT FROM OLD.gateway_method
       OR NEW.received_at <> OLD.received_at OR NEW.received_by IS DISTINCT FROM OLD.received_by) THEN
        RAISE EXCEPTION 'Payment % can''t be changed; void or refund it instead', OLD.number
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.status = OLD.status THEN
        RETURN NEW;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM payment_status_transitions t
                    WHERE t.from_status IS NOT DISTINCT FROM (CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE OLD.status END)
                      AND t.to_status = NEW.status)
       -- only the gateway creates authorised payments and gives money back after a failed refund
       OR (NEW.method <> 'ONLINE' AND ((TG_OP = 'INSERT' AND NEW.status = 'AUTHORIZED')
                                     OR (TG_OP = 'UPDATE' AND OLD.status IN ('REFUNDED','PARTIALLY_REFUNDED')
                                         AND NEW.status IN ('CAPTURED','PARTIALLY_REFUNDED')))) THEN
        RAISE EXCEPTION 'Payment % can''t move from % to %', NEW.number,
            CASE WHEN TG_OP = 'INSERT' THEN '(new)' ELSE OLD.status END, NEW.status
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_payments_status_transition';
    END IF;
    RETURN NEW;
END $$;

-- ---------------------------------------------------------------------
-- refunds : method ONLINE, sent back through Razorpay
-- ---------------------------------------------------------------------
ALTER TABLE refunds DROP CONSTRAINT ck_refunds_method;
ALTER TABLE refunds ADD CONSTRAINT ck_refunds_method CHECK (method IN ('CASH','UPI','CARD','BANK_TRANSFER','ONLINE'));
ALTER TABLE refunds ADD COLUMN gateway_refund_id text NULL;
ALTER TABLE refunds ADD CONSTRAINT ck_refunds_gateway CHECK (
    (method = 'ONLINE') = (gateway_refund_id IS NOT NULL) AND (gateway_refund_id IS NULL OR gateway_refund_id ~ '^rfnd_[A-Za-z0-9]{1,40}$'));
CREATE INDEX ix_refunds_gateway ON refunds (gateway_refund_id) WHERE gateway_refund_id IS NOT NULL;

CREATE OR REPLACE FUNCTION fn_refunds_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.payment_id <> OLD.payment_id OR NEW.folio_id IS DISTINCT FROM OLD.folio_id OR NEW.amount <> OLD.amount
       OR NEW.method <> OLD.method OR NEW.reason <> OLD.reason OR NEW.reference IS DISTINCT FROM OLD.reference
       OR NEW.cash_drawer_session_id IS DISTINCT FROM OLD.cash_drawer_session_id OR NEW.refunded_at <> OLD.refunded_at
       OR NEW.refunded_by IS DISTINCT FROM OLD.refunded_by OR NEW.gateway_refund_id IS DISTINCT FROM OLD.gateway_refund_id
       OR (NEW.status <> OLD.status AND NOT (OLD.status = 'PENDING' AND NEW.status IN ('PROCESSED','FAILED'))) THEN
        RAISE EXCEPTION 'Refunds can''t be changed' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END $$;

-- ---------------------------------------------------------------------
-- webhook_events : each verified Razorpay delivery, once per event id
-- ---------------------------------------------------------------------
CREATE TABLE webhook_events (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    provider          text        NOT NULL,
    event_id          text        NOT NULL,
    event_type        text        NOT NULL,
    -- known once the event is matched to one of our payment links
    organization_id   uuid        NULL REFERENCES organizations (id),
    property_id       uuid        NULL REFERENCES properties (id),
    gateway_order_id  uuid        NULL REFERENCES payment_gateway_orders (id),
    payment_id        uuid        NULL REFERENCES payments (id),
    status            text        NOT NULL,
    -- what the event did, in words ("P00012 captured; R00034 confirmed"), or why it changed nothing
    outcome           text        NULL,
    -- the event with what we need of it (ids, amounts, statuses, error); no card details, contact or UPI id
    payload           jsonb       NOT NULL,
    attempts          integer     NOT NULL DEFAULT 1,
    received_at       timestamptz NOT NULL,
    processed_at      timestamptz NULL,
    CONSTRAINT ck_webhook_events_provider CHECK (provider IN ('RAZORPAY')),
    CONSTRAINT ck_webhook_events_event_id CHECK (length(event_id) BETWEEN 1 AND 100),
    CONSTRAINT ck_webhook_events_type     CHECK (length(event_type) BETWEEN 1 AND 100),
    CONSTRAINT ck_webhook_events_status   CHECK (status IN ('PROCESSED','IGNORED','FAILED')),
    CONSTRAINT ck_webhook_events_outcome  CHECK (outcome IS NULL OR length(outcome) <= 1000),
    CONSTRAINT ck_webhook_events_done     CHECK ((status = 'FAILED') = (processed_at IS NULL)),
    CONSTRAINT ux_webhook_events          UNIQUE (provider, event_id)
);
CREATE INDEX ix_webhook_events_order ON webhook_events (gateway_order_id, received_at) WHERE gateway_order_id IS NOT NULL;
CREATE INDEX ix_webhook_events_received ON webhook_events (received_at DESC);

-- An event is processed once: after that only a FAILED one moves (to PROCESSED or IGNORED on Razorpay's retry).
CREATE FUNCTION fn_webhook_events_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.provider <> OLD.provider OR NEW.event_id <> OLD.event_id OR NEW.event_type <> OLD.event_type
       OR NEW.received_at <> OLD.received_at OR OLD.status <> 'FAILED' THEN
        RAISE EXCEPTION 'Webhook event % was already processed', OLD.event_id USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_webhook_events_guard BEFORE UPDATE ON webhook_events
    FOR EACH ROW EXECUTE FUNCTION fn_webhook_events_guard();
CREATE TRIGGER trg_webhook_events_no_delete BEFORE DELETE ON webhook_events
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- Data classification (brief §59).
COMMENT ON TABLE payment_gateway_orders IS 'class: confidential. Razorpay payment links for bookings; never deleted.';
COMMENT ON TABLE webhook_events         IS 'class: confidential. Verified Razorpay webhooks, once per event id; no card data.';
