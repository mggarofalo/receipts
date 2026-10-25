using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Infrastructure.IntegrationTests.Fixtures;

// Historical replay starts on an empty database: newer migrations may deliberately
// refuse downgrade to preserve immutable history.
internal sealed class MigrationDatabase(NpgsqlDataSource dataSource, string adminConnectionString, string name) : IAsyncDisposable
{
	private readonly NpgsqlDataSource _dataSource = dataSource;

	public static async Task<MigrationDatabase> CreateAsync(PostgresFixture fixture, string baseline)
	{
		string name = $"migration_{Guid.NewGuid():N}";
		await using (NpgsqlConnection admin = new(fixture.ConnectionString))
		{
			await admin.OpenAsync();
			await using NpgsqlCommand create = new($"CREATE DATABASE \"{name}\"", admin);
			await create.ExecuteNonQueryAsync();
		}

		NpgsqlConnectionStringBuilder connection = new(fixture.ConnectionString)
		{
			Database = name,
			SearchPath = "public,receipts,library,matching,ynab,audit,identity",
		};
		NpgsqlDataSourceBuilder builder = new(connection.ConnectionString);
		builder.UseVector();
		MigrationDatabase database = new(builder.Build(), fixture.ConnectionString, name);
		try
		{
			await using ApplicationDbContext context = database.CreateDbContext();
			await context.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(baseline);
			await using NpgsqlConnection reload = await database._dataSource.OpenConnectionAsync();
			await reload.ReloadTypesAsync();
			return database;
		}
		catch
		{
			await database.DisposeAsync();
			throw;
		}
	}

	public ValueTask<NpgsqlConnection> OpenConnectionAsync() => _dataSource.OpenConnectionAsync();

	public ApplicationDbContext CreateDbContext()
	{
		DbContextOptionsBuilder<ApplicationDbContext> builder = new();
		builder.UseNpgsql(_dataSource, options =>
		{
			options.UseVector();
			options.UsePublicMigrationsHistory();
		});
		return new(builder.Options);
	}

	public async ValueTask DisposeAsync()
	{
		await _dataSource.DisposeAsync();
		await using NpgsqlConnection admin = new(adminConnectionString);
		await admin.OpenAsync();
		await using NpgsqlCommand drop = new($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
		await drop.ExecuteNonQueryAsync();
	}
}
