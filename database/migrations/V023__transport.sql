-- =====================================================================
-- V023  Beach transport (Sprint 21, brief §47, §73)
--
-- The resort runs a shuttle on the 8 km road to the beach. Trips are put on for each day with a vehicle and a driver,
-- guests are booked on seats (₹500 a person goes on their room bill), the driver sees the day's trips and passenger list
-- on a phone and ticks people as they board, and the passenger list prints as a manifest PDF.
--
-- Model
--   vehicles           the resort's vehicles: code, name, registration number, seats.
--   transport_routes   where a trip goes: Resort → Tarkarli beach, 8 km, 20 minutes, ₹500 a seat (what a new trip gets).
--   vehicle_trips      one trip on a route at a time, with a vehicle and a driver (a staff account that can run trips).
--                      seats_booked is kept by a trigger from the passengers and can never pass capacity (CHECK): the
--                      final guard against overbooking (the service locks the trip row first and says why). A vehicle or a
--                      driver can't be on two trips at once (EXCLUDE). SCHEDULED → DEPARTED → COMPLETED; SCHEDULED →
--                      CANCELLED (with the reason; every passenger's charge is reversed).
--   trip_passengers    TP00001…: seats on a trip for a staying guest (the price goes on the stay's main folio, one line,
--                      category TRANSPORT, source TRANSPORT), or for anyone when it is free or complimentary (with the
--                      reason). BOOKED → BOARDED (the driver ticks them; can be undone before departure) or NO_SHOW (not on
--                      board when the trip left; the charge stays); BOOKED → CANCELLED before departure, or with the whole
--                      trip (the charge is reversed).
-- =====================================================================

-- ---------------------------------------------------------------------
-- vehicles
-- ---------------------------------------------------------------------
CREATE TABLE vehicles (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid        NOT NULL REFERENCES organizations (id),
    property_id      uuid        NOT NULL REFERENCES properties (id),
    code             text        NOT NULL,
    name             text        NOT NULL,
    registration     text        NOT NULL,          -- MH07AB1234, stored without spaces, upper case
    seats            smallint    NOT NULL,          -- passenger seats, not counting the driver
    note             text        NULL,
    sort_order       integer     NOT NULL DEFAULT 0,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid        NULL,
    updated_by       uuid        NULL,
    CONSTRAINT ck_vehicles_code   CHECK (code ~ '^[A-Z][A-Z0-9]{0,9}$'),
    CONSTRAINT ck_vehicles_name   CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT ck_vehicles_reg    CHECK (registration ~ '^[A-Z0-9]{4,12}$'),
    CONSTRAINT ck_vehicles_seats  CHECK (seats BETWEEN 1 AND 60),
    CONSTRAINT ck_vehicles_note   CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 200),
    CONSTRAINT ux_vehicles_code   UNIQUE (property_id, code),
    CONSTRAINT ux_vehicles_reg    UNIQUE (organization_id, registration),
    CONSTRAINT ux_vehicles_id_prop UNIQUE (id, property_id)
);
CREATE TRIGGER trg_vehicles_no_delete BEFORE DELETE ON vehicles
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- transport_routes
-- ---------------------------------------------------------------------
CREATE TABLE transport_routes (
    id                uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id   uuid          NOT NULL REFERENCES organizations (id),
    property_id       uuid          NOT NULL REFERENCES properties (id),
    code              text          NOT NULL,
    name              text          NOT NULL,
    origin            text          NOT NULL,
    destination       text          NOT NULL,
    distance_km       numeric(5,1)  NOT NULL,
    duration_minutes  smallint      NOT NULL,
    price             numeric(10,2) NOT NULL DEFAULT 0, -- per seat, one way; a new trip gets it
    sort_order        integer       NOT NULL DEFAULT 0,
    is_active         boolean       NOT NULL DEFAULT true,
    created_at        timestamptz   NOT NULL DEFAULT now(),
    updated_at        timestamptz   NOT NULL DEFAULT now(),
    created_by        uuid          NULL,
    updated_by        uuid          NULL,
    CONSTRAINT ck_transport_routes_code     CHECK (code ~ '^[A-Z][A-Z0-9]{0,9}$'),
    CONSTRAINT ck_transport_routes_name     CHECK (length(btrim(name)) BETWEEN 2 AND 60),
    CONSTRAINT ck_transport_routes_from     CHECK (length(btrim(origin)) BETWEEN 2 AND 60),
    CONSTRAINT ck_transport_routes_to       CHECK (length(btrim(destination)) BETWEEN 2 AND 60),
    CONSTRAINT ck_transport_routes_km       CHECK (distance_km > 0 AND distance_km <= 500),
    CONSTRAINT ck_transport_routes_duration CHECK (duration_minutes BETWEEN 5 AND 360),
    CONSTRAINT ck_transport_routes_price    CHECK (price BETWEEN 0 AND 1000000),
    CONSTRAINT ux_transport_routes_code     UNIQUE (property_id, code),
    CONSTRAINT ux_transport_routes_id_prop  UNIQUE (id, property_id)
);
CREATE UNIQUE INDEX ux_transport_routes_name ON transport_routes (property_id, lower(name));
CREATE TRIGGER trg_transport_routes_no_delete BEFORE DELETE ON transport_routes
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- ---------------------------------------------------------------------
-- vehicle_trips
-- ---------------------------------------------------------------------
CREATE TABLE vehicle_trips (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id  uuid          NOT NULL REFERENCES organizations (id),
    property_id      uuid          NOT NULL REFERENCES properties (id),
    route_id         uuid          NOT NULL,
    vehicle_id       uuid          NOT NULL,
    driver_user_id   uuid          NULL REFERENCES users (id),
    trip_date        date          NOT NULL,            -- the local date at the property
    departs_at       timestamptz   NOT NULL,
    arrives_at       timestamptz   NOT NULL,
    capacity         smallint      NOT NULL,
    price            numeric(10,2) NOT NULL,
    seats_booked     smallint      NOT NULL DEFAULT 0,  -- kept by trg_trip_passengers_seats; never set by hand
    status           text          NOT NULL DEFAULT 'SCHEDULED',
    note             text          NULL,
    departed_at      timestamptz   NULL,
    departed_by      uuid          NULL REFERENCES users (id),
    completed_at     timestamptz   NULL,
    completed_by     uuid          NULL REFERENCES users (id),
    cancel_reason    text          NULL,
    cancelled_at     timestamptz   NULL,
    cancelled_by     uuid          NULL REFERENCES users (id),
    created_at       timestamptz   NOT NULL DEFAULT now(),
    updated_at       timestamptz   NOT NULL DEFAULT now(),
    created_by       uuid          NULL,
    updated_by       uuid          NULL,
    CONSTRAINT ck_vehicle_trips_times    CHECK (arrives_at > departs_at AND arrives_at - departs_at <= interval '6 hours'),
    CONSTRAINT ck_vehicle_trips_price    CHECK (price BETWEEN 0 AND 1000000),
    CONSTRAINT ck_vehicle_trips_seats    CHECK (capacity BETWEEN 1 AND 60),
    -- The overbooking guard: the seats taken can never pass the seats there are.
    CONSTRAINT ck_vehicle_trips_capacity CHECK (seats_booked BETWEEN 0 AND capacity),
    CONSTRAINT ck_vehicle_trips_status   CHECK (status IN ('SCHEDULED','DEPARTED','COMPLETED','CANCELLED')),
    CONSTRAINT ck_vehicle_trips_steps    CHECK ((status IN ('DEPARTED','COMPLETED')) = (departed_at IS NOT NULL)
                                                AND (status = 'COMPLETED') = (completed_at IS NOT NULL)
                                                AND (status = 'CANCELLED') = (cancelled_at IS NOT NULL)
                                                AND (status = 'CANCELLED') = (cancel_reason IS NOT NULL)),
    CONSTRAINT ck_vehicle_trips_reason   CHECK (cancel_reason IS NULL OR length(btrim(cancel_reason)) BETWEEN 3 AND 300),
    CONSTRAINT ck_vehicle_trips_note     CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 200),
    CONSTRAINT fk_vehicle_trips_route    FOREIGN KEY (route_id, property_id) REFERENCES transport_routes (id, property_id),
    CONSTRAINT fk_vehicle_trips_vehicle  FOREIGN KEY (vehicle_id, property_id) REFERENCES vehicles (id, property_id),
    CONSTRAINT ux_vehicle_trips_id_prop  UNIQUE (id, property_id),
    -- A vehicle, and a driver, does one trip at a time (a cancelled trip doesn't count).
    CONSTRAINT ex_vehicle_trips_vehicle  EXCLUDE USING gist (vehicle_id WITH =, tstzrange(departs_at, arrives_at) WITH &&)
                                         WHERE (status <> 'CANCELLED'),
    CONSTRAINT ex_vehicle_trips_driver   EXCLUDE USING gist (driver_user_id WITH =, tstzrange(departs_at, arrives_at) WITH &&)
                                         WHERE (status <> 'CANCELLED' AND driver_user_id IS NOT NULL)
);
CREATE INDEX ix_vehicle_trips_day ON vehicle_trips (property_id, trip_date, departs_at);
CREATE INDEX ix_vehicle_trips_driver ON vehicle_trips (driver_user_id, trip_date) WHERE driver_user_id IS NOT NULL;
CREATE TRIGGER trg_vehicle_trips_no_delete BEFORE DELETE ON vehicle_trips
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- A trip only moves forward: SCHEDULED → DEPARTED → COMPLETED, or SCHEDULED → CANCELLED (mirrored in TripStatuses.Moves).
CREATE FUNCTION fn_vehicle_trips_status() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status = OLD.status THEN RETURN NEW; END IF;
    IF NOT (OLD.status, NEW.status) IN (('SCHEDULED','DEPARTED'), ('DEPARTED','COMPLETED'), ('SCHEDULED','CANCELLED')) THEN
        RAISE EXCEPTION 'a trip can''t go from % to %', OLD.status, NEW.status
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_vehicle_trips_transition';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_vehicle_trips_status BEFORE UPDATE OF status ON vehicle_trips
    FOR EACH ROW EXECUTE FUNCTION fn_vehicle_trips_status();

