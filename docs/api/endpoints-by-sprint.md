# Endpoints by sprint

_Generated from docs/sprints/sprints.yml — do not edit by hand._

Full specifications (permission, request/response, errors, idempotency, audit) are in [../API.md](../API.md) once built.

| Endpoint | Sprint | Status |
|---|---|---|
| `POST /api/v1/auth/login` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `POST /api/v1/auth/refresh` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `POST /api/v1/auth/logout` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `GET /api/v1/auth/me` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `POST /api/v1/auth/change-password` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `GET|PUT /api/v1/organization` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `GET|POST /api/v1/properties` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `GET|PUT /api/v1/properties/{propertyId}` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `GET /api/v1/properties/{propertyId}/settings` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `PUT /api/v1/properties/{propertyId}/settings/{key}` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `GET|POST /api/v1/users` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `GET|PUT /api/v1/users/{id}` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `PUT /api/v1/users/{id}/roles` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `POST /api/v1/users/{id}/deactivate | activate | reset-password` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `GET /api/v1/roles` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `GET /api/v1/permissions` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `GET /api/v1/audit-logs` | [Sprint 01](../sprints/sprint-01-foundation.md) | 🟡 In review |
| `GET|POST /api/v1/properties/{propertyId}/accommodation-types` | [Sprint 02](../sprints/sprint-02-accommodation.md) | ⚪ Planned |
| `GET|PUT /api/v1/properties/{propertyId}/accommodation-types/{id}` | [Sprint 02](../sprints/sprint-02-accommodation.md) | ⚪ Planned |
| `GET|POST /api/v1/properties/{propertyId}/rooms` | [Sprint 02](../sprints/sprint-02-accommodation.md) | ⚪ Planned |
| `GET|PUT /api/v1/properties/{propertyId}/rooms/{id}` | [Sprint 02](../sprints/sprint-02-accommodation.md) | ⚪ Planned |
| `GET|POST /api/v1/properties/{propertyId}/rooms/{id}/beds` | [Sprint 02](../sprints/sprint-02-accommodation.md) | ⚪ Planned |
| `PUT /api/v1/properties/{propertyId}/beds/{id}` | [Sprint 02](../sprints/sprint-02-accommodation.md) | ⚪ Planned |
| `GET|POST /api/v1/properties/{propertyId}/blocks` | [Sprint 02](../sprints/sprint-02-accommodation.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/blocks/{id}/release` | [Sprint 02](../sprints/sprint-02-accommodation.md) | ⚪ Planned |
| `GET /api/v1/properties/{propertyId}/amenities` | [Sprint 02](../sprints/sprint-02-accommodation.md) | ⚪ Planned |
| `GET|POST /api/v1/guests` | [Sprint 03](../sprints/sprint-03-guests.md) | ⚪ Planned |
| `GET|PUT /api/v1/guests/{id}` | [Sprint 03](../sprints/sprint-03-guests.md) | ⚪ Planned |
| `GET /api/v1/guests/{id}/stays` | [Sprint 03](../sprints/sprint-03-guests.md) | ⚪ Planned |
| `POST /api/v1/guests/{id}/documents` | [Sprint 03](../sprints/sprint-03-guests.md) | ⚪ Planned |
| `GET /api/v1/guests/{id}/documents/{docId}  (guest_document.read only)` | [Sprint 03](../sprints/sprint-03-guests.md) | ⚪ Planned |
| `POST /api/v1/guests/{id}/anonymise` | [Sprint 03](../sprints/sprint-03-guests.md) | ⚪ Planned |
| `POST /api/v1/guests/merge` | [Sprint 03](../sprints/sprint-03-guests.md) | ⚪ Planned |
| `GET|POST /api/v1/properties/{propertyId}/rate-plans` | [Sprint 04](../sprints/sprint-04-rate-plans-and-packages.md) | ⚪ Planned |
| `PUT /api/v1/properties/{propertyId}/rate-plans/{id}` | [Sprint 04](../sprints/sprint-04-rate-plans-and-packages.md) | ⚪ Planned |
| `PUT /api/v1/properties/{propertyId}/rate-plans/{id}/prices  (bulk date range)` | [Sprint 04](../sprints/sprint-04-rate-plans-and-packages.md) | ⚪ Planned |
| `GET /api/v1/properties/{propertyId}/rates/quote?type&from&to&adults&children` | [Sprint 04](../sprints/sprint-04-rate-plans-and-packages.md) | ⚪ Planned |
| `GET|POST /api/v1/properties/{propertyId}/packages` | [Sprint 04](../sprints/sprint-04-rate-plans-and-packages.md) | ⚪ Planned |
| `GET /api/v1/properties/{propertyId}/availability?from&to&adults` | [Sprint 05](../sprints/sprint-05-availability-engine-and-reservations.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/reservations  (Idempotency-Key header required)` | [Sprint 05](../sprints/sprint-05-availability-engine-and-reservations.md) | ⚪ Planned |
| `GET /api/v1/properties/{propertyId}/reservations` | [Sprint 05](../sprints/sprint-05-availability-engine-and-reservations.md) | ⚪ Planned |
| `GET /api/v1/properties/{propertyId}/reservations/{id}` | [Sprint 05](../sprints/sprint-05-availability-engine-and-reservations.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/reservations/{id}/confirm | cancel | no-show` | [Sprint 05](../sprints/sprint-05-availability-engine-and-reservations.md) | ⚪ Planned |
| `PUT /api/v1/properties/{propertyId}/reservations/{id}  (dates/guests; re-checks availability)` | [Sprint 05](../sprints/sprint-05-availability-engine-and-reservations.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/reservations/{id}/notes` | [Sprint 05](../sprints/sprint-05-availability-engine-and-reservations.md) | ⚪ Planned |
| `GET /api/v1/properties/{propertyId}/calendar?from&to` | [Sprint 06](../sprints/sprint-06-reservation-calendar.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/reservations/{id}/reassign  (reason required)` | [Sprint 06](../sprints/sprint-06-reservation-calendar.md) | ⚪ Planned |
| `GET /api/v1/properties/{propertyId}/front-desk/arrivals?date` | [Sprint 07](../sprints/sprint-07-front-desk.md) | ⚪ Planned |
| `GET /api/v1/properties/{propertyId}/front-desk/departures?date` | [Sprint 07](../sprints/sprint-07-front-desk.md) | ⚪ Planned |
| `GET /api/v1/properties/{propertyId}/front-desk/in-house` | [Sprint 07](../sprints/sprint-07-front-desk.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/reservations/{id}/assign-room` | [Sprint 07](../sprints/sprint-07-front-desk.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/reservations/{id}/check-in` | [Sprint 07](../sprints/sprint-07-front-desk.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/reservations/{id}/check-out` | [Sprint 07](../sprints/sprint-07-front-desk.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/walk-ins` | [Sprint 07](../sprints/sprint-07-front-desk.md) | ⚪ Planned |
| `GET /api/v1/properties/{propertyId}/folios/{id}` | [Sprint 08](../sprints/sprint-08-folio.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/folios/{id}/charges  (Idempotency-Key)` | [Sprint 08](../sprints/sprint-08-folio.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/folios/{id}/charges/{itemId}/reverse  (reason required)` | [Sprint 08](../sprints/sprint-08-folio.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/folios/{id}/split` | [Sprint 08](../sprints/sprint-08-folio.md) | ⚪ Planned |
| `GET|POST /api/v1/properties/{propertyId}/gst-registrations` | [Sprint 09](../sprints/sprint-09-gst-configuration-and-tax-engine.md) | ⚪ Planned |
| `GET|POST /api/v1/tax/categories` | [Sprint 09](../sprints/sprint-09-gst-configuration-and-tax-engine.md) | ⚪ Planned |
| `GET|POST /api/v1/tax/rules  (effective_from/effective_to)` | [Sprint 09](../sprints/sprint-09-gst-configuration-and-tax-engine.md) | ⚪ Planned |
| `POST /api/v1/tax/calculate  (preview)` | [Sprint 09](../sprints/sprint-09-gst-configuration-and-tax-engine.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/payments  (Idempotency-Key)` | [Sprint 10](../sprints/sprint-10-payments-refunds-cash-drawer.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/payments/{id}/allocate` | [Sprint 10](../sprints/sprint-10-payments-refunds-cash-drawer.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/payments/{id}/refund  (reason required)` | [Sprint 10](../sprints/sprint-10-payments-refunds-cash-drawer.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/payments/{id}/void  (reason required)` | [Sprint 10](../sprints/sprint-10-payments-refunds-cash-drawer.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/cash-drawer/open | close` | [Sprint 10](../sprints/sprint-10-payments-refunds-cash-drawer.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/cash-drawer/sessions/{id}/approve-variance` | [Sprint 10](../sprints/sprint-10-payments-refunds-cash-drawer.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/payments/razorpay/orders` | [Sprint 11](../sprints/sprint-11-razorpay.md) | ⚪ Planned |
| `POST /api/v1/webhooks/razorpay  (no auth; HMAC signature)` | [Sprint 11](../sprints/sprint-11-razorpay.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/folios/{id}/invoice` | [Sprint 12](../sprints/sprint-12-gst-invoices-credit-debit-notes-receipts.md) | ⚪ Planned |
| `GET /api/v1/properties/{propertyId}/invoices` | [Sprint 12](../sprints/sprint-12-gst-invoices-credit-debit-notes-receipts.md) | ⚪ Planned |
| `GET /api/v1/properties/{propertyId}/invoices/{id}  (+ /pdf)` | [Sprint 12](../sprints/sprint-12-gst-invoices-credit-debit-notes-receipts.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/invoices/{id}/credit-notes  (reason required)` | [Sprint 12](../sprints/sprint-12-gst-invoices-credit-debit-notes-receipts.md) | ⚪ Planned |
| `GET /api/v1/properties/{propertyId}/receipts/{id}/pdf` | [Sprint 12](../sprints/sprint-12-gst-invoices-credit-debit-notes-receipts.md) | ⚪ Planned |
| `GET /api/v1/properties/{propertyId}/housekeeping/board` | [Sprint 13](../sprints/sprint-13-housekeeping-and-maintenance-mobile.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/housekeeping/tasks/{id}/assign | start | finish | inspect` | [Sprint 13](../sprints/sprint-13-housekeeping-and-maintenance-mobile.md) | ⚪ Planned |
| `GET|POST /api/v1/properties/{propertyId}/maintenance/tickets` | [Sprint 13](../sprints/sprint-13-housekeeping-and-maintenance-mobile.md) | ⚪ Planned |
| `POST /api/v1/properties/{propertyId}/maintenance/tickets/{id}/assign | progress | resolve | close` | [Sprint 13](../sprints/sprint-13-housekeeping-and-maintenance-mobile.md) | ⚪ Planned |
| `/api/v1/properties/{propertyId}/menu/*` | [Sprint 14](../sprints/sprint-14-menu-restaurant-pos-and-kot.md) | ⚪ Planned |
| `/api/v1/properties/{propertyId}/orders/*` | [Sprint 14](../sprints/sprint-14-menu-restaurant-pos-and-kot.md) | ⚪ Planned |
| `/api/v1/properties/{propertyId}/kitchen/*` | [Sprint 14](../sprints/sprint-14-menu-restaurant-pos-and-kot.md) | ⚪ Planned |
| `/api/v1/webhooks/whatsapp` | [Sprint 24](../sprints/sprint-24-notification-engine-whatsapp-email.md) | ⚪ Planned |
| `/api/v1/webhooks/channel-manager` | [Sprint 26](../sprints/sprint-26-ota-channel-manager.md) | ⚪ Planned |
| `/api/v1/public/* — separate` | [Sprint 27](../sprints/sprint-27-direct-booking-website.md) | ⚪ Planned |
| `rate-limited` | [Sprint 27](../sprints/sprint-27-direct-booking-website.md) | ⚪ Planned |
| `no admin data` | [Sprint 27](../sprints/sprint-27-direct-booking-website.md) | ⚪ Planned |
