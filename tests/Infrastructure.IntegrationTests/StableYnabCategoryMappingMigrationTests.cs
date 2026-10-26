using FluentAssertions;
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
public class StableYnabCategoryMappingMigrationTests(PostgresFixture fixture)
{
	[Fact]
	public async Task UpKeepsSeparateUnmatchedNamesUnmatched_AndDownGuardsDuplicateDisplayNames()
	{
		const string previous = "20261026114208_AddCategoryAssistanceFoundation";
		const string current = "20261026134300_AddStableYnabCategoryMapping";
		const string budget = "stable-migration-budget";
		Guid firstId = Guid.NewGuid();
		Guid secondId = Guid.NewGuid();
		await using MigrationDatabase database = await MigrationDatabase.CreateAsync(fixture, previous);
		await using ApplicationDbContext db = database.CreateDbContext();
		IMigrator migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();

		await InsertMappingAsync(db, firstId, "unmatched-one", budget);
		await InsertMappingAsync(db, secondId, "unmatched-two", budget);
		await migrator.MigrateAsync(current);

		var migrated = await db.YnabCategoryMappings
			.Where(row => row.YnabBudgetId == budget)
			.OrderBy(row => row.ReceiptsCategory)
			.ToListAsync();
		migrated.Should().HaveCount(2);
		migrated.Should().OnlyContain(row => row.ReceiptsCategoryId == null
			&& row.LegacyResolution == "Unmatched"
			&& row.LegacyCandidateCategoryIds == "[]");

		await db.Database.ExecuteSqlRawAsync(
			"""UPDATE ynab."YnabCategoryMappings" SET "ReceiptsCategory" = {0}, "ReceiptsCategoryId" = {1} WHERE "Id" = {2}""",
			"duplicate-name", Guid.NewGuid(), firstId);
		await db.Database.ExecuteSqlRawAsync(
			"""UPDATE ynab."YnabCategoryMappings" SET "ReceiptsCategory" = {0}, "ReceiptsCategoryId" = {1} WHERE "Id" = {2}""",
			"duplicate-name", Guid.NewGuid(), secondId);

		Func<Task> downgrade = () => migrator.MigrateAsync(previous);
		PostgresException guard = (await downgrade.Should().ThrowAsync<PostgresException>()).Which;
		guard.MessageText.Should().Contain("duplicate legacy names exist in a budget");
		(await db.Database.GetAppliedMigrationsAsync()).Should().Contain(current);
		(await db.YnabCategoryMappings.CountAsync(row => row.YnabBudgetId == budget)).Should().Be(2);

		await db.Database.ExecuteSqlRawAsync(
			"""UPDATE ynab."YnabCategoryMappings" SET "ReceiptsCategory" = {0} WHERE "Id" = {1}""",
			"distinct-name", secondId);
		await migrator.MigrateAsync(previous);
		(await db.Database.GetAppliedMigrationsAsync()).Should().NotContain(current);
		string indexDefinition = await db.Database.SqlQueryRaw<string>(
			"""SELECT indexdef AS "Value" FROM pg_indexes WHERE schemaname = 'ynab' AND indexname = 'IX_YnabCategoryMappings_ReceiptsCategory_YnabBudgetId'""")
			.SingleAsync();
		indexDefinition.Should().Contain("UNIQUE INDEX");
	}

	private static Task<int> InsertMappingAsync(ApplicationDbContext db, Guid id, string name, string budget)
		=> db.Database.ExecuteSqlRawAsync(
			"""
            INSERT INTO ynab."YnabCategoryMappings"
                ("Id", "ReceiptsCategory", "YnabCategoryId", "YnabCategoryName",
                 "YnabCategoryGroupName", "YnabBudgetId", "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, 'remote', 'Remote', 'Group', {2}, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            """, id, name, budget);
}
