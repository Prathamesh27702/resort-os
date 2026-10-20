-- Database checks for Sprint 09 (GST configuration and the tax engine). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 09. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 09).

-- 1. Migration V011 applied. Expect V001 … V011.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The four new tables exist. Expect gst_registrations, tax_categories, tax_rule_slabs, tax_rules.
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('gst_registrations', 'tax_categories', 'tax_rules', 'tax_rule_slabs') ORDER BY table_name;

-- 3. GST registrations. (demo) the demo property: 30AABCT1234C1Z9, Goa, REGULAR, from 2017-07-01, no end date.
SELECT p.name AS property, g.gstin, g.state_code, g.registration_type, g.effective_from, g.effective_to
FROM gst_registrations g JOIN properties p ON p.id = g.property_id ORDER BY p.name, g.effective_from;

-- 4. Every rule with its rates, in date order. Expect for each organisation: ACCOMMODATION 2022-07-18 → 2025-09-21
--    (≤ ₹7,500 12%, above 18%) and 2025-09-22 → open (≤ ₹7,500 5%, above 18%); RESTAURANT 2017-11-15 → open (5%).
--    After the review script: RESTAURANT ends the day before the 1st of next month and an 18% rule starts that day.
SELECT c.code, r.effective_from, r.effective_to, r.rule_type,
       string_agg(coalesce('≤ ' || s.up_to::text, CASE WHEN r.rule_type = 'FLAT' THEN 'all' ELSE 'above' END) || ': ' || s.rate || '%' || CASE WHEN s.input_tax_credit THEN ' ITC' ELSE '' END,
                  ' · ' ORDER BY s.sort_order) AS rates,
       r.verified_at IS NOT NULL AS verified, r.source
FROM tax_rules r JOIN tax_categories c ON c.id = r.tax_category_id JOIN tax_rule_slabs s ON s.tax_rule_id = r.id
GROUP BY c.code, r.id ORDER BY c.code, r.effective_from;

-- 5. No two rules of a category overlap (the database refuses it too). Expect NO rows.
SELECT a.id, b.id FROM tax_rules a JOIN tax_rules b ON a.tax_category_id = b.tax_category_id AND a.id < b.id
WHERE daterange(a.effective_from, a.effective_to, '[]') && daterange(b.effective_from, b.effective_to, '[]');

-- 6. Each rule has exactly one rate with no upper limit, and a FLAT rule has only that one. Expect NO rows.
SELECT r.id, r.rule_type, count(*) AS rates, count(*) FILTER (WHERE s.up_to IS NULL) AS open_ended
FROM tax_rules r JOIN tax_rule_slabs s ON s.tax_rule_id = r.id GROUP BY r.id, r.rule_type
HAVING count(*) FILTER (WHERE s.up_to IS NULL) <> 1 OR (r.rule_type = 'FLAT' AND count(*) <> 1) OR (r.rule_type = 'SLAB' AND count(*) < 2);

-- 7. A line's tax is the sum of its tax rows, and every tax row names the rule it used. Expect NO rows.
SELECT i.id, i.description, i.tax_amount, coalesce(sum(t.amount), 0) AS taxes
FROM folio_items i LEFT JOIN folio_item_taxes t ON t.folio_item_id = i.id
GROUP BY i.id, i.description, i.tax_amount HAVING i.tax_amount <> coalesce(sum(t.amount), 0)
UNION ALL
SELECT t.folio_item_id, 'tax row without a rule', t.amount, NULL FROM folio_item_taxes t WHERE t.tax_rule_id IS NULL;

-- 8. The tax rows of the lines posted since Sprint 9, with the rule and rate they used.
--    (demo) after the review script: Seafood thali ₹1,450 → CGST 2.5% ₹36.25 + SGST 2.5% ₹36.25.
SELECT r.reference, i.description, i.service_date, i.amount, t.tax_code, t.rate, t.taxable_value, t.amount AS tax,
       c.code AS category, x.effective_from AS rule_from
FROM folio_item_taxes t JOIN folio_items i ON i.id = t.folio_item_id JOIN reservations r ON r.id = i.reservation_id
JOIN tax_rules x ON x.id = t.tax_rule_id JOIN tax_categories c ON c.id = x.tax_category_id
ORDER BY i.posted_at DESC, t.tax_code LIMIT 30;

-- 9. CGST and SGST of a line are always equal (the rate is split evenly). Expect NO rows.
SELECT folio_item_id, rate, sum(amount) FILTER (WHERE tax_code = 'CGST') AS cgst, sum(amount) FILTER (WHERE tax_code = 'SGST') AS sgst
FROM folio_item_taxes WHERE tax_code IN ('CGST', 'SGST') GROUP BY folio_item_id, rate
HAVING sum(amount) FILTER (WHERE tax_code = 'CGST') IS DISTINCT FROM sum(amount) FILTER (WHERE tax_code = 'SGST');

-- 10. Who changed the GST settings (the audit trail).
SELECT a.occurred_at, a.entity_type, a.action, u.full_name, a.reason
FROM audit_logs a LEFT JOIN users u ON u.id = a.user_id
WHERE a.entity_type IN ('GstRegistration', 'TaxCategory', 'TaxRule') ORDER BY a.occurred_at DESC LIMIT 20;
