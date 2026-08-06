# AGENTS.md

Guidance for AI agents working in this repository.

## Quick Start

```bash
dotnet restore Receipts.slnx   # .NET packages + configures git hooks
npm install                     # OpenAPI tooling (Spectral, js-yaml, cross-env)
```

For full prerequisites and Aspire setup, see **[docs/development.md](docs/development.md)**.

## Workflow Rules

### Plane

All issue work is tracked in Plane. Project: "Receipts" (identifier: `RECEIPTS`) — use `plane` CLI for all operations. Issues labeled `epic` are parent containers — skip and work their children. See **[docs/plane.md](docs/plane.md)** for full details.

### Branching

Single-trunk model: `main` is the only long-lived branch. Cut a short-lived
`<type>/receipts-<id>-<slug>` branch off `main`, open a PR, and **squash-merge to
`main`** — all branches target `main`. There is no `develop` branch and no
module/parent branches. Releases are tag-driven (push a `vX.Y.Z` tag). See
**[docs/branching.md](docs/branching.md)** and **[docs/releases.md](docs/releases.md)**.

**Never close or merge a pull request you did not open in the current session.**
If a PR looks like a blocker, report it and stop — do not close it.

### Commits

Conventional Commits: `<type>(<scope>): <description>`. Enforced by `commit-msg` hook and CI PR title check. See **[docs/development.md](docs/development.md#commit-convention)** for types, scopes, and config.

### OpenAPI

Spec-first workflow — edit `openapi/spec.yaml`, lint, build, check drift. See **[docs/api-guidelines.md](docs/api-guidelines.md)** for full details.

#### Generated TypeScript client types (`src/client/src/generated/api.d.ts`)

This file is **checked into git** (via Track B of RECEIPTS-534). It is a materialized view of `openapi/spec.yaml`, and the build guards against drift:

- **Pre-commit auto-regenerates** `api.d.ts` whenever `openapi/spec.yaml` is staged — you do not need to run the regenerate command manually before committing.
- **`npm run prebuild`** runs `generate:types:check`, which diffs the committed file against a freshly generated one. The build fails on mismatch.
- **`.gitattributes`** marks `api.d.ts` as `merge=ours`, and **`.githooks/post-merge`** regenerates it automatically when a merge touches `openapi/spec.yaml`.
- **After rebase or manual merge,** if `openapi/spec.yaml` was touched and the post-merge hook did not run (e.g. IDE merge), run `(cd src/client && npm run generate:types:write)` and commit the result.
- **Never hand-merge conflicts in `src/client/src/generated/`.** Always resolve by regeneration. If you see a conflict marker in `api.d.ts`, run `npm run generate:types:write` and stage the result.
- **`openapi-typescript` is pinned** to an exact version in `src/client/package.json` so two worktrees on the same SHA always produce byte-identical output.

## Build and Test

```bash
dotnet build Receipts.slnx                                    # Build entire solution
dotnet test Receipts.slnx --filter "Category!=Integration"    # Unit tests only (CI + pre-commit)
dotnet test Receipts.slnx                                     # All tests (requires ONNX model)
```

The API does not self-migrate or self-seed. See **[docs/development.md](docs/development.md#running-without-aspire)** for full commands including migrations, seeding, and single-project tests.

## Architecture

.NET 10 Clean Architecture with CQRS ([martinothamar/Mediator](https://github.com/martinothamar/Mediator)), Repository pattern, Mapperly, and soft-delete with audit logging. See **[docs/architecture.md](docs/architecture.md)**.

## Coding Standards

C# conventions, Mapperly rules, EF Core query guidelines, React hook stability rules. See **[docs/coding-standards.md](docs/coding-standards.md)**.

## API Error Contract

Every 4xx this API raises **with a reason** answers with an RFC 9457 problem document — never a bare JSON string, never an ad-hoc `{ message }` object. Build them with `ApiProblem` (`src/Presentation/API/Http/ApiProblem.cs`):

```csharp
return ApiProblem.BadRequest("offset must be >= 0");
return ApiProblem.NotFound($"Receipt {id} not found");
return ApiProblem.Conflict(
    $"Cannot delete — {count} transaction(s) reference this card",
    new Dictionary<string, object?> { ["transactionCount"] = count });
```

Rules:

- **The human-readable reason always goes in `detail`.** That is the one field the client renders (`extractErrorMessage` in `src/client/src/lib/problem-details.ts`).
- **Machine-readable context goes in extensions**, not encoded into the prose. Extension members serialise at the top level of the body, so a consumer reads `body.transactionCount` directly.
- **Declare the status in the return type** — `BadRequest<ProblemDetails>`, not `ProblemHttpResult`. The OpenAPI generator emits one schema per status from the `Results<…>` signature; `ProblemHttpResult` collapses them and the drift check will fail.
- **A rejection with nothing useful to say stays bodiless.** `TypedResults.NotFound()` is correct when the id simply is not there; do not invent prose to fill a document.
- **In `openapi/spec.yaml`, point 4xx responses at `#/components/schemas/ProblemDetails`.** A `type: string` error schema is the old contract and will not match what the server sends.

Changing an endpoint's error schema trips the **API Breaking Changes** CI gate — see the note under Workflow Rules about `breaking-changes-allowed`.

The client keeps a normalisation middleware (`errorNormalizationMiddleware`) for **bodiless** failures, which ASP.NET still produces for authorization 403s. That layer is a backstop, not the contract; do not rely on it to repair a body the server should have shaped correctly.

## React Best Practices

State management, Effects, component patterns, and custom hook conventions for the React client. See **[docs/react/README.md](docs/react/README.md)**.

## Agent Rules

**All new functionality must include tests — backend and frontend.** When implementing a feature, endpoint, command, query, or bug fix, include corresponding unit tests in the same PR. Never defer tests to a follow-up.

- **Backend:** Follow existing conventions (xUnit, Arrange/Act/Assert, FluentAssertions, Moq). Test Mediator handlers, mappers, validators, and services with business logic.
- **Frontend:** Every new hook (`useX`) must have a `useX.test.ts`. Every new page component must have a test covering rendered content, loading/error states, and primary interactions. Follow the mock patterns in [docs/testing.md](docs/testing.md#mock-fidelity-rules).
- **Test-first when possible.** Write the failing test before the implementation. Coverage is an observed outcome, not a target — never write tests solely to increase a coverage number.

For principles on test quality, what to test vs. skip, and Goodhart's Law risks, see **[docs/agentic-testing.md](docs/agentic-testing.md)**.

**Integration tests** use `[Trait("Category", "Integration")]` and are excluded from CI/pre-commit via `--filter "Category!=Integration"`.

**Never modify coverage thresholds or CI configuration** unless explicitly asked. Coverage gates are not part of feature implementation.

**Never write tests or perform code review in the main conversation context.** Always spawn subagents:
- Use `test-runner` or equivalent for running/writing tests
- Use `pr-review-toolkit:code-reviewer` or similar for code review
- This keeps the main context focused and prevents context window bloat
