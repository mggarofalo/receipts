using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class CategoryAssistanceDailyLimitMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	private const string Foundation = "20261026114208_AddCategoryAssistanceFoundation";
	private const string Predecessor = "20261026173332_AddHistoricalCategoryAssistanceRequests";

	[Fact]
	public async Task DowngradeRefusesHighLimitWithoutChangingSettingsOrMigrationHistory()
	{
		await using var database = await MigrationDatabase.CreateAsync(fixture, "0");
		await using var db = database.CreateDbContext();
		var migrator = db.GetService<IMigrator>();
		// Start at the guarded migration: EF can commit later migration rollbacks
		// before reaching this migration's refusal.
		await migrator.MigrateAsync("20261027110503_RaiseCategoryAssistanceDailyLimit");
		var settings = await db.CategoryAssistanceSettings.SingleAsync();
		settings.DailyLimit = 5000;
		settings.Revision = Guid.NewGuid();
		await db.SaveChangesAsync();
		Guid revision = settings.Revision;
		string[] applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();

		Func<Task> downgrade = () => migrator.MigrateAsync(Predecessor);
		(await downgrade.Should().ThrowAsync<PostgresException>()).Which.MessageText
			.Should().Contain("100 or less before downgrading");

		db.ChangeTracker.Clear();
		var unchanged = await db.CategoryAssistanceSettings.AsNoTracking().SingleAsync();
		unchanged.DailyLimit.Should().Be(5000);
		unchanged.Revision.Should().Be(revision);
		(await db.Database.GetAppliedMigrationsAsync()).Should().Equal(applied);
	}

	[Theory]
	[InlineData(false, 10)]
	[InlineData(true, 100)]
	public async Task DowngradeRestoresOnlyUntouchedSeedAndReinstatesOldConstraint(bool configured, int expected)
	{
		await using var database = await MigrationDatabase.CreateAsync(fixture, "0");
		await using var db = database.CreateDbContext();
		var migrator = db.GetService<IMigrator>();
		await migrator.MigrateAsync();
		var settings = await db.CategoryAssistanceSettings.SingleAsync();
		settings.DailyLimit.Should().Be(100);
		if (configured)
		{
			settings.Revision = Guid.NewGuid();
			await db.SaveChangesAsync();
		}
		Guid revision = settings.Revision;

		await migrator.MigrateAsync(Predecessor);

		db.ChangeTracker.Clear();
		var downgraded = await db.CategoryAssistanceSettings.AsNoTracking().SingleAsync();
		downgraded.DailyLimit.Should().Be(expected);
		downgraded.Revision.Should().Be(revision);
		(await db.Database.GetAppliedMigrationsAsync()).Last().Should().Be(Predecessor);
		Func<Task> invalid = () => db.Database.ExecuteSqlRawAsync("""
			UPDATE category_assistance."Settings" SET "DailyLimit" = 101 WHERE "Id" = 1
			""");
		(await invalid.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
	}

	[Fact]
	public async Task FreshInstallationHasOneHundredRequestsAndAcceptsFiveThousand()
	{
		await using var database = await MigrationDatabase.CreateAsync(fixture, "0");
		await using var db = database.CreateDbContext();
		await db.GetService<IMigrator>().MigrateAsync();
		(await db.CategoryAssistanceSettings.AsNoTracking().SingleAsync()).DailyLimit.Should().Be(100);
		await db.Database.ExecuteSqlRawAsync("""
			UPDATE category_assistance."Settings" SET "DailyLimit" = 5000 WHERE "Id" = 1
			""");
		(await db.CategoryAssistanceSettings.AsNoTracking().SingleAsync()).DailyLimit.Should().Be(5000);
		Func<Task> invalid = () => db.Database.ExecuteSqlRawAsync("""
			UPDATE category_assistance."Settings" SET "DailyLimit" = 5001 WHERE "Id" = 1
			""");
		(await invalid.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
	}

	[Theory]
	[InlineData("untouched", 100)]
	[InlineData("explicit ten", 10)]
	[InlineData("configured twenty", 20)]
	[InlineData("configured one hundred", 100)]
	[InlineData("changed pending", 10)]
	[InlineData("changed provider", 10)]
	[InlineData("changed restore generation", 10)]
	public async Task UpgradeChangesOnlyTheExactUntouchedSeed(string configuration, int expected)
	{
		await using var database = await MigrationDatabase.CreateAsync(fixture, Foundation);
		await using var db = database.CreateDbContext();
		var settings = await db.CategoryAssistanceSettings.SingleAsync();
		settings.DailyLimit.Should().Be(10);
		switch (configuration)
		{
			case "explicit ten": settings.Revision = Guid.NewGuid(); break;
			case "configured twenty": settings.DailyLimit = 20; settings.Revision = Guid.NewGuid(); break;
			case "configured one hundred": settings.DailyLimit = 100; settings.Revision = Guid.NewGuid(); break;
			case "changed pending": settings.PendingLimit = 4; break;
			case "changed provider": settings.Provider = "jev"; break;
			case "changed restore generation": settings.RestoreGeneration = Guid.NewGuid(); break;
		}
		await db.SaveChangesAsync();
		Guid revision = settings.Revision;
		Guid restoreGeneration = settings.RestoreGeneration;
		await db.GetService<IMigrator>().MigrateAsync();
		db.ChangeTracker.Clear();
		var upgraded = await db.CategoryAssistanceSettings.AsNoTracking().SingleAsync();
		upgraded.DailyLimit.Should().Be(expected);
		upgraded.Revision.Should().Be(revision);
		upgraded.RestoreGeneration.Should().Be(restoreGeneration);
		upgraded.PendingLimit.Should().Be(settings.PendingLimit);
		upgraded.Provider.Should().Be(settings.Provider);
		(await db.CategoryAssistanceSettingsAudits.CountAsync()).Should().Be(0);
	}
}
