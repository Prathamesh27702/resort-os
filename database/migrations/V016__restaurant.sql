-- =====================================================================
-- V016  Menu, restaurant POS and KOT (Sprint 14, brief §35, §55)
--
-- Model
--   menu_categories      sections of the menu (Starters, Mains, Drinks …) per property, in display order.
--   menu_items           what can be ordered: name, price before GST, veg / non-veg / egg, and whether it is available
--                        now (switched off when the kitchen runs out). Never deleted: switched off instead, so old
--                        orders keep their link.
--   menu_item_variants   sizes or portions with their own price (Half / Full, 300 ml / 500 ml). An item that has
--                        variants is ordered by variant.
--   modifier_groups      choices on an item (Spice level, Add-ons), with how many may be picked (min, max).
--   modifiers            one choice and what it adds to the price (Extra cheese +₹40; Less spicy +₹0).
--   restaurant_tables    tables by code (T1, T2 …) with seats and an area (Garden, Beach). A table has at most one
--                        open order (partial unique index).
--   orders               a bill being taken: dine-in at a table, room service to an in-house room, or takeaway.
--                        Numbered O00001 … per property. OPEN while items are added and sent; then SETTLED, either PAID
--                        (cash, UPI, card or bank transfer at the restaurant) or ROOM_CHARGE (one line on the guest's main
--                        folio, Sprint 8, with its GST from the tax engine, Sprint 9); or VOIDED with a reason
--                        (pos_order.void). Edited by more than one waiter: every change carries the order's version
--                        (xmin) and a stale one gets 409. Totals are kept on the row and recalculated on every change.
--   order_items          the lines: a copy of the item's name, variant, chosen modifiers and price at the time it was
--                        ordered (so a later menu change never alters an order), quantity and a note for the kitchen.
--                        NEW (not sent to the kitchen yet; can still be changed or removed) → SENT (on a KOT) or VOIDED
--                        (after it was sent, with a reason and pos_order.void).
--   kots                 kitchen order tickets, numbered K00001 … per property: the lines sent together. NEW →
--                        PREPARING → READY (kitchen) → SERVED (waiter); CANCELLED when every line on it is voided.
--   kot_items            the lines of a ticket as the kitchen reads them. A line voided after it was sent stays on the
--                        ticket, struck through (voided_at), so the kitchen knows to stop.
--
-- Money: prices are before GST. order subtotal = sum of live lines; discount (pos.discount, with a reason) comes off the
-- subtotal; GST is on what is left (restaurant service, the FOOD tax category); total = net + GST. Cash paid at the
-- restaurant goes into the open cash drawer (cash_drawer_movements type POS_SALE, linked to the order).
-- =====================================================================

