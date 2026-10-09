-- =====================================================================
-- V004  Accommodation: room types, rooms/dormitories, beds, amenities, images, blocks
-- Sprint 2 (brief §13)
--
-- Model
--   accommodation_types   what is sold: "Deluxe room", "Dormitory". inventory_mode decides HOW it is sold:
--                         UNIT = the whole unit is one sellable item (rooms); BED = each bed is sold (dormitory).
--   accommodation_units   the physical unit: Room 101 … Room 106, Dormitory.
--   beds                  only inside units whose type is BED-mode (enforced by trigger).
--   unit_blocks           a unit or a single bed taken out of sale for a date range (maintenance, owner use…).
--                         Dates are the PROPERTY's local calendar dates, [start_date, end_date) — end is exclusive,
--                         like a check-out date. Never converted to UTC.
--
-- Integrity
--   * composite FKs keep every unit/bed/block inside the same property as its parent
--   * EXCLUDE constraints (btree_gist) stop two active blocks overlapping on the same unit or the same bed
--   * a trigger stops a whole-unit block overlapping a bed block in that unit (and vice versa), taking a row
--     lock on the unit so concurrent requests serialise
--   * nothing here is deleted: units/types/beds are deactivated, blocks are released (history is kept)
-- =====================================================================

CREATE EXTENSION IF NOT EXISTS btree_gist;

-- ---------------------------------------------------------------------
-- amenities : catalogue. organization_id NULL = built-in, shared by everyone.
-- ---------------------------------------------------------------------
CREATE TABLE amenities (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NULL REFERENCES organizations (id),
    code             text        NOT NULL,
    name             text        NOT NULL,
    category         text        NOT NULL,
    sort_order       integer     NOT NULL DEFAULT 0,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_amenities_code     CHECK (code ~ '^[A-Z0-9_]{2,40}$'),
    CONSTRAINT ck_amenities_name     CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    CONSTRAINT ck_amenities_category CHECK (category IN ('ROOM','BATHROOM','VIEW','ACCESSIBILITY','DORMITORY','OTHER'))
);
CREATE UNIQUE INDEX ux_amenities_system_code ON amenities (code) WHERE organization_id IS NULL;
CREATE UNIQUE INDEX ux_amenities_org_code    ON amenities (organization_id, code) WHERE organization_id IS NOT NULL;

INSERT INTO amenities (code, name, category, sort_order) VALUES
('AIR_CONDITIONING',      'Air conditioning',            'ROOM',          10),
('CEILING_FAN',           'Ceiling fan',                 'ROOM',          20),
('WIFI',                  'Wi-Fi',                       'ROOM',          30),
('TELEVISION',            'Television',                  'ROOM',          40),
('MINI_FRIDGE',           'Mini fridge',                 'ROOM',          50),
('KETTLE',                'Kettle with tea/coffee',      'ROOM',          60),
('WARDROBE',              'Wardrobe',                    'ROOM',          70),
('WORK_DESK',             'Work desk',                   'ROOM',          80),
('MOSQUITO_NET',          'Mosquito net',                'ROOM',          90),
('ATTACHED_BATHROOM',     'Attached bathroom',           'BATHROOM',     110),
('SHARED_BATHROOM',       'Shared bathroom',             'BATHROOM',     120),
('HOT_WATER',             'Hot water',                   'BATHROOM',     130),
('TOILETRIES',            'Toiletries',                  'BATHROOM',     140),
('BALCONY',               'Balcony',                     'VIEW',         210),
('GARDEN_VIEW',           'Garden view',                 'VIEW',         220),
('POOL_VIEW',             'Pool view',                   'VIEW',         230),
('LOCKER',                'Personal locker',             'DORMITORY',    310),
('READING_LIGHT',         'Reading light per bed',       'DORMITORY',    320),
('POWER_SOCKET_PER_BED',  'Power socket per bed',        'DORMITORY',    330),
('PRIVACY_CURTAIN',       'Privacy curtain',             'DORMITORY',    340),
('WHEELCHAIR_ACCESSIBLE', 'Wheelchair accessible',       'ACCESSIBILITY',410),
('GROUND_FLOOR',          'Ground floor',                'ACCESSIBILITY',420);

