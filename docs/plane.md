# Plane

Guidance for AI agents working with the Plane workspace for this project.

## Workspace

- **Project:** Receipts (identifier: `RECEIPTS`)
- **CLI:** `plane` — use `plane --help` and `plane <command> --help` to discover commands. If you encounter bugs or missing features in the CLI, create an issue at https://github.com/mggarofalo/plane-cli
- **Output:** `-o json` (default) for programmatic parsing, `-o table` for human-readable

The CLI supports name resolution — use state names, label names, and member names instead of UUIDs.

## Labels

Every issue needs at least one **layer** label. Add **type** labels as appropriate.

| Layer | Type |
|-------|------|
| `backend`, `frontend`, `infra`, `docs` | `Feature`, `Improvement`, `Bug`, `cleanup`, `security`, `codegen`, `dx`, `testing`, `epic` |

Issues labeled `epic` are parent containers — skip and work their children.

## Modules

Phases are tracked as Plane **modules**. Use `plane module list --all -p RECEIPTS` to discover them and their current status.

**Every issue must be attached to a module.** `plane issue create` does NOT accept a `--module-id` flag — attach via a separate call after creation:

```bash
ID=$(plane issue create --name "..." --labels backend --priority medium --id-only -p RECEIPTS -w dev)
plane module add-work-items --module-id "Maintenance Backlog" --issues "$ID" -p RECEIPTS -w dev
```

If no phase fits, use **Maintenance Backlog** — the default bucket for small hardening, bug fixes, and test stabilization that don't belong to a larger phase.

## Priority

Priority reflects **execution readiness**, not importance: Urgent = ready now, High = one step away, Medium = blocked by 2+, Low = far future.

## What's Next

1. List issues in Backlog/Todo state, exclude Done/Cancelled/Duplicate
2. Skip `epic`-labeled issues — work their children
3. Skip issues with unresolved blockers
4. Pick the highest-priority unblocked issue

## Issue Workflow

`plane issue update` and `plane issue get` take the issue's UUID via `--resource-id`. They do not resolve a sequence ID like `RECEIPTS-42`, so look the UUID up first:

```bash
UUID=$(plane issue list --all -p RECEIPTS -w dev | jq -r '.results[] | select(.sequence_id==42) | .id')
```

**Start:** `plane issue update --resource-id "$UUID" --state "In Progress" -p RECEIPTS -w dev` — branch as `<type>/receipts-<id>-<desc>`

**Finish:** `plane issue update --resource-id "$UUID" --state Done -p RECEIPTS -w dev` — check if this unblocks downstream issues with `plane relation list --work-item-id "$UUID" -p RECEIPTS -w dev`

**Create:** Always use `-p RECEIPTS`, attach a module via `plane module add-work-items` (see Modules section — default to `Maintenance Backlog` if no phase fits), set priority by readiness, add at least one layer label