-- ---------------------------------------------------------------------
-- menu_categories
-- ---------------------------------------------------------------------
CREATE TABLE menu_categories (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    name             text        NOT NULL,
    sort_order       integer     NOT NULL DEFAULT 0,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_menu_categories_name   CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT ux_menu_categories_id_prop UNIQUE (id, property_id)
);
CREATE UNIQUE INDEX ux_menu_categories_name ON menu_categories (property_id, lower(name));
CREATE TRIGGER trg_menu_categories_no_delete BEFORE DELETE ON menu_categories
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- menu_items
-- ---------------------------------------------------------------------
CREATE TABLE menu_items (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    category_id      uuid          NOT NULL,
    code             text          NOT NULL,
    name             text          NOT NULL,
    description      text          NULL,
    food_type        text          NOT NULL DEFAULT 'VEG',
    price            numeric(12,2) NOT NULL,
    is_available     boolean       NOT NULL DEFAULT true,
    is_active        boolean       NOT NULL DEFAULT true,
    sort_order       integer       NOT NULL DEFAULT 0,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    updated_at       timestamptz   NOT NULL DEFAULT now(),
    created_by       uuid          NULL,
    updated_by       uuid          NULL,
    CONSTRAINT ck_menu_items_code      CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,19}$'),
    CONSTRAINT ck_menu_items_name      CHECK (length(btrim(name)) BETWEEN 2 AND 80),
    CONSTRAINT ck_menu_items_desc      CHECK (description IS NULL OR length(btrim(description)) BETWEEN 1 AND 300),
    CONSTRAINT ck_menu_items_food_type CHECK (food_type IN ('VEG','NON_VEG','EGG')),
    CONSTRAINT ck_menu_items_price     CHECK (price >= 0 AND price <= 1000000 AND price = round(price, 2)),
    CONSTRAINT fk_menu_items_category  FOREIGN KEY (category_id, property_id) REFERENCES menu_categories (id, property_id),
    CONSTRAINT ux_menu_items_code      UNIQUE (property_id, code),
    CONSTRAINT ux_menu_items_id_prop   UNIQUE (id, property_id)
);
CREATE INDEX ix_menu_items_category ON menu_items (category_id, sort_order);
CREATE TRIGGER trg_menu_items_no_delete BEFORE DELETE ON menu_items
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- menu_item_variants
-- ---------------------------------------------------------------------
CREATE TABLE menu_item_variants (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    menu_item_id     uuid          NOT NULL REFERENCES menu_items (id),
    name             text          NOT NULL,
    price            numeric(12,2) NOT NULL,
    sort_order       integer       NOT NULL DEFAULT 0,
    is_active        boolean       NOT NULL DEFAULT true,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_menu_item_variants_name  CHECK (length(btrim(name)) BETWEEN 1 AND 40),
    CONSTRAINT ck_menu_item_variants_price CHECK (price >= 0 AND price <= 1000000 AND price = round(price, 2))
);
CREATE UNIQUE INDEX ux_menu_item_variants_name ON menu_item_variants (menu_item_id, lower(name)) WHERE is_active;
CREATE TRIGGER trg_menu_item_variants_no_delete BEFORE DELETE ON menu_item_variants
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- modifier_groups / modifiers
-- ---------------------------------------------------------------------
CREATE TABLE modifier_groups (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    menu_item_id     uuid        NOT NULL REFERENCES menu_items (id),
    name             text        NOT NULL,
    min_select       smallint    NOT NULL DEFAULT 0,
    max_select       smallint    NOT NULL DEFAULT 1,
    sort_order       integer     NOT NULL DEFAULT 0,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_modifier_groups_name   CHECK (length(btrim(name)) BETWEEN 2 AND 40),
    CONSTRAINT ck_modifier_groups_select CHECK (min_select >= 0 AND max_select >= 1 AND min_select <= max_select AND max_select <= 20)
);
CREATE INDEX ix_modifier_groups_item ON modifier_groups (menu_item_id, sort_order);
CREATE TRIGGER trg_modifier_groups_no_delete BEFORE DELETE ON modifier_groups
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