-- ---------------------------------------------------------------------
-- accommodation_types
-- ---------------------------------------------------------------------
CREATE TABLE accommodation_types (
    id               uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid         NOT NULL REFERENCES organizations (id),
    property_id      uuid         NOT NULL REFERENCES properties (id),
    code             text         NOT NULL,
    name             text         NOT NULL,
    description      text         NULL,
    inventory_mode   text         NOT NULL,
    base_occupancy   smallint     NOT NULL DEFAULT 2,
    max_occupancy    smallint     NOT NULL DEFAULT 2,
    size_sqm         numeric(6,1) NULL,
    sort_order       integer      NOT NULL DEFAULT 0,
    is_active        boolean      NOT NULL DEFAULT true,
    created_at       timestamptz  NOT NULL DEFAULT now(),
    updated_at       timestamptz  NOT NULL DEFAULT now(),
    created_by       uuid         NULL,
    updated_by       uuid         NULL,
    CONSTRAINT ck_accommodation_types_code      CHECK (code ~ '^[A-Z0-9_]{2,30}$'),
    CONSTRAINT ck_accommodation_types_name      CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    CONSTRAINT ck_accommodation_types_desc      CHECK (description IS NULL OR length(description) <= 2000),
    CONSTRAINT ck_accommodation_types_mode      CHECK (inventory_mode IN ('UNIT','BED')),
    CONSTRAINT ck_accommodation_types_occupancy CHECK (base_occupancy >= 1 AND base_occupancy <= max_occupancy AND max_occupancy <= 50),
    -- a bed sleeps one guest
    CONSTRAINT ck_accommodation_types_bed_occ   CHECK (inventory_mode = 'UNIT' OR (base_occupancy = 1 AND max_occupancy = 1)),
    CONSTRAINT ck_accommodation_types_size      CHECK (size_sqm IS NULL OR size_sqm > 0),
    CONSTRAINT ux_accommodation_types_code      UNIQUE (property_id, code),
    CONSTRAINT ux_accommodation_types_id_prop   UNIQUE (id, property_id)          -- target of composite FKs
);
CREATE INDEX ix_accommodation_types_org ON accommodation_types (organization_id);

CREATE TABLE accommodation_type_amenities (
    accommodation_type_id  uuid NOT NULL REFERENCES accommodation_types (id) ON DELETE CASCADE,
    amenity_id             uuid NOT NULL REFERENCES amenities (id),
    PRIMARY KEY (accommodation_type_id, amenity_id)
);
CREATE INDEX ix_accommodation_type_amenities_amenity ON accommodation_type_amenities (amenity_id);

-- ---------------------------------------------------------------------
-- accommodation_units : rooms and dormitories
-- ---------------------------------------------------------------------
CREATE TABLE accommodation_units (
    id                     uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id        uuid        NOT NULL REFERENCES organizations (id),
    property_id            uuid        NOT NULL REFERENCES properties (id),
    accommodation_type_id  uuid        NOT NULL,
    code                   text        NOT NULL,
    name                   text        NOT NULL,
    floor                  text        NULL,
    notes                  text        NULL,
    sort_order             integer     NOT NULL DEFAULT 0,
    is_active              boolean     NOT NULL DEFAULT true,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now(),
    created_by             uuid        NULL,
    updated_by             uuid        NULL,
    CONSTRAINT ck_accommodation_units_code  CHECK (code ~ '^[A-Z0-9_-]{1,20}$'),
    CONSTRAINT ck_accommodation_units_name  CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    CONSTRAINT ck_accommodation_units_floor CHECK (floor IS NULL OR length(floor) <= 20),
    CONSTRAINT ck_accommodation_units_notes CHECK (notes IS NULL OR length(notes) <= 1000),
    CONSTRAINT fk_accommodation_units_type  FOREIGN KEY (accommodation_type_id, property_id)
                                            REFERENCES accommodation_types (id, property_id),
    CONSTRAINT ux_accommodation_units_code    UNIQUE (property_id, code),
    CONSTRAINT ux_accommodation_units_id_prop UNIQUE (id, property_id)
);
CREATE INDEX ix_accommodation_units_type ON accommodation_units (accommodation_type_id);
CREATE INDEX ix_accommodation_units_org  ON accommodation_units (organization_id);

