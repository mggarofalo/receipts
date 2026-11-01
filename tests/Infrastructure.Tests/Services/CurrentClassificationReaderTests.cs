using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

public class CurrentClassificationReaderTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PopulateAsync_BrokenRecordedAncestry_FailsClosedWithoutLosingAssignment(bool cycle)
	{
		Guid itemId = Guid.NewGuid();
		Guid assignmentId = Guid.NewGuid();
		Guid revisionId = Guid.NewGuid();
		Guid leafId = Guid.NewGuid();
		Guid parentId = Guid.NewGuid();
		await using ApplicationDbContext db = CreateContext();
		db.Classifications.Add(new PurchaseClassificationEntity
		{
			Id = assignmentId,
			ReceiptItemId = itemId,
			CategoryId = leafId,
			TaxonomyRevisionId = revisionId,
		});
		db.CurrentClassifications.Add(new CurrentClassificationEntity
		{
			ReceiptItemId = itemId,
			AssignmentId = assignmentId,
		});
		db.TaxonomyNodes.Add(new TaxonomyNodeEntity
		{
			RevisionId = revisionId,
			CategoryId = leafId,
			ParentId = parentId,
			Name = "Leaf",
		});
		if (cycle)
		{
			db.TaxonomyNodes.Add(new TaxonomyNodeEntity
			{
				RevisionId = revisionId,
				CategoryId = parentId,
				ParentId = leafId,
				Name = "Parent",
			});
		}
		await db.SaveChangesAsync();

		ReceiptItemEntity item = new() { Id = itemId };
		await CurrentClassificationReader.PopulateAsync(db, [item], CancellationToken.None);

		item.CurrentClassification.Should().NotBeNull();
		item.CurrentClassification!.AssignmentId.Should().Be(assignmentId);
		item.CurrentClassification.CategoryId.Should().Be(leafId);
		item.CurrentClassification.RecordedPath.Should().BeEmpty();
		ExternalCategoryMappingPolicy.ResolveRecorded(item.CurrentClassification,
				new ExternalCategoryScope("YNAB", "budget"),
				[new ExternalCategoryMapping(Guid.NewGuid(), leafId,
					new ExternalCategoryScope("YNAB", "budget"), "remote")])
			.Disposition.Should().Be(ExternalCategoryMappingDisposition.InvalidAncestry);
	}

	[Fact]
	public async Task PopulateAsync_ValidRecordedParent_ResolvesAncestorMapping()
	{
		Guid itemId = Guid.NewGuid();
		Guid assignmentId = Guid.NewGuid();
		Guid revisionId = Guid.NewGuid();
		Guid leafId = Guid.NewGuid();
		Guid parentId = Guid.NewGuid();
		await using ApplicationDbContext db = CreateContext();
		db.Classifications.Add(new PurchaseClassificationEntity
		{
			Id = assignmentId,
			ReceiptItemId = itemId,
			CategoryId = leafId,
			TaxonomyRevisionId = revisionId,
		});
		db.CurrentClassifications.Add(new CurrentClassificationEntity
		{
			ReceiptItemId = itemId,
			AssignmentId = assignmentId,
		});
		db.TaxonomyNodes.AddRange(
			new TaxonomyNodeEntity
			{
				RevisionId = revisionId,
				CategoryId = parentId,
				ParentId = null,
				Name = "Recorded parent",
			},
			new TaxonomyNodeEntity
			{
				RevisionId = revisionId,
				CategoryId = leafId,
				ParentId = parentId,
				Name = "Recorded leaf",
			});
		await db.SaveChangesAsync();

		ReceiptItemEntity item = new() { Id = itemId };
		await CurrentClassificationReader.PopulateAsync(db, [item], CancellationToken.None);

		item.CurrentClassification!.RecordedPath.Select(node => node.Id)
			.Should().Equal(parentId, leafId);
		ExternalCategoryMappingResolution resolution = ExternalCategoryMappingPolicy.ResolveRecorded(
			item.CurrentClassification, new ExternalCategoryScope("YNAB", "budget"),
			[new ExternalCategoryMapping(Guid.NewGuid(), parentId,
				new ExternalCategoryScope("YNAB", "budget"), "parent-remote")]);
		resolution.Disposition.Should().Be(ExternalCategoryMappingDisposition.Mapped);
		resolution.Mapping!.ExternalCategoryId.Should().Be("parent-remote");
		resolution.AncestorDistance.Should().Be(1);
	}

	[Fact]
	public async Task ReadAsync_ReturnsRecordedPathsKeyedByItem_OmittingItemsWithoutAssignment()
	{
		Guid assigned = Guid.NewGuid(), cleared = Guid.NewGuid(), missing = Guid.NewGuid();
		Guid revisionId = Guid.NewGuid(), rootId = Guid.NewGuid(), leafId = Guid.NewGuid();
		Guid assignedAssignment = Guid.NewGuid(), clearedAssignment = Guid.NewGuid();
		await using ApplicationDbContext db = CreateContext();
		db.Classifications.AddRange(
			new PurchaseClassificationEntity { Id = assignedAssignment, ReceiptItemId = assigned, CategoryId = leafId, TaxonomyRevisionId = revisionId },
			new PurchaseClassificationEntity { Id = clearedAssignment, ReceiptItemId = cleared, TaxonomyRevisionId = revisionId });
		db.CurrentClassifications.AddRange(
			new CurrentClassificationEntity { ReceiptItemId = assigned, AssignmentId = assignedAssignment },
			new CurrentClassificationEntity { ReceiptItemId = cleared, AssignmentId = clearedAssignment });
		db.TaxonomyNodes.AddRange(
			new TaxonomyNodeEntity { RevisionId = revisionId, CategoryId = rootId, Name = "Root" },
			new TaxonomyNodeEntity { RevisionId = revisionId, CategoryId = leafId, ParentId = rootId, Name = "Leaf" });
		await db.SaveChangesAsync();

		var byItem = await CurrentClassificationReader.ReadAsync(db, [assigned, cleared, missing], CancellationToken.None);

		byItem.Keys.Should().BeEquivalentTo([assigned, cleared]);
		byItem[assigned].RecordedPath.Select(node => node.Name).Should().Equal("Root", "Leaf");
		byItem[cleared].CategoryId.Should().BeNull();
		byItem[cleared].RecordedPath.Should().BeEmpty();
		(await CurrentClassificationReader.ReadAsync(db, [], CancellationToken.None)).Should().BeEmpty();
	}

	private static ApplicationDbContext CreateContext() => new(
		new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase($"classification-reader-{Guid.NewGuid():N}").Options);
}