CREATE TABLE modifiers (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    group_id         uuid          NOT NULL REFERENCES modifier_groups (id),
    name             text          NOT NULL,
    price_delta      numeric(12,2) NOT NULL DEFAULT 0,
    sort_order       integer       NOT NULL DEFAULT 0,
    is_active        boolean       NOT NULL DEFAULT true,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_modifiers_name  CHECK (length(btrim(name)) BETWEEN 1 AND 40),
    CONSTRAINT ck_modifiers_price CHECK (price_delta >= 0 AND price_delta <= 100000 AND price_delta = round(price_delta, 2))
);
CREATE INDEX ix_modifiers_group ON modifiers (group_id, sort_order);
CREATE TRIGGER trg_modifiers_no_delete BEFORE DELETE ON modifiers
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- restaurant_tables
-- ---------------------------------------------------------------------
CREATE TABLE restaurant_tables (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    code             text        NOT NULL,
    seats            smallint    NOT NULL DEFAULT 4,
    area             text        NULL,
    sort_order       integer     NOT NULL DEFAULT 0,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_restaurant_tables_code  CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,9}$'),
    CONSTRAINT ck_restaurant_tables_seats CHECK (seats BETWEEN 1 AND 50),
    CONSTRAINT ck_restaurant_tables_area  CHECK (area IS NULL OR length(btrim(area)) BETWEEN 1 AND 40),
    CONSTRAINT ux_restaurant_tables_code  UNIQUE (property_id, code),
    CONSTRAINT ux_restaurant_tables_id_prop UNIQUE (id, property_id)
);
CREATE TRIGGER trg_restaurant_tables_no_delete BEFORE DELETE ON restaurant_tables
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- orders
-- ---------------------------------------------------------------------
CREATE TABLE orders (
    id                 uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid          NOT NULL REFERENCES organizations (id),
    property_id        uuid          NOT NULL REFERENCES properties (id),
    number             text          NOT NULL,
    order_type         text          NOT NULL,
    status             text          NOT NULL DEFAULT 'OPEN',
    table_id           uuid          NULL,
    unit_id            uuid          NULL,
    reservation_id     uuid          NULL,
    covers             smallint      NULL,
    note               text          NULL,
    business_date      date          NOT NULL,
    subtotal           numeric(12,2) NOT NULL DEFAULT 0,
    discount_kind      text          NULL,
    discount_value     numeric(12,2) NULL,
    discount_amount    numeric(12,2) NOT NULL DEFAULT 0,
    discount_reason    text          NULL,
    discounted_by      uuid          NULL REFERENCES users (id),
    discounted_at      timestamptz   NULL,
    tax_amount         numeric(12,2) NOT NULL DEFAULT 0,
    total              numeric(12,2) NOT NULL DEFAULT 0,
    settlement         text          NULL,
    payment_method     text          NULL,
    payment_reference  text          NULL,
    cash_drawer_session_id uuid      NULL REFERENCES cash_drawer_sessions (id),
    folio_id           uuid          NULL REFERENCES folios (id),
    folio_item_id      uuid          NULL REFERENCES folio_items (id),
    settled_at         timestamptz   NULL,
    settled_by         uuid          NULL REFERENCES users (id),
    voided_at          timestamptz   NULL,
    voided_by          uuid          NULL REFERENCES users (id),
    void_reason        text          NULL,
    opened_by          uuid          NULL REFERENCES users (id),
    created_at         timestamptz   NOT NULL DEFAULT now(),
    updated_at         timestamptz   NOT NULL DEFAULT now(),
    created_by         uuid          NULL,
    updated_by         uuid          NULL,
    CONSTRAINT ck_orders_number     CHECK (number ~ '^O[0-9]{5,}$'),
    CONSTRAINT ck_orders_type       CHECK (order_type IN ('DINE_IN','ROOM_SERVICE','TAKEAWAY')),
    CONSTRAINT ck_orders_status     CHECK (status IN ('OPEN','SETTLED','VOIDED')),
    CONSTRAINT ck_orders_where      CHECK ((order_type = 'DINE_IN') = (table_id IS NOT NULL)
                                       AND (order_type <> 'ROOM_SERVICE' OR (unit_id IS NOT NULL AND reservation_id IS NOT NULL))),
    CONSTRAINT ck_orders_covers     CHECK (covers IS NULL OR covers BETWEEN 1 AND 99),
    CONSTRAINT ck_orders_note       CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 300),
    CONSTRAINT ck_orders_money      CHECK (subtotal >= 0 AND discount_amount >= 0 AND discount_amount <= subtotal AND tax_amount >= 0
                                       AND total = subtotal - discount_amount + tax_amount),
    CONSTRAINT ck_orders_discount   CHECK ((discount_kind IS NULL) = (discount_value IS NULL)
                                       AND (discount_kind IS NULL OR (discount_kind IN ('PERCENT','AMOUNT') AND discount_reason IS NOT NULL
                                            AND discounted_at IS NOT NULL))
                                       AND (discount_kind <> 'PERCENT' OR discount_value BETWEEN 0 AND 100)),
    CONSTRAINT ck_orders_settled    CHECK ((status = 'SETTLED') = (settlement IS NOT NULL AND settled_at IS NOT NULL)),
    CONSTRAINT ck_orders_settlement CHECK (settlement IS NULL
                                       OR (settlement = 'PAID' AND payment_method IN ('CASH','UPI','CARD','BANK_TRANSFER') AND folio_item_id IS NULL
                                           AND (payment_method = 'CASH' AND total > 0) = (cash_drawer_session_id IS NOT NULL))
                                       OR (settlement = 'ROOM_CHARGE' AND reservation_id IS NOT NULL AND folio_id IS NOT NULL AND payment_method IS NULL)),
    -- No card data (brief §3): a reference of 13 or more digits in a row is refused.
    CONSTRAINT ck_orders_reference  CHECK (payment_reference IS NULL OR (length(payment_reference) <= 100 AND payment_reference !~ '[0-9]{13,}')),
    CONSTRAINT ck_orders_voided     CHECK ((status = 'VOIDED') = (voided_at IS NOT NULL AND void_reason IS NOT NULL)),
    CONSTRAINT fk_orders_table      FOREIGN KEY (table_id, property_id) REFERENCES restaurant_tables (id, property_id),
    CONSTRAINT fk_orders_unit       FOREIGN KEY (unit_id, property_id) REFERENCES accommodation_units (id, property_id),
    CONSTRAINT fk_orders_reservation FOREIGN KEY (reservation_id, property_id) REFERENCES reservations (id, property_id),
    CONSTRAINT ux_orders_number     UNIQUE (property_id, number),
    CONSTRAINT ux_orders_id_prop    UNIQUE (id, property_id)
);
-- A table has at most one open order.
CREATE UNIQUE INDEX ux_orders_open_table ON orders (table_id) WHERE status = 'OPEN' AND table_id IS NOT NULL;
-- A folio line is the room charge of at most one order.
CREATE UNIQUE INDEX ux_orders_folio_item ON orders (folio_item_id) WHERE folio_item_id IS NOT NULL;
CREATE INDEX ix_orders_list ON orders (property_id, business_date DESC, status);
CREATE INDEX ix_orders_open ON orders (property_id) WHERE status = 'OPEN';
CREATE INDEX ix_orders_reservation ON orders (reservation_id) WHERE reservation_id IS NOT NULL;
CREATE TRIGGER trg_orders_no_delete BEFORE DELETE ON orders
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- A settled or voided order is final: nothing on it changes again.
CREATE FUNCTION fn_orders_final() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status <> 'OPEN' THEN
        RAISE EXCEPTION 'order % is % and can''t be changed', OLD.number, lower(OLD.status) USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_orders_final BEFORE UPDATE ON orders
    FOR EACH ROW EXECUTE FUNCTION fn_orders_final();

