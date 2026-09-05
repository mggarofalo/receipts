# DbMigrator

Applies EF Core database migrations to PostgreSQL.

The API does **not** self-migrate. This tool must run before the API starts (handled automatically by Aspire and Docker Compose orchestration).

## Usage

```bash
dotnet run --project src/Tools/DbMigrator/DbMigrator.csproj
```

Requires database connection environment variables: `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`.

## Creating New Migrations

```bash
dotnet ef migrations add MigrationName \
  --project src/Infrastructure/Infrastructure.csproj \
  --startup-project src/Tools/DbMigrator/DbMigrator.csproj
```

## Transaction account ownership migration

`20260905215220_DropTransactionAccountId` removes the redundant account column from transactions. Stop old API instances before applying this migration and start the matching application version afterward; the old schema and write contract are incompatible with the new release.

The migration locks transactions and cards in its transaction, counts disagreements including trash, and logs the observed count through `DatabaseMigratorService`. Any nonzero count aborts before the column is dropped. Investigate and reconcile those rows deliberately, then rerun the tool. The migration never silently chooses a value. Downgrading reconstructs transaction accounts from the cards' current parents, including trashed history.

The guard uses [PostgreSQL transaction-scoped table locks](https://www.postgresql.org/docs/current/explicit-locking.html) and [EF Core custom migration SQL](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing). The tool subscribes to [Npgsql notices](https://www.npgsql.org/doc/diagnostics/exceptions_notices.html) so successful precondition counts appear at the normal information log level.