-- ---------------------------------------------------------------------
-- beds : individual sellable beds inside a BED-mode unit
-- ---------------------------------------------------------------------
CREATE TABLE beds (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    unit_id          uuid        NOT NULL,
    code             text        NOT NULL,
    name             text        NOT NULL,
    bed_type         text        NOT NULL DEFAULT 'SINGLE',
    sort_order       integer     NOT NULL DEFAULT 0,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_beds_code     CHECK (code ~ '^[A-Z0-9_-]{1,20}$'),
    CONSTRAINT ck_beds_name     CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    CONSTRAINT ck_beds_type     CHECK (bed_type IN ('SINGLE','BUNK_LOWER','BUNK_UPPER','DOUBLE')),
    CONSTRAINT fk_beds_unit     FOREIGN KEY (unit_id, property_id) REFERENCES accommodation_units (id, property_id),
    CONSTRAINT ux_beds_code     UNIQUE (unit_id, code),
    CONSTRAINT ux_beds_id_unit  UNIQUE (id, unit_id)
);
CREATE INDEX ix_beds_property ON beds (property_id);

-- Beds are only allowed in units whose type sells by the bed.
CREATE FUNCTION fn_beds_require_bed_mode() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (SELECT t.inventory_mode
          FROM accommodation_units u
          JOIN accommodation_types t ON t.id = u.accommodation_type_id
         WHERE u.id = NEW.unit_id) IS DISTINCT FROM 'BED' THEN
        RAISE EXCEPTION 'Beds can only be added to bed-based (dormitory) units'
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_beds_bed_mode_unit';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_beds_require_bed_mode BEFORE INSERT OR UPDATE OF unit_id ON beds
    FOR EACH ROW EXECUTE FUNCTION fn_beds_require_bed_mode();

-- A unit that has beds can't be moved to a UNIT-mode type.
CREATE FUNCTION fn_units_keep_bed_mode() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.accommodation_type_id IS DISTINCT FROM OLD.accommodation_type_id
       AND EXISTS (SELECT 1 FROM beds WHERE unit_id = NEW.id)
       AND (SELECT inventory_mode FROM accommodation_types WHERE id = NEW.accommodation_type_id) <> 'BED' THEN
        RAISE EXCEPTION 'Unit % has beds, so its type must stay bed-based', NEW.code
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_units_with_beds_bed_mode';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_units_keep_bed_mode BEFORE UPDATE OF accommodation_type_id ON accommodation_units
    FOR EACH ROW EXECUTE FUNCTION fn_units_keep_bed_mode();

-- A type whose units have beds can't switch to UNIT mode.
CREATE FUNCTION fn_types_keep_bed_mode() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.inventory_mode = 'UNIT' AND OLD.inventory_mode = 'BED'
       AND EXISTS (SELECT 1 FROM beds b JOIN accommodation_units u ON u.id = b.unit_id
                    WHERE u.accommodation_type_id = NEW.id) THEN
        RAISE EXCEPTION 'Type % has units with beds, so it must stay bed-based', NEW.code
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_types_with_beds_bed_mode';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_types_keep_bed_mode BEFORE UPDATE OF inventory_mode ON accommodation_types
    FOR EACH ROW EXECUTE FUNCTION fn_types_keep_bed_mode();

-- ---------------------------------------------------------------------
-- unit_images : photo metadata for a type OR a unit. The file itself lives in blob storage
-- (upload API arrives with the file-storage service in Sprint 3).
-- ---------------------------------------------------------------------
CREATE TABLE unit_images (
    id                     uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id        uuid        NOT NULL REFERENCES organizations (id),
    property_id            uuid        NOT NULL REFERENCES properties (id),
    accommodation_type_id  uuid        NULL REFERENCES accommodation_types (id),
    unit_id                uuid        NULL REFERENCES accommodation_units (id),
    storage_key            text        NOT NULL,
    caption                text        NULL,
    sort_order             integer     NOT NULL DEFAULT 0,
    created_at             timestamptz NOT NULL DEFAULT now(),
    created_by             uuid        NULL,
    CONSTRAINT ck_unit_images_owner   CHECK ((accommodation_type_id IS NULL) <> (unit_id IS NULL)),
    CONSTRAINT ck_unit_images_key     CHECK (length(storage_key) BETWEEN 1 AND 500),
    CONSTRAINT ck_unit_images_caption CHECK (caption IS NULL OR length(caption) <= 200)
);
CREATE INDEX ix_unit_images_type ON unit_images (accommodation_type_id) WHERE accommodation_type_id IS NOT NULL;
CREATE INDEX ix_unit_images_unit ON unit_images (unit_id) WHERE unit_id IS NOT NULL;

