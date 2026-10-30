using System.Text.Json;
using FluentAssertions;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class DurableReviewPreferenceMigrationTests(PostgresFixture fixture)
{
	private const string Previous = "20261029015215_AddHistoricalMappingSweep";

	[Fact]
	public async Task BackfillsSourceAndRefusesDowngradeForDetachedPreferencesOrOperations()
	{
		await using var database = await MigrationDatabase.CreateAsync(fixture, Previous);
		await using var db = database.CreateDbContext();
		var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
		var attempt = new CategoryAssistanceAttemptEntity
		{
			Id = Guid.NewGuid(),
			RequestId = Guid.NewGuid(),
			RequestScope = Guid.NewGuid(),
			Location = "Store",
			Description = "Description",
			ItemCodeKey = "code",
			State = "suggested"
		};
		// This schema predates later attempt columns; populate only the columns it has.
		await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO category_assistance."Attempts"
            SELECT * FROM json_populate_record(NULL::category_assistance."Attempts", {JsonSerializer.Serialize(attempt)}::json)
            """);
		var revision = Guid.NewGuid();
		var operation = Guid.NewGuid();
		await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO category_assistance."ReviewPreferences" ("Actor", "AttemptId", "State", "Revision", "ExcludedItemIdsJson")
            VALUES ('actor', {attempt.Id}, 'active', {revision}, '[]');
            INSERT INTO category_assistance."ReviewOperations" ("Actor", "OperationId", "AttemptId", "RequestHash", "ResultJson", "RecordedAt")
            VALUES ('actor', {operation}, {attempt.Id}, 'hash', 'result', now());
            """);
		await migrator.MigrateAsync();
		db.ChangeTracker.Clear();
		var row = await db.CategoryAssistanceReviewPreferences.SingleAsync();
		(row.Location, row.Description, row.ItemCodeKey, row.Revision).Should().Be((attempt.Location, attempt.Description, attempt.ItemCodeKey, revision));
		(await db.CategoryAssistanceReviewOperations.SingleAsync()).OperationId.Should().Be(operation);
		Func<Task> downgrade = () => migrator.MigrateAsync(Previous);
		await db.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"ReviewPreferences\" SET \"Description\" = 'changed'");
		await downgrade.Should().ThrowAsync<PostgresException>().Where(x => x.MessageText.Contains("Cannot downgrade"));
		await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE category_assistance.\"ReviewPreferences\" SET \"Description\" = {attempt.Description}");
		await downgrade();
		await migrator.MigrateAsync();
		await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM category_assistance.\"Attempts\" WHERE \"Id\" = {attempt.Id}");
		await downgrade.Should().ThrowAsync<PostgresException>().Where(x => x.MessageText.Contains("Cannot downgrade"));
		await db.Database.ExecuteSqlRawAsync("DELETE FROM category_assistance.\"ReviewPreferences\"");
		await downgrade.Should().ThrowAsync<PostgresException>().Where(x => x.MessageText.Contains("Cannot downgrade"));
		(await db.CategoryAssistanceReviewOperations.CountAsync()).Should().Be(1);
	}
}

