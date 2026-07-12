using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Infrastructure.IntegrationTests.Fixtures;

public class PostgresFixture : IAsyncLifetime
{
	private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
		.Build();

	private NpgsqlDataSource? _dataSource;

	public string ConnectionString => _container.GetConnectionString();

	public async Task InitializeAsync()
	{
		await _container.StartAsync();

		// RECEIPTS-746: tables live in bounded-context schemas (receipts, library, matching,
		// ynab, audit, identity), not public. Set a search_path spanning every schema so the
		// hand-written raw SQL in tests resolves unqualified table names regardless of which
		// schema a table currently occupies. This matters most for MigrationSafetyTests, which
		// deliberately roll the DB back to a pre-746 state (tables still in public) and forward
		// again — the same table name must resolve in public before the move and in its schema
		// after. EF's own queries are always schema-qualified, so this only affects test SQL.
		//
		// public MUST come first: EF scaffolds the schema move as unqualified
		// `ALTER TABLE "Foo" SET SCHEMA <schema>`, and the historical migrations replayed by
		// MigrationSafetyTests use unqualified raw SQL — both assume public is the working
		// schema. Keeping public first makes that DDL deterministic while still resolving the
		// moved tables (a table exists in exactly one schema, so lookup falls through to it).
		NpgsqlConnectionStringBuilder connectionStringBuilder = new(ConnectionString)
		{
			SearchPath = "public,receipts,library,matching,ynab,audit,identity",
		};

		NpgsqlDataSourceBuilder dataSourceBuilder = new(connectionStringBuilder.ConnectionString);
		dataSourceBuilder.UseVector();
		_dataSource = dataSourceBuilder.Build();

		// Run migrations to create the schema. The first connection caches
		// npgsql's type info before the pgvector extension exists, so
		// UseVector()'s vector mapping can't resolve. Reload types after
		// migrations so the cache picks up the newly-created extension.
		await using (ApplicationDbContext context = CreateDbContext())
		{
			await context.Database.MigrateAsync();
		}

		await using NpgsqlConnection reloadConnection = await _dataSource.OpenConnectionAsync();
		await reloadConnection.ReloadTypesAsync();
	}

	public ApplicationDbContext CreateDbContext() => new(CreateOptions());

	// Exposes the same Npgsql/pgvector options CreateDbContext() builds, so a test can construct a
	// custom ApplicationDbContext subclass (e.g. one that injects a mid-transaction failure) against
	// the fixture's already-type-reloaded data source without standing up a second connection pool.
	public DbContextOptions<ApplicationDbContext> CreateOptions()
	{
		if (_dataSource is null)
		{
			throw new InvalidOperationException("Fixture has not been initialized. Call InitializeAsync() first.");
		}

		DbContextOptionsBuilder<ApplicationDbContext> builder = new();
		builder.UseNpgsql(_dataSource, b => b.UseVector());

		return builder.Options;
	}

	public async Task DisposeAsync()
	{
		if (_dataSource is not null)
		{
			await _dataSource.DisposeAsync();
		}

		await _container.DisposeAsync();
		GC.SuppressFinalize(this);
	}
}
