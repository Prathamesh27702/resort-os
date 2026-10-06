# Testing

## Run

```bash
scripts/test.sh                                         # everything
dotnet test ResortOS.sln                                # backend only (needs RESORT_TEST_DB)
dotnet test --filter "FullyQualifiedName~AuthTests"     # one class
dotnet test --filter "FullyQualifiedName~Unit"          # no database needed
cd frontend && npm run test:ci                          # frontend (headless Chrome)
cd frontend && npm test                                 # frontend, watch mode
```

`RESORT_TEST_DB` is a login on your local PostgreSQL that can create databases. `setup-dev` puts it in `.env`.

## Test layers

| Layer | Where | Tools | Rules |
|---|---|---|---|
| Unit | `backend/Resort.Tests/Unit` | xUnit | Pure domain logic: pricing, tax, timesheet maths, state machines. No DB, no HTTP |
| Integration / API | `backend/Resort.Tests/Integration` | xUnit + `WebApplicationFactory` + **real PostgreSQL** | Boot the real API against a fresh database per test class (`ResortApiFactory`). Call HTTP endpoints like a client. No EF in-memory provider, no DB mocks |
| Database | same | Npgsql | Constraints, triggers, migrations (e.g. append-only audit, migration checksum, exclusion constraints) |
| Authorization | same | | For every new endpoint: allowed role → 2xx, role without permission → 403, other tenant → 404 |
| Concurrency | same | `Task.WhenAll` + `Barrier` | Fire N identical requests at once; assert exactly the allowed number succeed. Run in a loop (≥ 20×) to catch flakiness |
| Webhooks | same + `tests/fakes` | Fake Razorpay / channel manager / WhatsApp servers | Duplicate delivery, bad signature, out-of-order events |
| Frontend unit | `frontend/src/**/*.spec.ts` | Jasmine + Karma | Services, guards, pipes, form validators; components with logic |
| E2E | `e2e/` (Sprint 13+) | Playwright | Critical workflows at desktop and 360 px mobile widths |

## What each sprint must test

Listed in every sprint guide under **How to test**. The brief's required categories (§67, §72) map to sprints:

| Required test (brief) | Sprint |
|---|---|
| Authorization, auth failures | 1 *(built)* |
| Two users booking the same room · same booking retry | 5 |
| Two users assigning same room · simultaneous reassignment | 6 |
| Simultaneous check-in | 7 |
| GST calculation | 9 |
| Two refunds / payment allocation | 10 |
| Payment webhook retry / duplicate | 11 |
| Invoice numbering and totals | 12 |
| Two restaurant users modifying an order | 14 |
| Two inventory adjustments | 15 |
| Timesheet calculation | 18 |
| Leave calculation | 19 |
| OTA callback retry / duplicate | 26 |
| E2E: booking, payment, check-in, restaurant charge, checkout, invoice, leave, attendance | 27–29 |
| Acceptance tests §73 (guest), §74 (employee), §75 (OTA) | 19 (§74), 26 (§75), 29 (§73 on staging) |

## Writing an integration test

```csharp
public class RoomTests(ResortApiFactory app) : IClassFixture<ResortApiFactory>
{
    [Fact]
    public async Task Receptionist_cannot_create_rooms()
    {
        var (email, pw) = await app.CreateUserAsync("RECEPTIONIST", await app.MainPropertyIdAsync());
        var desk = await app.SignInAsync(email, pw);
        var res = await desk.PostAsync($"/api/v1/properties/{await app.MainPropertyIdAsync()}/rooms", new { /* … */ });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
```

Helpers in `Support/ResortApiFactory.cs`: `SignInOwnerAsync`, `SignInAsync`, `CreateUserAsync(role, propertyId)`,
`CreateSecondTenantAsync`, `MainPropertyIdAsync`, `WithDbAsync`.

## Debugging a failing test

- Every API error in a test response has a `correlationId`; the test output includes the server log line with it.
- Run one test: `dotnet test --filter "FullyQualifiedName~Reusing_a_rotated_refresh_token"`.
- Keep the test database for inspection: set a breakpoint before `DisposeAsync`, then connect to `resort_test_<suffix>`.
- Concurrency failures: log `pg_locks` / `pg_stat_activity` from inside the test while requests are in flight.

## CI

`.github/workflows/ci.yml` runs on every PR and push to `develop`/`main`: build with warnings as errors, backend
tests against a PostgreSQL 16 service container, frontend tests, production build, migration-immutability check,
secret scan and vulnerable-package scan. A PR can't merge unless all jobs are green.
