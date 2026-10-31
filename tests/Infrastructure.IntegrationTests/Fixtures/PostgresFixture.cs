using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Infrastructure.IntegrationTests.Fixtures;

public class PostgresFixture : IAsyncLifetime
{
	private readonly PostgreSqlContainer? _container;
	private readonly string? _adminConnectionString;
	private readonly string? _databaseName;
	private readonly string? _isolatedConnectionString;

	private readonly string? _migration;

	/// <summary>Pinned: the kNN completeness rule needs pgvector 0.8 (RECEIPTS-1097).</summary>
	internal const string Image = "pgvector/pgvector:0.8.7-pg17";

	public PostgresFixture() : this(null) { }

	internal PostgresFixture(string? migration)
	{
		_migration = migration;
		_container = new PostgreSqlBuilder(Image).Build();
	}

	private PostgresFixture(string adminConnectionString, string databaseName, string? migration)
	{
		_migration = migration;
		_adminConnectionString = adminConnectionString;
		_databaseName = databaseName;
		_isolatedConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = databaseName }.ConnectionString;
	}

	// Each test owns a database, while the collection shares only the PostgreSQL server.
	// Disposal drops that test database; immutable application evidence is never reset.
	public async Task<PostgresFixture> CreateIsolatedAsync(string? migration = null)
	{
		string name = $"test_{Guid.NewGuid():N}";
		await using (NpgsqlConnection admin = new(ConnectionString))
		{
			await admin.OpenAsync();
			await using NpgsqlCommand create = new($"CREATE DATABASE \"{name}\"", admin);
			await create.ExecuteNonQueryAsync();
		}
		PostgresFixture isolated = new(ConnectionString, name, migration);
		try
		{
			await isolated.InitializeAsync();
			return isolated;
		}
		catch
		{
			await isolated.DisposeAsync();
			throw;
		}
	}

	private NpgsqlDataSource? _dataSource;

	public string ConnectionString => _isolatedConnectionString ?? _container!.GetConnectionString();

	public async Task InitializeAsync()
	{
		if (_container is not null)
		{
			await _container.StartAsync();
		}

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
			await context.GetService<IMigrator>().MigrateAsync(_migration);
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
		builder.UseNpgsql(_dataSource, b =>
		{
			b.UseVector();
			b.UsePublicMigrationsHistory();
		});

		return builder.Options;
	}

	public async Task DisposeAsync()
	{
		if (_dataSource is not null)
		{
			await _dataSource.DisposeAsync();
		}

		if (_container is not null)
		{
			await _container.DisposeAsync();
		}
		else
		{
			await using NpgsqlConnection admin = new(_adminConnectionString!);
			await admin.OpenAsync();
			await using NpgsqlCommand drop = new($"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", admin);
			await drop.ExecuteNonQueryAsync();
		}
		GC.SuppressFinalize(this);
	}
}
