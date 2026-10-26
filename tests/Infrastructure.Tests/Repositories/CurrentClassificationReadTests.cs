using Application.Models;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Moq;
using SampleData.Entities;

namespace Infrastructure.Tests.Repositories;

public class CurrentClassificationReadTests
{
	[Theory]
	[InlineData("detail")]
	[InlineData("list")]
	[InlineData("receipt")]
	[InlineData("deleted")]
	public async Task Read_CurrentPointer_PreservesRecordedDeepPathAndLegacyStrings(string route)
	{
		IDbContextFactory<ApplicationDbContext> factory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid revision = Guid.NewGuid(), newerRevision = Guid.NewGuid(), root = Guid.NewGuid(), branch = Guid.NewGuid(), leaf = Guid.NewGuid();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		string legacy = item.Category;
		Guid assignment = Guid.NewGuid();
		using (ApplicationDbContext db = factory.CreateDbContext())
		{
			db.Receipts.Add(receipt);
			db.ReceiptItems.Add(item);
			db.TaxonomyNodes.AddRange(
				new TaxonomyNodeEntity { RevisionId = revision, CategoryId = root, Name = "Original root" },
				new TaxonomyNodeEntity { RevisionId = revision, CategoryId = branch, ParentId = root, Name = "Branch" },
				new TaxonomyNodeEntity { RevisionId = revision, CategoryId = leaf, ParentId = branch, Name = "Recorded leaf", Archived = true },
				new TaxonomyNodeEntity { RevisionId = newerRevision, CategoryId = leaf, Name = "Moved and renamed" });
			db.Classifications.AddRange(
				new PurchaseClassificationEntity { Id = Guid.NewGuid(), ReceiptItemId = item.Id, Sequence = 1, TaxonomyRevisionId = newerRevision },
				new PurchaseClassificationEntity { Id = assignment, ReceiptItemId = item.Id, Sequence = 2, CategoryId = leaf, TaxonomyRevisionId = revision });
			db.CurrentClassifications.Add(new() { ReceiptItemId = item.Id, AssignmentId = assignment });
			await db.SaveChangesAsync();
			if (route == "deleted") { item.DeletedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
		}
		ReceiptItemRepository repository = new(factory);
		ReceiptItemEntity actual = route switch
		{
			"detail" => (await repository.GetByIdAsync(item.Id, default))!,
			"list" => (await repository.GetAllAsync(0, 50, SortParams.Default, default)).Single(),
			"receipt" => (await repository.GetByReceiptIdAsync(receipt.Id, 0, 50, SortParams.Default, default)).Single(),
			_ => (await repository.GetDeletedAsync(0, 50, SortParams.Default, default)).Single()
		};
		actual.CurrentClassification.Should().NotBeNull();
		actual.CurrentClassification!.AssignmentId.Should().Be(assignment);
		actual.CurrentClassification.CategoryId.Should().Be(leaf);
		actual.CurrentClassification.TaxonomyRevisionId.Should().Be(revision);
		actual.CurrentClassification.RecordedPath.Select(node => node.Name).Should().Equal("Original root", "Branch", "Recorded leaf");
		actual.CurrentClassification.RecordedPath.Last().Archived.Should().BeTrue();
		actual.Category.Should().Be(legacy);
		new ReceiptItemMapper().ToDomain(actual).CurrentClassification.Should().BeEquivalentTo(actual.CurrentClassification);
	}

	[Fact]
	public async Task ReceiptWithItemsQuery_UsesCurrentClassificationReadModel()
	{
		IDbContextFactory<ApplicationDbContext> factory = DbContextHelpers.CreateInMemoryContextFactory();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		Guid assignment = Guid.NewGuid(), revision = Guid.NewGuid();
		using (ApplicationDbContext db = factory.CreateDbContext())
		{
			db.Receipts.Add(receipt); db.ReceiptItems.Add(item);
			db.Classifications.Add(new() { Id = assignment, ReceiptItemId = item.Id, TaxonomyRevisionId = revision });
			db.CurrentClassifications.Add(new() { ReceiptItemId = item.Id, AssignmentId = assignment });
			await db.SaveChangesAsync();
		}
		Moq.Mock<Application.Interfaces.Services.IReceiptService> receipts = new();
		receipts.Setup(service => service.GetByIdAsync(receipt.Id, default)).ReturnsAsync(new ReceiptMapper().ToDomain(receipt));
		Moq.Mock<Application.Interfaces.Services.IAdjustmentService> adjustments = new();
		adjustments.Setup(service => service.GetByReceiptIdAsync(receipt.Id, 0, int.MaxValue, SortParams.Default, default))
			.ReturnsAsync(new PagedResult<Domain.Core.Adjustment>([], 0, 0, int.MaxValue));
		Infrastructure.Services.ReceiptItemService items = new(new ReceiptItemRepository(factory), new ReceiptItemMapper());
		Application.Queries.Aggregates.ReceiptsWithItems.GetReceiptWithItemsByReceiptIdQueryHandler handler = new(receipts.Object, items, adjustments.Object);
		Domain.Aggregates.ReceiptWithItems? actual = await handler.Handle(new(receipt.Id), default);
		actual!.Items.Single().CurrentClassification!.AssignmentId.Should().Be(assignment);
		actual.Items.Single().CurrentClassification!.TaxonomyRevisionId.Should().Be(revision);
	}

	[Fact]
	public async Task Read_MissingAssignmentAndExplicitUnclassified_RemainDistinct()
	{
		IDbContextFactory<ApplicationDbContext> factory = DbContextHelpers.CreateInMemoryContextFactory();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		List<ReceiptItemEntity> items = ReceiptItemEntityGenerator.GenerateList(2, receipt.Id);
		Guid assignment = Guid.NewGuid(), revision = Guid.NewGuid();
		using (ApplicationDbContext db = factory.CreateDbContext())
		{
			db.Receipts.Add(receipt); db.ReceiptItems.AddRange(items);
			db.Classifications.Add(new() { Id = assignment, ReceiptItemId = items[1].Id, TaxonomyRevisionId = revision });
			db.CurrentClassifications.Add(new() { ReceiptItemId = items[1].Id, AssignmentId = assignment });
			await db.SaveChangesAsync();
		}
		List<ReceiptItemEntity> actual = await new ReceiptItemRepository(factory).GetByReceiptIdAsync(receipt.Id, 0, 50, SortParams.Default, default);
		actual.Single(item => item.Id == items[0].Id).CurrentClassification.Should().BeNull();
		Domain.Taxonomy.CurrentClassification classified = actual.Single(item => item.Id == items[1].Id).CurrentClassification!;
		classified.AssignmentId.Should().Be(assignment);
		classified.TaxonomyRevisionId.Should().Be(revision);
		classified.CategoryId.Should().BeNull(); classified.RecordedPath.Should().BeEmpty();
	}
}
