using Application.Models.Reports;
using FluentAssertions;
using Infrastructure.Configurations;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

// Postgres-only coverage for RECEIPTS-839's GetHealthSummaryAsync. Two of its three counts are
// exactly the shapes this suite exists to guard (see OutOfBalanceReportTests / RECEIPTS-791):
// the out-of-balance count aggregates correlated `?? 0m` subqueries inside a WHERE, and the
// duplicate-group count is a GROUP BY ... HAVING COUNT(*) > 1 wrapped in an outer COUNT. The
// InMemory unit suite client-evaluates both, so it can never catch a translation regression;
// only a real relational provider throws.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class ReportServiceHealthSummaryTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	[Fact]
	public async Task UnclassifiedReport_AndHealthCountAgreeAcrossCurrentEvidenceAndBoundedPages()
	{
		Guid receiptId = Guid.NewGuid(), trashedReceiptId = Guid.NewGuid();
		Guid revisionId = Guid.NewGuid(), categoryId = Guid.NewGuid();
		Guid missingId = Guid.NewGuid(), clearedId = Guid.NewGuid();
		Guid unresolvedId = Guid.NewGuid(), evidenceId = Guid.NewGuid(), classifiedId = Guid.NewGuid();
		Guid deletedItemId = Guid.NewGuid(), deletedReceiptItemId = Guid.NewGuid();

		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			await using var transaction = await seed.Database.BeginTransactionAsync();
			seed.TaxonomyIdentities.Add(new TaxonomyIdentityEntity { Id = categoryId });
			seed.TaxonomyRevisions.Add(new TaxonomyRevisionEntity
			{
				Id = revisionId,
				Sequence = 1,
				PreviousRevisionId = TaxonomyConfiguration.GenesisId
			});
			seed.TaxonomyNodes.Add(new TaxonomyNodeEntity
			{
				RevisionId = revisionId,
				CategoryId = categoryId,
				Name = "Food"
			});
			(await seed.TaxonomyHeads.SingleAsync()).RevisionId = revisionId;
			AddReceipt(seed, receiptId, new(2025, 7, 5), "Report Store", 0m);
			AddReceipt(seed, trashedReceiptId, new(2025, 7, 6), "Deleted Store", 0m);
			void Add(Guid id, Guid receipt, string description, string oldLabel = "Food", bool deleted = false)
			{
				seed.ReceiptItems.Add(new ReceiptItemEntity
				{
					Id = id,
					ReceiptId = receipt,
					Description = description,
					Quantity = 1,
					UnitPrice = 1m,
					TotalAmount = 1m,
					Category = oldLabel,
					DeletedAt = deleted ? DateTimeOffset.UtcNow : null
				});
			}
			Add(missingId, receiptId, "A missing", "Legacy classified label");
			Add(clearedId, receiptId, "B cleared");
			Add(unresolvedId, receiptId, "C unresolved");
			Add(evidenceId, receiptId, "D evidence");
			Add(classifiedId, receiptId, "E classified", "Uncategorized");
			Add(deletedItemId, receiptId, "F deleted item", deleted: true);
			Add(deletedReceiptItemId, trashedReceiptId, "G deleted receipt");
			Guid Assign(Guid itemId, Guid? category, string? legacyResolution = null)
			{
				Guid assignmentId = Guid.NewGuid();
				seed.Classifications.Add(new PurchaseClassificationEntity
				{
					Id = assignmentId,
					ReceiptItemId = itemId,
					Sequence = 1,
					CategoryId = category,
					TaxonomyRevisionId = revisionId,
					Actor = "report-test",
					Source = "manual",
					Reason = "fixture",
					OperationId = Guid.NewGuid(),
					LegacyResolution = legacyResolution
				});
				seed.CurrentClassifications.Add(new CurrentClassificationEntity
				{ ReceiptItemId = itemId, AssignmentId = assignmentId });
				return assignmentId;
			}
			Assign(clearedId, null);
			Assign(unresolvedId, categoryId, "unresolved_path");
			Guid evidenceAssignment = Assign(evidenceId, categoryId);
			seed.LegacyMigrationItems.Add(new LegacyMigrationItemEntity
			{
				Actor = "report-test",
				OperationId = Guid.NewGuid(),
				ReceiptItemId = evidenceId,
				AssignmentId = evidenceAssignment,
				Resolution = "Unresolved"
			});
			Assign(classifiedId, categoryId);
			Assign(deletedItemId, null);
			Assign(deletedReceiptItemId, null);
			await seed.SaveChangesAsync();
			await seed.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"Receipts\" SET \"DeletedAt\" = {DateTimeOffset.UtcNow} WHERE \"Id\" = {trashedReceiptId}");
			await transaction.CommitAsync();
		}

		ReportService report = new(new FixtureDbContextFactory(fixture));
		UncategorizedItemsResult first = await report.GetUncategorizedItemsAsync("description", "asc", 1, 2, CancellationToken.None);
		UncategorizedItemsResult second = await report.GetUncategorizedItemsAsync("description", "asc", 2, 2, CancellationToken.None);
		ReportsHealthSummaryResult health = await report.GetHealthSummaryAsync(CancellationToken.None);

		first.TotalCount.Should().Be(4);
		second.TotalCount.Should().Be(4);
		health.UncategorizedItemCount.Should().Be(first.TotalCount);
		first.Items.Select(item => item.Id).Should().Equal(missingId, clearedId);
		second.Items.Select(item => item.Id).Should().Equal(unresolvedId, evidenceId);
		first.Items.Select(item => item.ClassificationState).Should().Equal("missing_assignment", "unclassified");
		second.Items.Select(item => item.ClassificationState)
				.Should().Equal("unresolved_legacy", "unresolved_legacy");
		first.Items[0].AssignmentId.Should().BeNull();
		first.Items[1].AssignmentId.Should().NotBeNull();
		second.Items[0].CategoryId.Should().Be(categoryId);
		first.Items.Concat(second.Items).Select(item => item.Id)
				.Should().NotContain(new[] { classifiedId, deletedItemId, deletedReceiptItemId });
	}

	[Fact]
	public async Task GetHealthSummaryAsync_TracksPairCompletenessWithoutLoadingReceiptTotals()
	{
		Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
		await SeedAsync(Guid.NewGuid(), Guid.NewGuid(), context =>
		{
			foreach (Guid id in ids)
			{
				AddReceipt(context, id, new(2025, 6, 1), "Same Store", 0m);
			}
		});
		FixtureDbContextFactory factory = new(fixture);
		DuplicateWorkflowFixture workflow = new(factory);
		ReportService report = new(factory);
		await workflow.AcceptDuplicateGroupAsync([ids[0], ids[1]], CancellationToken.None);
		(await report.GetHealthSummaryAsync(CancellationToken.None)).DuplicateGroupCount.Should().Be(1);
		await workflow.AcceptDuplicateGroupAsync([.. ids], CancellationToken.None);
		(await report.GetHealthSummaryAsync(CancellationToken.None)).DuplicateGroupCount.Should().Be(0);
		await workflow.UnacceptDuplicateGroupAsync([ids[0], ids[1]], CancellationToken.None);
		(await report.GetHealthSummaryAsync(CancellationToken.None)).DuplicateGroupCount.Should().Be(1);
	}

	[Fact]
	public async Task GetHealthSummaryAsync_TranslatesEveryCountToSql_AndReturnsCorrectTotals()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		Guid revisionId = Guid.NewGuid();
		Guid categoryId = Guid.NewGuid();

		Guid unbalanced = Guid.NewGuid();        // items 30, transaction 100 => difference -70
		Guid balanced = Guid.NewGuid();          // items 50, transaction 50  => excluded
		Guid uncategorizedHost = Guid.NewGuid(); // items 1 + 2, transaction 3 => balanced, 2 uncategorized

		DateOnly duplicateDate = new(2025, 6, 1);
		DateOnly otherDate = new(2025, 6, 2);

		await SeedAsync(accountId, cardId, seed =>
		{
			AddTaxonomyCategory(seed, revisionId, categoryId);
			AddReceipt(seed, unbalanced, new DateOnly(2025, 1, 1), "Solo Store A", tax: 0m);
			Classify(seed, AddItem(seed, unbalanced, 30m, "Uncategorized"), revisionId, categoryId);
			AddTransaction(seed, unbalanced, accountId, cardId, 100m);

			AddReceipt(seed, balanced, new DateOnly(2025, 1, 2), "Solo Store B", tax: 0m);
			Classify(seed, AddItem(seed, balanced, 50m, "Food"), revisionId, categoryId);
			AddTransaction(seed, balanced, accountId, cardId, 50m);

			AddReceipt(seed, uncategorizedHost, new DateOnly(2025, 1, 3), "Solo Store C", tax: 0m);
			AddItem(seed, uncategorizedHost, 1m, "Food");
			AddItem(seed, uncategorizedHost, 2m, "Food");
			AddTransaction(seed, uncategorizedHost, accountId, cardId, 3m);

			// Three receipts sharing date + location collapse into ONE duplicate group...
			for (int i = 0; i < 3; i++)
			{
				AddReceipt(seed, Guid.NewGuid(), duplicateDate, "Duplicate Store", tax: 0m);
			}

			// ...and two on a different date + location make a second.
			for (int i = 0; i < 2; i++)
			{
				AddReceipt(seed, Guid.NewGuid(), otherDate, "Another Store", tax: 0m);
			}

			// A lone receipt on its own date + location is not a group. These duplicate receipts
			// carry no items and no transactions, so 0 == 0 keeps them out of the balance count.
			AddReceipt(seed, Guid.NewGuid(), new DateOnly(2025, 6, 3), "Lone Store", tax: 0m);
		});

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act — if any of the three counts failed to translate, EF throws here rather than
		// silently evaluating on the client (the RECEIPTS-791 failure mode).
		ReportsHealthSummaryResult result = await service.GetHealthSummaryAsync(CancellationToken.None);

		// Assert
		result.OutOfBalanceCount.Should().Be(1);

		// Groups, not receipts: the 3 + 2 duplicate receipts collapse into 2 groups.
		result.DuplicateGroupCount.Should().Be(2);

		result.UncategorizedItemCount.Should().Be(2);
	}

	[Fact]
	public async Task GetHealthSummaryAsync_ReturnsZeros_WhenNothingNeedsAttention()
	{
		// Arrange — one balanced receipt with a current classification and a unique date + location.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		Guid revisionId = Guid.NewGuid();
		Guid categoryId = Guid.NewGuid();

		await SeedAsync(accountId, cardId, seed =>
		{
			AddTaxonomyCategory(seed, revisionId, categoryId);
			AddReceipt(seed, receiptId, new DateOnly(2025, 7, 4), "Only Store", tax: 2m);
			Classify(seed, AddItem(seed, receiptId, 8m, "Uncategorized"), revisionId, categoryId);
			AddTransaction(seed, receiptId, accountId, cardId, 10m);
		});

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act
		ReportsHealthSummaryResult result = await service.GetHealthSummaryAsync(CancellationToken.None);

		// Assert
		result.OutOfBalanceCount.Should().Be(0);
		result.DuplicateGroupCount.Should().Be(0);
		result.UncategorizedItemCount.Should().Be(0);
	}

	private async Task SeedAsync(Guid accountId, Guid cardId, Action<ApplicationDbContext> seed)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();

		// The summary counts across ALL receipts globally, so isolate from other tests in the collection.

		// Transactions need valid Account + Card FKs (both Restrict, NOT NULL).
		AccountEntity account = AccountEntityGenerator.Generate();
		account.Id = accountId;
		CardEntity card = CardEntityGenerator.Generate();
		card.Id = cardId;
		card.AccountId = accountId;
		context.Accounts.Add(account);
		context.Cards.Add(card);
		await context.SaveChangesAsync();

		seed(context);
		await context.SaveChangesAsync();
	}

	private static void AddReceipt(ApplicationDbContext context, Guid receiptId, DateOnly date, string location, decimal tax)
	{
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.Id = receiptId;
		receipt.Date = date;
		receipt.Location = location;
		receipt.TaxAmount = tax;
		context.Receipts.Add(receipt);
	}

	private static ReceiptItemEntity AddItem(ApplicationDbContext context, Guid receiptId, decimal totalAmount, string category)
	{
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receiptId);
		item.Quantity = 1m;
		item.UnitPrice = totalAmount;
		item.TotalAmount = totalAmount;
		item.Category = category;
		context.ReceiptItems.Add(item);
		return item;
	}

	private static void AddTaxonomyCategory(ApplicationDbContext context, Guid revisionId, Guid categoryId)
	{
		context.TaxonomyIdentities.Add(new TaxonomyIdentityEntity { Id = categoryId });
		context.TaxonomyRevisions.Add(new TaxonomyRevisionEntity
		{
			Id = revisionId,
			Sequence = 1,
			PreviousRevisionId = TaxonomyConfiguration.GenesisId
		});
		context.TaxonomyNodes.Add(new TaxonomyNodeEntity
		{
			RevisionId = revisionId,
			CategoryId = categoryId,
			Name = "Food"
		});
		context.TaxonomyHeads.Single().RevisionId = revisionId;
	}

	private static void Classify(ApplicationDbContext context, ReceiptItemEntity item, Guid revisionId, Guid categoryId)
	{
		Guid assignmentId = Guid.NewGuid();
		context.Classifications.Add(new PurchaseClassificationEntity
		{
			Id = assignmentId,
			ReceiptItemId = item.Id,
			Sequence = 1,
			CategoryId = categoryId,
			TaxonomyRevisionId = revisionId,
			Actor = "report-test",
			Source = "manual",
			Reason = "fixture",
			OperationId = Guid.NewGuid()
		});
		context.CurrentClassifications.Add(new CurrentClassificationEntity
		{
			ReceiptItemId = item.Id,
			AssignmentId = assignmentId
		});
	}

	private static void AddTransaction(ApplicationDbContext context, Guid receiptId, Guid accountId, Guid cardId, decimal amount)
	{
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receiptId, accountId, cardId);
		transaction.Amount = amount;
		context.Transactions.Add(transaction);
	}

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
