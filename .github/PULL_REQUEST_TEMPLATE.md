## What

<!-- One or two sentences. -->

Closes #

**Sprint:** S__ · **Module:**

## Why

## How it was tested

- [ ] Unit tests
- [ ] Integration tests (real PostgreSQL)
- [ ] Authorization: allowed role 2xx, other role 403, other tenant 404
- [ ] Concurrency / idempotency (if money, bookings or inventory)
- [ ] Manually in the browser at desktop and 360 px

## Checklist

- [ ] Business logic is in `Resort.Application`, not endpoints or components
- [ ] Every state change has a permission check, validator and audit entry
- [ ] New migration is a new `V###__*.sql` file (no edits to merged ones), backwards-compatible
- [ ] New permissions added in a migration and to `docs/security/permission-matrix.md`
- [ ] `docs/API.md` / `docs/DATABASE.md` updated
- [ ] No secrets, keys, real guest data or production data in code, tests or screenshots
- [ ] UI has loading, empty and error states

## Screenshots (UI changes)

## Migration / deployment notes
