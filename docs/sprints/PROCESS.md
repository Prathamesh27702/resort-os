# How we run sprints

## Roles

| Role | Who | Responsibilities |
|---|---|---|
| Product owner | Prathamesh | Priorities, accepts or rejects each sprint at the review, answers `needs:owner-decision` issues, issues integration keys |
| Tech lead | assigned by owner | Breaks sprint tasks into issues, reviews architecture-affecting PRs, owns `main` releases |
| Developers | team | Pick issues, branch, build, test, open PRs, review each other |

## Cadence (2-week sprints)

| When | Meeting | Output |
|---|---|---|
| Day 1 | **Sprint planning** (60 min) | Sprint goal agreed; issues assigned in the sprint milestone; `current:` updated in `sprints.yml` |
| Daily | **Stand-up** (15 min) | Yesterday / today / blockers. Blocked issues get `status:blocked` |
| Day 9 | **Code freeze** | Only fixes merged; tech lead runs the acceptance checks |
| Day 10 | **Sprint review** (45 min) | Demo script from the sprint guide, run live on the dev environment. Owner accepts or files issues |
| Day 10 | **Retro** (30 min) | One or two process changes, written into this file |

## Where to see what's happening

1. **Current sprint:** top of [docs/sprints/README.md](README.md) and the root README.
2. **Work in the sprint:** GitHub → *Issues* → filter by milestone `Sprint NN — …`. The `[Epic]` issue has the full checklist.
3. **What a task means:** the sprint guide in `docs/sprints/sprint-NN-*.md`.
4. **What has shipped:** [CHANGELOG.md](../../CHANGELOG.md) and the `sprint-NN` tags on `main`.

## Issue lifecycle

`open` (in the milestone) → assigned + branch pushed → PR opened (linked with `Closes #123`) → review → merged to `develop` → issue closes automatically.

Labels: `type:*` (feature/bug/chore/docs/test/security), `area:*` (db/api/web/infra/integration), `priority:high`, `status:blocked`,
`needs:owner-decision`, `good first issue`.

## Definition of Ready (before an issue enters a sprint)

- [ ] Clear outcome and acceptance criteria in the issue
- [ ] Tables/endpoints named, or explicitly left to the developer
- [ ] Permission(s) identified (existing code or a new one to add to the matrix)
- [ ] Dependencies merged or in the same sprint
- [ ] Any external account/key needed is available (ask the owner early)

## Definition of Done (every issue)

- [ ] Code merged to `develop` through a PR with one approving review and green CI
- [ ] Tests written for the behaviour (see the sprint guide's required tests)
- [ ] State-changing endpoints have: permission check, FluentValidation validator, audit entry, idempotency where money/bookings are involved
- [ ] No secrets, keys or personal data committed
- [ ] `docs/API.md` / `docs/DATABASE.md` updated if endpoints or tables changed
- [ ] UI has loading, empty and error states and works at 360 px width

## Branching and releases

```
main      ── production-ready, tagged sprint-NN after each accepted sprint (protected)
develop   ── integration branch; every feature PR targets this (protected)
feature/S02-41-room-editor   ── one per issue, from develop
fix/S02-57-block-overlap     ── bug fix during the sprint
hotfix/…  ── from main, only for production incidents; merged back to develop
```

- Squash-merge feature PRs. The PR title becomes the commit: `feat(rooms): dorm bed editor (#41)`.
- At sprint acceptance the tech lead merges `develop` → `main` (merge commit) and tags `sprint-NN`.

## Updating the plan

`docs/sprints/sprints.yml` is the single source of truth. To change a sprint (scope, status, current sprint):

```bash
# edit docs/sprints/sprints.yml, then
python3 scripts/github/sync-sprints.py docs                       # regenerate the guides
python3 scripts/github/sync-sprints.py github Prathamesh27702/resort-os   # update milestones + epics
python3 scripts/github/sync-sprints.py tasks  Prathamesh27702/resort-os 2 # create task issues for sprint 2
```

Commit the regenerated docs in the same PR.
