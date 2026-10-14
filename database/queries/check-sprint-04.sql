-- Database checks for Sprint 04 (rate plans and packages). Read-only: every statement is a SELECT.
-- How to run: docs/testing/VERIFYING.md → "Database checks" (VS Code PostgreSQL extension, or one docker command).
-- Each block says what you should see. Numbers marked (demo) assume a freshly reset database plus the review script.

-- 1. Migration V006 applied. Expect V001 … V006.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. Rate plans. (demo) BAR, "Best available rate", EP, is_default = true.
SELECT p.code AS property, r.code, r.name, r.meal_plan, r.is_default, r.is_active
FROM rate_plans r JOIN properties p ON p.id = r.property_id ORDER BY p.code, r.sort_order, r.code;

-- 3. The rate grid for the next 14 nights of the default plan, one row per night, one column per room type.
--    (demo) Standard ₹3,500 and Deluxe ₹5,000 on weeknights; ₹4,200 / ₹6,000 on Friday and Saturday nights.
SELECT x.stay_date, to_char(x.stay_date, 'Dy') AS night,
       max(x.price) FILTER (WHERE t.code = 'STANDARD_ROOM') AS standard,
       max(x.price) FILTER (WHERE t.code = 'DELUXE_ROOM')   AS deluxe,
       max(x.price) FILTER (WHERE t.code = 'DORMITORY')     AS dorm_bed
FROM rate_plan_prices x
JOIN rate_plans r ON r.id = x.rate_plan_id AND r.is_default
JOIN accommodation_types t ON t.id = x.accommodation_type_id
JOIN properties p ON p.id = x.property_id
WHERE x.stay_date >= (now() AT TIME ZONE p.timezone)::date
  AND x.stay_date <  (now() AT TIME ZONE p.timezone)::date + 14
GROUP BY x.stay_date ORDER BY x.stay_date;

-- 4. Nights in the next 30 days with NO price for an active room type in the default plan (they can't be quoted).
--    (demo) Expect NO rows.
SELECT t.name, d::date AS night
FROM properties p
JOIN rate_plans r ON r.property_id = p.id AND r.is_default
JOIN accommodation_types t ON t.property_id = p.id AND t.is_active
CROSS JOIN LATERAL generate_series((now() AT TIME ZONE p.timezone)::date, (now() AT TIME ZONE p.timezone)::date + 29, interval '1 day') d
WHERE NOT EXISTS (SELECT 1 FROM rate_plan_prices x WHERE x.rate_plan_id = r.id AND x.accommodation_type_id = t.id AND x.stay_date = d::date)
ORDER BY t.name, night;

-- 5. Every package's split adds up: shares to 100 %, amounts to the price, and it has a ROOM part. Expect NO rows.
SELECT k.code, k.price, sum(c.amount) AS amounts, sum(c.share_percent) AS shares,
       count(*) FILTER (WHERE c.category = 'ROOM') AS room_parts
FROM packages k LEFT JOIN package_components c ON c.package_id = k.id
GROUP BY k.id, k.code, k.price
HAVING coalesce(sum(c.amount), 0) <> k.price OR coalesce(sum(c.share_percent), 0) <> 100
    OR count(*) FILTER (WHERE c.category = 'ROOM') = 0;

-- 6. Packages and their revenue by category. (demo) COUPLE 14,999.00 = ROOM 10,499.30 + FOOD 2,249.85
--    + TRANSPORT 749.95 + ACTIVITY 1,499.90; ROOM_BREAKFAST 4,100.00 = ROOM 3,485.00 + FOOD 615.00.
SELECT k.code, k.price, c.category, c.description, c.share_percent, c.amount
FROM packages k JOIN package_components c ON c.package_id = k.id
ORDER BY k.sort_order, k.code, c.sort_order;

-- 7. Price changes in the audit trail, with old and new values (brief §87). After the review: the weekend prices you set.
SELECT a.occurred_at, u.full_name AS who, a.action, x.stay_date, t.name AS room_type,
       a.changes -> 'Price' ->> 'old' AS old_price, a.changes -> 'Price' ->> 'new' AS new_price
FROM audit_logs a
LEFT JOIN users u ON u.id = a.user_id
LEFT JOIN rate_plan_prices x ON x.id::text = a.entity_id
LEFT JOIN accommodation_types t ON t.id = x.accommodation_type_id
WHERE a.entity_type = 'RatePlanPrice'
ORDER BY a.id DESC LIMIT 30;

-- 8. Money is never stored as a float. Expect NO rows.
SELECT table_name, column_name, data_type FROM information_schema.columns
WHERE table_schema = 'public'
  AND table_name IN ('rate_plans', 'rate_plan_prices', 'packages', 'package_components', 'reservation_sources')
  AND data_type IN ('real', 'double precision', 'money');

-- 9. Booking channels available to reservations (Sprint 5). Expect 14 built-in sources.
SELECT code, name, channel FROM reservation_sources WHERE organization_id IS NULL ORDER BY sort_order;
