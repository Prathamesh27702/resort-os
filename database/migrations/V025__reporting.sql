-- =====================================================================
-- V025  Reports and dashboards (Sprint 23, brief §50–55, §88)
--
-- Dashboards and reports never add up the raw transaction tables when someone opens a page. A refresh (a background job
-- every 15 minutes, a nightly rebuild, or Refresh now on the reports page) adds up one property's days into small summary
-- tables in the schema `reporting`, one row per property and day; every dashboard and report reads only those, so a year
-- is 365 rows however many bookings, bills and orders it had. Power BI (or Excel) reads the same numbers through the
-- views reporting.v_*, with a read-only role that can see nothing else.
--
-- Model (schema reporting)
--   daily_stats          per property and day: rooms and dorm beds available (active, not out of order), sold, guests
--                        in house, arrivals, departures; revenue by kind before GST (from the bills: charges minus reversals
--                        and credit notes, by the night or day of the service; restaurant bills paid at the table added to
--                        food and drink); GST charged; payments received and refunds paid; restaurant sales and the stock
--                        their dishes used (food cost), stock written off; output GST on invoices and debit notes less
--                        credit notes, and input GST that can be claimed on supplier bills and approved expenses.
--   daily_channel_stats  per property, day and booking source (Direct, Booking.com…): rooms and beds sold, room revenue and
--                        the commission (reporting.commission_percent), so the OTA net is revenue − commission.
--   daily_costs          per property, day and department (cost centre; NULL = not given a department): approved expenses
--                        at cost, supplier bills at cost (the taxable value when the GST comes back as input credit) and
--                        the pay bill estimated by reporting.labour_cost.
--   refresh_state        per property: when its numbers were last refreshed, which days, how long it took, and when the
--                        last full (nightly) rebuild ran.
--   labour_cost()        what each employee costs on each day: a monthly salary (basic + allowances) spread over the days
--                        of the month they were employed, a daily rate (basic + allowances) for each day worked on a
--                        timesheet that wasn't rejected. Employer PF/ESI and unpaid leave are not in it; payroll itself
--                        is done outside Resort OS (docs/PAYROLL.md).
--   refresh()            recomputes the days from..to (at most 800) of one property in one transaction, under a lock per
--                        property so two refreshes of the same property queue.
--   v_daily, v_monthly, v_channels_monthly, v_costs_monthly, v_gst_monthly   the views Power BI reads.
--
-- Also
--   departments.cost_centre_id       the budget department an employee department's pay counts against (matched by
--                                     name or code where both exist).
--   supplier_invoices.cost_centre_id the budget department a supplier bill counts against (Sprint 22 left supplier bills
--                                     and pay out of the budget's actuals).
--   Role resort_reporting (NOLOGIN): USAGE on reporting and SELECT on its views only. Created when the migrating login may
--   create roles; scripts/powerbi-user.sql makes a login for Power BI that is a member of it.
-- =====================================================================

-- ---------------------------------------------------------------------
-- Budget departments for pay and supplier bills
-- ---------------------------------------------------------------------
ALTER TABLE departments ADD COLUMN cost_centre_id uuid NULL;
ALTER TABLE departments ADD CONSTRAINT fk_departments_cost_centre
    FOREIGN KEY (cost_centre_id, property_id) REFERENCES cost_centres (id, property_id);
UPDATE departments d
   SET cost_centre_id = c.id
  FROM cost_centres c
 WHERE c.property_id = d.property_id
   AND (lower(btrim(c.name)) = lower(btrim(d.name)) OR c.code = d.code)
   AND d.cost_centre_id IS NULL;

ALTER TABLE supplier_invoices ADD COLUMN cost_centre_id uuid NULL;
ALTER TABLE supplier_invoices ADD CONSTRAINT fk_supplier_invoices_cost_centre
    FOREIGN KEY (cost_centre_id, property_id) REFERENCES cost_centres (id, property_id);
-- Bills already recorded for the kitchen store count for Food and beverage where that department exists.
UPDATE supplier_invoices si
   SET cost_centre_id = c.id
  FROM purchase_orders po
  JOIN stock_locations l ON l.id = po.location_id AND l.is_kitchen
  JOIN cost_centres c ON c.property_id = l.property_id AND c.code = 'FB'
 WHERE po.id = si.order_id AND si.cost_centre_id IS NULL;

-- ---------------------------------------------------------------------
-- Indexes so a refresh of a range of days reads only those days
-- ---------------------------------------------------------------------
CREATE INDEX ix_folio_items_service_date ON folio_items (property_id, service_date);
CREATE INDEX ix_refunds_property ON refunds (property_id, refunded_at);
CREATE INDEX ix_credit_notes_date ON credit_notes (property_id, note_date);
CREATE INDEX ix_debit_notes_date ON debit_notes (property_id, note_date);
CREATE INDEX ix_supplier_invoices_date ON supplier_invoices (property_id, invoice_date);
CREATE INDEX ix_reservations_check_in ON reservations (property_id, check_in);
CREATE INDEX ix_reservations_check_out ON reservations (property_id, check_out);

-- ---------------------------------------------------------------------
-- The reporting schema
-- ---------------------------------------------------------------------
CREATE SCHEMA reporting;

CREATE TABLE reporting.daily_stats (
    organization_id      uuid          NOT NULL REFERENCES organizations (id),
    property_id          uuid          NOT NULL REFERENCES properties (id),
    business_date        date          NOT NULL,
    rooms_available      integer       NOT NULL DEFAULT 0,  -- rooms sold one at a time, active, not out of order
    rooms_sold           integer       NOT NULL DEFAULT 0,
    beds_available       integer       NOT NULL DEFAULT 0,  -- dorm beds
    beds_sold            integer       NOT NULL DEFAULT 0,
    out_of_order         integer       NOT NULL DEFAULT 0,  -- rooms and beds taken out of order or for maintenance
    guests_in_house      integer       NOT NULL DEFAULT 0,
    arrivals             integer       NOT NULL DEFAULT 0,
    departures           integer       NOT NULL DEFAULT 0,
    room_revenue         numeric(14,2) NOT NULL DEFAULT 0,  -- before GST
    fnb_revenue          numeric(14,2) NOT NULL DEFAULT 0,
    activity_revenue     numeric(14,2) NOT NULL DEFAULT 0,
    transport_revenue    numeric(14,2) NOT NULL DEFAULT 0,
    other_revenue        numeric(14,2) NOT NULL DEFAULT 0,  -- equipment, laundry and other charges
    total_revenue        numeric(14,2) NOT NULL DEFAULT 0,
    tax_charged          numeric(14,2) NOT NULL DEFAULT 0,  -- GST on the bills and restaurant bills of the day
    payments_received    numeric(14,2) NOT NULL DEFAULT 0,
    refunds_paid         numeric(14,2) NOT NULL DEFAULT 0,
    restaurant_sales     numeric(14,2) NOT NULL DEFAULT 0,  -- settled restaurant orders after discount, before GST
    restaurant_orders    integer       NOT NULL DEFAULT 0,
    food_cost            numeric(14,2) NOT NULL DEFAULT 0,  -- stock used by the dishes served
    stock_written_off    numeric(14,2) NOT NULL DEFAULT 0,
    output_taxable       numeric(14,2) NOT NULL DEFAULT 0,  -- GST invoices + debit notes − credit notes
    output_cgst          numeric(14,2) NOT NULL DEFAULT 0,
    output_sgst          numeric(14,2) NOT NULL DEFAULT 0,
    output_igst          numeric(14,2) NOT NULL DEFAULT 0,
    output_cess          numeric(14,2) NOT NULL DEFAULT 0,
    pos_gst_uninvoiced   numeric(14,2) NOT NULL DEFAULT 0,  -- GST on restaurant bills paid at the table (no GST invoice yet)
    input_cgst           numeric(14,2) NOT NULL DEFAULT 0,  -- claimable: supplier bills and approved expenses
    input_sgst           numeric(14,2) NOT NULL DEFAULT 0,
    input_igst           numeric(14,2) NOT NULL DEFAULT 0,
    refreshed_at         timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT pk_daily_stats PRIMARY KEY (property_id, business_date),
    CONSTRAINT ck_daily_stats_rooms CHECK (rooms_available >= 0 AND rooms_sold >= 0 AND beds_available >= 0 AND beds_sold >= 0
                                           AND out_of_order >= 0 AND guests_in_house >= 0 AND arrivals >= 0 AND departures >= 0),
    CONSTRAINT ck_daily_stats_total CHECK (total_revenue = room_revenue + fnb_revenue + activity_revenue + transport_revenue + other_revenue)
);

CREATE TABLE reporting.daily_channel_stats (
    organization_id      uuid          NOT NULL REFERENCES organizations (id),
    property_id          uuid          NOT NULL REFERENCES properties (id),
    business_date        date          NOT NULL,
    source_id            uuid          NOT NULL REFERENCES reservation_sources (id),
    units_sold           integer       NOT NULL DEFAULT 0,  -- rooms + dorm beds
    room_revenue         numeric(14,2) NOT NULL DEFAULT 0,
    commission           numeric(14,2) NOT NULL DEFAULT 0,
    CONSTRAINT pk_daily_channel_stats PRIMARY KEY (property_id, business_date, source_id)
);

CREATE TABLE reporting.daily_costs (
    organization_id      uuid          NOT NULL REFERENCES organizations (id),
    property_id          uuid          NOT NULL REFERENCES properties (id),
    business_date        date          NOT NULL,
    cost_centre_id       uuid          NULL,                -- NULL: not given a department
    expenses             numeric(14,2) NOT NULL DEFAULT 0,
    supplier_bills       numeric(14,2) NOT NULL DEFAULT 0,
    labour               numeric(14,2) NOT NULL DEFAULT 0,
    CONSTRAINT fk_daily_costs_cost_centre FOREIGN KEY (cost_centre_id, property_id) REFERENCES cost_centres (id, property_id)
);
CREATE UNIQUE INDEX ux_daily_costs ON reporting.daily_costs
    (property_id, business_date, coalesce(cost_centre_id, '00000000-0000-0000-0000-000000000000'::uuid));

CREATE TABLE reporting.refresh_state (
    property_id          uuid          PRIMARY KEY REFERENCES properties (id),
    organization_id      uuid          NOT NULL REFERENCES organizations (id),
    refreshed_at         timestamptz   NOT NULL,
    refreshed_from       date          NOT NULL,
    refreshed_to         date          NOT NULL,
    duration_ms          integer       NOT NULL,
    full_refreshed_at    timestamptz   NULL
);

-- ---------------------------------------------------------------------
-- reporting.labour_cost: what each employee costs on each day of a range
-- ---------------------------------------------------------------------
CREATE FUNCTION reporting.labour_cost(p_property uuid, p_from date, p_to date)
RETURNS TABLE (work_date date, employee_id uuid, department_id uuid, cost_centre_id uuid, amount numeric)
LANGUAGE sql STABLE AS $$
    WITH days AS (
        SELECT d::date AS day FROM generate_series(p_from, p_to, interval '1 day') AS d
    ),
    employed AS (
        -- Every day each employee worked here, with the pay that applied that day.
        SELECT dy.day, e.id AS employee_id, e.department_id, dep.cost_centre_id, c.pay_basis, c.basic + c.allowances AS rate
          FROM employees e
          JOIN days dy ON dy.day >= e.joined_on AND (e.left_on IS NULL OR dy.day <= e.left_on)
          JOIN departments dep ON dep.id = e.department_id
          JOIN LATERAL (
               SELECT ec.pay_basis, ec.basic, ec.allowances
                 FROM employee_compensation ec
                WHERE ec.employee_id = e.id AND ec.effective_from <= dy.day
                ORDER BY ec.effective_from DESC
                LIMIT 1) c ON true
         WHERE e.property_id = p_property
    ),
    worked AS (
        SELECT DISTINCT t.employee_id, l.work_date
          FROM timesheets t
          JOIN timesheet_lines l ON l.timesheet_id = t.id
         WHERE t.property_id = p_property
           AND t.week_start BETWEEN p_from - 6 AND p_to
           AND t.status <> 'REJECTED'
           AND l.status = 'WORKED'
           AND l.work_date BETWEEN p_from AND p_to
    )
    SELECT x.day, x.employee_id, x.department_id, x.cost_centre_id,
           CASE WHEN x.pay_basis = 'MONTHLY'
                THEN round(x.rate / extract(day FROM (date_trunc('month', x.day) + interval '1 month - 1 day'))::numeric, 2)
                ELSE x.rate END
      FROM employed x
     WHERE x.pay_basis = 'MONTHLY'
        OR EXISTS (SELECT 1 FROM worked w WHERE w.employee_id = x.employee_id AND w.work_date = x.day)
$$;

-- ---------------------------------------------------------------------
-- reporting.commission_percent: what a booking source takes from the room revenue
--   The property setting reports.commission_percent ({"BOOKING_COM": 18, "AGODA": 17.5, …}, set on the reports page),
--   else the source's own commission_percent, else 18 % for an OTA and 10 % for a travel agent, else nothing. Mirrored in
--   ReportService.DefaultCommission.
-- ---------------------------------------------------------------------
CREATE FUNCTION reporting.commission_percent(p_property uuid, p_source uuid) RETURNS numeric
LANGUAGE sql STABLE AS $$
    SELECT coalesce(
               (SELECT CASE WHEN jsonb_typeof(ps.value -> src.code) = 'number' THEN (ps.value ->> src.code)::numeric END
                  FROM property_settings ps
                 WHERE ps.property_id = p_property AND ps.key = 'reports.commission_percent'),
               src.commission_percent,
               CASE src.channel WHEN 'OTA' THEN 18 WHEN 'TRAVEL_AGENT' THEN 10 ELSE 0 END)
      FROM reservation_sources src
     WHERE src.id = p_source
$$;

-- ---------------------------------------------------------------------
-- reporting.refresh: recompute one property's days from..to
-- ---------------------------------------------------------------------
CREATE FUNCTION reporting.refresh(p_property uuid, p_from date, p_to date, p_full boolean DEFAULT false)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE
    v_org   uuid;
    v_tz    text;
    v_since timestamptz;
    v_until timestamptz;
    v_start timestamptz := clock_timestamp();
    v_rows  integer;
BEGIN
    IF p_from IS NULL OR p_to IS NULL OR p_to < p_from OR p_to - p_from > 800 THEN
        RAISE EXCEPTION 'refresh at most 800 days at a time (from % to %)', p_from, p_to
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_reporting_refresh_range';
    END IF;
    SELECT organization_id, timezone INTO v_org, v_tz FROM properties WHERE id = p_property;
    IF v_org IS NULL THEN
        RAISE EXCEPTION 'property % not found', p_property USING ERRCODE = 'no_data_found';
    END IF;
    -- One refresh of a property at a time; others wait here (the job, a nightly rebuild and Refresh now may meet).
    PERFORM pg_advisory_xact_lock(hashtextextended('reporting:' || p_property::text, 0));
    v_since := p_from::timestamp AT TIME ZONE v_tz;
    v_until := (p_to + 1)::timestamp AT TIME ZONE v_tz;

    DELETE FROM reporting.daily_stats WHERE property_id = p_property AND business_date BETWEEN p_from AND p_to;
    DELETE FROM reporting.daily_channel_stats WHERE property_id = p_property AND business_date BETWEEN p_from AND p_to;
    DELETE FROM reporting.daily_costs WHERE property_id = p_property AND business_date BETWEEN p_from AND p_to;

    WITH days AS (
        SELECT d::date AS day FROM generate_series(p_from, p_to, interval '1 day') AS d
    ),
    capacity AS (
        SELECT count(*) FILTER (WHERE t.inventory_mode = 'UNIT') AS rooms,
               coalesce(sum((SELECT count(*) FROM beds b WHERE b.unit_id = u.id AND b.is_active))
                        FILTER (WHERE t.inventory_mode = 'BED'), 0) AS beds
          FROM accommodation_units u
          JOIN accommodation_types t ON t.id = u.accommodation_type_id
         WHERE u.property_id = p_property AND u.is_active AND t.is_active
    ),
    ooo AS (
        -- Rooms and beds out of order each day (a block on a whole dorm takes all its beds).
        SELECT dy.day,
               count(DISTINCT bl.unit_id) FILTER (WHERE bl.bed_id IS NULL AND t.inventory_mode = 'UNIT') AS rooms,
               count(DISTINCT coalesce(bl.bed_id, bd.id)) FILTER (WHERE t.inventory_mode = 'BED') AS beds
          FROM days dy
          JOIN unit_blocks bl ON bl.property_id = p_property AND bl.status = 'ACTIVE'
                             AND bl.block_type IN ('OUT_OF_ORDER', 'MAINTENANCE')
                             AND bl.start_date <= dy.day AND dy.day < bl.end_date
          JOIN accommodation_units u ON u.id = bl.unit_id AND u.is_active
          JOIN accommodation_types t ON t.id = u.accommodation_type_id
          LEFT JOIN beds bd ON bd.unit_id = u.id AND bd.is_active AND bl.bed_id IS NULL AND t.inventory_mode = 'BED'
         GROUP BY dy.day
    ),
    stays AS (
        -- What is sold each night: the current rows of confirmed, in-house and checked-out bookings.
        SELECT dy.day,
               count(DISTINCT ru.unit_id) FILTER (WHERE ru.bed_id IS NULL) AS rooms,
               count(DISTINCT ru.bed_id) AS beds,
               sum(ru.adults + ru.children) AS guests
          FROM days dy
          JOIN reservation_units ru ON ru.property_id = p_property AND ru.superseded_at IS NULL
                                   AND ru.start_date <= dy.day AND dy.day < ru.end_date
          JOIN reservations r ON r.id = ru.reservation_id AND r.status IN ('CONFIRMED', 'CHECKED_IN', 'CHECKED_OUT')
         GROUP BY dy.day
    ),
    arrivals AS (
        SELECT r.check_in AS day, count(*) AS n FROM reservations r
         WHERE r.property_id = p_property AND r.check_in BETWEEN p_from AND p_to
           AND r.status IN ('CONFIRMED', 'CHECKED_IN', 'CHECKED_OUT')
         GROUP BY r.check_in
    ),
    departures AS (
        SELECT r.check_out AS day, count(*) AS n FROM reservations r
         WHERE r.property_id = p_property AND r.check_out BETWEEN p_from AND p_to
           AND r.status IN ('CONFIRMED', 'CHECKED_IN', 'CHECKED_OUT')
         GROUP BY r.check_out
    ),
    bills AS (
        -- Charges, reversals (negative) and credit notes (negative) by the day of the service.
        SELECT fi.service_date AS day,
               sum(fi.amount) FILTER (WHERE fi.category = 'ROOM') AS room,
               sum(fi.amount) FILTER (WHERE fi.category IN ('FOOD', 'BEVERAGE')) AS fnb,
               sum(fi.amount) FILTER (WHERE fi.category = 'ACTIVITY') AS activity,
               sum(fi.amount) FILTER (WHERE fi.category = 'TRANSPORT') AS transport,
               sum(fi.amount) FILTER (WHERE fi.category IN ('EQUIPMENT', 'LAUNDRY', 'OTHER')) AS other,
               sum(fi.tax_amount) AS tax
          FROM folio_items fi
         WHERE fi.property_id = p_property AND fi.service_date BETWEEN p_from AND p_to
         GROUP BY fi.service_date
    ),
    pos AS (
        SELECT o.business_date AS day,
               sum(o.subtotal - o.discount_amount) AS sales,
               count(*) AS orders,
               sum(o.subtotal - o.discount_amount) FILTER (WHERE o.settlement = 'PAID') AS walk_in,
               sum(o.tax_amount) FILTER (WHERE o.settlement = 'PAID') AS walk_in_tax
          FROM orders o
         WHERE o.property_id = p_property AND o.business_date BETWEEN p_from AND p_to AND o.status = 'SETTLED'
         GROUP BY o.business_date
    ),
    used AS (
        SELECT o.business_date AS day, -sum(m.value) AS cost
          FROM orders o
          JOIN order_items oi ON oi.order_id = o.id
          JOIN stock_movements m ON m.order_item_id = oi.id AND m.movement_type = 'CONSUMPTION'
         WHERE o.property_id = p_property AND o.business_date BETWEEN p_from AND p_to AND o.status <> 'VOIDED'
         GROUP BY o.business_date
    ),
    wasted AS (
        SELECT (m.created_at AT TIME ZONE v_tz)::date AS day, -sum(m.value) AS cost
          FROM stock_movements m
         WHERE m.property_id = p_property AND m.movement_type = 'WASTAGE'
           AND m.created_at >= v_since AND m.created_at < v_until
         GROUP BY 1
    ),
    paid AS (
        SELECT (p.received_at AT TIME ZONE v_tz)::date AS day, sum(p.amount) AS amount
          FROM payments p
         WHERE p.property_id = p_property AND p.status IN ('CAPTURED', 'REFUNDED', 'PARTIALLY_REFUNDED')
           AND p.received_at >= v_since AND p.received_at < v_until
         GROUP BY 1
    ),
    refunded AS (
        SELECT (rf.refunded_at AT TIME ZONE v_tz)::date AS day, sum(rf.amount) AS amount
          FROM refunds rf
         WHERE rf.property_id = p_property AND rf.status = 'PROCESSED'
           AND rf.refunded_at >= v_since AND rf.refunded_at < v_until
         GROUP BY 1
    ),
    output_gst AS (
        SELECT day, sum(taxable) AS taxable, sum(cgst) AS cgst, sum(sgst) AS sgst, sum(igst) AS igst, sum(cess) AS cess
          FROM (SELECT i.invoice_date AS day, i.taxable_value AS taxable, i.cgst, i.sgst, i.igst, i.cess
                  FROM invoices i
                 WHERE i.property_id = p_property AND i.document_type = 'INVOICE' AND i.invoice_date BETWEEN p_from AND p_to
                UNION ALL
                SELECT d.note_date, d.taxable_value, d.cgst, d.sgst, d.igst, d.cess
                  FROM debit_notes d
                 WHERE d.property_id = p_property AND d.note_date BETWEEN p_from AND p_to
                UNION ALL
                SELECT c.note_date, -c.taxable_value, -c.cgst, -c.sgst, -c.igst, -c.cess
                  FROM credit_notes c
                  JOIN invoices i ON i.id = c.invoice_id AND i.document_type = 'INVOICE'
                 WHERE c.property_id = p_property AND c.note_date BETWEEN p_from AND p_to) x
         GROUP BY day
    ),
    input_gst AS (
        SELECT day, sum(cgst) AS cgst, sum(sgst) AS sgst, sum(igst) AS igst
          FROM (SELECT s.invoice_date AS day, s.cgst, s.sgst, s.igst
                  FROM supplier_invoices s
                 WHERE s.property_id = p_property AND s.itc_eligible AND s.invoice_date BETWEEN p_from AND p_to
                UNION ALL
                SELECT e.expense_date, e.cgst, e.sgst, e.igst
                  FROM expenses e
                 WHERE e.property_id = p_property AND e.status = 'APPROVED' AND e.itc_eligible
                   AND e.expense_date BETWEEN p_from AND p_to) x
         GROUP BY day
    )
    INSERT INTO reporting.daily_stats (
        organization_id, property_id, business_date,
        rooms_available, rooms_sold, beds_available, beds_sold, out_of_order, guests_in_house, arrivals, departures,
        room_revenue, fnb_revenue, activity_revenue, transport_revenue, other_revenue, total_revenue, tax_charged,
        payments_received, refunds_paid, restaurant_sales, restaurant_orders, food_cost, stock_written_off,
        output_taxable, output_cgst, output_sgst, output_igst, output_cess, pos_gst_uninvoiced,
        input_cgst, input_sgst, input_igst)
    SELECT v_org, p_property, dy.day,
           greatest(cap.rooms - coalesce(ooo.rooms, 0), 0), coalesce(st.rooms, 0),
           greatest(cap.beds - coalesce(ooo.beds, 0), 0), coalesce(st.beds, 0),
           coalesce(ooo.rooms, 0) + coalesce(ooo.beds, 0), coalesce(st.guests, 0), coalesce(ar.n, 0), coalesce(de.n, 0),
           coalesce(b.room, 0), coalesce(b.fnb, 0) + coalesce(pos.walk_in, 0), coalesce(b.activity, 0),
           coalesce(b.transport, 0), coalesce(b.other, 0),
           coalesce(b.room, 0) + coalesce(b.fnb, 0) + coalesce(pos.walk_in, 0) + coalesce(b.activity, 0)
             + coalesce(b.transport, 0) + coalesce(b.other, 0),
           coalesce(b.tax, 0) + coalesce(pos.walk_in_tax, 0),
           coalesce(pd.amount, 0), coalesce(rf.amount, 0), coalesce(pos.sales, 0), coalesce(pos.orders, 0),
           coalesce(us.cost, 0), coalesce(wa.cost, 0),
           coalesce(og.taxable, 0), coalesce(og.cgst, 0), coalesce(og.sgst, 0), coalesce(og.igst, 0), coalesce(og.cess, 0),
           coalesce(pos.walk_in_tax, 0),
           coalesce(ig.cgst, 0), coalesce(ig.sgst, 0), coalesce(ig.igst, 0)
      FROM days dy
     CROSS JOIN capacity cap
      LEFT JOIN ooo ON ooo.day = dy.day
      LEFT JOIN stays st ON st.day = dy.day
      LEFT JOIN arrivals ar ON ar.day = dy.day
      LEFT JOIN departures de ON de.day = dy.day
      LEFT JOIN bills b ON b.day = dy.day
      LEFT JOIN pos ON pos.day = dy.day
      LEFT JOIN used us ON us.day = dy.day
      LEFT JOIN wasted wa ON wa.day = dy.day
      LEFT JOIN paid pd ON pd.day = dy.day
      LEFT JOIN refunded rf ON rf.day = dy.day
      LEFT JOIN output_gst og ON og.day = dy.day
      LEFT JOIN input_gst ig ON ig.day = dy.day;
    GET DIAGNOSTICS v_rows = ROW_COUNT;

    -- Rooms sold and room revenue by where the booking came from.
    WITH days AS (
        SELECT d::date AS day FROM generate_series(p_from, p_to, interval '1 day') AS d
    ),
    sold AS (
        SELECT dy.day, r.source_id, count(DISTINCT coalesce(ru.bed_id, ru.unit_id)) AS units
          FROM days dy
          JOIN reservation_units ru ON ru.property_id = p_property AND ru.superseded_at IS NULL
                                   AND ru.start_date <= dy.day AND dy.day < ru.end_date
          JOIN reservations r ON r.id = ru.reservation_id AND r.status IN ('CONFIRMED', 'CHECKED_IN', 'CHECKED_OUT')
         GROUP BY dy.day, r.source_id
    ),
    revenue AS (
        SELECT fi.service_date AS day, r.source_id, sum(fi.amount) AS amount
          FROM folio_items fi
          JOIN reservations r ON r.id = fi.reservation_id
         WHERE fi.property_id = p_property AND fi.category = 'ROOM' AND fi.service_date BETWEEN p_from AND p_to
         GROUP BY fi.service_date, r.source_id
    )
    INSERT INTO reporting.daily_channel_stats (organization_id, property_id, business_date, source_id, units_sold, room_revenue, commission)
    SELECT v_org, p_property, coalesce(s.day, rv.day), coalesce(s.source_id, rv.source_id), coalesce(s.units, 0),
           coalesce(rv.amount, 0), round(coalesce(rv.amount, 0) * reporting.commission_percent(p_property, src.id) / 100, 2)
      FROM sold s
      FULL JOIN revenue rv ON rv.day = s.day AND rv.source_id = s.source_id
      JOIN reservation_sources src ON src.id = coalesce(s.source_id, rv.source_id);

    -- Spending by department: approved expenses and supplier bills at cost, and the pay bill.
    INSERT INTO reporting.daily_costs (organization_id, property_id, business_date, cost_centre_id, expenses, supplier_bills, labour)
    SELECT v_org, p_property, day, cost_centre_id, sum(expenses), sum(bills), sum(labour)
      FROM (SELECT e.expense_date AS day, e.cost_centre_id, e.cost_amount AS expenses, 0::numeric AS bills, 0::numeric AS labour
              FROM expenses e
             WHERE e.property_id = p_property AND e.status = 'APPROVED' AND e.expense_date BETWEEN p_from AND p_to
            UNION ALL
            SELECT s.invoice_date, s.cost_centre_id, 0, CASE WHEN s.itc_eligible THEN s.taxable_value ELSE s.total END, 0
              FROM supplier_invoices s
             WHERE s.property_id = p_property AND s.invoice_date BETWEEN p_from AND p_to
            UNION ALL
            SELECT lc.work_date, lc.cost_centre_id, 0, 0, lc.amount
              FROM reporting.labour_cost(p_property, p_from, p_to) lc) x
     GROUP BY day, cost_centre_id;

    INSERT INTO reporting.refresh_state (property_id, organization_id, refreshed_at, refreshed_from, refreshed_to, duration_ms, full_refreshed_at)
    VALUES (p_property, v_org, now(), p_from, p_to,
            (extract(epoch FROM clock_timestamp() - v_start) * 1000)::integer, CASE WHEN p_full THEN now() END)
    ON CONFLICT (property_id) DO UPDATE
       SET refreshed_at = EXCLUDED.refreshed_at, refreshed_from = EXCLUDED.refreshed_from,
           refreshed_to = EXCLUDED.refreshed_to, duration_ms = EXCLUDED.duration_ms,
           full_refreshed_at = coalesce(EXCLUDED.full_refreshed_at, reporting.refresh_state.full_refreshed_at);
    RETURN v_rows;
END $$;

-- ---------------------------------------------------------------------
-- Views for Power BI and Excel (one row per property and day or month; money before GST unless named gst)
-- ---------------------------------------------------------------------
CREATE VIEW reporting.v_daily AS
SELECT p.code AS property_code, p.name AS property_name, s.business_date,
       date_trunc('month', s.business_date)::date AS month,
       s.rooms_available + s.beds_available AS units_available,
       s.rooms_sold + s.beds_sold AS units_sold,
       s.rooms_available, s.rooms_sold, s.beds_available, s.beds_sold, s.out_of_order,
       round(100.0 * (s.rooms_sold + s.beds_sold) / nullif(s.rooms_available + s.beds_available, 0), 1) AS occupancy_percent,
       round(s.room_revenue / nullif(s.rooms_sold + s.beds_sold, 0), 2) AS adr,
       round(s.room_revenue / nullif(s.rooms_available + s.beds_available, 0), 2) AS revpar,
       s.guests_in_house, s.arrivals, s.departures,
       s.room_revenue, s.fnb_revenue, s.activity_revenue, s.transport_revenue, s.other_revenue, s.total_revenue,
       s.tax_charged, s.payments_received, s.refunds_paid, s.restaurant_sales, s.restaurant_orders,
       s.food_cost, s.stock_written_off,
       s.output_taxable, s.output_cgst, s.output_sgst, s.output_igst, s.output_cess, s.pos_gst_uninvoiced,
       s.input_cgst, s.input_sgst, s.input_igst, s.refreshed_at
  FROM reporting.daily_stats s
  JOIN properties p ON p.id = s.property_id;

CREATE VIEW reporting.v_monthly AS
SELECT p.code AS property_code, p.name AS property_name, m.*,
       round(100.0 * m.units_sold / nullif(m.units_available, 0), 1) AS occupancy_percent,
       round(m.room_revenue / nullif(m.units_sold, 0), 2) AS adr,
       round(m.room_revenue / nullif(m.units_available, 0), 2) AS revpar,
       round(100.0 * m.food_cost / nullif(m.restaurant_sales, 0), 1) AS food_cost_percent
  FROM (SELECT s.property_id, date_trunc('month', s.business_date)::date AS month,
               sum(s.rooms_available + s.beds_available) AS units_available, sum(s.rooms_sold + s.beds_sold) AS units_sold,
               sum(s.arrivals) AS arrivals, sum(s.guests_in_house) AS guest_nights,
               sum(s.room_revenue) AS room_revenue, sum(s.fnb_revenue) AS fnb_revenue, sum(s.activity_revenue) AS activity_revenue,
               sum(s.transport_revenue) AS transport_revenue, sum(s.other_revenue) AS other_revenue,
               sum(s.total_revenue) AS total_revenue, sum(s.tax_charged) AS tax_charged,
               sum(s.payments_received) AS payments_received, sum(s.refunds_paid) AS refunds_paid,
               sum(s.restaurant_sales) AS restaurant_sales, sum(s.food_cost) AS food_cost, sum(s.stock_written_off) AS stock_written_off
          FROM reporting.daily_stats s
         GROUP BY s.property_id, date_trunc('month', s.business_date)) m
  JOIN properties p ON p.id = m.property_id;

CREATE VIEW reporting.v_channels_monthly AS
SELECT p.code AS property_code, date_trunc('month', c.business_date)::date AS month,
       src.code AS source_code, src.name AS source_name, src.channel,
       sum(c.units_sold) AS units_sold, sum(c.room_revenue) AS room_revenue, sum(c.commission) AS commission,
       sum(c.room_revenue) - sum(c.commission) AS net_revenue
  FROM reporting.daily_channel_stats c
  JOIN properties p ON p.id = c.property_id
  JOIN reservation_sources src ON src.id = c.source_id
 GROUP BY p.code, date_trunc('month', c.business_date), src.code, src.name, src.channel;

CREATE VIEW reporting.v_costs_monthly AS
SELECT p.code AS property_code, date_trunc('month', c.business_date)::date AS month,
       cc.code AS department_code, coalesce(cc.name, 'No department') AS department_name,
       sum(c.expenses) AS expenses, sum(c.supplier_bills) AS supplier_bills, sum(c.labour) AS labour,
       sum(c.expenses + c.supplier_bills + c.labour) AS total
  FROM reporting.daily_costs c
  JOIN properties p ON p.id = c.property_id
  LEFT JOIN cost_centres cc ON cc.id = c.cost_centre_id
 GROUP BY p.code, date_trunc('month', c.business_date), cc.code, cc.name;

CREATE VIEW reporting.v_gst_monthly AS
SELECT p.code AS property_code, date_trunc('month', s.business_date)::date AS month,
       sum(s.output_taxable) AS output_taxable, sum(s.output_cgst) AS output_cgst, sum(s.output_sgst) AS output_sgst,
       sum(s.output_igst) AS output_igst, sum(s.output_cess) AS output_cess, sum(s.pos_gst_uninvoiced) AS pos_gst_uninvoiced,
       sum(s.input_cgst) AS input_cgst, sum(s.input_sgst) AS input_sgst, sum(s.input_igst) AS input_igst
  FROM reporting.daily_stats s
  JOIN properties p ON p.id = s.property_id
 GROUP BY p.code, date_trunc('month', s.business_date);

-- ---------------------------------------------------------------------
-- The read-only role for Power BI: the views and nothing else
-- ---------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'resort_reporting') THEN
        BEGIN
            CREATE ROLE resort_reporting NOLOGIN;
        EXCEPTION
            WHEN insufficient_privilege THEN
                RAISE NOTICE 'resort_reporting not created: this login may not create roles. Ask the database admin to run scripts/powerbi-user.sql.';
            WHEN duplicate_object OR unique_violation THEN
                NULL;  -- another database on this server made it at the same moment
        END;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'resort_reporting') THEN
        EXECUTE 'GRANT USAGE ON SCHEMA reporting TO resort_reporting';
        EXECUTE 'GRANT SELECT ON reporting.v_daily, reporting.v_monthly, reporting.v_channels_monthly, '
             || 'reporting.v_costs_monthly, reporting.v_gst_monthly TO resort_reporting';
    END IF;
END $$;
