-- Database checks for Sprint 22 (expenses and budgets). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 22. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database (dev seed S019).

-- 1. Migration V024 applied. Expect V001 … V024.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new tables exist. Expect 6 rows.
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('expense_categories', 'cost_centres', 'expenses', 'expense_attachments', 'budgets', 'budget_lines')
ORDER BY table_name;

-- 3. The guards are there. Expect 6 rows: a decided expense frozen, receipts only while it waits, a budget month inside
--    its year, the GST sums, the cost rule, and one bill of a vendor recorded once.
SELECT tgname AS name, tgrelid::regclass AS on_table FROM pg_trigger
WHERE tgname IN ('trg_expenses_frozen', 'trg_expense_attachments_open', 'trg_budget_lines_month')
UNION ALL
SELECT conname, conrelid::regclass FROM pg_constraint WHERE conname IN ('ck_expenses_gst', 'ck_expenses_cost')
UNION ALL
SELECT indexname, tablename::regclass FROM pg_indexes WHERE indexname = 'ux_expenses_bill'
ORDER BY 1;

-- 4. Expenses by status. (demo) APPROVED 10, SUBMITTED 2.
SELECT status, count(*), sum(amount) AS paid, sum(cost_amount) AS counted FROM expenses GROUP BY status ORDER BY status;

-- 5. Every expense's GST adds up and its cost follows the input-credit rule. Expect NO rows.
SELECT code, amount, gst_amount, taxable_value, cgst, sgst, igst, itc_eligible, cost_amount FROM expenses
WHERE taxable_value <> amount - gst_amount OR cgst + sgst + igst <> gst_amount
   OR cost_amount <> CASE WHEN itc_eligible THEN taxable_value ELSE amount END;

-- 6. Input credit only on claimable categories with a GSTIN. Expect NO rows.
SELECT e.code, c.name, e.vendor_gstin FROM expenses e JOIN expense_categories c ON c.id = e.category_id
WHERE e.itc_eligible AND (NOT c.gst_claimable OR e.vendor_gstin IS NULL);

-- 7. Every decided expense says when and by whom, and none waiting does. Expect NO rows.
SELECT code, status, decided_at, decided_by FROM expenses
WHERE (status = 'SUBMITTED') <> (decided_at IS NULL) OR (status <> 'SUBMITTED' AND decided_by IS NULL);

-- 8. Each budget's total is the sum of its lines. Expect NO rows.
SELECT b.year_start, b.total, coalesce(sum(l.amount), 0) AS lines FROM budgets b LEFT JOIN budget_lines l ON l.budget_id = b.id
GROUP BY b.id HAVING b.total <> coalesce(sum(l.amount), 0);

-- 9. Budget against actual this financial year by department, approved expenses only, at cost.
--    (demo) 8 departments with a budget of ₹1,80,000 … ₹7,20,000 a year; spending on each that has some.
WITH fy AS (SELECT make_date(CASE WHEN extract(month FROM current_date) >= 4 THEN extract(year FROM current_date)::int
                                  ELSE extract(year FROM current_date)::int - 1 END, 4, 1) AS start)
SELECT c.code, c.name,
       (SELECT coalesce(sum(l.amount), 0) FROM budget_lines l JOIN budgets b ON b.id = l.budget_id, fy
         WHERE l.cost_centre_id = c.id AND b.year_start = fy.start) AS budget_year,
       (SELECT coalesce(sum(e.cost_amount), 0) FROM expenses e, fy
         WHERE e.cost_centre_id = c.id AND e.status = 'APPROVED' AND e.expense_date >= fy.start) AS spent,
       (SELECT coalesce(sum(e.cost_amount), 0) FROM expenses e, fy
         WHERE e.cost_centre_id = c.id AND e.status = 'SUBMITTED' AND e.expense_date >= fy.start) AS waiting
FROM cost_centres c ORDER BY c.sort_order, c.code;

-- 10. Approvals are audited with what counted. (demo) none until you approve one; then "EX00013 approved: ₹1,000 for Spa".
SELECT occurred_at, entity_id, reason FROM audit_logs WHERE entity_type = 'Expense' AND reason LIKE '%approved%' ORDER BY id DESC LIMIT 10;
