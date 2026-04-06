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

Two-tier model: module branches for CI/PR gating, issue branches for individual work. **PRs target `develop`, not `main`** — only `develop`, `release-please--*`, and `hotfix/*` branches may merge to `main` (enforced by CI). See **[docs/branching.md](docs/branching.md)** for strategy, merge procedures, worktree setup, and directory isolation.

### Commits

Conventional Commits: `<type>(<scope>): <description>`. Enforced by `commit-msg` hook and CI PR title check. See **[docs/development.md](docs/development.md#commit-convention)** for types, scopes, and config.

### OpenAPI

Spec-first workflow — edit `openapi/spec.yaml`, lint, build, check drift. See **[docs/api-guidelines.md](docs/api-guidelines.md)** for full details.

## Build and Test

```bash
dotnet build Receipts.slnx                                    # Build entire solution
dotnet test Receipts.slnx --filter "Category!=Integration"    # Unit tests only (CI + pre-commit)
dotnet test Receipts.slnx                                     # All tests (requires ONNX model)
```

The API does not self-migrate or self-seed. See **[docs/development.md](docs/development.md#running-without-aspire)** for full commands including migrations, seeding, and single-project tests.

## Architecture

.NET 10 Clean Architecture with CQRS (MediatR), Repository pattern, Mapperly, and soft-delete with audit logging. See **[docs/architecture.md](docs/architecture.md)**.

## Coding Standards

C# conventions, Mapperly rules, EF Core query guidelines, React hook stability rules. See **[docs/coding-standards.md](docs/coding-standards.md)**.

## React Best Practices

State management, Effects, component patterns, and custom hook conventions for the React client. See **[docs/react/README.md](docs/react/README.md)**.

## Agent Rules

**All new functionality must include tests — backend and frontend.** When implementing a feature, endpoint, command, query, or bug fix, include corresponding unit tests in the same PR. Never defer tests to a follow-up.

- **Backend:** Follow existing conventions (xUnit, Arrange/Act/Assert, FluentAssertions, Moq). Test MediatR handlers, mappers, validators, and services with business logic.
- **Frontend:** Every new hook (`useX`) must have a `useX.test.ts`. Every new page component must have a test covering rendered content, loading/error states, and primary interactions. Follow the mock patterns in [docs/testing.md](docs/testing.md#mock-fidelity-rules).
- **Test-first when possible.** Write the failing test before the implementation. Coverage is an observed outcome, not a target — never write tests solely to increase a coverage number.

For principles on test quality, what to test vs. skip, and Goodhart's Law risks, see **[docs/agentic-testing.md](docs/agentic-testing.md)**.

**Integration tests** use `[Trait("Category", "Integration")]` and are excluded from CI/pre-commit via `--filter "Category!=Integration"`.

**Never modify coverage thresholds or CI configuration** unless explicitly asked. Coverage gates are not part of feature implementation.

**Never write tests or perform code review in the main conversation context.** Always spawn subagents:
- Use `test-runner` or equivalent for running/writing tests
- Use `pr-review-toolkit:code-reviewer` or similar for code review
- This keeps the main context focused and prevents context window bloat
