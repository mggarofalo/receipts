using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Infrastructure.IntegrationTests.Fixtures;

// One PostgreSQL server per test process. Tests never share a database on it:
// each database is copied from a template that was migrated once to the requested migration,
// so a test pays for a file copy rather than a replay of every migration.
internal static class PostgresServer
{
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
	internal const string SearchPath = "public,receipts,library,matching,ynab,audit,identity";

	// Parallel test classes each hold a pool, and concurrency tests open extra sessions.
	private const int MaxConnections = 500;

	private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(StartAsync);
	private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Templates = new();

	public static async Task<string> AdminConnectionStringAsync() => (await Container.Value).GetConnectionString();

	public static string ConnectionString(string adminConnectionString, string database) =>
		new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = database, SearchPath = SearchPath }.ConnectionString;

	// Creates an empty-of-tests database at the given migration (null = latest). The caller owns it.
	public static async Task<string> CreateDatabaseAsync(string prefix, string? migration)
	{
		string template = await Templates.GetOrAdd(migration ?? "", key => new(() => BuildTemplateAsync(key))).Value;
		string name = $"{prefix}_{Guid.NewGuid():N}";
		// FILE_COPY copies the template's files rather than WAL-logging every block; measured
		// about 8% faster over the whole lane (5.9 vs 6.4 min) on this small schema.
		await ExecuteAsync($"CREATE DATABASE \"{name}\" TEMPLATE \"{template}\" STRATEGY FILE_COPY");
		return name;
	}

	public static Task DropDatabaseAsync(string name) => ExecuteAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");

	private static async Task<string> BuildTemplateAsync(string migration)
	{
		string name = $"template_{Guid.NewGuid():N}";
		await ExecuteAsync($"CREATE DATABASE \"{name}\"");

		// No pooling: every session on the template must be closed before it can be copied.
		NpgsqlConnectionStringBuilder connection = new(ConnectionString(await AdminConnectionStringAsync(), name)) { Pooling = false };
		NpgsqlDataSourceBuilder builder = new(connection.ConnectionString);
		builder.UseVector();
		await using (NpgsqlDataSource dataSource = builder.Build())
		{
			DbContextOptionsBuilder<ApplicationDbContext> options = new();
			options.UseNpgsql(dataSource, b =>
			{
				b.UseVector();
				b.UsePublicMigrationsHistory();
			});
			await using ApplicationDbContext context = new(options.Options);
			await context.GetService<IMigrator>().MigrateAsync(migration.Length == 0 ? null : migration);
		}

		// Refuse new sessions so a stray connection can never block, or be copied mid-write into, a test database.
		await ExecuteAsync($"ALTER DATABASE \"{name}\" WITH IS_TEMPLATE true ALLOW_CONNECTIONS false");
		return name;
	}

	private static async Task ExecuteAsync(string sql)
	{
		await using NpgsqlConnection admin = new(await AdminConnectionStringAsync());
		await admin.OpenAsync();
		await using NpgsqlCommand command = new(sql, admin);
		await command.ExecuteNonQueryAsync();
	}

	private static async Task<PostgreSqlContainer> StartAsync()
	{
		PostgreSqlContainer container = new PostgreSqlBuilder(PostgresFixture.Image)
			.WithCommand("-c", $"max_connections={MaxConnections}")
			.Build();
		await container.StartAsync();
		// The Testcontainers reaper also removes it if the process dies before this runs.
		AppDomain.CurrentDomain.ProcessExit += (_, _) => container.DisposeAsync().AsTask().GetAwaiter().GetResult();
		return container;
	}
}
