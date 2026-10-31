using System.Text.Json;
using FluentAssertions;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class OrganizerMigrationOrderTests(PostgresFixture fixture)
{
	private const string PublishedMain = "20261029014850_AddCategoryAssistanceSecondDispatch";

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PublishedMain_UpgradeAndDowngrade_RoundTripsSchemaAndDispatch(bool useScript)
	{
		await using var database = await MigrationDatabase.CreateAsync(fixture, PublishedMain);
		await using var db = database.CreateDbContext();
		var migrator = db.GetService<IMigrator>();
		string[] mainHistory = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
		string[] mainSchema = await Schema(db);
		Guid attemptId = Guid.NewGuid();
		Guid dispatchToken = Guid.NewGuid();
		var seeded = new CategoryAssistanceAttemptEntity
		{
			Id = attemptId,
			RequestId = Guid.NewGuid(),
			RequestScope = Guid.NewGuid(),
			Location = "Store",
			Description = "Item",
			ItemCodeKey = "code",
			State = "suggested",
			DispatchToken = dispatchToken,
			FirstStageInputTokens = 42,
			FirstStageOutputTokens = 11
		};
		// This schema predates later attempt columns; populate only the columns it has.
		await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO category_assistance."Attempts"
            SELECT * FROM json_populate_record(NULL::category_assistance."Attempts", {JsonSerializer.Serialize(seeded)}::json)
            """);

		if (useScript)
		{
			await db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(PublishedMain));
		}
		else
		{
			await migrator.MigrateAsync();
		}

		var attempt = await db.CategoryAssistanceAttempts.SingleAsync();
		attempt.DispatchToken.Should().Be(dispatchToken);
		attempt.FirstStageInputTokens.Should().Be(42);
		attempt.FirstStageOutputTokens.Should().Be(11);
		string[] upgradedSchema = await Schema(db);
		var assembly = db.GetService<IMigrationsAssembly>();
		var latest = assembly.CreateMigration(assembly.Migrations.Last().Value, db.Database.ProviderName!);
		var target = db.GetService<IModelRuntimeInitializer>().Initialize(latest.TargetModel, designTime: true);
		db.GetService<IMigrationsModelDiffer>().GetDifferences(target.GetRelationalModel(),
			db.GetService<IDesignTimeModel>().Model.GetRelationalModel()).Should().BeEmpty();
		db.Database.HasPendingModelChanges().Should().BeFalse();

		if (useScript)
		{
			await db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(assembly.Migrations.Last().Key, PublishedMain));
		}
		else
		{
			await migrator.MigrateAsync(PublishedMain);
		}

		(await db.Database.GetAppliedMigrationsAsync()).Should().Equal(mainHistory);
		(await Schema(db)).Should().Equal(mainSchema);
		await migrator.MigrateAsync();
		(await Schema(db)).Should().Equal(upgradedSchema);
		(await db.CategoryAssistanceAttempts.AsNoTracking().SingleAsync()).DispatchToken.Should().Be(dispatchToken);
	}

	[Fact]
	public async Task EveryDraftTargetModel_DescribesItsActualSchema()
	{
		await using var database = await MigrationDatabase.CreateAsync(fixture, PublishedMain);
		await using var db = database.CreateDbContext();
		var assembly = db.GetService<IMigrationsAssembly>();
		foreach (var entry in assembly.Migrations.Where(x => string.CompareOrdinal(x.Key, PublishedMain) >= 0))
		{
			await db.GetService<IMigrator>().MigrateAsync(entry.Key);
			var migration = assembly.CreateMigration(entry.Value, db.Database.ProviderName!);
			var target = db.GetService<IModelRuntimeInitializer>().Initialize(migration.TargetModel, designTime: true);
			string[] expected = target.GetRelationalModel().Tables
				.SelectMany(table => table.Columns.Select(column => $"{table.Schema ?? "public"}.{table.Name}.{column.Name}:{(column.IsNullable ? "YES" : "NO")}"))
				.Order(StringComparer.Ordinal).ToArray();
			string[] actual = await db.Database.SqlQueryRaw<string>("""
                            SELECT concat(table_schema, '.', table_name, '.', column_name, ':', is_nullable) AS "Value"
                            FROM information_schema.columns
                            WHERE table_schema NOT IN ('pg_catalog', 'information_schema') AND table_name <> '__EFMigrationsHistory'
                            """).ToArrayAsync();
			// Handwritten migrations also maintain tables outside EF's model.
			var modeledTables = target.GetRelationalModel().Tables
				.Select(table => $"{table.Schema ?? "public"}.{table.Name}").ToHashSet();
			actual.Where(column => modeledTables.Contains(column[..column.LastIndexOf('.')]))
				.Order(StringComparer.Ordinal).Should().Equal(expected, entry.Key);
		}
	}

	private static async Task<string[]> Schema(ApplicationDbContext db) =>
		await db.Database.SqlQueryRaw<string>("""
                SELECT concat(table_schema, '.', table_name, '.', column_name, ':', data_type, ':', is_nullable, ':', column_default) AS "Value"
                FROM information_schema.columns
                WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
                ORDER BY 1
                """).ToArrayAsync();
}
