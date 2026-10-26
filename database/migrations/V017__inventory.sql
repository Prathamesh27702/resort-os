-- =====================================================================
-- V017  Inventory and recipe costing (Sprint 15, brief §36, §37, §58)
--
-- Model
--   inventory_units       the units stock is counted in: g, kg, ml, l, pcs, dozen. One list for everyone (like permissions);
--                         each unit has a dimension (MASS, VOLUME, COUNT) and how many base units it holds (kg = 1000 g),
--                         so a recipe can say 200 g of paneer that is stocked in kg.
--   inventory_categories  groups of stock items per property (Dairy, Vegetables, Spices …), in display order.
--   stock_locations       where stock is kept per property (Main store, Kitchen). One location is the kitchen: dishes
--                         served by the restaurant use their ingredients from it.
--   inventory_items       what is stocked: code, name, the unit it is counted in, what one unit costs before GST (a moving
--                         average, updated when stock is received) and the level below which it should be reordered.
--   stock_movements       the ledger. Append-only: every change of stock is one row with a signed quantity in the item's
--                         unit and its value at the cost of the time. Types: OPENING, RECEIVED, CONSUMPTION (a served dish's
--                         recipe), WASTAGE, ADJUSTMENT (a correction), COUNT (a physical count's difference), TRANSFER_OUT /
--                         TRANSFER_IN (between locations).
--   stock_levels          on hand per item and location, materialised: kept by a trigger on stock_movements, so on hand is
--                         always the sum of the movements (one atomic UPSERT per movement: two changes at once both count).
--   stock_adjustments     a person's change of stock with its reason (required, brief §58): stock received, wastage,
--                         expired, damaged, a correction or a transfer. Numbered SA00001 … per property. Never changed.
--   stock_counts          a physical count of one item at one location: what the system expected, what was counted, the
--                         difference (posted as a COUNT movement) and why. Numbered SC00001 … per property. Never changed.
--   recipes               what one portion of a dish uses. A recipe belongs to a menu item, or to one of its portions
--                         (variant) — a portion's own recipe wins over the dish's.
--   recipe_ingredients    one stock item and how much of it, in any unit of the item's dimension.
--
-- Costing: a recipe's cost = Σ ingredient quantity (in the item's unit) × the item's unit cost. Food cost % = cost / price
-- × 100; contribution = price − cost; margin % = contribution / price × 100 (prices before GST).
-- =====================================================================

-- ---------------------------------------------------------------------
-- inventory_units : one fixed list
-- ---------------------------------------------------------------------
CREATE TABLE inventory_units (
    id         uuid           PRIMARY KEY DEFAULT gen_random_uuid(),
    code       text           NOT NULL UNIQUE,
    name       text           NOT NULL,
    dimension  text           NOT NULL,
    factor     numeric(12,4)  NOT NULL,
    sort_order integer        NOT NULL DEFAULT 0,
    CONSTRAINT ck_inventory_units_code      CHECK (code ~ '^[a-z]{1,10}$'),
    CONSTRAINT ck_inventory_units_dimension CHECK (dimension IN ('MASS','VOLUME','COUNT')),
    CONSTRAINT ck_inventory_units_factor    CHECK (factor > 0)
);
INSERT INTO inventory_units (code, name, dimension, factor, sort_order) VALUES
    ('kg',    'Kilogram',   'MASS',   1000, 1),
    ('g',     'Gram',       'MASS',   1,    2),
    ('l',     'Litre',      'VOLUME', 1000, 3),
    ('ml',    'Millilitre', 'VOLUME', 1,    4),
    ('pcs',   'Piece',      'COUNT',  1,    5),
    ('dozen', 'Dozen',      'COUNT',  12,   6);
