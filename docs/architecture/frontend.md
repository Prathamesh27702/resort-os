# Frontend

Angular 20, standalone components, signals, reactive forms, lazy-loaded routes, Angular Material + Tailwind
utilities (no preflight). Mobile-friendly first; installable PWA from Sprint 18.

## Folder structure (brief §79)

```
frontend/src/app/
  core/        auth.service, http interceptor + error mapping, guards, property context, models   (singletons)
  shared/      reusable directives/components/pipes (e.g. the *appCan permission directive)
  layout/      shell: side nav, top bar with property clock, user menu
  features/
    auth/          login, change password
    dashboard/     role-aware home
    settings/      properties (and later room types, rates, GST, integrations)
    users/         staff accounts, roles matrix
    audit/
    reservations/ guests/ rooms/ front-desk/ folios/ payments/ invoices/ housekeeping/ maintenance/
    restaurant/ inventory/ employees/ attendance/ timesheets/ leave/ roster/ activities/ transport/
    equipment/ expenses/ budgets/ reports/ integrations/          ← added by their sprints
```

Rules: components render and collect input; **business logic lives in the API** (brief §84). A feature talks to the
API through its own typed service. Never inject `HttpClient` into a template-facing component for anything beyond a simple read.

## H. Screen / route map (brief §64)

| Route | Screen | Permission | Sprint |
|---|---|---|---|
| `/login` | Sign in | — | 1 ✅ |
| `/dashboard` | Role-aware home (setup checklist today; owner/reception/housekeeping/restaurant/employee dashboards later) | signed in | 1 ✅ / 23 |
| `/account/password` | Change password (forced for temporary passwords) | signed in | 1 ✅ |
| `/settings/properties`, `/settings/properties/:id` | Properties | property.read | 1 ✅ |
| `/users`, `/users/new`, `/users/:id` | Staff accounts | user.read | 1 ✅ |
| `/roles` | Permission matrix | role.read | 1 ✅ |
| `/audit` | Audit trail | audit.read | 1 ✅ |
| `/rooms`, `/rooms/status` | Rooms & beds, room status board | accommodation.read | 2, 13 |
| `/settings/room-types`, `/settings/rates`, `/settings/packages`, `/settings/gst` | Configuration | *.manage | 2, 4, 9 |
| `/guests`, `/guests/:id` | Guests | guest.read | 3 |
| `/reservations`, `/reservations/new`, `/reservations/:id` | Reservations | reservation.read | 5 |
| `/reservations/calendar` | Calendar | reservation.read | 6 |
| `/front-desk/arrivals`, `/departures`, `/in-house` | Front desk | frontdesk.* | 7 |
| `/folios`, `/folios/:id` | Folios | folio.read | 8 |
| `/payments`, `/invoices` | Payments, invoices | payment.read, invoice.read | 10, 12 |
| `/housekeeping`, `/maintenance` | Mobile-first boards | housekeeping.read, maintenance.read | 13 |
| `/restaurant`, `/restaurant/orders`, `/restaurant/kitchen`, `/restaurant/menu` | POS, KOT, menu | pos_order.read, kot.read, menu.read | 14 |
| `/inventory`, `/inventory/purchases`, `/inventory/suppliers` | Stock, purchasing | inventory.read, purchase.read, supplier.read | 15, 16 |
| `/employees`, `/roster` | People | employee.read, roster.read | 17 |
| `/attendance`, `/timesheets`, `/leave` | Self-service + approvals | attendance.*, timesheet.*, leave.* | 18, 19 |
| `/activities`, `/equipment`, `/transport` | Guest experiences | activity.read, equipment.read, transport.read | 20, 21 |
| `/expenses`, `/budgets` | Finance control | expense.read, budget.read | 22 |
| `/reports` | Reports | report.operational.read / financial_report.read | 23 |
| `/integrations` | Integration settings & sync status | integration.manage | 24–26 |
| Public app: `/`, `/rooms`, `/book`, `/book/pay`, `/booking/:code` | Direct booking | public | 27 |

Guards only hide what a user can't use; the API checks every request again.

## UI conventions (brief §65)

| Element | Convention |
|---|---|
| Layout | `.page` > `.page-head` (h1 + one-line purpose + primary action on the right) > `.panel` |
| Buttons | One `mat-flat-button` (kokum) primary per screen; `mat-stroked-button` secondary; text buttons for cancel |
| Forms | Reactive forms, `appearance="outline"`, two-column grid collapsing to one at 640 px; server field errors mapped onto controls |
| Tables | `table.data`; secondary columns `.hide-sm` on phones; clickable rows keyboard-accessible |
| Notifications | `MatSnackBar`, 2.5 s, past-tense of the action ("Changes saved") |
| States | Every list has loading, empty (with the next action) and error states. Errors show the API `message` |
| Time | Always rendered in the **property's** timezone via `inZone()`; IST shown as "IST" |
| Motion | None beyond Material defaults; `prefers-reduced-motion` respected |
| Touch | Targets ≥ 44 px on mobile screens used by housekeeping, kitchen, drivers |
| Offline (Sprint 28) | Status chip ONLINE / OFFLINE / SYNCING / SYNC FAILED in the top bar; financial actions disabled offline |

Palette: tide `#0E3B43` (navigation), lime-wash `#F5F7F6` (background), kokum `#8A2449` (primary actions), palm
`#2E6B3C` (success), ink `#1B2A2E` (text). Type: IBM Plex Sans (self-hosted; has a Devanagari companion for future
Hindi/Marathi UI).
