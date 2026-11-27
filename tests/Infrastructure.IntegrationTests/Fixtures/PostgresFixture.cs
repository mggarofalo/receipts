using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests.Fixtures;

// A private database on the process-wide PostgreSQL server (see PostgresServer), migrated to
// the requested migration (null = latest). Disposal drops it; immutable application evidence
// is never reset, so a test that needs a clean slate takes a new fixture.
public class PostgresFixture : IAsyncLifetime
{
	/// <summary>Pinned: the kNN completeness rule needs pgvector 0.8 (RECEIPTS-1097).</summary>
	internal const string Image = "pgvector/pgvector:0.8.7-pg17";

	private readonly string? _migration;
	private string? _databaseName;
	private string? _connectionString;
	private NpgsqlDataSource? _dataSource;

	public PostgresFixture() : this(null) { }

	internal PostgresFixture(string? migration) => _migration = migration;

	// Each test owns a database; classes share only the PostgreSQL server.
	public async Task<PostgresFixture> CreateIsolatedAsync(string? migration = null)
	{
		PostgresFixture isolated = new(migration);
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

	public string ConnectionString => _connectionString
		?? throw new InvalidOperationException("Fixture has not been initialized. Call InitializeAsync() first.");

	public async Task InitializeAsync()
	{
		string admin = await PostgresServer.AdminConnectionStringAsync();
		_databaseName = await PostgresServer.CreateDatabaseAsync("test", _migration);
		_connectionString = PostgresServer.ConnectionString(admin, _databaseName);

		// The template already holds the pgvector extension, so the first connection's type
		// cache includes the vector mapping.
		NpgsqlDataSourceBuilder dataSourceBuilder = new(_connectionString);
		dataSourceBuilder.UseVector();
		_dataSource = dataSourceBuilder.Build();
	}

	public ApplicationDbContext CreateDbContext() => new(CreateOptions());

	internal ValueTask<NpgsqlConnection> OpenConnectionAsync() =>
		(_dataSource ?? throw new InvalidOperationException("Fixture has not been initialized. Call InitializeAsync() first.")).OpenConnectionAsync();

	// Exposes the same Npgsql/pgvector options CreateDbContext() builds, so a test can construct a
	// custom ApplicationDbContext subclass (e.g. one that injects a mid-transaction failure) against
	// the fixture's data source without standing up a second connection pool.
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

		if (_databaseName is not null)
		{
			await PostgresServer.DropDatabaseAsync(_databaseName);
		}
		GC.SuppressFinalize(this);
	}
}
