-- Database checks for Sprint 10 (payments, refunds, cash drawer). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 10. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 10).

-- 1. Migration V012 applied. Expect V001 … V012.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new tables exist. Expect cash_drawer_movements, cash_drawer_sessions, cash_drawers, payment_allocations,
--    payment_status_transitions, payments, refunds.
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('payments', 'payment_allocations', 'refunds', 'cash_drawers', 'cash_drawer_sessions', 'cash_drawer_movements',
                     'payment_status_transitions') ORDER BY table_name;

-- 3. Every payment with what became of it. (demo) P00001 ₹500 CASH on R00007's bill (yesterday's shift), P00002 ₹2,000 UPI on
--    account for R00002; after the review script also the cash and UPI payments of the demo stay, the cash one PARTIALLY_REFUNDED.
SELECT p.number, r.reference, p.method, p.status, p.amount,
       coalesce((SELECT sum(a.amount) FROM payment_allocations a WHERE a.payment_id = p.id), 0) AS on_bills,
       coalesce((SELECT sum(x.amount) FROM refunds x WHERE x.payment_id = p.id), 0) AS refunded,
       p.reference AS payment_reference, p.void_reason
FROM payments p JOIN reservations r ON r.id = p.reservation_id ORDER BY p.number;

-- 4. Money never goes over the payment: what is on bills plus what was refunded from account ≤ the amount, and refunds
--    ≤ the amount (the database refuses it too). Expect NO rows.
SELECT p.number, p.amount, a.on_bills, x.refunded, x.from_account
FROM payments p
LEFT JOIN LATERAL (SELECT coalesce(sum(amount), 0) AS on_bills FROM payment_allocations WHERE payment_id = p.id) a ON true
LEFT JOIN LATERAL (SELECT coalesce(sum(amount), 0) AS refunded, coalesce(sum(amount) FILTER (WHERE folio_id IS NULL), 0) AS from_account
                   FROM refunds WHERE payment_id = p.id) x ON true
WHERE x.refunded > p.amount OR a.on_bills + x.from_account > p.amount;

-- 5. A refund from a bill never exceeds what the payment put on that bill. Expect NO rows.
SELECT x.payment_id, x.folio_id, sum(x.amount) AS refunded, (SELECT sum(a.amount) FROM payment_allocations a
       WHERE a.payment_id = x.payment_id AND a.folio_id = x.folio_id) AS allocated
FROM refunds x WHERE x.folio_id IS NOT NULL GROUP BY x.payment_id, x.folio_id
HAVING sum(x.amount) > coalesce((SELECT sum(a.amount) FROM payment_allocations a WHERE a.payment_id = x.payment_id AND a.folio_id = x.folio_id), 0);

-- 6. The status agrees with the refunds: REFUNDED = all of it back, PARTIALLY_REFUNDED = some, CAPTURED = none. Expect NO rows.
SELECT p.number, p.status, p.amount, coalesce(sum(x.amount), 0) AS refunded
FROM payments p LEFT JOIN refunds x ON x.payment_id = p.id
WHERE p.status IN ('CAPTURED', 'PARTIALLY_REFUNDED', 'REFUNDED') GROUP BY p.id
HAVING (p.status = 'CAPTURED' AND coalesce(sum(x.amount), 0) <> 0)
    OR (p.status = 'PARTIALLY_REFUNDED' AND NOT (coalesce(sum(x.amount), 0) > 0 AND coalesce(sum(x.amount), 0) < p.amount))
    OR (p.status = 'REFUNDED' AND coalesce(sum(x.amount), 0) <> p.amount);

-- 7. No card numbers: no payment or refund reference or note holds 13 or more digits in a row. Expect NO rows.
SELECT 'payment' AS what, number, reference, note FROM payments
WHERE regexp_replace(coalesce(reference, '') || ' ' || coalesce(note, ''), '[ -]', '', 'g') ~ '[0-9]{13,}'
UNION ALL
SELECT 'refund', payment_id::text, reference, reason FROM refunds
WHERE regexp_replace(coalesce(reference, '') || ' ' || reason, '[ -]', '', 'g') ~ '[0-9]{13,}';

-- 8. Cash and the drawer: every cash payment and cash refund belongs to a shift, nothing else does. Expect NO rows.
SELECT 'payment' AS what, number, method, cash_drawer_session_id FROM payments
WHERE (method = 'CASH') <> (cash_drawer_session_id IS NOT NULL)
UNION ALL
SELECT 'refund', payment_id::text, method, cash_drawer_session_id FROM refunds
WHERE (method = 'CASH') <> (cash_drawer_session_id IS NOT NULL);

-- 9. Shifts with their ledger. Expected = float + every movement after it; a closed shift's variance = counted − expected.
--    (demo) yesterday: float ₹5,000, +₹500, counted ₹5,500, variance 0, CLOSED. After the review script: today's shift
--    float ₹5,000, +₹1,000, −₹200, expected ₹5,800, counted ₹5,750, variance −50, CLOSED with the manager's approval.
SELECT d.name AS drawer, s.status, s.opened_at, s.opening_float, sum(m.amount) FILTER (WHERE m.movement_type <> 'OPENING_FLOAT') AS movements,
       s.expected_amount, s.counted_amount, s.variance, s.close_note, s.approved_at IS NOT NULL AS approved, s.approval_note
FROM cash_drawer_sessions s JOIN cash_drawers d ON d.id = s.cash_drawer_id LEFT JOIN cash_drawer_movements m ON m.session_id = s.id
GROUP BY d.name, s.id ORDER BY s.opened_at;

-- 10. The ledger adds up: a closed shift's expected amount is the sum of its movements, and its variance is counted −
--     expected. A shift with a variance is closed only after approval. Expect NO rows.
SELECT s.id, s.status, s.expected_amount, sum(m.amount) AS ledger, s.counted_amount, s.variance
FROM cash_drawer_sessions s JOIN cash_drawer_movements m ON m.session_id = s.id
WHERE s.status <> 'OPEN' GROUP BY s.id
HAVING s.expected_amount <> sum(m.amount) OR s.variance <> s.counted_amount - s.expected_amount
    OR (s.status = 'CLOSED' AND s.variance <> 0 AND s.approved_at IS NULL);

-- 11. At most one open shift per drawer (a unique index refuses a second). Expect NO rows.
SELECT cash_drawer_id, count(*) FROM cash_drawer_sessions WHERE status = 'OPEN' GROUP BY cash_drawer_id HAVING count(*) > 1;

-- 12. Who took, refunded and voided money, and who approved variances (the audit trail).
SELECT a.occurred_at, a.entity_type, a.action, u.full_name, a.reason
FROM audit_logs a LEFT JOIN users u ON u.id = a.user_id
WHERE a.entity_type IN ('Payment', 'PaymentAllocation', 'Refund', 'CashDrawerSession') ORDER BY a.occurred_at DESC LIMIT 20;
