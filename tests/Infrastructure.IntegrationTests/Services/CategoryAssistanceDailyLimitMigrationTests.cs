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

	private const string DailyLimitMigration = "20261027110503_RaiseCategoryAssistanceDailyLimit";

	private sealed record SettingsRow(int DailyLimit, int PendingLimit, string? Provider, Guid Revision, Guid RestoreGeneration);

	// These schemas predate later Settings columns: read and write only the columns they define.
	private static Task<SettingsRow> ReadSettingsAsync(ApplicationDbContext db) =>
		db.CategoryAssistanceSettings.AsNoTracking()
			.Select(x => new SettingsRow(x.DailyLimit, x.PendingLimit, x.Provider, x.Revision, x.RestoreGeneration)).SingleAsync();

	[Fact]
	public async Task DowngradeRefusesHighLimitWithoutChangingSettingsOrMigrationHistory()
	{
		await using var database = await MigrationDatabase.CreateAsync(fixture, "0");
		await using var db = database.CreateDbContext();
		var migrator = db.GetService<IMigrator>();
		// Start at the migration under test: EF commits each later downgrade separately.
		await migrator.MigrateAsync(DailyLimitMigration);
		Guid revision = Guid.NewGuid();
		await db.CategoryAssistanceSettings.ExecuteUpdateAsync(s => s
			.SetProperty(x => x.DailyLimit, 5000).SetProperty(x => x.Revision, revision));
		string[] applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();

		Func<Task> downgrade = () => migrator.MigrateAsync(Predecessor);
		(await downgrade.Should().ThrowAsync<PostgresException>()).Which.MessageText
			.Should().Contain("100 or less before downgrading");

		var unchanged = await ReadSettingsAsync(db);
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
		// Start at the migration under test: EF commits each later downgrade separately.
		await migrator.MigrateAsync(DailyLimitMigration);
		var settings = await ReadSettingsAsync(db);
		settings.DailyLimit.Should().Be(100);
		Guid revision = settings.Revision;
		if (configured)
		{
			revision = Guid.NewGuid();
			await db.CategoryAssistanceSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, revision));
		}

		await migrator.MigrateAsync(Predecessor);

		var downgraded = await ReadSettingsAsync(db);
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
		var settings = await ReadSettingsAsync(db);
		settings.DailyLimit.Should().Be(10);
		settings = configuration switch
		{
			"explicit ten" => settings with { Revision = Guid.NewGuid() },
			"configured twenty" => settings with { DailyLimit = 20, Revision = Guid.NewGuid() },
			"configured one hundred" => settings with { DailyLimit = 100, Revision = Guid.NewGuid() },
			"changed pending" => settings with { PendingLimit = 4 },
			"changed provider" => settings with { Provider = "jev" },
			"changed restore generation" => settings with { RestoreGeneration = Guid.NewGuid() },
			_ => settings
		};
		await db.CategoryAssistanceSettings.ExecuteUpdateAsync(s => s
			.SetProperty(x => x.DailyLimit, settings.DailyLimit).SetProperty(x => x.PendingLimit, settings.PendingLimit)
			.SetProperty(x => x.Provider, settings.Provider).SetProperty(x => x.Revision, settings.Revision)
			.SetProperty(x => x.RestoreGeneration, settings.RestoreGeneration));
		Guid revision = settings.Revision;
		Guid restoreGeneration = settings.RestoreGeneration;
		await db.GetService<IMigrator>().MigrateAsync();
		var upgraded = await db.CategoryAssistanceSettings.AsNoTracking().SingleAsync();
		upgraded.DailyLimit.Should().Be(expected);
		upgraded.Revision.Should().Be(revision);
		upgraded.RestoreGeneration.Should().Be(restoreGeneration);
		upgraded.PendingLimit.Should().Be(settings.PendingLimit);
		upgraded.Provider.Should().Be(settings.Provider);
		(await db.CategoryAssistanceSettingsAudits.CountAsync()).Should().Be(0);
	}
}
