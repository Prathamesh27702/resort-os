-- =====================================================================
-- V006  Rate plans, nightly prices, packages, reservation sources
-- Sprint 4 (brief §14)
--
-- Model
--   rate_plans            how a stay is priced: "Best available rate" (BAR), "Room + breakfast". One plan per property
--                         is the default (is_default): quotes use it when no plan is named.
--   rate_plan_prices      one row per plan × room type × night (property-local date). price is per unit per night for
--                         UNIT-mode types (rooms) and per bed per night for BED-mode types (dormitories).
--                         extra_adult / extra_child are charged per guest per night above the type's standard occupancy
--                         (rooms only). min_stay is checked on the arrival night.
--   packages              a fixed-price bundle sold with a stay: N nights in a room type + food/transport/activities.
--   package_components    how a package's price is split into revenue categories. share_percent is what the manager
--                         enters; amount is computed by the API (largest-remainder rounding, see docs/DATABASE.md) so the
--                         amounts always add up to exactly the package price. A deferred trigger enforces that here too.
--   reservation_sources   where bookings come from (walk-in, phone, website, OTAs…). organization_id NULL = built-in.
--                         Used by reservations from Sprint 5.
--
-- Money is numeric(12,2) — never float. Prices are before tax (GST arrives in Sprint 12).
-- Nothing here is deleted except a package's components, which are replaced as a set when the package is edited.
-- =====================================================================

-- ---------------------------------------------------------------------
-- reservation_sources
-- ---------------------------------------------------------------------
CREATE TABLE reservation_sources (
    id                  uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id     uuid         NULL REFERENCES organizations (id),
    code                text         NOT NULL,
    name                text         NOT NULL,
    channel             text         NOT NULL,
    commission_percent  numeric(5,2) NULL,
    sort_order          integer      NOT NULL DEFAULT 0,
    is_active           boolean      NOT NULL DEFAULT true,
    created_at          timestamptz  NOT NULL DEFAULT now(),
    updated_at          timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT ck_reservation_sources_code       CHECK (code ~ '^[A-Z0-9_]{2,30}$'),
    CONSTRAINT ck_reservation_sources_name       CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    CONSTRAINT ck_reservation_sources_channel    CHECK (channel IN ('DIRECT','OTA','TRAVEL_AGENT','CORPORATE','OTHER')),
    CONSTRAINT ck_reservation_sources_commission CHECK (commission_percent IS NULL OR (commission_percent >= 0 AND commission_percent <= 100))
);
CREATE UNIQUE INDEX ux_reservation_sources_system_code ON reservation_sources (code) WHERE organization_id IS NULL;
CREATE UNIQUE INDEX ux_reservation_sources_org_code    ON reservation_sources (organization_id, code) WHERE organization_id IS NOT NULL;

INSERT INTO reservation_sources (code, name, channel, sort_order) VALUES
('WALK_IN',      'Walk-in',               'DIRECT',        10),
('PHONE',        'Phone',                 'DIRECT',        20),
('WHATSAPP',     'WhatsApp',              'DIRECT',        30),
('EMAIL',        'Email',                 'DIRECT',        40),
('WEBSITE',      'Own website',           'DIRECT',        50),
('BOOKING_COM',  'Booking.com',           'OTA',          110),
('AGODA',        'Agoda',                 'OTA',          120),
('MAKEMYTRIP',   'MakeMyTrip',            'OTA',          130),
('GOIBIBO',      'Goibibo',               'OTA',          140),
('EXPEDIA',      'Expedia',               'OTA',          150),
('AIRBNB',       'Airbnb',                'OTA',          160),
('TRAVEL_AGENT', 'Travel agent',          'TRAVEL_AGENT', 210),
('CORPORATE',    'Corporate',             'CORPORATE',    310),
('OTHER',        'Other',                 'OTHER',        900);

