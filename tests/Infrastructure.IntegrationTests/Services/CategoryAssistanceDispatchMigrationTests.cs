using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class CategoryAssistanceDispatchMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	private const string DispatchMigration = "20261026134238_AddCategoryAssistanceDispatch";

	[Fact]
	public async Task DowngradeAllowsEmptyReplayButRetainsDispatchEvidence()
	{
		await using var database = await MigrationDatabase.CreateAsync(fixture, DispatchMigration);
		await using var db = database.CreateDbContext();
		var migrator = db.GetService<IMigrator>();
		string[] applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
		int dispatchIndex = Array.IndexOf(applied, DispatchMigration);
		dispatchIndex.Should().BeGreaterThan(0);
		string predecessor = applied[dispatchIndex - 1];

		await migrator.MigrateAsync(predecessor);
		(await db.Database.GetAppliedMigrationsAsync()).Should().Equal(applied.Take(dispatchIndex));
		await migrator.MigrateAsync(DispatchMigration);

		DateOnly day = new(2026, 10, 26);
		db.CategoryAssistanceDailyUsage.Add(new() { UtcDay = day, DispatchAttempts = 1 });
		await db.SaveChangesAsync();
		await AssertRefusedAsync(db, migrator, predecessor, applied, "ledger");
		(await db.CategoryAssistanceDailyUsage.AsNoTracking().SingleAsync()).DispatchAttempts.Should().Be(1);

		db.CategoryAssistanceDailyUsage.Remove(await db.CategoryAssistanceDailyUsage.SingleAsync());
		await db.SaveChangesAsync();
		Guid attemptId = Guid.NewGuid();
		// Exercise the historical schema directly; the current EF entity has later columns.
		const string requestJson = "{}";
		await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO category_assistance."Attempts"
                ("Id", "RequestId", "RestoreGeneration", "SourceHash", "Location",
                 "Description", "ItemCodeKey", "SourceRevision", "TaxonomyRevision",
                 "Provider", "Model", "QuestionPolicyVersion", "SettingsRevision",
                 "ConnectionGeneration", "ReceiptId", "ReceiptRevision", "ItemId",
                 "ItemRevision", "RequestJson", "State", "Deferrals", "CreatedAt")
            VALUES ({attemptId}, {Guid.NewGuid()}, {Guid.NewGuid()}, 'test-source', 'test',
                    'test', 'test', {Guid.NewGuid()}, {Guid.NewGuid()}, 'jev', 'jev-1.14.0',
                    'test', {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()},
                    {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, {requestJson},
                    'indeterminate', 0, {DateTimeOffset.UtcNow})
            """);
		await AssertRefusedAsync(db, migrator, predecessor, applied, "attempt");
		(await db.CategoryAssistanceAttempts.AsNoTracking().Select(x => x.Id).SingleAsync()).Should().Be(attemptId);
	}

	private static async Task AssertRefusedAsync(ApplicationDbContext db, IMigrator migrator,
		string predecessor, string[] applied, string reason)
	{
		Func<Task> downgrade = () => migrator.MigrateAsync(predecessor);
		var refusal = (await downgrade.Should().ThrowAsync<PostgresException>(reason)).Which;
		refusal.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
		refusal.MessageText.Should().Contain("dispatch evidence is retained");
		(await db.Database.GetAppliedMigrationsAsync()).Should().Equal(applied);
	}
}