CREATE TRIGGER trg_inventory_units_fixed BEFORE UPDATE OR DELETE ON inventory_units
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- ---------------------------------------------------------------------
-- inventory_categories
-- ---------------------------------------------------------------------
CREATE TABLE inventory_categories (
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
    CONSTRAINT ck_inventory_categories_name    CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT ux_inventory_categories_id_prop UNIQUE (id, property_id)
);
CREATE UNIQUE INDEX ux_inventory_categories_name ON inventory_categories (property_id, lower(name));
CREATE TRIGGER trg_inventory_categories_no_delete BEFORE DELETE ON inventory_categories
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- stock_locations
-- ---------------------------------------------------------------------
CREATE TABLE stock_locations (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    code             text        NOT NULL,
    name             text        NOT NULL,
    is_kitchen       boolean     NOT NULL DEFAULT false,
    sort_order       integer     NOT NULL DEFAULT 0,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_stock_locations_code    CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,19}$'),
    CONSTRAINT ck_stock_locations_name    CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT ck_stock_locations_kitchen CHECK (NOT is_kitchen OR is_active),
    CONSTRAINT ux_stock_locations_code    UNIQUE (property_id, code),
    CONSTRAINT ux_stock_locations_id_prop UNIQUE (id, property_id)
);
-- At most one kitchen per property: served dishes use their ingredients from it.
CREATE UNIQUE INDEX ux_stock_locations_kitchen ON stock_locations (property_id) WHERE is_kitchen;
CREATE TRIGGER trg_stock_locations_no_delete BEFORE DELETE ON stock_locations
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- inventory_items
-- ---------------------------------------------------------------------
CREATE TABLE inventory_items (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    category_id      uuid          NOT NULL,
    code             text          NOT NULL,
    name             text          NOT NULL,
    unit_id          uuid          NOT NULL REFERENCES inventory_units (id),
    unit_cost        numeric(12,4) NOT NULL DEFAULT 0,
    reorder_level    numeric(14,3) NULL,
    is_active        boolean       NOT NULL DEFAULT true,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    updated_at       timestamptz   NOT NULL DEFAULT now(),
    created_by       uuid          NULL,
    updated_by       uuid          NULL,
    CONSTRAINT ck_inventory_items_code     CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,19}$'),
    CONSTRAINT ck_inventory_items_name     CHECK (length(btrim(name)) BETWEEN 2 AND 80),
    CONSTRAINT ck_inventory_items_cost     CHECK (unit_cost >= 0 AND unit_cost <= 1000000),
    CONSTRAINT ck_inventory_items_reorder  CHECK (reorder_level IS NULL OR reorder_level >= 0),
    CONSTRAINT fk_inventory_items_category FOREIGN KEY (category_id, property_id) REFERENCES inventory_categories (id, property_id),
    CONSTRAINT ux_inventory_items_code     UNIQUE (property_id, code),
    CONSTRAINT ux_inventory_items_id_prop  UNIQUE (id, property_id)
);
CREATE INDEX ix_inventory_items_category ON inventory_items (category_id, name);
CREATE TRIGGER trg_inventory_items_no_delete BEFORE DELETE ON inventory_items
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- stock_adjustments : a person's change of stock, with its reason
-- ---------------------------------------------------------------------
CREATE TABLE stock_adjustments (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    number           text          NOT NULL,
    kind             text          NOT NULL,
    item_id          uuid          NOT NULL,
    location_id      uuid          NOT NULL,
    to_location_id   uuid          NULL,
    quantity         numeric(14,3) NOT NULL,
    unit_cost        numeric(12,4) NOT NULL,
    reason           text          NOT NULL,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    created_by       uuid          NULL REFERENCES users (id),
    CONSTRAINT ck_stock_adjustments_number   CHECK (number ~ '^SA[0-9]{5,}$'),
    CONSTRAINT ck_stock_adjustments_kind     CHECK (kind IN ('RECEIVED','WASTAGE','EXPIRED','DAMAGED','CORRECTION','TRANSFER')),
    -- Quantity is as applied to location_id: stock in is positive, stock out negative; a correction either way.
    CONSTRAINT ck_stock_adjustments_sign     CHECK (quantity <> 0
                                                AND (kind <> 'RECEIVED' OR quantity > 0)
                                                AND (kind NOT IN ('WASTAGE','EXPIRED','DAMAGED','TRANSFER') OR quantity < 0)),
    CONSTRAINT ck_stock_adjustments_transfer CHECK ((kind = 'TRANSFER') = (to_location_id IS NOT NULL) AND to_location_id IS DISTINCT FROM location_id),
    CONSTRAINT ck_stock_adjustments_cost     CHECK (unit_cost >= 0),
    CONSTRAINT ck_stock_adjustments_reason   CHECK (length(btrim(reason)) BETWEEN 3 AND 300),
    CONSTRAINT fk_stock_adjustments_item     FOREIGN KEY (item_id, property_id) REFERENCES inventory_items (id, property_id),
    CONSTRAINT fk_stock_adjustments_location FOREIGN KEY (location_id, property_id) REFERENCES stock_locations (id, property_id),
    CONSTRAINT fk_stock_adjustments_to       FOREIGN KEY (to_location_id, property_id) REFERENCES stock_locations (id, property_id),
    CONSTRAINT ux_stock_adjustments_number   UNIQUE (property_id, number)
);
CREATE INDEX ix_stock_adjustments_item ON stock_adjustments (item_id, created_at DESC);
CREATE TRIGGER trg_stock_adjustments_append_only BEFORE UPDATE OR DELETE ON stock_adjustments
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- ---------------------------------------------------------------------
-- stock_counts : one counted item at one location
-- ---------------------------------------------------------------------
CREATE TABLE stock_counts (
    id                uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id   uuid          NOT NULL REFERENCES organizations (id),
    property_id       uuid          NOT NULL REFERENCES properties (id),
    number            text          NOT NULL,
    item_id           uuid          NOT NULL,
    location_id       uuid          NOT NULL,
    system_quantity   numeric(14,3) NOT NULL,
    counted_quantity  numeric(14,3) NOT NULL,
    difference        numeric(14,3) NOT NULL,
    unit_cost         numeric(12,4) NOT NULL,
    reason            text          NULL,
    counted_at        timestamptz   NOT NULL DEFAULT now(),
    created_by        uuid          NULL REFERENCES users (id),
    CONSTRAINT ck_stock_counts_number     CHECK (number ~ '^SC[0-9]{5,}$'),
    CONSTRAINT ck_stock_counts_counted    CHECK (counted_quantity >= 0),
    CONSTRAINT ck_stock_counts_difference CHECK (difference = counted_quantity - system_quantity),
    -- A count that changes stock says why (brief §58).
    CONSTRAINT ck_stock_counts_reason     CHECK ((difference = 0 AND reason IS NULL) OR length(btrim(reason)) BETWEEN 3 AND 300),
    CONSTRAINT fk_stock_counts_item       FOREIGN KEY (item_id, property_id) REFERENCES inventory_items (id, property_id),
    CONSTRAINT fk_stock_counts_location   FOREIGN KEY (location_id, property_id) REFERENCES stock_locations (id, property_id),
    CONSTRAINT ux_stock_counts_number     UNIQUE (property_id, number)
);
CREATE INDEX ix_stock_counts_item ON stock_counts (item_id, counted_at DESC);
CREATE TRIGGER trg_stock_counts_append_only BEFORE UPDATE OR DELETE ON stock_counts
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- ---------------------------------------------------------------------
-- recipes / recipe_ingredients
-- ---------------------------------------------------------------------
CREATE TABLE recipes (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    menu_item_id     uuid        NOT NULL,
    variant_id       uuid        NULL REFERENCES menu_item_variants (id),
    note             text        NULL,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_recipes_note      CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 500),
    CONSTRAINT fk_recipes_menu_item FOREIGN KEY (menu_item_id, property_id) REFERENCES menu_items (id, property_id)
);
-- One recipe for a dish, and one for each of its portions.
CREATE UNIQUE INDEX ux_recipes_item    ON recipes (menu_item_id) WHERE variant_id IS NULL;
CREATE UNIQUE INDEX ux_recipes_variant ON recipes (variant_id) WHERE variant_id IS NOT NULL;
CREATE TRIGGER trg_recipes_no_delete BEFORE DELETE ON recipes
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