-- ---------------------------------------------------------------------
-- kots
-- ---------------------------------------------------------------------
CREATE TABLE kots (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    order_id         uuid        NOT NULL,
    number           text        NOT NULL,
    status           text        NOT NULL DEFAULT 'NEW',
    note             text        NULL,
    sent_at          timestamptz NOT NULL,
    sent_by          uuid        NULL REFERENCES users (id),
    started_at       timestamptz NULL,
    ready_at         timestamptz NULL,
    served_at        timestamptz NULL,
    served_by        uuid        NULL REFERENCES users (id),
    cancelled_at     timestamptz NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_kots_number CHECK (number ~ '^K[0-9]{5,}$'),
    CONSTRAINT ck_kots_status CHECK (status IN ('NEW','PREPARING','READY','SERVED','CANCELLED')),
    CONSTRAINT ck_kots_note   CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 300),
    CONSTRAINT ck_kots_ready  CHECK (status NOT IN ('READY','SERVED') OR ready_at IS NOT NULL),
    CONSTRAINT ck_kots_served CHECK ((status = 'SERVED') = (served_at IS NOT NULL)),
    CONSTRAINT ck_kots_cancel CHECK ((status = 'CANCELLED') = (cancelled_at IS NOT NULL)),
    CONSTRAINT fk_kots_order  FOREIGN KEY (order_id, property_id) REFERENCES orders (id, property_id),
    CONSTRAINT ux_kots_number UNIQUE (property_id, number)
);
CREATE INDEX ix_kots_kitchen ON kots (property_id, status, sent_at) WHERE status IN ('NEW','PREPARING','READY');
CREATE INDEX ix_kots_order ON kots (order_id);
CREATE TRIGGER trg_kots_no_delete BEFORE DELETE ON kots
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- order_items
-- ---------------------------------------------------------------------
CREATE TABLE order_items (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    order_id         uuid          NOT NULL REFERENCES orders (id),
    menu_item_id     uuid          NOT NULL REFERENCES menu_items (id),
    variant_id       uuid          NULL REFERENCES menu_item_variants (id),
    name             text          NOT NULL,
    variant_name     text          NULL,
    food_type        text          NOT NULL,
    modifiers        jsonb         NOT NULL DEFAULT '[]',
    quantity         smallint      NOT NULL,
    unit_price       numeric(12,2) NOT NULL,
    amount           numeric(12,2) NOT NULL,
    note             text          NULL,
    status           text          NOT NULL DEFAULT 'NEW',
    kot_id           uuid          NULL REFERENCES kots (id),
    added_by         uuid          NULL REFERENCES users (id),
    voided_at        timestamptz   NULL,
    voided_by        uuid          NULL REFERENCES users (id),
    void_reason      text          NULL,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    updated_at       timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_order_items_quantity  CHECK (quantity BETWEEN 1 AND 99),
    CONSTRAINT ck_order_items_price     CHECK (unit_price >= 0 AND amount = unit_price * quantity),
    CONSTRAINT ck_order_items_modifiers CHECK (jsonb_typeof(modifiers) = 'array'),
    CONSTRAINT ck_order_items_note      CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 200),
    CONSTRAINT ck_order_items_status    CHECK (status IN ('NEW','SENT','VOIDED')),
    CONSTRAINT ck_order_items_sent      CHECK ((status = 'NEW') = (kot_id IS NULL)),
    CONSTRAINT ck_order_items_voided    CHECK ((status = 'VOIDED') = (voided_at IS NOT NULL AND void_reason IS NOT NULL))
);
CREATE INDEX ix_order_items_order ON order_items (order_id, created_at);
CREATE INDEX ix_order_items_kot ON order_items (kot_id) WHERE kot_id IS NOT NULL;

