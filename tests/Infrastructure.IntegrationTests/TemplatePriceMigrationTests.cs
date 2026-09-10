using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class TemplatePriceMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	private const string MigrationId = "20260906000530_WidenItemTemplateUnitPricePrecision";

	[Fact]
	public async Task UpAndCentOnlyDown_PreserveActiveTrashAndNullPricesWithoutChangingValues()
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		IMigrator migrator = context.GetService<IMigrator>();
		string previous = Previous(context);
		await ClearAsync(context);
		await migrator.MigrateAsync(previous);
		Guid active = Guid.NewGuid(), trash = Guid.NewGuid(), missing = Guid.NewGuid();
		try
		{
			await SeedAsync(context, active, 3.45m, false);
			await SeedAsync(context, trash, 99999999999999.99m, true);
			await SeedAsync(context, missing, null, false);
			await migrator.MigrateAsync(MigrationId);
			(await ScaleAsync()).Should().Be(4);
			(await PriceAsync(active)).Should().Be(3.45m);
			(await PriceAsync(trash)).Should().Be(99999999999999.99m);
			(await PriceAsync(missing)).Should().BeNull();
			await migrator.MigrateAsync(previous);
			(await ScaleAsync()).Should().Be(2);
			(await PriceAsync(active)).Should().Be(3.45m);
			(await PriceAsync(trash)).Should().Be(99999999999999.99m);
			(await PriceAsync(missing)).Should().BeNull();
			(await context.Database.GetAppliedMigrationsAsync()).Should().NotContain(MigrationId);
		}
		finally { await ClearAsync(context); await migrator.MigrateAsync(); }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Up_RejectsOversizedHistoricalPrice_IncludingTrash_WithoutSchemaHistoryOrDataChange(bool trash)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		IMigrator migrator = context.GetService<IMigrator>();
		await ClearAsync(context);
		await migrator.MigrateAsync(Previous(context));
		Guid id = Guid.NewGuid();
		decimal price = trash ? -100000000000000m : 100000000000000m;
		await SeedAsync(context, id, price, trash);
		try
		{
			string[] before = [.. await context.Database.GetAppliedMigrationsAsync()];
			Func<Task> migrate = () => migrator.MigrateAsync(MigrationId);
			(await migrate.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("1 price(s)").And.Contain("including trash");
			(await ScaleAsync()).Should().Be(2);
			(await PriceAsync(id)).Should().Be(price);
			(await context.Database.GetAppliedMigrationsAsync()).Should().Equal(before);
			await context.Database.ExecuteSqlInterpolatedAsync($"""UPDATE library."ItemTemplates" SET "DefaultUnitPrice" = 3.45 WHERE "Id" = {id}""");
			await migrator.MigrateAsync(MigrationId);
			(await ScaleAsync()).Should().Be(4);
			(await PriceAsync(id)).Should().Be(3.45m);
		}
		finally { await ClearAsync(context); await migrator.MigrateAsync(); }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Down_RejectsSubcentPrice_IncludingTrash_WithoutRoundingOrRemovingHistory(bool trash)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		IMigrator migrator = context.GetService<IMigrator>();
		await ClearAsync(context);
		Guid id = Guid.NewGuid();
		decimal price = trash ? -7.1234m : 3.459m;
		await SeedAsync(context, id, price, trash);
		try
		{
			string[] before = [.. await context.Database.GetAppliedMigrationsAsync()];
			Func<Task> migrate = () => migrator.MigrateAsync(Previous(context));
			(await migrate.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("1 sub-cent price(s)").And.Contain("including trash");
			(await ScaleAsync()).Should().Be(4);
			(await PriceAsync(id)).Should().Be(price);
			(await context.Database.GetAppliedMigrationsAsync()).Should().Equal(before);
			await context.Database.ExecuteSqlInterpolatedAsync($"""UPDATE library."ItemTemplates" SET "DefaultUnitPrice" = 3.45 WHERE "Id" = {id}""");
			await migrator.MigrateAsync(Previous(context));
			(await ScaleAsync()).Should().Be(2);
			(await PriceAsync(id)).Should().Be(3.45m);
		}
		finally { await ClearAsync(context); await migrator.MigrateAsync(); }
	}

	private static string Previous(ApplicationDbContext context) => context.Database.GetMigrations().TakeWhile(id => id != MigrationId).Last();
	private static Task ClearAsync(ApplicationDbContext context) => context.Database.ExecuteSqlRawAsync("""TRUNCATE library."ItemTemplates" CASCADE""");
	private static Task SeedAsync(ApplicationDbContext context, Guid id, decimal? price, bool trash) => context.Database.ExecuteSqlInterpolatedAsync($"""
		INSERT INTO library."ItemTemplates" ("Id", "Name", "DefaultUnitPrice", "DefaultUnitPriceCurrency", "DeletedAt")
		VALUES ({id}, {id.ToString()}, {price}, CASE WHEN {price.HasValue} THEN 'USD' ELSE NULL END, CASE WHEN {trash} THEN CURRENT_TIMESTAMP ELSE NULL END)
		""");
	private async Task<int> ScaleAsync()
	{
		await using NpgsqlConnection connection = new(fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT numeric_scale FROM information_schema.columns WHERE table_schema='library' AND table_name='ItemTemplates' AND column_name='DefaultUnitPrice'", connection);
		return (int)(await command.ExecuteScalarAsync())!;
	}
	private async Task<decimal?> PriceAsync(Guid id)
	{
		await using NpgsqlConnection connection = new(fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("""SELECT "DefaultUnitPrice" FROM library."ItemTemplates" WHERE "Id"=@id""", connection);
		command.Parameters.AddWithValue("id", id);
		object? result = await command.ExecuteScalarAsync();
		return result is DBNull ? null : (decimal)result!;
	}
}