CREATE TABLE recipe_ingredients (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    recipe_id        uuid          NOT NULL REFERENCES recipes (id),
    item_id          uuid          NOT NULL REFERENCES inventory_items (id),
    quantity         numeric(12,3) NOT NULL,
    unit_id          uuid          NOT NULL REFERENCES inventory_units (id),
    sort_order       integer       NOT NULL DEFAULT 0,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_recipe_ingredients_quantity CHECK (quantity > 0 AND quantity <= 100000),
    CONSTRAINT ux_recipe_ingredients_item     UNIQUE (recipe_id, item_id)
);
CREATE INDEX ix_recipe_ingredients_item ON recipe_ingredients (item_id);

-- ---------------------------------------------------------------------
-- stock_movements : the append-only ledger
-- ---------------------------------------------------------------------
CREATE TABLE stock_movements (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    item_id          uuid          NOT NULL,
    location_id      uuid          NOT NULL,
    movement_type    text          NOT NULL,
    quantity         numeric(14,3) NOT NULL,
    unit_cost        numeric(12,4) NOT NULL,
    value            numeric(14,2) NOT NULL,
    on_hand_after    numeric(14,3) NOT NULL,
    note             text          NULL,
    adjustment_id    uuid          NULL REFERENCES stock_adjustments (id),
    count_id         uuid          NULL REFERENCES stock_counts (id),
    order_item_id    uuid          NULL REFERENCES order_items (id),
    kot_id           uuid          NULL REFERENCES kots (id),
    created_at       timestamptz   NOT NULL DEFAULT now(),
    created_by       uuid          NULL REFERENCES users (id),
    CONSTRAINT ck_stock_movements_type   CHECK (movement_type IN ('OPENING','RECEIVED','CONSUMPTION','WASTAGE','ADJUSTMENT','COUNT','TRANSFER_OUT','TRANSFER_IN')),
    CONSTRAINT ck_stock_movements_sign   CHECK (quantity <> 0
                                            AND (movement_type NOT IN ('OPENING','RECEIVED','TRANSFER_IN') OR quantity > 0)
                                            AND (movement_type NOT IN ('CONSUMPTION','WASTAGE','TRANSFER_OUT') OR quantity < 0)),
    CONSTRAINT ck_stock_movements_value  CHECK (unit_cost >= 0 AND value = round(quantity * unit_cost, 2)),
    CONSTRAINT ck_stock_movements_source CHECK ((movement_type = 'CONSUMPTION') = (order_item_id IS NOT NULL)
                                            AND (movement_type <> 'COUNT' OR count_id IS NOT NULL)
                                            AND (movement_type NOT IN ('RECEIVED','WASTAGE','ADJUSTMENT','TRANSFER_OUT','TRANSFER_IN') OR adjustment_id IS NOT NULL)),
    CONSTRAINT fk_stock_movements_item     FOREIGN KEY (item_id, property_id) REFERENCES inventory_items (id, property_id),
    CONSTRAINT fk_stock_movements_location FOREIGN KEY (location_id, property_id) REFERENCES stock_locations (id, property_id)
);
CREATE INDEX ix_stock_movements_item ON stock_movements (item_id, created_at DESC);
CREATE INDEX ix_stock_movements_property ON stock_movements (property_id, created_at DESC);
CREATE INDEX ix_stock_movements_order_item ON stock_movements (order_item_id) WHERE order_item_id IS NOT NULL;
-- A served dish uses each ingredient once.
CREATE UNIQUE INDEX ux_stock_movements_consumption ON stock_movements (order_item_id, item_id) WHERE movement_type = 'CONSUMPTION';
CREATE TRIGGER trg_stock_movements_append_only BEFORE UPDATE OR DELETE ON stock_movements
    FOR EACH ROW EXECUTE FUNCTION fn_reject_modification();