-- Lines sent to the kitchen are never deleted, and only their voiding changes them. A NEW line (not sent yet) may still be
-- changed or removed by the waiter.
CREATE FUNCTION fn_order_items_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status <> 'NEW' THEN
            RAISE EXCEPTION 'order line % was sent to the kitchen and can''t be deleted; void it instead', OLD.id
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        RETURN OLD;
    END IF;
    IF OLD.status = 'VOIDED' THEN
        RAISE EXCEPTION 'order line % is voided and can''t be changed', OLD.id USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.status = 'SENT' AND (NEW.status <> 'VOIDED' OR NEW.quantity <> OLD.quantity OR NEW.unit_price <> OLD.unit_price
                                OR NEW.amount <> OLD.amount OR NEW.kot_id IS DISTINCT FROM OLD.kot_id OR NEW.menu_item_id <> OLD.menu_item_id
                                OR NEW.modifiers <> OLD.modifiers OR NEW.note IS DISTINCT FROM OLD.note) THEN
        RAISE EXCEPTION 'order line % was sent to the kitchen; it can only be voided', OLD.id USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_order_items_guard BEFORE UPDATE OR DELETE ON order_items
    FOR EACH ROW EXECUTE FUNCTION fn_order_items_guard();

-- ---------------------------------------------------------------------
-- kot_items : the ticket as the kitchen reads it
-- ---------------------------------------------------------------------
CREATE TABLE kot_items (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    kot_id           uuid        NOT NULL REFERENCES kots (id),
    order_item_id    uuid        NOT NULL REFERENCES order_items (id),
    name             text        NOT NULL,
    quantity         smallint    NOT NULL,
    modifiers        text        NULL,
    note             text        NULL,
    voided_at        timestamptz NULL,
    void_reason      text        NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_kot_items_quantity CHECK (quantity BETWEEN 1 AND 99),
    CONSTRAINT ck_kot_items_voided   CHECK ((voided_at IS NULL) = (void_reason IS NULL)),
    CONSTRAINT ux_kot_items_line     UNIQUE (order_item_id)
);
CREATE INDEX ix_kot_items_kot ON kot_items (kot_id);
-- Only the void columns of a ticket line ever change.
CREATE FUNCTION fn_kot_items_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' OR OLD.voided_at IS NOT NULL OR NEW.kot_id <> OLD.kot_id OR NEW.order_item_id <> OLD.order_item_id
       OR NEW.name <> OLD.name OR NEW.quantity <> OLD.quantity OR NEW.modifiers IS DISTINCT FROM OLD.modifiers
       OR NEW.note IS DISTINCT FROM OLD.note THEN
        RAISE EXCEPTION 'kitchen ticket lines can''t be changed, only voided' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_kot_items_guard BEFORE UPDATE OR DELETE ON kot_items
    FOR EACH ROW EXECUTE FUNCTION fn_kot_items_guard();