-- ---------------------------------------------------------------------
-- trip_passengers
-- ---------------------------------------------------------------------
CREATE TABLE trip_passengers (
    id                    uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id       uuid          NOT NULL REFERENCES organizations (id),
    property_id           uuid          NOT NULL REFERENCES properties (id),
    code                  text          NOT NULL,
    trip_id               uuid          NOT NULL,
    reservation_id        uuid          NULL REFERENCES reservations (id),
    guest_id              uuid          NULL REFERENCES guests (id),
    guest_name            text          NOT NULL,
    seats                 smallint      NOT NULL,
    unit_price            numeric(10,2) NOT NULL,
    amount                numeric(12,2) NOT NULL,
    settlement            text          NOT NULL,       -- ROOM_CHARGE | FREE | COMPLIMENTARY
    complimentary_reason  text          NULL,
    folio_id              uuid          NULL REFERENCES folios (id),
    folio_item_id         uuid          NULL REFERENCES folio_items (id),
    note                  text          NULL,           -- e.g. "Pick up at the cottages", "Child seat"
    status                text          NOT NULL DEFAULT 'BOOKED',
    boarded_at            timestamptz   NULL,
    boarded_by            uuid          NULL REFERENCES users (id),
    cancel_reason         text          NULL,
    cancelled_at          timestamptz   NULL,
    cancelled_by          uuid          NULL REFERENCES users (id),
    refund                text          NULL,           -- REVERSED (the charge is off the bill) | NONE
    reversal_item_id      uuid          NULL REFERENCES folio_items (id),
    created_at            timestamptz   NOT NULL DEFAULT now(),
    updated_at            timestamptz   NOT NULL DEFAULT now(),
    created_by            uuid          NULL,
    updated_by            uuid          NULL,
    CONSTRAINT ck_trip_passengers_code       CHECK (code ~ '^TP[0-9]{5,}$'),
    CONSTRAINT ck_trip_passengers_name       CHECK (length(btrim(guest_name)) BETWEEN 2 AND 100),
    CONSTRAINT ck_trip_passengers_seats      CHECK (seats BETWEEN 1 AND 60),
    CONSTRAINT ck_trip_passengers_amount     CHECK (unit_price >= 0 AND amount = seats * unit_price),
    CONSTRAINT ck_trip_passengers_settlement CHECK (CASE settlement
                                                       WHEN 'ROOM_CHARGE'   THEN reservation_id IS NOT NULL AND folio_item_id IS NOT NULL AND amount > 0
                                                       WHEN 'FREE'          THEN amount = 0 AND folio_item_id IS NULL
                                                       WHEN 'COMPLIMENTARY' THEN folio_item_id IS NULL AND complimentary_reason IS NOT NULL
                                                       ELSE false END),
    CONSTRAINT ck_trip_passengers_comp       CHECK (complimentary_reason IS NULL OR length(btrim(complimentary_reason)) BETWEEN 3 AND 200),
    CONSTRAINT ck_trip_passengers_note       CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 200),
    CONSTRAINT ck_trip_passengers_status     CHECK (status IN ('BOOKED','BOARDED','NO_SHOW','CANCELLED')),
    CONSTRAINT ck_trip_passengers_boarded    CHECK ((status = 'BOARDED') = (boarded_at IS NOT NULL)),
    CONSTRAINT ck_trip_passengers_cancel     CHECK ((status = 'CANCELLED') = (cancelled_at IS NOT NULL)
                                                    AND (status = 'CANCELLED') = (cancel_reason IS NOT NULL)
                                                    AND (status = 'CANCELLED') = (refund IS NOT NULL)),
    CONSTRAINT ck_trip_passengers_reason     CHECK (cancel_reason IS NULL OR length(btrim(cancel_reason)) BETWEEN 3 AND 300),
    CONSTRAINT ck_trip_passengers_refund     CHECK (CASE refund
                                                       WHEN 'REVERSED' THEN reversal_item_id IS NOT NULL
                                                       WHEN 'NONE'     THEN reversal_item_id IS NULL
                                                       ELSE refund IS NULL END),
    CONSTRAINT fk_trip_passengers_trip       FOREIGN KEY (trip_id, property_id) REFERENCES vehicle_trips (id, property_id),
    CONSTRAINT ux_trip_passengers_code       UNIQUE (property_id, code)
);
-- A charge belongs to one passenger line.
CREATE UNIQUE INDEX ux_trip_passengers_charge ON trip_passengers (folio_item_id) WHERE folio_item_id IS NOT NULL;
CREATE INDEX ix_trip_passengers_trip ON trip_passengers (trip_id);
CREATE INDEX ix_trip_passengers_reservation ON trip_passengers (reservation_id) WHERE reservation_id IS NOT NULL;
CREATE TRIGGER trg_trip_passengers_no_delete BEFORE DELETE ON trip_passengers
    FOR EACH ROW EXECUTE FUNCTION fn_reject_delete();

