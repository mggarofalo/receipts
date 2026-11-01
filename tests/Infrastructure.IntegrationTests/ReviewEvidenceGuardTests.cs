using System.Diagnostics;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.IntegrationTests;

// RECEIPTS-1112: the evidence guard runs matching.review_item_prepared for every written purchase.
// It must trust exactly the items the current save prepared, at a cost linear in their number.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class ReviewEvidenceGuardTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	[Fact]
	public async Task PreparedPurchasesKeepTheirEvidence_AndOthersInTheSameTransactionAreDowngraded()
	{
		// Arrange
		Guid[] items = await AddItemsAsync(2);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		await using (var transaction = await db.Database.BeginTransactionAsync())
		{
			await db.PrepareReviewEvidenceAsync([items[0]], CancellationToken.None);

			// Act
			await VerifyAsync(db, items);
			await transaction.CommitAsync();
		}

		// Assert
		(await StatesAsync(db, items)).Should().Equal("Verified", "NeedsReview");
	}

	[Fact]
	public async Task OnlyTheLatestPreparationIsTrusted_AndNoneAfterTheSaveResetsIt()
	{
		// Arrange
		Guid[] items = await AddItemsAsync(2);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		await using (var transaction = await db.Database.BeginTransactionAsync())
		{
			await db.PrepareReviewEvidenceAsync([items[0]], CancellationToken.None);
			await db.PrepareReviewEvidenceAsync([items[1]], CancellationToken.None);

			// Act
			await VerifyAsync(db, [items[0]]);
			// What ApplicationDbContext runs after every save.
			await db.Database.ExecuteSqlRawAsync("SELECT set_config('receipts.review_prepared', '', true)");
			await VerifyAsync(db, [items[1]]);
			await transaction.CommitAsync();
		}

		// Assert
		(await StatesAsync(db, items)).Should().Equal("NeedsReview", "NeedsReview");
	}

	[Fact]
	public async Task GuardCostIsLinearInThePreparedItems()
	{
		// Arrange: three times the owner's restore. The joined-string search took 51 s here on 2 CPUs.
		Guid[] ids = [.. Enumerable.Range(0, 50_000).Select(_ => Guid.NewGuid())];
		await using ApplicationDbContext db = fixture.CreateDbContext();
		await using var transaction = await db.Database.BeginTransactionAsync();
		await db.PrepareReviewEvidenceAsync(ids, CancellationToken.None);
		Stopwatch clock = Stopwatch.StartNew();

		// Act
		int prepared = await db.Database.SqlQuery<int>($"""
			SELECT count(*) FILTER (WHERE matching.review_item_prepared(id))::int AS "Value" FROM unnest({ids}) AS id
			""").SingleAsync();

		// Assert
		prepared.Should().Be(ids.Length);
		clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task DownRestoresTheStringSearchAndUpReplacesItAgain()
	{
		// Arrange
		const string migration = "20261101121424_MakeReviewItemPreparedLinear";
		await using MigrationDatabase database = await MigrationDatabase.CreateAsync(fixture, migration);
		await using ApplicationDbContext db = database.CreateDbContext();
		IMigrator migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
		string[] applied = [.. await db.Database.GetAppliedMigrationsAsync()];

		// Act
		await migrator.MigrateAsync(applied[Array.IndexOf(applied, migration) - 1]);
		string down = await LanguageAsync(db);
		await migrator.MigrateAsync(migration);

		// Assert
		down.Should().Be("sql");
		(await LanguageAsync(db)).Should().Be("plpgsql");
	}

	private static Task<string> LanguageAsync(ApplicationDbContext db) =>
		db.Database.SqlQuery<string>($"""
			SELECT l.lanname AS "Value" FROM pg_proc p JOIN pg_language l ON l.oid = p.prolang
			WHERE p.oid = 'matching.review_item_prepared(uuid)'::regprocedure
			""").SingleAsync();

	private async Task<Guid[]> AddItemsAsync(int count)
	{
		Guid receiptId = Guid.NewGuid();
		Guid[] items = [.. Enumerable.Range(0, count).Select(_ => Guid.NewGuid())];
		await using ApplicationDbContext db = fixture.CreateDbContext();
		await db.Database.ExecuteSqlInterpolatedAsync($"""
			INSERT INTO receipts."Receipts" ("Id", "Location", "Date", "TaxAmount", "TaxAmountCurrency")
			VALUES ({receiptId}, 'Store', DATE '2025-01-01', 0, 'USD');
			INSERT INTO receipts."ReceiptItems" ("Id", "ReceiptId", "Description", "Quantity", "UnitPrice", "UnitPriceCurrency",
				"TotalAmount", "TotalAmountCurrency", "Category")
			SELECT id, {receiptId}, 'MILK', 1, 3, 'USD', 3, 'USD', 'Food' FROM unnest({items}) AS id;
			""");
		return items;
	}

	private static Task VerifyAsync(ApplicationDbContext db, Guid[] items) =>
		db.Database.ExecuteSqlInterpolatedAsync($"""
			UPDATE receipts."ReceiptItems" SET "NormalizationReviewState" = 'Verified', "ReviewedBy" = 'reviewer', "ReviewedAt" = now()
			WHERE "Id" = ANY ({items})
			""");

	private static async Task<string[]> StatesAsync(ApplicationDbContext db, Guid[] items)
	{
		Dictionary<Guid, string> states = await db.ReceiptItems.AsNoTracking().IgnoreQueryFilters().Where(item => items.Contains(item.Id))
			.ToDictionaryAsync(item => item.Id, item => item.NormalizationReviewState.ToString());
		return [.. items.Select(id => states[id])];
	}
}
