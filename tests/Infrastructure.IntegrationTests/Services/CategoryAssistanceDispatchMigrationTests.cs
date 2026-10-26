using FluentAssertions;
using Infrastructure.Entities.CategoryAssistance;
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
		db.CategoryAssistanceAttempts.Add(new CategoryAssistanceAttemptEntity
		{
			Id = attemptId,
			RequestId = Guid.NewGuid(),
			RestoreGeneration = Guid.NewGuid(),
			SourceHash = "test-source",
			Location = "test",
			Description = "test",
			ItemCodeKey = "test",
			SourceRevision = Guid.NewGuid(),
			TaxonomyRevision = Guid.NewGuid(),
			Provider = "jev",
			Model = "jev-1.14.0",
			QuestionPolicyVersion = "test",
			SettingsRevision = Guid.NewGuid(),
			ConnectionGeneration = Guid.NewGuid(),
			ReceiptId = Guid.NewGuid(),
			ReceiptRevision = Guid.NewGuid(),
			ItemId = Guid.NewGuid(),
			ItemRevision = Guid.NewGuid(),
			RequestJson = "{}",
			State = "indeterminate",
			CreatedAt = DateTimeOffset.UtcNow
		});
		await db.SaveChangesAsync();
		await AssertRefusedAsync(db, migrator, predecessor, applied, "attempt");
		(await db.CategoryAssistanceAttempts.AsNoTracking().SingleAsync()).Id.Should().Be(attemptId);
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
