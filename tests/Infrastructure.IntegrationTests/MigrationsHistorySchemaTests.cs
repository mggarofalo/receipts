using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// RECEIPTS-830: guards the migrations-history table against <c>search_path</c> shadowing.
/// </summary>
/// <remarks>
/// The deployed compose stack runs PostgreSQL as <c>POSTGRES_USER=receipts</c>, and RECEIPTS-746 added a
/// schema named <c>receipts</c>. PostgreSQL's default <c>search_path</c> is <c>"$user", public</c>, so the
/// moment that schema exists <c>current_schema()</c> becomes <c>receipts</c> rather than <c>public</c>.
/// EF Core 9+ issues <c>CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory"</c> unconditionally before taking
/// the migration lock; unqualified, that lands an empty second history table in <c>receipts</c>, which then
/// reads back as "nothing applied" and replays every migration against a populated database.
///
/// This class runs its own container because the shared <see cref="Fixtures.PostgresFixture"/> cannot
/// reproduce the collision: it connects as the default Testcontainers role and pins an explicit
/// <c>search_path</c> with <c>public</c> first, which masks the bug on both counts.
/// </remarks>
[Trait("Category", "Integration")]
public class MigrationsHistorySchemaTests : IAsyncLifetime
{
	/// <summary>Role name and database name, chosen to collide with the <c>receipts</c> schema.</summary>
	private const string CollidingName = "receipts";

	private const string HistoryTable = "__EFMigrationsHistory";

	// Deliberately no SearchPath override — the default "$user", public is the whole point of the test.
	private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
		.WithUsername(CollidingName)
		.WithDatabase(CollidingName)
		.Build();

	private NpgsqlDataSource? _dataSource;

	public async Task InitializeAsync()
	{
		await _container.StartAsync();

		NpgsqlDataSourceBuilder dataSourceBuilder = new(_container.GetConnectionString());
		dataSourceBuilder.UseVector();
		_dataSource = dataSourceBuilder.Build();
	}

	public async Task DisposeAsync()
	{
		if (_dataSource is not null)
		{
			await _dataSource.DisposeAsync();
		}

		await _container.DisposeAsync();
	}

	[Fact]
	public async Task MigrateAsync_WhenSchemaNameMatchesRoleName_SecondRunAppliesNothing()
	{
		// Arrange — first boot. The `receipts` schema does not exist yet, so current_schema() is still
		// public and every migration applies cleanly (this is why the initial deploy succeeded).
		await using (ApplicationDbContext firstBoot = CreateDbContext())
		{
			await firstBoot.Database.MigrateAsync();
		}

		// The first connection cached Npgsql's type info before the pgvector extension existed; reload so
		// the vector mapping resolves for any later context.
		await using (NpgsqlConnection reloadConnection = await _dataSource!.OpenConnectionAsync())
		{
			await reloadConnection.ReloadTypesAsync();
		}

		// Sanity-check that the collision this test exists for is actually live: applying RECEIPTS-746
		// created the `receipts` schema, which "$user" now resolves to.
		(await ScalarAsync<string>("SELECT current_schema()")).Should().Be(CollidingName);

		// Act — second boot against the already-migrated database. This is what a container restart does,
		// and what crash-looped in production.
		await using ApplicationDbContext secondBoot = CreateDbContext();
		Func<Task> act = () => secondBoot.Database.MigrateAsync();

		// Assert — nothing left to apply, and no shadow history table outside public.
		await act.Should().NotThrowAsync();

		List<string> historySchemas = await HistoryTableSchemasAsync();
		historySchemas.Should().Equal("public");
	}

	private ApplicationDbContext CreateDbContext()
	{
		DbContextOptionsBuilder<ApplicationDbContext> builder = new();
		builder.UseNpgsql(_dataSource!, b =>
		{
			b.UseVector();
			b.UsePublicMigrationsHistory();
		});

		return new ApplicationDbContext(builder.Options);
	}

	/// <summary>Every schema that holds a <c>__EFMigrationsHistory</c> table, ordered by name.</summary>
	private async Task<List<string>> HistoryTableSchemasAsync()
	{
		await using NpgsqlCommand command = _dataSource!.CreateCommand(
			"""
			SELECT n.nspname
			FROM pg_catalog.pg_class c
			JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
			WHERE c.relname = @table
			ORDER BY n.nspname
			""");
		command.Parameters.AddWithValue("table", HistoryTable);

		List<string> schemas = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			schemas.Add(reader.GetString(0));
		}

		return schemas;
	}

	private async Task<T> ScalarAsync<T>(string sql)
	{
		await using NpgsqlCommand command = _dataSource!.CreateCommand(sql);
		return (T)(await command.ExecuteScalarAsync())!;
	}
}