-- ---------------------------------------------------------------------
-- rate_plans
-- ---------------------------------------------------------------------
CREATE TABLE rate_plans (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    code             text        NOT NULL,
    name             text        NOT NULL,
    description      text        NULL,
    meal_plan        text        NOT NULL DEFAULT 'EP',
    is_default       boolean     NOT NULL DEFAULT false,
    sort_order       integer     NOT NULL DEFAULT 0,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_rate_plans_code        CHECK (code ~ '^[A-Z0-9_]{2,30}$'),
    CONSTRAINT ck_rate_plans_name        CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    CONSTRAINT ck_rate_plans_desc        CHECK (description IS NULL OR length(description) <= 1000),
    -- Indian meal-plan codes: EP room only, CP + breakfast, MAP + breakfast and one meal, AP all meals
    CONSTRAINT ck_rate_plans_meal_plan   CHECK (meal_plan IN ('EP','CP','MAP','AP')),
    CONSTRAINT ck_rate_plans_default_act CHECK (NOT is_default OR is_active),
    CONSTRAINT ux_rate_plans_code        UNIQUE (property_id, code),
    CONSTRAINT ux_rate_plans_id_prop     UNIQUE (id, property_id)
);
CREATE INDEX ix_rate_plans_org ON rate_plans (organization_id);
-- at most one default plan per property
CREATE UNIQUE INDEX ux_rate_plans_default ON rate_plans (property_id) WHERE is_default;

-- ---------------------------------------------------------------------
-- rate_plan_prices : the rate grid (plan × room type × night)
-- ---------------------------------------------------------------------
CREATE TABLE rate_plan_prices (
    id                     uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id        uuid          NOT NULL REFERENCES organizations (id),
    property_id            uuid          NOT NULL REFERENCES properties (id),
    rate_plan_id           uuid          NOT NULL,
    accommodation_type_id  uuid          NOT NULL,
    stay_date              date          NOT NULL,
    price                  numeric(12,2) NOT NULL,
    extra_adult            numeric(12,2) NOT NULL DEFAULT 0,
    extra_child            numeric(12,2) NOT NULL DEFAULT 0,
    min_stay               smallint      NOT NULL DEFAULT 1,
    created_at             timestamptz   NOT NULL DEFAULT now(),
    updated_at             timestamptz   NOT NULL DEFAULT now(),
    created_by             uuid          NULL,
    updated_by             uuid          NULL,
    CONSTRAINT ck_rate_plan_prices_price    CHECK (price >= 0 AND price <= 10000000),
    CONSTRAINT ck_rate_plan_prices_extras   CHECK (extra_adult >= 0 AND extra_adult <= 10000000
                                                   AND extra_child >= 0 AND extra_child <= 10000000),
    CONSTRAINT ck_rate_plan_prices_min_stay CHECK (min_stay BETWEEN 1 AND 30),
    CONSTRAINT fk_rate_plan_prices_plan     FOREIGN KEY (rate_plan_id, property_id) REFERENCES rate_plans (id, property_id),
    CONSTRAINT fk_rate_plan_prices_type     FOREIGN KEY (accommodation_type_id, property_id)
                                            REFERENCES accommodation_types (id, property_id),
    CONSTRAINT ux_rate_plan_prices_cell     UNIQUE (rate_plan_id, accommodation_type_id, stay_date)
);
CREATE INDEX ix_rate_plan_prices_property_date ON rate_plan_prices (property_id, stay_date);
CREATE INDEX ix_rate_plan_prices_type ON rate_plan_prices (accommodation_type_id);

-- ---------------------------------------------------------------------
-- packages
-- ---------------------------------------------------------------------
CREATE TABLE packages (
    id                     uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id        uuid          NOT NULL REFERENCES organizations (id),
    property_id            uuid          NOT NULL REFERENCES properties (id),
    accommodation_type_id  uuid          NOT NULL,
    code                   text          NOT NULL,
    name                   text          NOT NULL,
    description            text          NULL,
    nights                 smallint      NOT NULL,
    adults                 smallint      NOT NULL,
    children               smallint      NOT NULL DEFAULT 0,
    price                  numeric(12,2) NOT NULL,
    sort_order             integer       NOT NULL DEFAULT 0,
    is_active              boolean       NOT NULL DEFAULT true,
    created_at             timestamptz   NOT NULL DEFAULT now(),
    updated_at             timestamptz   NOT NULL DEFAULT now(),
    created_by             uuid          NULL,
    updated_by             uuid          NULL,
    CONSTRAINT ck_packages_code     CHECK (code ~ '^[A-Z0-9_]{2,30}$'),
    CONSTRAINT ck_packages_name     CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    CONSTRAINT ck_packages_desc     CHECK (description IS NULL OR length(description) <= 2000),
    CONSTRAINT ck_packages_nights   CHECK (nights BETWEEN 1 AND 30),
    CONSTRAINT ck_packages_guests   CHECK (adults BETWEEN 1 AND 50 AND children BETWEEN 0 AND 50),
    CONSTRAINT ck_packages_price    CHECK (price > 0 AND price <= 10000000),
    CONSTRAINT fk_packages_type     FOREIGN KEY (accommodation_type_id, property_id)
                                    REFERENCES accommodation_types (id, property_id),
    CONSTRAINT ux_packages_code     UNIQUE (property_id, code),
    CONSTRAINT ux_packages_id_prop  UNIQUE (id, property_id)
);
CREATE INDEX ix_packages_type ON packages (accommodation_type_id);
CREATE INDEX ix_packages_org  ON packages (organization_id);

