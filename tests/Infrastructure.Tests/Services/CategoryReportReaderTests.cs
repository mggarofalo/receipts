using Application.Models.Reports;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Infrastructure.Tests.Repositories;
using SampleData.Entities;

namespace Infrastructure.Tests.Services;

public class CategoryReportReaderTests
{
	[Fact]
	public async Task Read_DistinguishesExplicitNull_UnresolvedMigration_AndMissingPointer()
	{
		var factory = DbContextHelpers.CreateInMemoryContextFactory();
		await using var db = factory.CreateDbContext();
		var receipt = ReceiptEntityGenerator.Generate();
		receipt.Date = new(2020, 3, 1);
		var items = ReceiptItemEntityGenerator.GenerateList(5, receipt.Id);
		var revision = Guid.NewGuid();
		db.Receipts.Add(receipt); db.ReceiptItems.AddRange(items);
		for (int index = 0; index < 3; index++)
		{
			var assignment = Guid.NewGuid();
			db.Classifications.Add(new()
			{
				Id = assignment,
				ReceiptItemId = items[index].Id,
				TaxonomyRevisionId = revision,
				Source = index == 1 ? "legacy_import" : "manual",
				LegacyResolution = index == 2 ? "unresolved_path" : null
			});
			db.CurrentClassifications.Add(new() { ReceiptItemId = items[index].Id, AssignmentId = assignment });
			if (index == 1)
			{
				db.LegacyMigrationItems.Add(new()
				{
					Actor = "migration",
					OperationId = Guid.NewGuid(),
					ReceiptItemId = items[index].Id,
					AssignmentId = assignment,
					Resolution = "Unresolved"
				});
			}
		}
		items[4].DeletedAt = DateTimeOffset.UtcNow;
		await db.SaveChangesAsync();
		var result = await CategoryReportReader.ReadAsync(db, db.ReceiptItems,
			new(2020, 1, 1), new(2020, 12, 31), CategoryReportView.AsClassified);
		result.Buckets.Sum(bucket => bucket.PurchaseCount).Should().Be(4);
		result.Buckets.Where(bucket => bucket.State == CategoryReportState.Unclassified).Sum(bucket => bucket.PurchaseCount).Should().Be(1);
		result.Buckets.Where(bucket => bucket.State == CategoryReportState.UnresolvedLegacy).Sum(bucket => bucket.PurchaseCount).Should().Be(2);
		result.Buckets.Where(bucket => bucket.State == CategoryReportState.MissingAssignment).Sum(bucket => bucket.PurchaseCount).Should().Be(1);
	}

