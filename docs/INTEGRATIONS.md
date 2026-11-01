# Integrations

Resort OS owns operational data; specialist work goes to external systems (brief §3). Every integration follows the
same pattern so they behave the same under failure.

| System | Doc | Interface | Sprint |
|---|---|---|---|
| Razorpay (payments) | [RAZORPAY.md](RAZORPAY.md) | `IPaymentGateway` | 11 |
| Zoho Books (accounting) + Tally export | [ZOHO_BOOKS.md](ZOHO_BOOKS.md) | `IAccountingProvider`, `IAccountingExport` | 25 |
| Channel manager → Booking.com, MakeMyTrip, Goibibo, Airbnb, Agoda, Expedia | [OTA.md](OTA.md) | `IChannelManager` | 26 |
| WhatsApp Business Platform + email | [WHATSAPP.md](WHATSAPP.md) | `IMessageSender` | 24 |
| GST / e-invoice | [GST.md](GST.md) | via accounting provider | 9, 12, 25 |
| Payroll provider (not chosen yet, Q3) | [PAYROLL.md](PAYROLL.md) | CSV file `GENERIC_CSV_V1` (`PayrollCsv`); a provider adapter once chosen | 19 |
| File storage | — | `IFileStore` | 3 |

## The pattern

1. **Interface in Application, implementation in Infrastructure.** Business code never sees a vendor SDK type.
2. **Outbound calls go through the outbox.** The business transaction writes an `outbox_messages` row; a background
   worker sends it, records the external id, and retries with exponential backoff. A crash between "saved" and "sent"
   loses nothing.
3. **Inbound webhooks:** verify signature over the raw body → insert into `webhook_events` with a **unique
   (provider, event_id)** → process in a transaction → mark processed. A duplicate delivery hits the unique key and
   is acknowledged without reprocessing. The webhook is authoritative; browser callbacks are not.
4. **Idempotent external writes:** send our own reference (invoice number, reservation id) as the provider's
   idempotency key or reference field; store `external_id`, `sync_status`, `last_sync_at`, `sync_error`. Before
   retrying a create, look the record up by our reference.
5. **Named `HttpClient` per provider** with timeouts, retry policy and an allow-listed base URL (SSRF defence).
6. **Fake server per provider** in tests, plus a manual test against the provider's sandbox before each release.
7. **Keys only from configuration** (`.env` locally, secret store on servers). Test-mode keys only outside production.

## Who provides what

The owner creates the vendor accounts (merchant KYC, Meta Business verification, Zoho organisation, channel manager
contract) and hands developers **sandbox/test credentials** privately when the sprint starts. Production credentials
are entered only into the production secret store.