-- ---------------------------------------------------------------------
-- stock_levels : on hand per item and location = the sum of its movements
-- ---------------------------------------------------------------------
CREATE TABLE stock_levels (
    item_id          uuid          NOT NULL REFERENCES inventory_items (id),
    location_id      uuid          NOT NULL REFERENCES stock_locations (id),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    on_hand          numeric(14,3) NOT NULL DEFAULT 0,
    updated_at       timestamptz   NOT NULL DEFAULT now(),
    PRIMARY KEY (item_id, location_id)
);
CREATE INDEX ix_stock_levels_property ON stock_levels (property_id);

-- Every movement adds its quantity to its level in the same statement (one atomic UPSERT, so two changes at once both
-- count) and records the level after it on the movement.
CREATE FUNCTION fn_stock_movements_level() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO stock_levels (item_id, location_id, organization_id, property_id, on_hand, updated_at)
    VALUES (NEW.item_id, NEW.location_id, NEW.organization_id, NEW.property_id, NEW.quantity, now())
    ON CONFLICT (item_id, location_id) DO UPDATE SET on_hand = stock_levels.on_hand + EXCLUDED.on_hand, updated_at = now()
    RETURNING on_hand INTO NEW.on_hand_after;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_stock_movements_level BEFORE INSERT ON stock_movements
    FOR EACH ROW EXECUTE FUNCTION fn_stock_movements_level();

-- Data classification (brief §59).
COMMENT ON TABLE inventory_units      IS 'class: public. Units stock is counted in (fixed list).';
COMMENT ON TABLE inventory_categories IS 'class: internal. Groups of stock items.';
COMMENT ON TABLE stock_locations      IS 'class: internal. Stores; one is the kitchen.';
COMMENT ON TABLE inventory_items      IS 'class: internal. Stock items with unit cost before GST.';
COMMENT ON TABLE stock_movements      IS 'class: internal. Append-only stock ledger.';
COMMENT ON TABLE stock_levels         IS 'class: internal. On hand per item and location; kept by a trigger on stock_movements.';
COMMENT ON TABLE stock_adjustments    IS 'class: internal. Stock changes with their reason; never changed.';
COMMENT ON TABLE stock_counts         IS 'class: internal. Physical counts and their differences; never changed.';
COMMENT ON TABLE recipes              IS 'class: confidential. What a dish uses (its cost is business-sensitive).';
COMMENT ON TABLE recipe_ingredients   IS 'class: confidential. Ingredients of a recipe.';
