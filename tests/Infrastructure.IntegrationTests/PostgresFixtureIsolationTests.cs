using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class PostgresFixtureIsolationTests(PostgresFixture server) : IClassFixture<PostgresFixture>
{
	private const string HistoricalMigration = "20261030134404_AddEnvelopes";

	[Fact]
	public async Task IsolatedDatabases_HaveIndependentFullyMigratedStateAndDisposeIndependently()
	{
		PostgresFixture first = await server.CreateIsolatedAsync();
		PostgresFixture? second = null;
		bool firstDisposed = false;
		string firstName = new NpgsqlConnectionStringBuilder(first.ConnectionString).Database!;
		try
		{
			second = await server.CreateIsolatedAsync();
			first.ConnectionString.Should().NotBe(second.ConnectionString);
			await using (ApplicationDbContext db = first.CreateDbContext())
			{
				(await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
				await db.Database.ExecuteSqlRawAsync("CREATE TABLE public.isolation_marker (id integer)");
			}
			await first.DisposeAsync();
			firstDisposed = true;
			await using (NpgsqlConnection connection = new(second.ConnectionString))
			{
				await connection.OpenAsync();
				await using NpgsqlCommand marker = new("SELECT to_regclass('public.isolation_marker') IS NULL", connection);
				(await marker.ExecuteScalarAsync()).Should().Be(true);
				await using NpgsqlCommand dropped = new("SELECT count(*) FROM pg_database WHERE datname = @name", connection);
				dropped.Parameters.AddWithValue("name", firstName);
				(await dropped.ExecuteScalarAsync()).Should().Be(0L);
			}
			await using ApplicationDbContext remaining = second.CreateDbContext();
			(await remaining.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
			(await remaining.HistoricalOperations.CountAsync()).Should().Be(0);
		}
		finally
		{
			if (!firstDisposed)
			{
				await first.DisposeAsync();
			}

			if (second is not null)
			{
				await second.DisposeAsync();
			}
		}
	}

	[Fact]
	public async Task HistoricalAndLatestDatabases_EachGetTheTemplateForTheirOwnMigration()
	{
		PostgresFixture historical = await server.CreateIsolatedAsync(HistoricalMigration);
		PostgresFixture? latest = null;
		try
		{
			latest = await server.CreateIsolatedAsync();
			await using (ApplicationDbContext db = historical.CreateDbContext())
			{
				List<string> applied = [.. await db.Database.GetAppliedMigrationsAsync()];
				applied.Should().Equal(db.Database.GetMigrations().TakeWhile(m => string.CompareOrdinal(m, HistoricalMigration) <= 0));
				applied[^1].Should().Be(HistoricalMigration);
				(await db.Database.GetPendingMigrationsAsync()).Should().NotBeEmpty();
			}

			await using ApplicationDbContext current = latest.CreateDbContext();
			(await current.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
		}
		finally
		{
			await historical.DisposeAsync();
			if (latest is not null)
			{
				await latest.DisposeAsync();
			}
		}
	}

	[Fact]
	public async Task ConcurrentlyCreatedDatabases_AreDistinctAndIndependent()
	{
		Task<PostgresFixture>[] creations = [.. Enumerable.Range(0, 8)
			.Select(i => server.CreateIsolatedAsync(i % 2 == 0 ? null : HistoricalMigration))];
		try
		{
			PostgresFixture[] databases = await Task.WhenAll(creations);
			databases.Select(d => d.ConnectionString).Should().OnlyHaveUniqueItems();

			await using (NpgsqlConnection writer = await databases[0].OpenConnectionAsync())
			{
				await using NpgsqlCommand create = new("CREATE TABLE public.isolation_marker (id integer)", writer);
				await create.ExecuteNonQueryAsync();
			}

			foreach (PostgresFixture other in databases.Skip(1))
			{
				await using NpgsqlConnection connection = await other.OpenConnectionAsync();
				await using NpgsqlCommand marker = new("SELECT to_regclass('public.isolation_marker') IS NULL", connection);
				(await marker.ExecuteScalarAsync()).Should().Be(true);
			}
		}
		finally
		{
			// Dispose every creation that succeeded, even if a sibling failed.
			foreach (Task<PostgresFixture> creation in creations)
			{
				if (creation.IsCompletedSuccessfully)
				{
					await (await creation).DisposeAsync();
				}
			}
		}
	}

	[Fact]
	public async Task TemplateDatabases_RefuseConnections()
	{
		await using NpgsqlConnection connection = await server.OpenConnectionAsync();
		await using NpgsqlCommand templates = new(
			@"SELECT datname, datallowconn FROM pg_database WHERE datistemplate AND datname LIKE 'template\_%'",
			connection);
		Dictionary<string, bool> allowConnections = [];
		await using (NpgsqlDataReader reader = await templates.ExecuteReaderAsync())
		{
			while (await reader.ReadAsync())
			{
				allowConnections[reader.GetString(0)] = reader.GetBoolean(1);
			}
		}

		allowConnections.Should().NotBeEmpty();
		allowConnections.Should().AllSatisfy(t => t.Value.Should().BeFalse($"template {t.Key} must refuse connections"));
	}
}
