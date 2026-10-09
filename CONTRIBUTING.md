# Contributing

New to the team? Start with [docs/ONBOARDING.md](docs/ONBOARDING.md). Setup: [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md) (reference: [docs/SETUP.md](docs/SETUP.md)). Sprint cadence and ceremonies: [docs/sprints/PROCESS.md](docs/sprints/PROCESS.md).

## Branches

| Branch | Purpose | Who merges |
|---|---|---|
| `main` | Accepted sprints only. Tagged `sprint-NN`. Protected | tech lead, after sprint review |
| `develop` | Integration branch. All PRs target it. Protected | any reviewer after green CI |
| `feature/S<NN>-<issue>-<short-name>` | One issue | you, via PR |
| `fix/S<NN>-<issue>-<short-name>` | Bug in the current sprint | you, via PR |
| `hotfix/<issue>-<short-name>` | Production incident, from `main` | tech lead |
| `docs/<short-name>` | Documentation only | you, via PR |

```bash
git switch develop && git pull
git switch -c feature/S02-41-room-editor
```

## Commits

[Conventional Commits](https://www.conventionalcommits.org) with the module as scope:

```
feat(rooms): add dorm bed editor
fix(auth): keep refresh cookie path under /api/v1
test(reservations): two simultaneous bookings for the last room
docs(api): document /rooms endpoints
chore(deps): bump Npgsql to 10.0.1
db(rooms): V004 accommodation tables
```

Small commits are fine. The PR is squash-merged, so its **title** must follow the same format and end with the issue: `feat(rooms): dorm bed editor (#41)`.

## Pull requests

1. Open against `develop`; fill in the template (what, why, how tested, screenshots for UI, migration notes).
2. Link the issue with `Closes #41`.
3. CI must be green: build (warnings are errors), backend + frontend tests, migration check, secret scan, dependency scan.
4. One approving review (two for anything touching auth, permissions, money, tax or migrations; CODEOWNERS requests them).
5. The author merges (squash) after approval. Delete the branch.

Keep PRs under ~400 changed lines where possible; split schema, API and UI if needed.

## Code rules

**Backend**
- Endpoints are thin: bind, validate (`.Validate<T>()`), authorise (`.RequirePermission(...)`), call a service. No business logic in endpoints (brief §84).
- One service per use-case area in `Resort.Application/<Module>`; resource-level checks via `CallerAccess`.
- Every state change: permission + validator + audit (mark the entity `IAudited`) + `version` for updates + `Idempotency-Key` for money/bookings.
- Throw the typed `AppException`s (`NotFoundAppException`, `BusinessRuleAppException`, …); never return raw exception text.
- Money is `decimal`; time is `DateTimeOffset` UTC; business dates via `Property.LocalDate`.
- No `DELETE` of financial records: reverse, refund, credit note (brief §85).
- `async` all the way, pass `CancellationToken`.

**Database**
- New schema = new `database/migrations/V###__name.sql`. **Never edit a merged migration.** See [docs/DATABASE.md](docs/DATABASE.md).

**Frontend**
- Standalone components, signals, reactive forms; feature code under `features/<module>`.
- Use `*appCan="'permission.code'"` to hide what users can't use; the API still decides.
- Show loading, empty and error states; check at 360 px width.
- Times shown in the property's timezone (`inZone`).

**Formatting**: `.editorconfig` (4 spaces C#, 2 spaces TS/HTML/SCSS, LF). Run `dotnet format` before pushing.

## Secrets — never commit

- No keys, passwords, tokens, connection strings, `.env`, certificates or production data in Git, issues, PRs or screenshots.
- Use `.env` (gitignored). Add new variable **names** to `.env.example` with blank values.
- CI runs gitleaks. If a secret is committed: tell the owner immediately, rotate it, then remove it from history.

## Tests

Every PR adds tests for the behaviour it changes. Required tests per sprint are listed in the sprint guide. See [docs/TESTING.md](docs/TESTING.md).

## Documentation

Changed an endpoint → update [docs/API.md](docs/API.md) and `docs/api/openapi-v1.json`. Changed tables → update
[docs/DATABASE.md](docs/DATABASE.md) / the sprint schema file. Changed the sprint plan → edit `docs/sprints/sprints.yml`
and run `python3 scripts/github/sync-sprints.py docs`.
