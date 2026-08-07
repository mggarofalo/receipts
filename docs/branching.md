# Branching Strategy

This project uses a **single-trunk** model. `main` is the only long-lived branch.
All work happens on short-lived feature/issue branches that are cut from `main`,
opened as PRs against `main`, and squash-merged back.

There is no `develop` branch, no module/parent branches, and no sync-back automation.

## Merge Flow

```
<type>/receipts-<id>-<slug>  ──PR──▶  main  ──tag vX.Y.Z──▶  release
```

Every change — features, fixes, chores, docs, hotfixes — flows the same way:
branch off `main`, open a PR to `main`, squash-merge.

## Branch Types

### `main` — the trunk

- The single long-lived branch. Always releasable.
- Protected: changes land only via squash-merged PRs.
- Pushing a `vX.Y.Z` tag on `main` cuts a release (see [releases.md](releases.md)).

### Feature / Issue branches

One short-lived branch per tracked Plane issue:

- Branch off the latest `main`.
- Name it `<type>/receipts-<id>-<slug>` — e.g. `feat/receipts-123-add-pagination`,
  `fix/receipts-145-null-guard`, `docs/receipts-160-update-readme`.
- `<type>` is the Conventional Commits type (`feat`, `fix`, `docs`, `refactor`,
  `test`, `chore`).
- Open a PR to `main`. CI runs build, test, lint, security, docker-build, and
  API-compat checks — this is the safety net.
- After review, **squash-merge** into `main`. The PR title becomes the squash
  commit message, so it must be a valid Conventional Commit.
- Delete the branch after merge.

### Hotfixes

A hotfix is not special — it is an ordinary `fix:` PR to `main`:

- Branch off `main` as `fix/receipts-<id>-<slug>`.
- Open a PR to `main`, squash-merge.
- Cut a release by pushing a new patch tag (`vX.Y.Z`).

## Keeping a branch current

Before opening a PR, rebase onto the latest `main` so the PR merges cleanly:

```bash
git fetch origin
git rebase origin/main
```

## Releases

Releases are **tag-driven**. There is no release PR and no release-please.
After the desired commits are on `main`, push an annotated semver tag:

```bash
git tag -a v1.4.0 -m "v1.4.0"
git push origin v1.4.0
```

The tag triggers two workflows:

- `docker-publish.yml` — builds and publishes multi-arch images to GHCR.
- `github-release.yml` — creates a GitHub Release with auto-generated notes.

The .NET assembly version is derived from the tag by [MinVer](https://github.com/adamralph/minver).
See [releases.md](releases.md) for the full process, including how to compute the
next semver from the conventional commits since the last tag. The `/release`
command automates it.

## Direct commits to main

**Do not commit directly to `main`.** All changes — including trivial ones — flow
through a PR. Branch protection enforces this.

## Directory Isolation

Two mechanisms are available for working on issue branches without disturbing the
main working tree.

### Git Worktrees (preferred for AI agents)

`git worktree add` creates a linked working tree sharing the same `.git` directory:

```bash
git worktree add .claude/worktrees/<branch-name> -b <branch-name> origin/main
```

- Shares git history and refs with the main worktree (no duplication).
- Claude Code's `isolation: "worktree"` parameter automates this for subagents.
- Worktrees live in `.claude/worktrees/` (gitignored).

#### Detecting a worktree

- `test -f .git` — if `.git` is a **file** (not a directory), you're in a worktree.
- `git rev-parse --show-toplevel` — confirms your working directory root.

#### What's shared vs not shared

| Shared (via common `.git` dir) | NOT shared (need fresh setup) |
|-------------------------------|-------------------------------|
| Git history, refs, branches | `node_modules/` (root and `src/client/`) |
| Hooks config (`core.hooksPath`) | `bin/` / `obj/` (.NET build output) |
| `src/client/src/generated/api.d.ts` (checked in) | `openapi/generated/` |
| | `src/Presentation/API/Generated/*.g.cs` |

#### Bootstrap commands

Run these in order (or use `dotnet run scripts/worktree-setup.cs` to run them all):

```bash
dotnet restore Receipts.slnx          # NuGet packages + configures git hooks
npm install                            # Root tooling (Spectral, js-yaml, cross-env)
cd src/client && npm install && cd -   # React client dependencies
dotnet run scripts/download-onnx-model.cs  # ONNX embedding model (~1.34 GB, once per machine)
dotnet build Receipts.slnx             # Compiles + generates DTOs and openapi/generated/API.json
cd src/client && npm run generate:types:write && cd -  # TypeScript types from OpenAPI spec
```

#### Permission settings

The file `.claude/settings.local.json` pre-approves read operations, MCP tools, git
commands, and build tools so agents don't face excessive approval prompts. This file
is gitignored (`.local.json` suffix) so it's per-user. Copy it from the main worktree
if it's missing.

### Local Clones (alternative)

`git clone --local` creates a lightweight local clone:

```bash
git clone --local . .clones/<branch-name>
```

- Hardlinks objects (fast, no network), fully independent git repo.
- `cd`, `git commit`, etc. all work normally.
- Clones live in `.clones/` at the repo root (gitignored).

### When to Use Which

- **Worktrees** — preferred for parallel AI agent work (faster setup, shared git state).
- **Local clones** — preferred for human developers who want full independence.
- **Neither** — for simple/small changes, working on a branch in the main worktree is fine.