-- seats_booked = the seats of the trip's passengers that aren't cancelled (booked, on board or no-show all held a seat),
-- moved in the same statement as the passenger. The UPDATE takes the trip's row lock, so two bookings queue here even if
-- something skipped the service's lock, and the CHECK ck_vehicle_trips_capacity refuses the one that doesn't fit. The
-- passenger moves allowed are checked here too (mirrored in PassengerStatuses.Moves).
CREATE FUNCTION fn_trip_passengers_seats() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    delta int := 0;
    s     text;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.status <> 'BOOKED' THEN
            RAISE EXCEPTION 'a new passenger is BOOKED'
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_trip_passengers_fixed';
        END IF;
        SELECT status INTO s FROM vehicle_trips WHERE id = NEW.trip_id;
        IF s <> 'SCHEDULED' THEN
            RAISE EXCEPTION 'trip % is %', NEW.trip_id, s
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_trip_passengers_trip_open';
        END IF;
        delta := NEW.seats;
    ELSE
        IF NEW.trip_id <> OLD.trip_id OR NEW.seats <> OLD.seats THEN
            RAISE EXCEPTION 'a passenger keeps their trip and seats; cancel and book again'
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_trip_passengers_fixed';
        END IF;
        IF NEW.status <> OLD.status AND NOT (OLD.status, NEW.status) IN (
            ('BOOKED','BOARDED'), ('BOARDED','BOOKED'), ('BOOKED','NO_SHOW'), ('BOOKED','CANCELLED'), ('BOARDED','CANCELLED')) THEN
            RAISE EXCEPTION 'a passenger can''t go from % to %', OLD.status, NEW.status
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_trip_passengers_fixed';
        END IF;
        IF OLD.status <> 'CANCELLED' AND NEW.status = 'CANCELLED' THEN
            delta := -OLD.seats;
        END IF;
    END IF;
    IF delta <> 0 THEN
        UPDATE vehicle_trips SET seats_booked = seats_booked + delta WHERE id = NEW.trip_id;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_trip_passengers_seats AFTER INSERT OR UPDATE ON trip_passengers
    FOR EACH ROW EXECUTE FUNCTION fn_trip_passengers_seats();

-- ---------------------------------------------------------------------
-- folio_items: a transfer charged to a room is one line with source TRANSPORT
-- ---------------------------------------------------------------------
ALTER TABLE folio_items DROP CONSTRAINT ck_folio_items_source;
ALTER TABLE folio_items ADD CONSTRAINT ck_folio_items_source
    CHECK (source IN ('MANUAL','ROOM_NIGHT','PACKAGE','RESTAURANT','ACTIVITY','TRANSPORT'));

-- ---------------------------------------------------------------------
-- Data classification (brief §59)
-- ---------------------------------------------------------------------
COMMENT ON TABLE vehicles         IS 'class: internal. The resort''s vehicles with their registration and seats.';
COMMENT ON TABLE transport_routes IS 'class: internal. Where the shuttle goes, how far and how long, and the price per seat.';
COMMENT ON TABLE vehicle_trips    IS 'class: internal. One trip with its vehicle and driver; seats_booked kept by a trigger, never above capacity.';
COMMENT ON TABLE trip_passengers  IS 'class: personal. Guests booked on a trip; the price goes on the room bill; the driver marks who boarded.';