-- ---------------------------------------------------------------------
-- unit_blocks : take a unit (bed_id NULL) or one bed out of sale for [start_date, end_date)
-- ---------------------------------------------------------------------
CREATE TABLE unit_blocks (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    unit_id          uuid        NOT NULL,
    bed_id           uuid        NULL,
    block_type       text        NOT NULL,
    start_date       date        NOT NULL,
    end_date         date        NOT NULL,
    period           daterange   GENERATED ALWAYS AS (daterange(start_date, end_date, '[)')) STORED,
    reason           text        NOT NULL,
    status           text        NOT NULL DEFAULT 'ACTIVE',
    released_at      timestamptz NULL,
    released_by      uuid        NULL,
    release_reason   text        NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_unit_blocks_type    CHECK (block_type IN ('MAINTENANCE','OUT_OF_ORDER','OWNER_USE','HOLD','OTHER')),
    CONSTRAINT ck_unit_blocks_dates   CHECK (end_date > start_date AND end_date - start_date <= 366),
    CONSTRAINT ck_unit_blocks_reason  CHECK (length(btrim(reason)) BETWEEN 3 AND 500),
    CONSTRAINT ck_unit_blocks_status  CHECK (
        (status = 'ACTIVE'   AND released_at IS NULL AND release_reason IS NULL) OR
        (status = 'RELEASED' AND released_at IS NOT NULL AND release_reason IS NOT NULL
                             AND length(btrim(release_reason)) BETWEEN 3 AND 500)),
    CONSTRAINT fk_unit_blocks_unit    FOREIGN KEY (unit_id, property_id) REFERENCES accommodation_units (id, property_id),
    CONSTRAINT fk_unit_blocks_bed     FOREIGN KEY (bed_id, unit_id) REFERENCES beds (id, unit_id),
    -- the final guard against double blocking (same unit, or same bed, overlapping dates)
    CONSTRAINT ex_unit_blocks_unit_overlap EXCLUDE USING gist (unit_id WITH =, period WITH &&)
        WHERE (status = 'ACTIVE' AND bed_id IS NULL),
    CONSTRAINT ex_unit_blocks_bed_overlap  EXCLUDE USING gist (bed_id WITH =, period WITH &&)
        WHERE (status = 'ACTIVE' AND bed_id IS NOT NULL)
);
CREATE INDEX ix_unit_blocks_property_active ON unit_blocks (property_id, start_date, end_date) WHERE status = 'ACTIVE';
CREATE INDEX ix_unit_blocks_unit ON unit_blocks (unit_id);

-- A whole-unit block must not overlap a bed block in the same unit, and vice versa. EXCLUDE can't express that
-- asymmetric rule, so this trigger checks it after locking the unit row: concurrent block requests for the same unit
-- wait for each other, and each plpgsql statement takes a fresh snapshot, so the check sees committed rows.
CREATE FUNCTION fn_unit_blocks_no_mixed_overlap() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status <> 'ACTIVE' THEN
        RETURN NEW;
    END IF;
    PERFORM 1 FROM accommodation_units WHERE id = NEW.unit_id FOR UPDATE;
    IF EXISTS (
        SELECT 1 FROM unit_blocks b
         WHERE b.unit_id = NEW.unit_id
           AND b.id <> NEW.id
           AND b.status = 'ACTIVE'
           AND b.period && daterange(NEW.start_date, NEW.end_date, '[)')
           AND ((NEW.bed_id IS NULL AND b.bed_id IS NOT NULL) OR (NEW.bed_id IS NOT NULL AND b.bed_id IS NULL))) THEN
        RAISE EXCEPTION 'Block overlaps an existing block on this unit'
            USING ERRCODE = 'exclusion_violation', CONSTRAINT = 'ex_unit_blocks_mixed_overlap';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_unit_blocks_no_mixed_overlap BEFORE INSERT OR UPDATE ON unit_blocks
    FOR EACH ROW EXECUTE FUNCTION fn_unit_blocks_no_mixed_overlap();

-- Blocks are history: release them, never delete them.
CREATE FUNCTION fn_reject_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Rows in % cannot be deleted', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END $$;
CREATE TRIGGER trg_unit_blocks_no_delete BEFORE DELETE ON unit_blocks
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();
