-- =====================================================================
-- DEV SEED S001 — demo accommodation for the bootstrap property (brief §71)
--   2 Deluxe rooms (101, 102), 4 Standard rooms (103–106), 1 Dormitory with 10 beds (5 bunks)
-- Runs ONLY in Development (DevSeed:Enabled) and ONLY if the property has no room types yet,
-- so it never touches data you have entered yourself. Not used in staging/production.
-- {{ORGANIZATION_CODE}} and {{PROPERTY_CODE}} are replaced by the runner with validated codes.
-- =====================================================================
DO $$
DECLARE
    p_id     uuid;
    o_id     uuid;
    t_deluxe uuid;
    t_std    uuid;
    t_dorm   uuid;
    u_dorm   uuid;
    i        int;
BEGIN
    SELECT p.id, p.organization_id INTO p_id, o_id
      FROM properties p JOIN organizations o ON o.id = p.organization_id
     WHERE o.code = '{{ORGANIZATION_CODE}}' AND p.code = '{{PROPERTY_CODE}}' AND p.deleted_at IS NULL;
    IF p_id IS NULL THEN
        RAISE NOTICE 'dev seed S001: bootstrap property not found, skipped';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM accommodation_types WHERE property_id = p_id) THEN
        RAISE NOTICE 'dev seed S001: property already has room types, skipped';
        RETURN;
    END IF;

    INSERT INTO accommodation_types (organization_id, property_id, code, name, description, inventory_mode,
                                     base_occupancy, max_occupancy, size_sqm, sort_order)
    VALUES (o_id, p_id, 'DELUXE_ROOM', 'Deluxe room', 'Garden-facing room with balcony, king bed, sleeps up to 3.',
            'UNIT', 2, 3, 28.0, 10)
    RETURNING id INTO t_deluxe;
    INSERT INTO accommodation_types (organization_id, property_id, code, name, description, inventory_mode,
                                     base_occupancy, max_occupancy, size_sqm, sort_order)
    VALUES (o_id, p_id, 'STANDARD_ROOM', 'Standard room', 'Queen bed, attached bathroom.',
            'UNIT', 2, 2, 20.0, 20)
    RETURNING id INTO t_std;
    INSERT INTO accommodation_types (organization_id, property_id, code, name, description, inventory_mode,
                                     base_occupancy, max_occupancy, size_sqm, sort_order)
    VALUES (o_id, p_id, 'DORMITORY', 'Dormitory bed', 'Bed in a shared 10-bed dormitory with lockers.',
            'BED', 1, 1, NULL, 30)
    RETURNING id INTO t_dorm;

    INSERT INTO accommodation_type_amenities (accommodation_type_id, amenity_id)
    SELECT t_deluxe, id FROM amenities WHERE organization_id IS NULL
       AND code IN ('AIR_CONDITIONING','WIFI','TELEVISION','MINI_FRIDGE','KETTLE','WARDROBE','ATTACHED_BATHROOM','HOT_WATER','TOILETRIES','BALCONY','GARDEN_VIEW');
    INSERT INTO accommodation_type_amenities (accommodation_type_id, amenity_id)
    SELECT t_std, id FROM amenities WHERE organization_id IS NULL
       AND code IN ('AIR_CONDITIONING','WIFI','WARDROBE','ATTACHED_BATHROOM','HOT_WATER','TOILETRIES');
    INSERT INTO accommodation_type_amenities (accommodation_type_id, amenity_id)
    SELECT t_dorm, id FROM amenities WHERE organization_id IS NULL
       AND code IN ('CEILING_FAN','WIFI','SHARED_BATHROOM','HOT_WATER','LOCKER','READING_LIGHT','POWER_SOCKET_PER_BED','PRIVACY_CURTAIN');

    INSERT INTO accommodation_units (organization_id, property_id, accommodation_type_id, code, name, floor, sort_order) VALUES
        (o_id, p_id, t_deluxe, '101', 'Room 101', 'Ground', 101),
        (o_id, p_id, t_deluxe, '102', 'Room 102', 'Ground', 102),
        (o_id, p_id, t_std,    '103', 'Room 103', 'First',  103),
        (o_id, p_id, t_std,    '104', 'Room 104', 'First',  104),
        (o_id, p_id, t_std,    '105', 'Room 105', 'First',  105),
        (o_id, p_id, t_std,    '106', 'Room 106', 'First',  106);
    INSERT INTO accommodation_units (organization_id, property_id, accommodation_type_id, code, name, floor, sort_order)
    VALUES (o_id, p_id, t_dorm, 'DORM', 'Dormitory', 'Ground', 200)
    RETURNING id INTO u_dorm;

    FOR i IN 1..10 LOOP
        INSERT INTO beds (organization_id, property_id, unit_id, code, name, bed_type, sort_order)
        VALUES (o_id, p_id, u_dorm, 'B' || lpad(i::text, 2, '0'), 'Bed ' || i,
                CASE WHEN i % 2 = 1 THEN 'BUNK_LOWER' ELSE 'BUNK_UPPER' END, i);
    END LOOP;

    RAISE NOTICE 'dev seed S001: 3 room types, 7 units, 10 beds created';
END $$;
