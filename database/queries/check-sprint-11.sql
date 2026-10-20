-- Database checks for Sprint 11 (Razorpay). Read-only: every statement is a SELECT.
-- How to run: docs/RUN_AND_TEST.md → Sprint 11. Each block says what you should see.
-- Numbers marked (demo) assume a freshly reset database plus the review script (REVIEWING.md → Sprint 11).

-- 1. Migration V013 applied. Expect V001 … V013.
SELECT version, name, applied_at FROM schema_migrations ORDER BY version;

-- 2. The new tables exist. Expect payment_gateway_orders, webhook_events.
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('payment_gateway_orders', 'webhook_events') ORDER BY table_name;

-- 3. Every payment link and what became of it. (demo) R00003's link PAID with its payment number; the check script's ₹1
--    links CANCELLED with their reason.
SELECT r.reference, l.amount, l.status, l.gateway_order_id, l.attempts, l.last_error, p.number AS payment, p.status AS payment_status,
       l.paid_at, l.cancel_reason, l.created_at
FROM payment_gateway_orders l JOIN reservations r ON r.id = l.reservation_id LEFT JOIN payments p ON p.id = l.payment_id
ORDER BY l.created_at;

-- 4. Every webhook Razorpay sent, once each (event ids are unique), with what it did. (demo) payment.authorized PROCESSED
--    ("P… authorised"), payment.captured and order.paid IGNORED or PROCESSED, nothing FAILED.
SELECT event_id, event_type, status, attempts, outcome, received_at, processed_at
FROM webhook_events ORDER BY received_at;

-- 5. One payment per Razorpay payment (the webhook can arrive twice, or out of order). Expect NO rows.
SELECT reference AS razorpay_payment, count(*) FROM payments WHERE method = 'ONLINE' GROUP BY reference HAVING count(*) > 1;

-- 6. A PAID link points at a captured (or since refunded) online payment for the same booking and amount. Expect NO rows.
SELECT l.id, l.status, p.number, p.method, p.status, p.amount, l.amount
FROM payment_gateway_orders l LEFT JOIN payments p ON p.id = l.payment_id
WHERE l.status = 'PAID' AND (p.id IS NULL OR p.method <> 'ONLINE' OR p.reservation_id <> l.reservation_id OR p.amount <> l.amount
                              OR p.status NOT IN ('CAPTURED', 'PARTIALLY_REFUNDED', 'REFUNDED'));

-- 7. Online payments came through a link and carry Razorpay's payment id; desk payments carry neither. Expect NO rows.
SELECT number, method, reference, gateway_order_id FROM payments
WHERE (method = 'ONLINE') <> (gateway_order_id IS NOT NULL) OR (method = 'ONLINE' AND reference NOT LIKE 'pay\_%');

-- 8. Online refunds went through Razorpay (a refund id) and wait for it while PENDING. (demo) none unless you refunded online.
SELECT p.number, x.amount, x.method, x.status, x.gateway_refund_id, x.reason, x.refunded_at
FROM refunds x JOIN payments p ON p.id = x.payment_id WHERE x.method = 'ONLINE' ORDER BY x.refunded_at;

-- 9. Bookings a captured online payment confirmed: the history says why. (demo) R00003 PENDING → CONFIRMED.
SELECT r.reference, h.from_status, h.to_status, h.reason, h.changed_at
FROM reservation_status_history h JOIN reservations r ON r.id = h.reservation_id
WHERE h.reason LIKE '%online%' OR h.reason LIKE '%Razorpay%' ORDER BY h.changed_at;

-- 10. Nothing stored has card details, the guest's contact or UPI id (the payload is trimmed before it is saved). Expect NO rows.
SELECT event_id FROM webhook_events
WHERE payload::text ~* '"(card|email|contact|vpa|card_id|bank_account)"\s*:';