-- ---------------------------------------------------------------------
-- package_components : revenue split of a package
-- ---------------------------------------------------------------------
CREATE TABLE package_components (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    package_id       uuid          NOT NULL,
    category         text          NOT NULL,
    description      text          NOT NULL,
    share_percent    numeric(5,2)  NOT NULL,
    amount           numeric(12,2) NOT NULL,
    sort_order       integer       NOT NULL DEFAULT 0,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    updated_at       timestamptz   NOT NULL DEFAULT now(),
    created_by       uuid          NULL,
    updated_by       uuid          NULL,
    CONSTRAINT ck_package_components_category CHECK (category IN ('ROOM','FOOD','TRANSPORT','ACTIVITY')),
    CONSTRAINT ck_package_components_desc     CHECK (length(btrim(description)) BETWEEN 1 AND 200),
    CONSTRAINT ck_package_components_share    CHECK (share_percent > 0 AND share_percent <= 100),
    CONSTRAINT ck_package_components_amount   CHECK (amount >= 0),
    CONSTRAINT fk_package_components_package  FOREIGN KEY (package_id, property_id)
                                              REFERENCES packages (id, property_id) ON DELETE CASCADE
);
CREATE INDEX ix_package_components_package ON package_components (package_id);

-- The split must add up: shares to exactly 100 %, amounts to exactly the package price, and there must be a ROOM
-- component. Checked at COMMIT (deferred), so a package and its components can be written in any order.
CREATE FUNCTION fn_packages_split_adds_up() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    pkg_id  uuid;
    pkg     record;
    total   numeric;
    shares  numeric;
    rooms   int;
BEGIN
    -- separate branches: plpgsql resolves NEW/OLD fields per statement, and the two tables have different columns
    IF TG_TABLE_NAME = 'packages' THEN
        pkg_id := NEW.id;
    ELSIF TG_OP = 'DELETE' THEN
        pkg_id := OLD.package_id;
    ELSE
        pkg_id := NEW.package_id;
    END IF;
    SELECT id, code, price INTO pkg FROM packages WHERE id = pkg_id;
    IF pkg.id IS NULL THEN
        RETURN NULL;  -- the package itself was deleted
    END IF;
    SELECT coalesce(sum(amount), 0), coalesce(sum(share_percent), 0), count(*) FILTER (WHERE category = 'ROOM')
      INTO total, shares, rooms
      FROM package_components WHERE package_id = pkg_id;
    IF rooms = 0 THEN
        RAISE EXCEPTION 'Package % has no ROOM component', pkg.code
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_packages_has_room';
    END IF;
    IF shares <> 100 THEN
        RAISE EXCEPTION 'Package % shares add up to % %%, not 100 %%', pkg.code, shares
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_packages_shares_100';
    END IF;
    IF total <> pkg.price THEN
        RAISE EXCEPTION 'Package % components add up to %, not the package price %', pkg.code, total, pkg.price
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_packages_split_sum';
    END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER trg_packages_split_adds_up
    AFTER INSERT OR UPDATE OF price ON packages
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fn_packages_split_adds_up();
CREATE CONSTRAINT TRIGGER trg_package_components_split_adds_up
    AFTER INSERT OR UPDATE OR DELETE ON package_components
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fn_packages_split_adds_up();

-- Packages and prices are history for reservations and reports: deactivate a package, never delete it.
CREATE TRIGGER trg_packages_no_delete BEFORE DELETE ON packages
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();
CREATE TRIGGER trg_rate_plans_no_delete BEFORE DELETE ON rate_plans
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();