	[Fact]
	public async Task Read_UsesCorrectedPointer_PurchaseDate_AndExistingItemScope()
	{
		var factory = DbContextHelpers.CreateInMemoryContextFactory();
		await using var db = factory.CreateDbContext();
		var receipt = ReceiptEntityGenerator.Generate();
		receipt.Date = new(2020, 3, 1);
		var items = ReceiptItemEntityGenerator.GenerateList(3, receipt.Id);
		items[0].TotalAmount = 12.345m;
		var revision = Guid.NewGuid();
		var oldCategory = Guid.NewGuid();
		var correctedCategory = Guid.NewGuid();
		var corrected = Guid.NewGuid();
		db.Receipts.Add(receipt); db.ReceiptItems.AddRange(items);
		db.TaxonomyNodes.Add(new() { RevisionId = revision, CategoryId = correctedCategory, Name = "Corrected" });
		db.Classifications.AddRange(
			new PurchaseClassificationEntity
			{
				Id = Guid.NewGuid(),
				ReceiptItemId = items[0].Id,
				CategoryId = oldCategory,
				TaxonomyRevisionId = revision,
				Sequence = 1
			},
			new PurchaseClassificationEntity
			{
				Id = corrected,
				ReceiptItemId = items[0].Id,
				CategoryId = correctedCategory,
				TaxonomyRevisionId = revision,
				Sequence = 2,
				RecordedAt = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero)
			});
		db.CurrentClassifications.Add(new() { ReceiptItemId = items[0].Id, AssignmentId = corrected });
		await db.SaveChangesAsync();
		Guid included = items[0].Id;
		var result = await CategoryReportReader.ReadAsync(db, db.ReceiptItems.Where(item => item.Id == included),
			new(2020, 1, 1), new(2020, 12, 31), CategoryReportView.AsClassified);
		result.Buckets.Should().ContainSingle();
		result.Buckets.Single().CategoryId.Should().Be(correctedCategory);
		result.Buckets.Single().Amount.Should().Be(12.345m);
		result.Buckets.Single().PurchaseCount.Should().Be(1);
		(await CategoryReportReader.ReadAsync(db, db.ReceiptItems, new(2026, 1, 1), new(2026, 12, 31),
			CategoryReportView.AsClassified)).Buckets.Should().BeEmpty();
		db.Classifications.Count().Should().Be(2);
	}

	[Theory]
	[InlineData(CategoryReportView.AsClassified)]
	[InlineData(CategoryReportView.CurrentHierarchy)]
	public async Task Read_CutoffOrdersRecordedTimeThenSequence_AndKeepsPurchaseDatesIndependent(CategoryReportView view)
	{
		var factory = DbContextHelpers.CreateInMemoryContextFactory();
		await using var db = factory.CreateDbContext();
		var receipt = ReceiptEntityGenerator.Generate();
		receipt.Date = new(2000, 1, 1);
		var item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		Guid revision = Guid.NewGuid(), captured = Guid.NewGuid(), category = Guid.NewGuid();
		DateTimeOffset importedAt = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
		db.Receipts.Add(receipt); db.ReceiptItems.Add(item);
		db.TaxonomyHeads.Add(new() { Id = 1, RevisionId = revision });
		foreach (Guid tree in new[] { revision, captured })
		{ db.TaxonomyNodes.Add(new() { RevisionId = tree, CategoryId = category, Name = tree == revision ? "Original" : "Captured" }); }
		var imported = new PurchaseClassificationEntity
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = item.Id,
			Sequence = 1,
			CategoryId = category,
			TaxonomyRevisionId = revision,
			Source = "legacy_import",
			RecordedAt = importedAt
		};
		var cleared = new PurchaseClassificationEntity
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = item.Id,
			Sequence = 2,
			TaxonomyRevisionId = revision,
			RecordedAt = importedAt.AddDays(1)
		};
		var tiedCorrection = new PurchaseClassificationEntity
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = item.Id,
			Sequence = 3,
			CategoryId = category,
			TaxonomyRevisionId = revision,
			RecordedAt = cleared.RecordedAt
		};
		var backdatedObservation = new PurchaseClassificationEntity
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = item.Id,
			Sequence = 4,
			TaxonomyRevisionId = revision,
			RecordedAt = importedAt.AddHours(1)
		};
		db.Classifications.AddRange(imported, cleared, tiedCorrection, backdatedObservation);
		db.CurrentClassifications.Add(new() { ReceiptItemId = item.Id, AssignmentId = backdatedObservation.Id });
		await db.SaveChangesAsync();
		Task<CategoryReportResult> Read(DateTimeOffset? cutoff) => CategoryReportReader.ReadAsync(db, db.ReceiptItems,
			new(2000, 1, 1), new(2000, 1, 1), view, CategoryReportRollup.Root, knowledgeCutoff: cutoff,
			capturedCurrentRevisionId: view == CategoryReportView.CurrentHierarchy ? captured : null);
		var before = await Read(importedAt.AddTicks(-1));
		before.Buckets.Single().State.Should().Be(CategoryReportState.BeforeMigrationHistory);
		var atImport = await Read(importedAt);
		atImport.Buckets.Single().CategoryId.Should().Be(category);
		var between = await Read(importedAt.AddHours(2));
		between.Buckets.Single().State.Should().Be(CategoryReportState.Unclassified);
		var tied = await Read(cleared.RecordedAt);
		tied.Buckets.Single().CategoryId.Should().Be(category);
		tied.KnowledgeCutoff.Should().Be(cleared.RecordedAt);
		tied.AssignmentSelection.Should().Be(CategoryReportAssignmentSelection.LatestRecordedAtOrBeforeCutoff);
		tied.Rollup.Should().Be(CategoryReportRollup.Root);
		tied.CurrentRevisionId.Should().Be(view == CategoryReportView.CurrentHierarchy ? captured : null);
		tied.Buckets.Single().Path.Single().Name.Should().Be(view == CategoryReportView.CurrentHierarchy ? "Captured" : "Original");
		var live = await Read(null);
		live.Buckets.Single().State.Should().Be(CategoryReportState.Unclassified);
		live.AssignmentSelection.Should().Be(CategoryReportAssignmentSelection.CurrentPointer);
		live.KnowledgeCutoff.Should().BeNull();
	}
}
