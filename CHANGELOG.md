# Changelog

One entry per accepted sprint, tagged `sprint-NN` on `main`. Format: [Keep a Changelog](https://keepachangelog.com).

## [sprint-14] — 2026-10-26 — Menu, restaurant POS and KOT (accepted)

### Added
- Migration V016: the menu (`menu_categories`, `menu_items` with `VEG` / `NON_VEG` / `EGG` and sold out,
  `menu_item_variants` for portions, `modifier_groups` and `modifiers` for choices; switched off, never deleted),
  `restaurant_tables`, `orders` (`O00001` …; at most one open order per table; final once settled or voided; totals and
  CHECKs on the row; no card numbers in the reference), `order_items` (name, portion, choices and price copied when
  ordered; a line sent to the kitchen can only be voided), `kots` (`K00001` …) and `kot_items` (only voided, never
  changed). `cash_drawer_movements` adds `order_id` and type `POS_SALE`; `folio_items.source` adds `RESTAURANT`.
- Orders: open at a table, room service to an in-house room, or takeaway; add dishes with their portion and choices;
  change or remove lines not sent; send to the kitchen as one ticket; served; paid at the restaurant (cash into the open
  cash drawer, UPI, card, bank transfer) or charged to the room as one FOOD line on the guest's folio with its GST.
  Every change carries the order's version: two waiters changing the same order at once, the second gets 409 and sees
  the first one's change. Discounts need `pos.discount` and voids `pos_order.void`, each with a reason. A bill is
  settled only once everything is sent and served.
- Kitchen display: tickets in New, Preparing and Ready, oldest first, red after 15 minutes, voided lines struck
  through; refreshes every 10 seconds (polling, no websockets yet); Start and Ready in one tap.
- API: `GET /menu`, `POST|PUT /menu/categories`, `POST|PUT /menu/items`, `POST /menu/items/{id}/availability`, `GET|POST|PUT
  /menu/tables`; `GET /orders`, `/orders/floor`, `/orders/rooms`, `POST /orders` (Idempotency-Key optional), `GET|PUT
  /orders/{id}`, `POST /orders/{id}/items`, `PUT|DELETE /orders/{id}/items/{itemId}`, `POST /orders/{id}/items/{itemId}/void`,
  `POST /orders/{id}/send | discount | void | pay | charge-to-room`, `POST /orders/{id}/kots/{kotId}/serve`; `GET
  /kitchen/kots`, `POST /kitchen/kots/{id}/start | ready`.
- Staff app: **Restaurant** (`/restaurant`, the floor on a waiter's phone), an order (`/restaurant/orders/:id`), **Today's
  orders** (`/restaurant/orders`), **Kitchen** (`/restaurant/kitchen`), **Menu** (`/restaurant/menu`: sections, dishes with
  portions and choices, sold out, tables).
- Dev seed S011: a 14-dish menu in five sections (Paneer Tikka with Half / Full, a spice level and add-ons; Masala Chai
  sold out), tables T1–T4 and B1, B2, and O00001 at T2 in the kitchen as K00001.
- Tests: backend `RestaurantTests` (two waiters at once → 409, the ₹1,250 room charge, permissions, GST with a discount,
  cash with the drawer, table and room rules, portions and choices, database immutability), `RestaurantUnitTests`;
  frontend `restaurant.spec.ts`; browser steps S14-01 … S14-09 (waiter on a 360 px phone, kitchen on a tablet);
  `check-api.mjs` Sprint 14 block; `database/queries/check-sprint-14.sql`.
- Docs: API.md, DATABASE.md, schema-sprint-14.md, system design Sprint 14 and 4.24 with the restaurant flow diagram,
  ARCHITECTURE.md module map, RUN_AND_TEST, REVIEWING.

### Changed
- The shell menu adds **Restaurant** and **Kitchen**; the note about later modules now names activities, inventory and
  staff.

### Open points
- Walk-in restaurant bills paid at the restaurant get no GST tax invoice yet (room charges are invoiced with the stay,
  Sprint 12). To decide with the accountant: a bill of supply series for restaurant sales, or one at day end.
- The plan's demo used Room 103; the review uses Room 104 (R00008), because Room 103 is kept free for Sprint 2's review.

## [sprint-13] — 2026-10-26 — Housekeeping and maintenance, mobile-first (accepted)

### Added
- Migration V015: `housekeeping_tasks` (one per DIRTY state change, `source_change_id` UNIQUE; at most one open task per
  room or dorm bed; never deleted), `housekeeping_checklist_templates` and `housekeeping_checklist_items` (one active per
  property, kind of cleaning and room type; every property gets a 7-line "Departure clean"), `maintenance_assets`,
  `maintenance_tickets` (`M00001` …; where = room, bed, equipment or place; out of order needs a room and a date),
  `maintenance_comments` and `maintenance_attachments` (append-only). Rooms already dirty get their task.
  `unit_housekeeping_history.source` adds `HOUSEKEEPING` and `MAINTENANCE`.
- Check-out, room move and the front desk's "dirty" create the room's cleaning task in the same transaction; three
  check-outs of the same guest at once make one task.
- Two taps for a housekeeper (Start, Done with every line ticked); a supervisor marks the room READY or sends it back with
  what to redo (property setting `housekeeping.inspection_required`, default on). Assigning, tasks by hand (stay-over,
  deep clean) and checklist editing for supervisors.
- Maintenance tickets: report with photos (shrunk on the phone, checked by their bytes, at most 2 MB and 6 per ticket,
  encrypted in the file store); Out of order blocks the room from tonight to the expected date (at most 90 days; refused
  when a guest is booked in it, naming the booking); assign, in progress / on hold, resolve (releases the block and sends
  the room to cleaning), close; a timeline of every change. Reporters without `maintenance.read` see their own tickets.
- API: `GET /housekeeping/board`, `/staff`, `POST /housekeeping/tasks`, `GET /housekeeping/tasks/{id}`, `POST
  /housekeeping/tasks/{id}/assign | start | finish | inspect`, `GET|POST /housekeeping/checklists`, `PUT
  /housekeeping/checklists/{id}`; `GET|POST /maintenance/tickets` (Idempotency-Key optional), `GET
  /maintenance/tickets/{id}`, `POST /maintenance/tickets/{id}/assign | progress | resolve | close | comments | photos`,
  `GET /maintenance/tickets/{id}/photos/{photoId}`, `GET /maintenance/staff`, `GET|POST /maintenance/assets`, `PUT
  /maintenance/assets/{id}`.
- Staff app, built for a 360 px phone: **Housekeeping** (`/housekeeping`: one card per room, rush rooms first, one big
  button; task sheet with checklist, assign, send back, report a problem), **Checklists**, **Maintenance**
  (`/maintenance`), **Report a problem** (`/maintenance/new`), a ticket's page (`/maintenance/:id`).
- Dev seed S010: departure checklist, Room 101's task, equipment AC-101, GEYSER-106, POOL-PUMP, tickets M00001 (pool pump)
  and M00002 (tap in Room 104).
- Tests: backend `HousekeepingTests`, `MaintenanceTests`, `HousekeepingUnitTests`; frontend `housekeeping.spec.ts`; browser
  steps S13-01 … S13-07 on a 360 px phone; `check-api.mjs` Sprint 13 block; `database/queries/check-sprint-13.sql`.
- Docs: API.md, DATABASE.md, schema-sprint-13.md, system design Sprint 13 and 4.23 with the housekeeping flow diagram,
  ARCHITECTURE.md module map, RUN_AND_TEST, REVIEWING.

### Changed
- `GET /rooms/status` adds today's cleaning state (`housekeeping`) and `openRepairs` to each room; the Room status screen
  shows them. The front desk labels CLEAN as "To inspect" and INSPECTION as "Inspecting".
- `BlockService` shares `AddAsync` and `Release` with maintenance, so out-of-order blocks follow the Sprint 2 rules.

## [sprint-12] — 2026-10-25 — GST invoices, credit/debit notes, receipts (accepted)

### Added
- Migration V014: `invoice_series` (one gap-free counter per property, document type and financial year; only ever +1),
  `invoices`, `invoice_items`, `invoice_item_taxes`, `credit_notes` (`CANCEL` or `REDUCE`), `credit_note_items`,
  `credit_note_item_taxes`, `debit_notes`, `debit_note_items`, `debit_note_item_taxes`, `receipts` (one per payment) and
  `einvoice_records` (for Sprint 25). Documents refuse UPDATE and DELETE; header totals must equal their lines (checked at
  commit); a line can't be credited for more than is left. `folio_items` gets type `CREDIT` (`adjusts_item_id`,
  `credit_note_id`).
- Numbers `BEACH/2627/00001` (invoice), `BEACH/B2627/0001` (bill of supply), `BEACH/C2627/0001` (credit note),
  `BEACH/D2627/0001` (debit note), `BEACH/R2627/00001` (receipt): at most 16 characters (rule 46(b)), taken by one
  upsert inside the issuing transaction, so they are consecutive with no gaps under concurrency.
- Check-out issues the GST invoice of each folio (to the bill-to guest on file; a company with a GSTIN makes it B2B)
  and a receipt for each payment, in the check-out transaction (property setting `invoice.issue_at_checkout`, default
  on). A folio that can't be invoiced doesn't stop the check-out: a booking note says why.
- API: `GET /folios/{id}/invoice-preview`, `POST /folios/{id}/invoice` (Idempotency-Key), `GET /invoices`,
  `GET /invoices/{id}` (+ `/pdf`), `POST /invoices/{id}/credit-notes` (CANCEL or REDUCE, reason), `POST
  /invoices/{id}/debit-notes` (reason), `GET /credit-notes/{id}/pdf`, `GET /debit-notes/{id}/pdf`, `POST
  /payments/{id}/receipt`, `GET /receipts/{id}/pdf`. Taxes are copied from the folio lines, never recalculated; place of
  supply is the property's state; customer GSTINs are validated with their check digit; a composition property issues a
  bill of supply.
- A4 PDFs drawn on the server with PDFsharp/MigraDoc 6.2.4 (MIT) and an embedded DejaVu Sans font (with ₹). Each invoice,
  note and receipt has **PDF** (download) and **Print** (the browser's print dialog on the same PDF).
- Staff app: **Invoices** (`/invoices`, `/invoices/:id` with Credit note and Debit note dialogs and PDFs); **Issue
  invoice** on a bill with a preview of the lines and the customer; billed charges marked with their invoice on the bill;
  the booking links to its invoices; **Receipt** on a payment.
- Tests: backend `InvoiceTests` (the demo, 20 parallel invoices numbered 1–20, B2B intra- and inter-state, the database
  refusing edits and deletes, REDUCE, debit notes, a wrong GSTIN at check-out, receipts, idempotency, permissions) and
  `InvoiceUnitTests`; frontend `invoices.spec.ts`; browser steps S12-01 … S12-05; `check-api.mjs` Sprint 12 block;
  `database/queries/check-sprint-12.sql`.
- Docs: API.md, DATABASE.md, schema-sprint-12.md, GST.md (invoices, notes, open questions for the accountant), system
  design Sprint 12 and 4.22 with the invoice flow diagram, RUN_AND_TEST, REVIEWING.

### Changed
- A folio charge on an invoice that stands, or one a credit note reduced, can't be reversed or moved (422 `INVOICED`,
  saying which invoice and to issue a credit note). `FolioItemDto` adds `invoiceNumber` and `adjustable`;
  `FolioDto.reversals` includes credit lines.
- `System.Security.Cryptography.Pkcs` pinned to 10.0.13 (PDFsharp brought in a version with known vulnerabilities).

## [sprint-11] — 2026-10-23 — Razorpay (accepted)

### Added
- Migration V013: `payment_gateway_orders` (a payment link: a Razorpay order for a booking and an amount, the guest's
  private token, `CREATED` → `PAID` or `CANCELLED`, declined tries and the bank's reason; what it is for never changes;
  never deleted) and `webhook_events` (each verified Razorpay delivery, unique on `(provider, event_id)`, `PROCESSED`,
  `IGNORED` or `FAILED` with what it did; only a failed one can be retried; card, contact and UPI id never stored).
  `payments` gets method `ONLINE`, `gateway_order_id`, `gateway_method` and a unique index on Razorpay's payment id;
  `refunds` gets `ONLINE` with `gateway_refund_id`; new status moves (authorised on creation; back from refunded after a
  failed gateway refund) for `ONLINE` only.
- `IPaymentGateway` and `RazorpayPaymentGateway` (HttpClient; a timeout per try; up to 3 tries with exponential backoff
  and jitter where safe; a refund is never retried on an unclear failure), and `FakeRazorpayServer`, which answers the
  same HTTP calls and signs webhooks, used by the tests and by development without keys (never production).
- API: `POST|GET /properties/{id}/payments/razorpay/orders` (send a link with an `Idempotency-Key`, amount at most what
  the booking owes less open links; a booking's links), `GET …/orders/{id}` (with its webhook events),
  `POST …/orders/{id}/cancel` (reason); `POST /api/v1/webhooks/razorpay` (no sign-in: HMAC-SHA256 over the raw body,
  event id claimed once, payment fetched from Razorpay, applied in one transaction, never backwards); the guest's page
  `GET /api/v1/pay/{token}`, `POST …/callback` (signature checked; shows "confirming", records nothing) and
  `POST …/fake-checkout` (fake Razorpay only).
- A captured online payment goes on the main bill (the rest on account), marks the link paid and confirms a pending
  booking, in one transaction. Refunds of online payments go back through Razorpay and wait for `refund.processed`;
  `refund.failed` puts the money back on the payment.
- Staff app: **Online payment links** on a booking (send, copy, WhatsApp, open, cancel, History of what Razorpay sent);
  the guest's page `/pay/:token` outside the staff app (Razorpay Checkout, or test buttons with the fake); online
  details and Razorpay refunds on a payment; *Online (Razorpay)* in the payments filter.
- Tests: backend `RazorpayTests` (the demo, duplicate delivery including two at once, bad signature → 401 with nothing
  stored, captured before authorised, a declined try, a cancelled link paid anyway, retries and timeouts, online refunds,
  permissions and tenants, database immutability) and `RazorpayUnitTests`; frontend `payment-links.spec.ts`; browser
  steps S11-01 … S11-06; `check-api.mjs` Sprint 11 block; `database/queries/check-sprint-11.sql`.
- Docs: RAZORPAY.md (setup in test mode with a tunnel), API.md, DATABASE.md, schema-sprint-11.md, system design Sprint 11
  and 4.21 with the Razorpay flow diagram, RUN_AND_TEST, REVIEWING.

### Changed
- `POST /payments` refuses method `ONLINE` under `Method` (only Razorpay records online payments). Online payments can't
  be voided. `PaymentDto` adds `gatewayMethod` and `paymentLinkId`.
- An anonymous request (webhook, guest page) acts for the link's organisation through `SystemActor`, so the tenant
  filters still apply.
- `.env.example`: the Razorpay keys are Sprint 11; leave them empty to use the fake Razorpay.

## [sprint-10] — 2026-10-20 — Payments, refunds, cash drawer (accepted)

### Added
- Migration V012: `payments` (numbered P00001 per property; CASH, UPI, CARD, BANK_TRANSFER; status graph in
  `payment_status_transitions`, enforced by a trigger; only the status and the void fields can change; cash only with a
  drawer shift; a card reference can't hold 13 or more digits), `payment_allocations` (which bill a payment settles),
  `refunds` (reason 3–500 characters; cash only with a drawer shift), `cash_drawers` ("Front desk" for every property),
  `cash_drawer_sessions` (OPEN → CLOSED or VARIANCE_PENDING → CLOSED; one open shift per drawer; counts never change) and
  `cash_drawer_movements` (the drawer's signed ledger; rows only while the shift is open). Nothing in these tables can be
  deleted. `fn_payment_money_check` locks the payment row and refuses an allocation or refund that would overspend it.
- Payments API: `GET|POST /properties/{id}/payments` (list with totals by method; take money on a bill or on account,
  `Idempotency-Key`), `GET /payments/{id}`, `POST /payments/{id}/allocate`, `/refund` (`Idempotency-Key`, reason) and
  `/void` (reason). Cash drawer API: `GET /cash-drawer`, `GET /cash-drawer/sessions/{id}`, `POST /cash-drawer/open`,
  `/close` and `/sessions/{id}/approve-variance`. Permissions `payment.read`, `payment.create`, `payment.refund`,
  `payment.void`, `cash_drawer.operate`, `cash_drawer.approve_variance` (seeded to roles since Sprint 1).
- Refunds can't overspend: the booking, the payment and the cash shift are locked in that order, the service checks
  what is left, and the database checks again; a refund takes from on account first, then the bills the payment paid.
- No card data: anything that looks like a card number (13–19 digits passing the Luhn check) is refused under its
  field, in the API and in the forms.
- Dev seed S009: yesterday's closed shift (₹5,000 float, ₹500 cash from R00007 as P00001, counted ₹5,500) and a ₹2,000
  UPI advance on account for R00002 (P00002).
- Staff app: **Payments** (`/payments`: totals by method, filters, search, the cash drawer panel) and one payment
  (`/payments/:id`: bills, on account, refunds, Refund, Void, Put on a bill); **Take payment** and the bill's payment
  lines on `/folios/:id`; the cash drawer panel on the front desk (Open drawer, Close drawer with the variance as you
  type, Approve).
- Tests: backend `PaymentTests` (the demo flow, concurrent refunds through a barrier, DELETE/UPDATE refused by the
  database, idempotent take and refund, field errors, cash needing an open drawer, an advance put on a bill, a refund
  when a guest leaves early, void rules, the list and permissions), `PaymentUnitTests` and a dev seed test; frontend
  `payments.spec.ts`; browser steps S10-01 … S10-07; `check-api.mjs` Sprint 10 block;
  `database/queries/check-sprint-10.sql`.

### Changed
- Bill balances subtract real payments (net of refunds); the front desk balance and check-out use the booking's bills
  less every payment that counts, so money on account counts too. `FolioDto` has `paymentLines`. The calendar's payment
  status is `NONE`, `PARTIAL` or `PAID` from real payments.

### Documentation
- API.md, DATABASE.md, `schema-sprint-10.md`, system design 3 (Sprint 10) and 4.20 with the payment flow diagram,
  ARCHITECTURE module map, RUN_AND_TEST.md and REVIEWING.md Sprint 10 sections.

## [sprint-09] — 2026-10-20 — GST configuration and tax engine (accepted)

### Added
- Migration V011: `gst_registrations` (a property's GSTIN from/to a date; format CHECK, state = first two digits,
  periods of a property can't overlap), `tax_categories` (what is taxed, SAC/HSN, the bill charge category it covers —
  one tax category per charge category), `tax_rules` (a category's rule from/to a date, FLAT or SLAB, its source and
  notes, who verified it; rules of a category can't overlap; only the end date and the verification can change) and
  `tax_rule_slabs` (rates by value per unit per night, with or without input tax credit; immutable).
  `folio_item_taxes.tax_rule_id` now references `tax_rules`. Nothing in these tables can be deleted.
- Seed rules **as data** (`fn_seed_tax_defaults`, also run for every new organisation by a trigger): accommodation
  (SAC 996311) 18 Jul 2022 – 21 Sep 2025 up to ₹7,500 12% / above 18%, and from 22 Sep 2025 up to ₹7,500 5% without ITC
  / above 18% with ITC; restaurant service (SAC 996331) 5% without ITC from 15 Nov 2017 — each with its notification as
  the source, unverified until the accountant presses Verify.
- The tax engine (`TaxCalculator`, `TaxMath`): the registration, category and rule effective on the day of the service;
  slab by the value of one unit for one night (a value exactly at a limit is in that slab); CGST + SGST at half the rate
  each inside the supplier's state, IGST across states; each tax rounded to the paisa once per line (halves away from
  zero). No registration on the day, a composition registration or no tax category → no GST, with the reason.
- GST API: `GET|POST /properties/{id}/gst-registrations`, `GET|POST /tax/categories`, `GET|POST /tax/rules` (a new rule
  ends the running one the day before), `POST /tax/rules/{id}/verify`, `POST /tax/calculate` (preview).
- Dev seed S008: the demo property is registered for GST in Goa (30AABCT1234C1Z9, made up, valid check digit).
- Staff app: **Settings → GST** (`/settings/gst`): the property's registration, every tax category with its rules over
  time (Now / Upcoming, source, Verify), *Add registration*, *Add category*, *Add rule* (one rate, or rates by value per
  night) and *Try a charge* (the preview, CGST/SGST or IGST). The bill shows each line's GST under its amount.
- Tests: backend `TaxTests` (seeded rules, the same night before/after the rule change date, IGST and no-tax reasons,
  room nights by slab, food vs transport, reversal and moved line keep their tax, package components, **a rule change
  doesn't change a posted charge**, the accountant adds next month's rule, field errors, `TAX_RULE_MISSING`, permissions
  and tenancy), `TaxUnitTests` (slab boundaries, intra- vs inter-state, rounding per line, GSTIN check digit) and a dev
  seed test; frontend `gst.spec.ts`; browser steps S09-01 … S09-07; `check-api.mjs` Sprint 09 block;
  `database/queries/check-sprint-09.sql`.

### Changed
- Posting a charge, a package or a room night now stores its GST (`folio_items.tax_amount` and `folio_item_taxes` with the
  rule id and rate). Charges in a taxed category on a day no rule covers are refused (`TAX_RULE_MISSING`) instead of
  being posted without GST. `FolioItemDto` has `taxes`.
- Browser steps S08-02 … S08-04 expect 5% GST on the dinner they post (the demo property is now GST-registered).

### Documentation
- GST.md worked examples; API.md, DATABASE.md, `schema-sprint-09.md`, system design 4.19 and its diagram, ARCHITECTURE
  module map, RUN_AND_TEST.md and REVIEWING.md Sprint 09 sections.

## [sprint-08] — 2026-10-18 — Folio (accepted)

### Added
- Migration V010: `folios` (one main folio per confirmed booking, numbered F00001 … per property; more to split a bill),
  `folio_items` (immutable lines: CHARGE or REVERSAL, categories ROOM, FOOD, BEVERAGE, ACTIVITY, TRANSPORT, EQUIPMENT,
  LAUNDRY, OTHER; source MANUAL, ROOM_NIGHT or PACKAGE) and `folio_item_taxes` (filled from Sprint 9). Triggers refuse
  UPDATE and DELETE on lines and taxes; CHECKs make amount = quantity × price and total = amount + tax; a trigger makes
  every reversal the exact negative of a charge on the same folio; a line is reversed or moved at most once; a partial
  unique index charges each night of a stay once. `fn_open_missing_folios()` opens main folios for existing bookings.
- Folio API under `/api/v1/properties/{id}`: `GET folios?q&show`, `GET reservations/{rid}/folios`, `GET folios/{fid}`,
  `POST folios/{fid}/charges` (Idempotency-Key; a charge or a package as one line per component),
  `POST folios/{fid}/charges/{itemId}/reverse` (`folio.adjust` and a reason; 409 `ALREADY_REVERSED` names who and when),
  `POST folios/{fid}/split` (to an existing or a new folio; reversal here, copy there). 422 `FOLIO_CLOSED` for bookings
  that don't take charges (enquiry, pending, cancelled, no-show).
- The main folio is opened when a booking becomes CONFIRMED. Room nights are posted at check-in for the whole stay
  (`folio.room_charge_posting = "AT_CHECK_IN"`, the default) or night by night (`"NIGHTLY"`) by a background job that
  runs every 15 minutes (`Folio:RoomChargeJob:Enabled`). Every writer locks the reservation row first.
- Dev seed S007: main folios for the demo bookings; R00008 with room nights, airport pick-up and dinner; R00007 with its
  room night and laundry; `frontdesk.allow_checkout_with_balance = true` on the demo property only (payments arrive in
  Sprint 10).
- Staff app: **Folios** (`/folios`: search, in house / arriving / all) and the bill (`/folios/:id`: balance, sums,
  chips by category, every line with who posted it, reversed lines struck through with the reason, a switcher between
  the stay's folios) with **Post charge** (charge or package), **Reverse** (managers, with a reason) and **Move lines**;
  a **Bill** button on the booking page and the front desk lists.
- Tests: backend `FolioTests` (the brief's ₹10,950 example, DELETE/UPDATE refused by the database, reversal needs
  `folio.adjust` and a reason, 10 pairs of simultaneous reversals, idempotent posting, field errors, package lines and
  split, folio at confirmation and `FOLIO_CLOSED` after cancel, nightly posting with a pinned clock, early departure,
  permissions and tenancy), `FolioUnitTests` and a dev seed test; frontend `folios.spec.ts`; browser steps
  S08-01 … S08-07; `check-api.mjs` Sprint 08 block; `database/queries/check-sprint-08.sql`.

### Changed
- The front desk's balance and the check-out rule use the real folio balance (`FolioBalances` replaces the Sprint 7
  placeholder). Check-out posts any night still missing and, when the guest leaves early, reverses the released nights
  before checking the balance.
- `ReservationService` opens the main folio on confirmation; `FrontDeskService` posts room nights at check-in.
- Browser step S07-03 expects the demo bill on R00007 (*still owed*) instead of *Nothing owed*.

### Documentation
- `docs/architecture/system-design.md`: Sprint 08 in section 3, part 4.18 *Folios: the bill of a stay*, and the folio
  flow diagram (`tools/handbooks/assets/folio-flow.png`). API.md, DATABASE.md, `schema-sprint-08.md`, ARCHITECTURE
  module map, RUN_AND_TEST.md and REVIEWING.md Sprint 08 sections.

## [sprint-07] — 2026-10-18 — Front desk (accepted)

### Added
- Migration V009: `reservations.checked_in_at` / `checked_out_at`; `unit_housekeeping_status` (cleaning state per room
  or dorm bed; no row = READY) and append-only `unit_housekeeping_history` (`CHECK_OUT`, `ROOM_MOVE`, `MANUAL` events,
  `consumed_at` for Sprint 13).
- Front desk API under `/api/v1/properties/{id}`: `front-desk/arrivals?date` (with late arrivals), `front-desk/departures?date`
  (with overdue guests), `front-desk/in-house`, `front-desk/dashboard` (counts and rooms board),
  `reservations/{rid}/room-options`, `reservations/{rid}/assign-room`, `reservations/{rid}/check-in`,
  `reservations/{rid}/check-out`, `walk-ins` (Idempotency-Key) and `PUT rooms/{unitId}/housekeeping`.
- Every "today" is the property's business date from an injected clock (`TimeProvider`), never the server's date.
- Check-in locks the reservation, then its rooms by id; refuses with the reason when the previous guest hasn't left
  (`ROOM_OCCUPIED`), the room isn't ready (`HOUSEKEEPING_NOT_READY`, or check in anyway with a reason when
  `frontdesk.allow_checkin_when_not_ready` allows it), it is the wrong day (`CHECK_IN_DATE`) or already done
  (`ALREADY_CHECKED_IN`, naming who and when). PENDING bookings are confirmed on the way in.
- Check-out refuses while the folio balance is above zero unless `frontdesk.allow_checkout_with_balance` (the balance
  comes from Sprint 8; until then it is 0); leaving early needs a reason and releases the nights left; rooms become DIRTY
  with a `CHECK_OUT` event in the same transaction.
- Moving an in-house guest splits the stay at today; the old room becomes DIRTY (`ROOM_MOVE`).
- Walk-in: guest (found or new), booking from today on a ready room and check-in in one transaction.
- Dev seed S006: R00007 in house leaving today, R00008 in house for two more nights, Room 101 dirty.
- Staff app: **Front desk** (`/front-desk/arrivals | departures | in-house | rooms`) with tiles, lists, one-click
  check-in and check-out, a window that says why a check-in can't go straight through (choose another room or check in
  anyway), a rooms board with *Mark ready*; **Walk-in** (`/front-desk/walk-in`, three steps on one screen); Check in,
  Check out and Assign room on the booking page.
- Tests: backend `FrontDeskTests` (the same booking checked in by 20 pairs of simultaneous clicks, the demo flow,
  date rules, readiness and its override, settings, the check-out event, balance, early departure, walk-in with replay and
  rollback, field errors, permissions, dashboard and in-house moves), `FrontDeskClockTests` (arrivals at 23:30 and 00:30
  IST, mid-stay move, overdue guest), `FrontDeskUnitTests` and a dev seed test; frontend `front-desk.spec.ts`; browser
  steps S07-01 … S07-09; `check-api.mjs` Sprint 07 block; `database/queries/check-sprint-07.sql`.

### Changed
- `ReservationService`: booking and room-move steps shared with the front desk (`BookAsync`, `MoveRoomAsync`); a checked-in
  booking's room is changed from the front desk (the calendar's reassign says so).
- `ReservationDto` gains `checkedInAt` and `checkedOutAt`.
- `unit_housekeeping_status` is created in Sprint 07 instead of Sprint 13 (tables-by-sprint updated).

### Documentation
- `docs/architecture/system-design.md`: Sprint 07 in section 3, part 4.17 *The front desk*, and the check-in flow
  diagram (`tools/handbooks/assets/check-in-flow.png`). API.md, DATABASE.md, `schema-sprint-07.md`, lifecycles,
  ARCHITECTURE module map, RUN_AND_TEST.md and REVIEWING.md Sprint 07 sections.

## [sprint-06] — 2026-10-17 — Reservation calendar (accepted)

### Added
- Migration V008: index `ix_reservation_units_calendar` on current reservation rows (property, dates) for the calendar.
- `GET /api/v1/properties/{id}/calendar?from&to` (up to 62 days): rooms and dorm beds, bookings (guest, status, source,
  payment status, version) and active blocks, one indexed query per layer (`CalendarService`).
- `POST /api/v1/properties/{id}/reservations/{rid}/reassign`: moves one room or dorm bed of a booking to another for the
  same nights, with a required reason. Same locking as booking (reservation row, then room row), the availability
  engine's rules, the old row superseded and the nightly prices copied (price unchanged), a note on the booking and
  audit entries with who and why. 409 `ROOM_UNAVAILABLE` names the booking or block in the way; 422
  `RESERVATION_NOT_MOVABLE` for checked-in or finished bookings.
- Dev seed S005: Room 106 blocked for maintenance from tomorrow for two nights.
- Staff app: **Calendar** (`/reservations/calendar`, Day / Week / Month) with bookings and blocks as bars, CDK
  drag-and-drop between rooms (dates locked), a details and move window (status, source, payment, reason), an inline
  reason when a drop can't work, and a list by day on phones. Deep links `?view=&date=`.
- Tests: backend `CalendarTests` (two moves into the same room raced 20 times, audit who/why, blocked and booked rooms,
  field errors, the window, permissions, the index is used) and a dev seed test; frontend `calendar.spec.ts` (windows,
  rows, placement, move reasons, phone list, 7 rooms × 31 days rendered in under 200 ms); browser steps S06-01 … S06-08;
  `check-api.mjs` Sprint 06 block; `database/queries/check-sprint-06.sql`.

### Changed
- Test helpers that build rooms, plans, guests and bookings moved to `Resort.Tests/Support/BookingKit.cs`, shared by the
  reservation and calendar tests.

### Documentation
- `docs/architecture/system-design.md`: Sprint 06 in section 3, new part 4.16 *Moving a guest to another room*, and the
  room-move flow diagram (`tools/handbooks/assets/room-move-flow.png`). API.md, DATABASE.md, `schema-sprint-06.md`,
  RUN_AND_TEST.md and REVIEWING.md Sprint 06 sections.

## [sprint-05] — 2026-10-15 — Availability engine and reservations (accepted)

### Added
- Migration V007: `reservations`, `reservation_units` (rooms and dorm beds held, with `EXCLUDE USING gist` on
  `(unit_id, stay)` and `(bed_id, stay)` as the final guard against double booking), `reservation_guests`,
  `reservation_rate_breakdowns`, `reservation_notes`, `reservation_status_history`, `reservation_status_transitions`
  (the status graph, enforced by a trigger), `idempotency_keys`, `property_counters` (references R00001 …). Reservations,
  their rooms, prices, notes and history are never deleted. A block and a booking can't cover the same room and night
  (triggers both ways).
- Availability engine (`Resort.Domain/Reservations/Availability.cs`, pure and unit-tested): free rooms and beds for every
  night of a stay, allocation of rooms and one bed per dorm guest, and the reason when something doesn't fit.
- Booking algorithm (`docs/architecture/booking-algorithm.md`): one transaction that claims the Idempotency-Key, locks the
  rooms of the requested types in id order, allocates, prices every night from the rate grid and stores the response.
- APIs under `/api/v1/properties/{id}`: `availability`, `reservations` (list, get, create with `Idempotency-Key`, change
  dates/guests, confirm, cancel, no-show, notes). Permissions `reservation.read/create/update/cancel`.
- Dev seed S004: six demo bookings from today (R00001–R00006), leaving Room 102 as the last Deluxe room tonight.
- Staff app: Reservations (list with search and Booked / Enquiries / All), New booking wizard (dates → room → guest →
  rate → confirm, with quick-add of a walk-in guest), booking page (rooms, price night by night, guests, notes, history,
  confirm, change dates or guests, cancel and no-show with a reason). Guest profiles list their stays with references.
- Tests: backend `ReservationTests` (last room raced 50 times, last dorm bed with 5 parallel requests, database guard,
  idempotency, transitions, cancel frees the room, blocks vs bookings) and `ReservationUnitTests`; frontend form tests;
  browser steps S05-01 … S05-07 (including the last room booked from two browsers); `check-api.mjs` Sprint 05 block;
  `database/queries/check-sprint-05.sql`.

### Changed
- Creating a block on a booked room is refused with 409 `BLOCK_OVERLAPS_RESERVATION` naming the booking.
- `GET /guests/{id}/stays` returns real reservations (with `reference`); merging guests moves their reservations.
- Constraint errors map to their own codes (`ROOM_UNAVAILABLE`, `BLOCK_OVERLAPS_RESERVATION`, `INVALID_STATUS_TRANSITION`).
- Every date field in the staff app shows "15 Oct 2026" with a calendar button and accepts typed dates (15 Oct 2026,
  15-10-2026, 2026-10-15); a date it can't read says "Enter a date like 15 Oct 2026." under the field
  (`core/dates.ts`, from owner review).
- Field labels on the first row of dialogs and wizard steps are no longer cut off; the change-booking dialog lines up
  rooms under the dates; finished wizard steps show a tick; Back sits next to the step's main button.

### Documentation
- `docs/architecture/system-design.md`: how the code base works for new joiners, sprint by sprint with diagrams, and
  the system design of passwords, sign-in, permissions, tenants, audit, encryption, no double booking, idempotency,
  money, time and errors. Closing a sprint now updates it (PROCESS.md step 2).
- Handbooks 1.5 with the reservation tables and booking-flow diagrams.

## [sprint-04] — 2026-10-14 — Rate plans and packages (accepted)

### Added
- Migration V006: `rate_plans`, `rate_plan_prices`, `packages`, `package_components`, `reservation_sources` (14 built-in
  booking channels). One default plan per property; a deferred trigger refuses a package whose split doesn't add up.
- Quote engine (`Resort.Domain/Rates/QuoteEngine.cs`): nightly breakdown, extra adult/child above standard occupancy,
  per-bed dormitory prices, minimum stay on the arrival night; `MoneySplit` with the largest-remainder rounding rule.
- Rate APIs under `/api/v1/properties/{id}`: `rate-plans`, `rate-plans/{id}/prices` (read and bulk edit by date range
  and days of the week), `rates/quote` (by room or type, id or code), `packages`, `reservation-sources`. Every price
  change is audited with old and new values.
- Dev seed S003: BAR with Friday/Saturday uplift for a year; Couple getaway and Room + breakfast packages.
- Staff app: Rates (grid, bulk edit, quote) and Packages (list, editor with live revenue split).
- Tests: 29 backend (11 integration, 18 unit), 11 frontend, 6 browser steps (S04-01…S04-06); `check-api.mjs` Sprint 04
  block; `database/queries/check-sprint-04.sql` (9 queries).
- `docs/RUN_AND_TEST.md`: one handbook with every command to run the app and test it, sprint by sprint (backend
  filter, browser test file, API and database checks) and all sprints together.

### Changed
- `BusinessRuleAppException` can carry details (the quote lists the nights that have no price).
- Clear reasons for unique-constraint races on rate tables instead of the rule name.
- `showFieldErrors` also reaches fields inside lists (`Components[0].Description`).

### Fixed
- Rate grid: prices sat one column right of their dates because the table's style name clashed with a layout style.
  Browser step S04-01 now checks each price sits under its date.

## [sprint-03] — 2026-10-11 — Guests (accepted)

### Added
- Migration V005: `guests`, `guest_addresses`, `guest_documents`, `guest_preferences`, `guest_consents`, a data
  classification on every column, `pg_trgm` search indexes and database guards (guests never deleted, anonymised rows
  empty, Aadhaar never stored in full, consent history append-only).
- Field-level encryption (AES-256-GCM, HKDF-derived keys, key id per row, row-bound associated data) for guest ID
  numbers, document details and scans; key rotation steps in `docs/SECURITY.md`. The API refuses to start without a
  valid `Encryption__Key`.
- File storage abstraction (`IFileStore`) with a local-disk implementation (`App_Data/files`, gitignored).
- Guest APIs under `/api/v1/guests`: search by any part of a phone number or by name/email/company, duplicate warning
  (`409 POSSIBLE_DUPLICATE`), edit with versions, consent history, merge, anonymise, retention dry run, ID documents
  (multipart upload, masked list, audited opening of number and scan).
- Phone numbers normalised to E.164 (Indian numbers can be typed the local way).
- Audit trail: personal guest fields are named but stored as `***`; `SENSITIVE_READ` rows when a document is opened;
  labels for room, block and guest entries in the Audit trail screen.
- Dev seed S002: 20 invented guests (14 Indian, 6 international).
- Staff app: Guests list and search, add/edit guest with duplicate warning, guest profile with consent, stays and
  ID documents, add-document, merge and anonymise dialogs.
- Tests: 44 backend (13 integration, 31 unit), 6 frontend, 13 browser steps (S03-01…S03-13);
  `check-api.mjs` Sprint 03 block (12 checks); `database/queries/check-sprint-03.sql` (14 queries).
- `E2E_SCREENSHOTS=<folder>` makes the browser tests save the review screenshots.

### Changed
- CI job *Browser tests — Sprint 01–02 screens* renamed *Browser tests — sprint review scripts*.
- `ConflictAppException` can carry details in the error body (used for the duplicate list).
- Handbooks: `docs/handbooks/Resort-OS-Product-Overview.docx` (for clients) and
  `Resort-OS-Software-Documentation.docx` (architecture, UI/API/database design per sprint, code, tests), generated
  from the repository by `tools/handbooks` and rebuilt at every sprint close. Diagrams in `tools/handbooks/diagrams`.
- `sprints.yml`: a plain-language `client:` line for every sprint and a `handbook:` block for accepted sprints.
- *Release tags* workflow: tags `sprint-NN` on `main` for every sprint marked done.
- PROCESS.md: *Closing a sprint* checklist.

### Fixed
- Add ID document, Aadhaar: a number that isn't 12 digits (or the last 4) was refused with only *Some fields need
  fixing*, because the field's own message never showed. The dialog now checks the number as it is typed and says why
  under the field (e.g. "An Aadhaar number has 12 digits; this has 16"); other documents are checked for 4–30 letters
  or digits. Any message the API sends for the number also shows under the field. Tests: validator unit test (backend),
  2 frontend tests, browser step S03-06 extended.
- Choose your own password: reusing the temporary password as the new one was refused with only *Some fields need
  fixing*. The page now says so under New password before sending, and every API field message on this page and in
  Add ID document shows under its field (`showFieldErrors` in `core/http.ts`). Browser step S01-06 extended.
- Errors say why: a validation error's `message` is now the reasons themselves (up to 3, e.g. "New password must
  differ from the current one."), never "Some fields need fixing". Database rule errors name the rule, and an
  unreadable request says what couldn't be read.

## [sprint-02] — 2026-10-10 — Accommodation (accepted)

### Added
- Migration V004: accommodation types, rooms, dormitory beds, amenities (22 built in), unit images, unit blocks.
  `btree_gist` `EXCLUDE` constraints and triggers stop overlapping blocks and beds in per-room types at database level.
- Room-based (UNIT) and bed-based (BED) inventory with a shared capacity calculation.
- APIs under `/api/v1/properties/{propertyId}`: amenities, accommodation types, rooms, beds, room status board, blocks and
  release. All permission-checked, validated and audited; block and release reasons go into the audit trail.
- Development seed runner (`database/seed/dev`, Development only, idempotent) with demo rooms: Deluxe 101–102,
  Standard 103–106, a 10-bed dormitory.
- Staff app: Rooms, room/dorm editor with bulk bed add, block dialog, release with reason, Room status board, Room types
  editor with amenities; *Set up rooms* on the Today checklist.
- 21 backend tests (incl. 10 concurrent block requests → exactly one wins) and 8 frontend tests.
- Docs: Sprint 02 API spec in `docs/API.md`, `docs/database/schema-sprint-02.md`, *What each sprint needs* in
  `docs/GETTING_STARTED.md`, Sprint 02 review script in `docs/sprints/REVIEWING.md`, screenshots.

### Added — review tooling
- Browser tests (`frontend/e2e`, Playwright): the Sprint 01 and 02 review scripts automated, 26 steps; watch them
  with `npm run e2e:headed`. They use their own API and a throwaway database.
- `scripts/check-api.mjs` (34 API checks, PASS/FAIL) and `database/queries/check-sprint-01-02.sql` (16 read-only checks).
- `docs/testing/VERIFYING.md`: UI → API → database checks per feature, and where `.env` is.
- CI: *Browser tests* and *Backend — tests on Windows* jobs.

### Fixed
- Time zone "IST" was accepted on Windows (ICU alias) but refused on Linux. Only IANA `Area/City` names or `UTC`
  are accepted everywhere now. Found by the owner's test run on Windows.

### Changed
- An audit reason sent in the request body (e.g. a block's reason) is recorded in preference to the `X-Change-Reason` header.

## [sprint-01] — 2026-10-10 — Foundation (accepted)

### Added
- Modular-monolith solution (.NET 10): Api, Application, Domain, Infrastructure, Contracts, Tests.
- SQL migrations V001–V003 with checksum-guarded migrator; 13 tables; 117 permissions; 17 system roles.
- Sign-in with lockout, rotating refresh cookie with reuse detection, forced password change, security-stamp sign-out.
- Organisation, properties and property settings; staff accounts with property-scoped roles and escalation guard.
- Audit trail with old/new values, reason, device, IP, correlation and request ids; append-only at database level.
- `/api/v1` routes; standard error body `{code, message, details, correlationId}`; security headers; rate limiting; health checks; OpenAPI.
- Angular 20 staff app: login, dashboard setup checklist, properties, staff accounts, roles matrix, audit trail, change password.
- 30 backend tests (integration on real PostgreSQL) and 6 frontend tests; GitHub Actions CI.
- Owner and developer guides: `docs/GETTING_STARTED.md` (Windows/macOS step by step, VS Code), `docs/sprints/REVIEWING.md`,
  `docs/COSTS.md`; every sprint guide now opens with *Where this sprint stands* (now / done / files / results / outcome).
- Shared VS Code workspace (`.vscode/`: recommended extensions, F5 debugging, tasks); `.gitattributes` (LF everywhere).
- `Onboarding` workflow: runs the setup script on a clean Linux runner through to a signed-in API, and the PowerShell
  script on Windows.

### Fixed
- `scripts/setup-dev.ps1` failed to generate keys (PowerShell unrolled the byte array); now requires PowerShell 7 and
  stops on any failed step. Added `scripts/db-reset.ps1`.
