# Risks, open questions and decisions still needed (brief §91 Z)

Each item becomes a GitHub issue labelled `needs:owner-decision` and is due before the sprint that needs it.

## Decisions the owner must make

| # | Question | Needed by | Default if not decided |
|---|---|---|---|
| Q1 | Cloud provider and region for staging/production (Azure Central India, AWS Mumbai, or a VPS) | Sprint 29 (budget now) | Azure Central India, App Service B1 + PostgreSQL Flexible Server B1ms |
| Q2 | Which **channel manager** (e.g. a provider supporting MakeMyTrip/Goibibo + Booking.com + Airbnb) | Sprint 26 | — must decide |
| Q3 | Which **payroll provider** and its import format | Sprint 19 | CSV in a documented generic format |
| Q4 | Transactional **email provider** | Sprint 24 | Amazon SES or Postmark |
| Q5 | Is the organisation required to issue **e-invoices** (turnover threshold)? Through Zoho or a GSP? | Sprint 25 | Store e-invoice fields; send via Zoho if required |
| Q6 | Guest **ID collection** rules for this location (Form-C for foreign nationals, local police requirements) | Sprint 3 | Collect ID type + number + scan only for foreign nationals and where required |
| Q7 | **Retention** periods: ID scans, guest profiles, staff documents | Sprint 3 | 365 days after checkout for ID scans; anonymise profiles on request |
| Q8 | Cancellation / no-show / refund **policy** wording and rules | Sprint 5 | Per rate plan: free until N days, then first night |
| Q9 | Check-out with unpaid balance allowed? (`frontdesk.allow_checkout_with_balance`) | Sprint 7 | Not allowed |
| Q10 | **Tax rules** to seed — accountant/CA to verify current GST rates for accommodation, restaurant, transport, activities | Sprint 9 | No seed; accountant enters them |
| Q11 | Invoice **series format** (prefix, per property, per FY) | Sprint 12 | `{PROPERTYCODE}/{FY}/{00001}` |
| Q12 | Geofence radius and whether staff may clock in off-site with manager approval | Sprint 18 | 150 m; override with reason |
| Q13 | Leave types, accrual rules, holiday calendar (state-specific) | Sprint 19 | Annual/sick/casual/unpaid with monthly accrual |
| Q14 | RPO / RTO targets and backup budget | Sprint 29 | RPO 15 min, RTO 4 h |
| Q15 | Domain name(s) for staff app, booking site and API | Sprint 27 | — |
| Q16 | Who is the **tech lead** and how many developers; affects sprint sizes | Sprint 2 | 2 developers, 2-week sprints |

## Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Plan is large (30 sprints ≈ 14 months with 2 developers) | Slow time to value | MVP cut-line after Sprint 12 (book, pay, invoice) — the resort can go live on Phases 1–3 + Sprint 13 and add the rest |
| Channel manager API access/contract delays | OTA sync late; double-booking risk while OTAs are managed manually | Choose provider early (Q2); until then, block OTA-sold rooms manually |
| GST rule errors | Wrong invoices, compliance risk | Rules are data verified by the accountant; tests per rule; invoices store the rule used |
| Intermittent connectivity at the resort | Staff can't work | PWA read caches (Sprint 28), lightweight pages, Cloudflare edge |
| Key-person dependency (owner holds all integration keys) | Blocked sprints | Request sandbox keys at sprint planning; document in a password manager shared with the tech lead |
| 148 candidate tables | Over-engineering | Each sprint confirms tables at design review; merge detail tables where not justified |
| Refresh-token strict reuse detection | Sign-outs with many tabs | Monitor; add a grace window if reported (ADR-0005) |
| Single API instance | Downtime during deploys | Short maintenance window at night (property time) until a second instance is justified |

## Missing from the brief (to confirm)

- Room **rates for dorm beds** vs private rooms, and child/extra-bed pricing.
- **Deposits** and partial payment rules for direct bookings.
- **Multi-currency** display for international guests (charge in INR only?).
- Whether **staff meals / complimentary items** go through the POS.
- **Housekeeping checklist** contents per room type.
- **Language**: English only at launch, or Hindi/Marathi/Konkani for staff screens?

## Phase-by-phase estimate (brief §91 Y)

Assuming 2 developers, 2-week sprints. S/M/L sizes per sprint are in [docs/sprints/README.md](../sprints/README.md).

| Phase | Sprints | Calendar (2 devs) |
|---|---|---|
| 0 Architecture | 0 | 1 week |
| 1 Foundation, rooms | 1–2 | 4 weeks |
| 2 Guests → front desk | 3–7 | 10 weeks |
| 3 Money (folio → invoices) | 8–12 | 10 weeks |
| 4 Housekeeping, maintenance | 13 | 2 weeks |
| 5 Restaurant, inventory, purchasing | 14–16 | 6 weeks |
| 6 People | 17–19 | 6 weeks |
| 7 Activities, transport | 20–21 | 3 weeks |
| 8 Expenses, budgets, reports | 22–23 | 4 weeks |
| 9 Integrations, booking site | 24–27 | 8 weeks |
| 10 Hardening, go-live | 28–29 | 4 weeks |
| **Total** | 30 sprints | **≈ 14 months** (≈ 9–10 with 3–4 developers running independent sprints in parallel) |
