# Development Guide

Local development uses **.NET Aspire** to orchestrate all services — API, PostgreSQL, and PgAdmin — with a single F5 press in VS Code.

## Prerequisites

| Tool | Version | Purpose |
|------|---------|---------|
| [.NET 10 SDK](https://dot.net) | 10.0+ | Build and run the API |
| [Aspire CLI](https://aspire.dev/get-started/install-cli/) | Any | Orchestrate local dev stack from CLI |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | Any | PostgreSQL container (Aspire manages it) |
| [Node.js](https://nodejs.org) | 18+ | OpenAPI spec linting and drift detection |
| [VS Code](https://code.visualstudio.com) | Any | Recommended IDE |
| [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit) | Any | VS Code C# support |

> **Docker is required** — Aspire provisions PostgreSQL as a container automatically. You don't need to manage the database manually.

## Initial Setup

```bash
# Clone the repository
git clone https://github.com/mggarofalo/Receipts.git
cd Receipts

# Install Aspire CLI (if not already installed)
dotnet tool install --global Aspire.Cli

# Restore .NET packages and install tools (also configures native Git hooks)
dotnet restore Receipts.slnx

# Install Node dependencies (OpenAPI linting tools)
npm install

# Download the ONNX embedding model (~90MB, required at runtime)
dotnet run scripts/download-onnx-model.cs
```

## F5 Debugging (Recommended)

1. Open the repository root in VS Code
2. Press **F5** (or **Run → Start Debugging**)
3. Select **"Launch Aspire AppHost"** if prompted
4. Wait ~30 seconds for all services to start

VS Code will automatically open the **Aspire Dashboard** in your browser.

### What Starts

| Service | Default URL | Description |
|---------|-------------|-------------|
| Aspire Dashboard | http://localhost:15888 | Observability — logs, traces, metrics |
| API (HTTP) | http://localhost:5000 | REST API |
| API (HTTPS) | https://localhost:5001 | REST API (HTTPS) |
| API Docs (Scalar) | http://localhost:5000/scalar | Interactive API documentation |
| PgAdmin | auto-assigned | PostgreSQL admin UI |

> **Note:** Aspire assigns dynamic ports and the defaults above may differ. Check the **Aspire Dashboard → Resources** view for the actual URLs assigned to each service in your session.

### Debug Configurations

- **Launch Aspire AppHost** — Starts the entire stack (API + DB + Dashboard)
- **Attach to API** — Attach debugger to a running API process for breakpoints
- **Debug All (AppHost + API)** — Launch AppHost and immediately attach the debugger to the API

## Aspire Dashboard

The dashboard provides full observability without any external tooling:

| View | What You See |
|------|-------------|
| **Resources** | All running services with health status |
| **Traces** | Distributed request traces across API → DB |
| **Metrics** | Request rates, response times, runtime stats |
| **Logs** | Structured logs from all services, searchable |
| **Console** | Raw stdout/stderr from each service |

### Database Tracing

EF Core queries are automatically traced and visible in the Traces view. Each HTTP request shows the full span tree including SQL statements executed against PostgreSQL.

## Running Without Aspire

If you prefer to run the API directly (without Docker/Aspire):

```bash
# Set database environment variables
export POSTGRES_HOST=localhost
export POSTGRES_PORT=5432
export POSTGRES_USER=postgres
export POSTGRES_PASSWORD=yourpassword
export POSTGRES_DB=receiptsdb

# Apply migrations and seed the database (required before first run)
dotnet run --project src/Tools/DbMigrator/DbMigrator.csproj
dotnet run --project src/Tools/DbSeeder/DbSeeder.csproj

# Run the API
dotnet run --project src/Presentation/API/API.csproj
```

The API does not self-migrate or self-seed. You must run DbMigrator and DbSeeder before starting the API. Re-run DbMigrator after pulling new migrations.

### Admin User Seeding

The DbSeeder creates an initial admin user when `AdminSeed__Email` and `AdminSeed__Password` are set. Under **Aspire**, these are passed automatically via `AppHost.cs`. For **standalone** runs, set the environment variables manually:

```bash
export AdminSeed__Email=admin@receipts.local
export AdminSeed__Password="Admin123!@#"
export AdminSeed__FirstName=Admin
export AdminSeed__LastName=User
dotnet run --project src/Tools/DbSeeder/DbSeeder.csproj
```

If the variables are absent, the seeder logs a warning and seeds only roles (no admin user). The seed is not recorded in `__SeedHistory` when admin config is missing, so you can re-run the seeder with the correct variables later.

> **Tip:** The `src/Tools/DbSeeder/appsettings.Development.json` file provides these defaults automatically when running with `DOTNET_ENVIRONMENT=Development` (the default for `dotnet run`).

## Build and Test

```bash
# Build entire solution
dotnet build Receipts.slnx

# Run unit tests (same as CI)
dotnet test Receipts.slnx --filter "Category!=Integration"

# Run all tests including integration (requires Docker + ONNX model)
dotnet test Receipts.slnx

# Run integration tests only (requires Docker)
dotnet test tests/Infrastructure.IntegrationTests --filter "Category=Integration"

# Run tests for a specific project
dotnet test tests/Application.Tests/Application.Tests.csproj
```

### Integration Tests (Testcontainers)

The `Infrastructure.IntegrationTests` project runs EF Core against a real PostgreSQL instance via [Testcontainers](https://dotnet.testcontainers.org/). These tests catch bugs that InMemory unit tests cannot, such as:

- **DateTimeOffset UTC validation** — Npgsql rejects non-UTC offsets for `timestamptz` columns
- **Column type mapping** — `decimal(18,2)`, `uuid`, `text`, `date`, enum-to-string, pgvector
- **Soft-delete cascades** — parent deletion cascades `DeletedAt` to owned children via real SQL
- **Audit logging** — full `SaveChangesAsync` pipeline with real database round-trips
- **Query filters** — `HasQueryFilter` generates real SQL `WHERE` clauses

**Requirements:** Docker must be running. The tests automatically start and stop a PostgreSQL container — no manual database setup needed.

**CI note:** Integration tests are tagged `[Trait("Category", "Integration")]` and excluded from the CI unit test step (`--filter "Category!=Integration"`). They run locally or in CI environments with Docker available.

## Git Hooks

Git hooks are installed automatically by `dotnet restore` (or `bash .githooks/setup.sh`). Two hooks run on every commit:

### Commit Convention

All commits follow [Conventional Commits](https://www.conventionalcommits.org/) format: `<type>(<scope>): <description>`

| Types | `feat`, `fix`, `docs`, `refactor`, `test`, `chore` |
|-------|-----------------------------------------------------|
| Scopes | `api`, `client`, `domain`, `application`, `infrastructure`, `infra`, `common`, `shared`, `ci`, `hooks` |

Multiple scopes are allowed with a comma separator (e.g., `feat(api,client): add pagination`).

Examples:
- `feat(api): add pagination to receipts endpoint`
- `fix(client): prevent infinite re-render in TransactionForm`
- `chore: update dependencies`

**Enforcement:**
- **Local:** `commit-msg` hook runs `commitlint` on every commit (see `.githooks/commit-msg`)
- **CI:** PR title validation via `amannn/action-semantic-pull-request` (squash-merge means the PR title becomes the commit on `main`)
- **Config:** `commitlint.config.mjs` at the repo root defines allowed types, scopes, and header length (100 chars max)

### `pre-commit` hook

Every `git commit` runs the full quality pipeline automatically:

0. **Prerequisites** — `dotnet run scripts/worktree-setup.cs -- --check`
1. **OpenAPI spec lint** — `npx spectral lint openapi/spec.yaml`
2. **Code format check** — `dotnet format --verify-no-changes`
3. **Build with warnings-as-errors** — also regenerates DTOs and `openapi/generated/API.json`
4. **Semantic drift check** — compares spec vs generated output for structural differences
5. **Tests** — `dotnet test --no-build --filter "Category!=Integration"`
6. **TypeScript types** — `npx tsc --noEmit`
7. **ESLint** — `npx eslint src/client/src`

For faster iteration, quick mode runs only prerequisites, format, tsc, and eslint:
```bash
PRECOMMIT_QUICK=1 git commit -m "message"
```

## OpenAPI Spec-First Workflow

All API changes follow a spec-first workflow:

1. Edit `openapi/spec.yaml` — this is the single source of truth
2. `npm run lint:spec` — validate the spec
3. `dotnet build` — regenerates DTOs and the built output
4. `npm run check:drift` — verify spec and implementation stay in sync

See **[docs/api-guidelines.md](api-guidelines.md)** for the full spec-first workflow details.

## Troubleshooting

### Port conflicts
If ports 5000/5001 are in use, Aspire will pick alternative ports. Check the Dashboard Resources view for the actual URLs.

### Docker not running
Aspire requires Docker to provision the PostgreSQL container. Start Docker Desktop before pressing F5.

### Database connection issues
The API waits for PostgreSQL to be healthy before starting (`.WaitFor(db)` in AppHost). If the API starts before the database is ready, Aspire restarts it automatically.

### Pre-commit hook failures
- **Spec lint fails** — fix the OpenAPI spec error reported by Spectral
- **Format fails** — run `dotnet format Receipts.slnx` to auto-fix
- **Drift check fails** — the spec and generated API are out of sync; update the spec or the implementation to match
- **Tests fail** — fix the failing tests before committing

## Releases

See **[docs/releases.md](releases.md)** for the full release process, including how release-please automates versioning, changelogs, and GitHub Releases from conventional commits.
