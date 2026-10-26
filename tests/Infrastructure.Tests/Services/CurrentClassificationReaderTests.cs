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

	private static ApplicationDbContext CreateContext() => new(
		new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase($"classification-reader-{Guid.NewGuid():N}").Options);
}