-- ---------------------------------------------------------------------
-- cash_drawer_movements : cash taken for a restaurant bill (POS_SALE)
-- ---------------------------------------------------------------------
ALTER TABLE cash_drawer_movements ADD COLUMN order_id uuid NULL REFERENCES orders (id);
ALTER TABLE cash_drawer_movements DROP CONSTRAINT ck_cash_drawer_movements_type;
ALTER TABLE cash_drawer_movements ADD CONSTRAINT ck_cash_drawer_movements_type
    CHECK (movement_type IN ('OPENING_FLOAT','PAYMENT','REFUND','VOID','PAY_IN','PAY_OUT','POS_SALE'));
ALTER TABLE cash_drawer_movements DROP CONSTRAINT ck_cash_drawer_movements_amount;
ALTER TABLE cash_drawer_movements ADD CONSTRAINT ck_cash_drawer_movements_amount CHECK (
        (movement_type IN ('OPENING_FLOAT') AND amount >= 0)
     OR (movement_type IN ('PAYMENT','PAY_IN','POS_SALE') AND amount > 0)
     OR (movement_type IN ('REFUND','VOID','PAY_OUT') AND amount < 0));
ALTER TABLE cash_drawer_movements ADD CONSTRAINT ck_cash_drawer_movements_order
    CHECK ((movement_type = 'POS_SALE') = (order_id IS NOT NULL));
CREATE UNIQUE INDEX ux_cash_drawer_movements_order ON cash_drawer_movements (order_id) WHERE order_id IS NOT NULL;

-- ---------------------------------------------------------------------
-- folio_items : a restaurant bill charged to a room is one line with source RESTAURANT
-- ---------------------------------------------------------------------
ALTER TABLE folio_items DROP CONSTRAINT ck_folio_items_source;
ALTER TABLE folio_items ADD CONSTRAINT ck_folio_items_source CHECK (source IN ('MANUAL','ROOM_NIGHT','PACKAGE','RESTAURANT'));

-- Data classification (brief §59).
COMMENT ON TABLE menu_categories    IS 'class: internal. Sections of the restaurant menu.';
COMMENT ON TABLE menu_items         IS 'class: internal. Dishes and drinks with their price before GST.';
COMMENT ON TABLE menu_item_variants IS 'class: internal. Portions or sizes with their own price.';
COMMENT ON TABLE modifier_groups    IS 'class: internal. Choices on a dish (spice level, add-ons).';
COMMENT ON TABLE modifiers          IS 'class: internal. One choice and what it adds to the price.';
COMMENT ON TABLE restaurant_tables  IS 'class: internal. Restaurant tables; at most one open order each.';
COMMENT ON TABLE orders             IS 'class: confidential. Restaurant bills; settled by payment or a room charge. Never deleted.';
COMMENT ON TABLE order_items        IS 'class: internal. Order lines with the price at the time; sent lines are only voided.';
COMMENT ON TABLE kots               IS 'class: internal. Kitchen order tickets.';
COMMENT ON TABLE kot_items          IS 'class: internal. Lines of a kitchen ticket; only voided, never changed.';
