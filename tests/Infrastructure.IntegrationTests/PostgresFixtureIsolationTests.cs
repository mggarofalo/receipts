using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class PostgresFixtureIsolationTests(PostgresFixture server)
{
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
}
