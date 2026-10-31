using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.IntegrationTests;

// RECEIPTS-1105. plpgsql resolves column names only when a function runs, so a dropped column that
// a trigger still assigns surfaces as a runtime failure on the next ordinary edit, not at migrate time.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class DropMatchScoreMigrationTests(PostgresFixture fixture)
{
	private const string Migration = "20261031190554_DropNormalizedDescriptionMatchScore";
	private static readonly string[] RetiredColumns = ["NormalizedDescriptionMatchScore", "PreviousScore", "SourceMatchScore"];

	[Fact]
	public async Task NoDatabaseFunctionReferencesARetiredScoreColumn()
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();

		List<string> offenders = await FunctionsReferencingRetiredColumnsAsync(db);

		offenders.Should().BeEmpty();
	}

	[Fact]
	public async Task EditingAMappedItemStillUnlinksItThroughTheCatalogGuard()
	{
		await using MigrationDatabase database = await MigrationDatabase.CreateAsync(fixture, Migration);
		await using ApplicationDbContext db = database.CreateDbContext();
		Guid receiptId = Guid.NewGuid(), canonicalId = Guid.NewGuid(), itemId = Guid.NewGuid();
		await db.Database.ExecuteSqlInterpolatedAsync($"""
			INSERT INTO receipts."Receipts" ("Id", "Location", "Date", "TaxAmount", "TaxAmountCurrency")
			VALUES ({receiptId}, 'Store', DATE '2025-01-01', 0, 'USD');
			INSERT INTO matching."NormalizedDescriptions" ("Id", "CanonicalName", "Status", "CreatedAt")
			VALUES ({canonicalId}, 'Mapped milk', 'Active', now());
			INSERT INTO receipts."ReceiptItems" ("Id", "ReceiptId", "Description", "Quantity", "UnitPrice", "UnitPriceCurrency",
				"TotalAmount", "TotalAmountCurrency", "Category", "NormalizedDescriptionId", "NormalizationOrigin")
			VALUES ({itemId}, {receiptId}, 'MILK', 1, 3, 'USD', 3, 'USD', 'Food', {canonicalId}, 'mapped');
			""");

		await db.Database.ExecuteSqlInterpolatedAsync(
			$"""UPDATE receipts."ReceiptItems" SET "Description" = 'MILK 2%' WHERE "Id" = {itemId}""");

		string origin = await db.Database.SqlQuery<string>(
			$"""SELECT "NormalizationOrigin" AS "Value" FROM receipts."ReceiptItems" WHERE "Id" = {itemId}""").SingleAsync();
		bool linked = await db.Database.SqlQuery<bool>(
			$"""SELECT "NormalizedDescriptionId" IS NOT NULL AS "Value" FROM receipts."ReceiptItems" WHERE "Id" = {itemId}""").SingleAsync();
		origin.Should().Be("unassigned");
		linked.Should().BeFalse();
	}

	[Fact]
	public async Task DownRestoresTheColumnsAndTheGuardAndUpRemovesThemAgain()
	{
		await using MigrationDatabase database = await MigrationDatabase.CreateAsync(fixture, Migration);
		await using ApplicationDbContext db = database.CreateDbContext();
		IMigrator migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
		string[] applied = [.. await db.Database.GetAppliedMigrationsAsync()];
		string previous = applied[Array.IndexOf(applied, Migration) - 1];

		await migrator.MigrateAsync(previous);
		(await ColumnCountAsync(db)).Should().Be(3);
		(await FunctionsReferencingRetiredColumnsAsync(db)).Should().Contain("guard_catalog_assignment");

		await migrator.MigrateAsync(Migration);
		(await ColumnCountAsync(db)).Should().Be(0);
		(await FunctionsReferencingRetiredColumnsAsync(db)).Should().BeEmpty();
	}

	private static Task<List<string>> FunctionsReferencingRetiredColumnsAsync(ApplicationDbContext db) =>
		db.Database.SqlQuery<string>($"""
			SELECT p.proname AS "Value" FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
			WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
			AND p.prosrc ~ ANY ({RetiredColumns.Select(column => $"\"{column}\"").ToArray()})
			""").ToListAsync();

	private static Task<int> ColumnCountAsync(ApplicationDbContext db) =>
		db.Database.SqlQuery<int>($"""
			SELECT count(*)::int AS "Value" FROM information_schema.columns
			WHERE column_name = ANY ({RetiredColumns})
			""").SingleAsync();
}
