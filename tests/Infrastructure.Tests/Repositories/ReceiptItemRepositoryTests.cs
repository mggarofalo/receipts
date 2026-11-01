using Application.Models;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.Tests.Repositories;

public class ReceiptItemRepositoryTests
{
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

	private async Task<ReceiptEntity> CreateParentReceiptAsync()
	{
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		await context.Receipts.AddAsync(receipt);
		await context.SaveChangesAsync(CancellationToken.None);
		return receipt;
	}

	[Fact]
	public async Task GetByIdAsync_ExistingId_ReturnsReceiptItem()
	{
		// Arrange
		ReceiptEntity receipt = await CreateParentReceiptAsync();
		using ApplicationDbContext context = _contextFactory.CreateDbContext();

		ReceiptItemEntity entity = ReceiptItemEntityGenerator.Generate(receipt.Id);
		await context.ReceiptItems.AddAsync(entity);
		await context.SaveChangesAsync(CancellationToken.None);

		ReceiptItemRepository repository = new(_contextFactory);

		// Act
		ReceiptItemEntity? actual = await repository.GetByIdAsync(entity.Id, CancellationToken.None);

		// Assert
		Assert.NotNull(actual);
		actual.Should().BeEquivalentTo(entity, opt => opt.Excluding(member => member.Name == nameof(ReceiptItemEntity.Receipt)));

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetByIdAsync_NonExistingId_ReturnsNull()
	{
		// Arrange
		ReceiptItemRepository repository = new(_contextFactory);

		// Act
		ReceiptItemEntity? result = await repository.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

		// Assert
		Assert.Null(result);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetByReceiptIdAsync_ExistingReceiptId_ReturnsReceiptItems()
	{
		// Arrange
		const int expectedItemCount = 3;
		ReceiptEntity receipt = await CreateParentReceiptAsync();
		using ApplicationDbContext context = _contextFactory.CreateDbContext();

		List<ReceiptItemEntity> entities = ReceiptItemEntityGenerator.GenerateList(expectedItemCount, receipt.Id);
		await context.ReceiptItems.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);

		ReceiptItemRepository repository = new(_contextFactory);

		// Act
		List<ReceiptItemEntity> actual = await repository.GetByReceiptIdAsync(receipt.Id, 0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		actual.Should().BeEquivalentTo(entities, opt => opt.Excluding(member => member.Name == nameof(ReceiptItemEntity.Receipt)));

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAllAsync_ReturnsAllReceiptItems()
	{
		// Arrange
		const int expectedItemCount = 3;
		ReceiptEntity receipt = await CreateParentReceiptAsync();
		using ApplicationDbContext context = _contextFactory.CreateDbContext();

		List<ReceiptItemEntity> entities = ReceiptItemEntityGenerator.GenerateList(expectedItemCount, receipt.Id);
		await context.ReceiptItems.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);

		ReceiptItemRepository repository = new(_contextFactory);

		// Act
		List<ReceiptItemEntity> actual = await repository.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None);

		// Assert
		actual.Should().BeEquivalentTo(entities, opt => opt.Excluding(member => member.Name == nameof(ReceiptItemEntity.Receipt)));

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task CreateAsync_ValidReceiptItems_ReturnsCreatedReceiptItems()
	{
		// Arrange
		const int expectedItemCount = 2;
		List<ReceiptItemEntity> entities = ReceiptItemEntityGenerator.GenerateList(expectedItemCount);
		entities.ForEach(e => e.Id = Guid.Empty);
		ReceiptItemRepository repository = new(_contextFactory);

		// Act
		List<ReceiptItemEntity> actual = await repository.CreateAsync(entities, CancellationToken.None);

		// Assert
		Assert.All(actual, r =>
		{
			Assert.NotEqual(Guid.Empty, r.Id);
		});

		actual.Should().BeEquivalentTo(entities, opt => opt.Excluding(x => x.Id));

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task UpdateAsync_ValidReceiptItems_UpdatesReceiptItems()
	{
		// Arrange
		const int expectedItemCount = 2;
		ReceiptEntity receipt = await CreateParentReceiptAsync();
		using ApplicationDbContext context = _contextFactory.CreateDbContext();

		List<ReceiptItemEntity> entities = ReceiptItemEntityGenerator.GenerateList(expectedItemCount, receipt.Id);
		await context.ReceiptItems.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);

		ReceiptItemRepository repository = new(_contextFactory);

		Dictionary<Guid, Guid> originalRevisions = entities.ToDictionary(item => item.Id, item => item.NormalizationRevision);
		var originalHashes = entities.ToDictionary(item => item.Id, item => item.NormalizationSourceHash);
		// Modify receipt items
		entities.ForEach(e =>
		{
			e.Description = "Updated " + e.Description;
			e.Quantity++;
		});

		// Act
		await repository.UpdateAsync(entities, CancellationToken.None);

		using ApplicationDbContext verifyContext = _contextFactory.CreateDbContext();
		List<ReceiptItemEntity> updatedEntities = await verifyContext.ReceiptItems.ToListAsync();

		// Assert
		updatedEntities.Should().BeEquivalentTo(entities, opt => opt
			.Excluding(member => member.Name == nameof(ReceiptItemEntity.Receipt))
			.Excluding(item => item.NormalizationRevision)
			.Excluding(item => item.NormalizationSourceHash));
		updatedEntities.Should().OnlyContain(item => item.NormalizationSourceHash != null && item.NormalizationSourceHash != originalHashes[item.Id], "source edits recompute the server-owned source fingerprint");
		updatedEntities.Should().OnlyContain(item => item.NormalizationRevision != Guid.Empty && item.NormalizationRevision != originalRevisions[item.Id],
			"an edit rotates the server-owned stale-write token independently of detached input");

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task DeleteAsync_ValidIds_DeletesReceiptItems()
	{
		// Arrange
		const int initialItemCount = 5;
		const int itemsToDeleteCount = 2;
		const int expectedRemainingCount = 3;

		ReceiptEntity receipt = await CreateParentReceiptAsync();
		using ApplicationDbContext context = _contextFactory.CreateDbContext();

		List<ReceiptItemEntity> entities = ReceiptItemEntityGenerator.GenerateList(initialItemCount, receipt.Id);
		await context.ReceiptItems.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);

		List<Guid> idsToDelete = [.. entities.Take(itemsToDeleteCount).Select(e => e.Id)];
		ReceiptItemRepository repository = new(_contextFactory);

		// Act
		await repository.DeleteAsync(idsToDelete, CancellationToken.None);

		using ApplicationDbContext verifyContext = _contextFactory.CreateDbContext();
		List<ReceiptItemEntity> remainingEntities = await verifyContext.ReceiptItems.ToListAsync();

		// Assert
		remainingEntities.Count.Should().Be(expectedRemainingCount);
		Assert.DoesNotContain(remainingEntities, e => idsToDelete.Contains(e.Id));

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task ExistsAsync_ExistingId_ReturnsTrue()
	{
		// Arrange
		const int expectedCount = 1;
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		ReceiptItemEntity entity = ReceiptItemEntityGenerator.Generate();
		await context.ReceiptItems.AddAsync(entity);
		await context.SaveChangesAsync(CancellationToken.None);

		ReceiptItemRepository repository = new(_contextFactory);

		// Act
		bool result = await repository.ExistsAsync(entity.Id, CancellationToken.None);

		// Assert
		Assert.True(result);
		(await context.ReceiptItems.CountAsync()).Should().Be(expectedCount);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task ExistsAsync_NonExistingId_ReturnsFalse()
	{
		// Arrange
		const int expectedCount = 0;
		ReceiptItemRepository repository = new(_contextFactory);

		// Act
		bool result = await repository.ExistsAsync(Guid.NewGuid(), CancellationToken.None);

		// Assert
		Assert.False(result);
		(await _contextFactory.CreateDbContext().ReceiptItems.CountAsync()).Should().Be(expectedCount);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetCountAsync_ReturnsCorrectCount()
	{
		// Arrange
		const int expectedCount = 3;
		using ApplicationDbContext context = _contextFactory.CreateDbContext();
		List<ReceiptItemEntity> entities = ReceiptItemEntityGenerator.GenerateList(expectedCount);
		await context.ReceiptItems.AddRangeAsync(entities);
		await context.SaveChangesAsync(CancellationToken.None);

		ReceiptItemRepository repository = new(_contextFactory);

		// Act
		int count = await repository.GetCountAsync(CancellationToken.None);

		// Assert
		count.Should().Be(expectedCount);

		_contextFactory.ResetDatabase();
	}

	// ── RECEIPTS-877: normalized-description link and filter ───────────────────────────

	[Fact]
	public async Task GetAllAsync_CarriesTheNormalizedDescriptionLink()
	{
		// The list projection used to build a fresh entity that omitted these columns, so
		// GET /api/receipt-items reported normalizedDescriptionId: null on every row despite the
		// spec documenting it — and the review queue's split dialog, which filters on exactly
		// that field, could never match anything. Nothing covered it, which is why it survived.
		ReceiptEntity receipt = await CreateParentReceiptAsync();
		Guid normalizedId = Guid.NewGuid();

		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			context.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "MILK 2% GAL",
				DisplayLabel = "Milk",
				Status = Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});

			ReceiptItemEntity linked = ReceiptItemEntityGenerator.Generate(receipt.Id);
			linked.NormalizedDescriptionId = normalizedId;
			context.ReceiptItems.Add(linked);
			await context.SaveChangesAsync(CancellationToken.None);
		}

		ReceiptItemRepository repository = new(_contextFactory);

		List<ReceiptItemEntity> actual = await repository.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None);

		ReceiptItemEntity row = actual.Should().ContainSingle().Subject;
		row.NormalizedDescriptionId.Should().Be(normalizedId);
		// The neighbour comes through as a trimmed stand-in so the mapper can denormalize a name
		// without an Include dragging the embedding across the wire for every row.
		row.NormalizedDescription.Should().NotBeNull();
		row.NormalizedDescription!.DisplayLabel.Should().Be("Milk");
		row.NormalizedDescription.CanonicalName.Should().Be("MILK 2% GAL");

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAllAsync_FiltersToOneNormalizedDescription()
	{
		ReceiptEntity receipt = await CreateParentReceiptAsync();
		Guid wantedId = Guid.NewGuid();
		Guid otherId = Guid.NewGuid();
		Guid wantedItemId = Guid.NewGuid();

		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			ReceiptItemEntity wanted = ReceiptItemEntityGenerator.Generate(receipt.Id);
			wanted.Id = wantedItemId;
			wanted.NormalizedDescriptionId = wantedId;

			ReceiptItemEntity other = ReceiptItemEntityGenerator.Generate(receipt.Id);
			other.NormalizedDescriptionId = otherId;

			ReceiptItemEntity unlinked = ReceiptItemEntityGenerator.Generate(receipt.Id);
			unlinked.NormalizedDescriptionId = null;

			context.ReceiptItems.AddRange(wanted, other, unlinked);
			await context.SaveChangesAsync(CancellationToken.None);
		}

		ReceiptItemRepository repository = new(_contextFactory);

		List<ReceiptItemEntity> actual = await repository.GetAllAsync(0, 50, SortParams.Default, q: null, wantedId, CancellationToken.None);
		int count = await repository.GetCountAsync(q: null, wantedId, CancellationToken.None);

		actual.Should().ContainSingle().Which.Id.Should().Be(wantedItemId);
		// The count has to agree with the page, or the dialog pages against a total that spans
		// rows it will never be shown.
		count.Should().Be(1);

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSuggestionsAsync_TakesItemTypeFromLatestItemsRecordedPath_NotLegacyLabels()
	{
		// Arrange
		Guid revision = Guid.NewGuid(), food = Guid.NewGuid(), dairy = Guid.NewGuid(), household = Guid.NewGuid(),
			cleaning = Guid.NewGuid(), sprays = Guid.NewGuid(), pets = Guid.NewGuid();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity older = SuggestionItem(receipt.Id, "00000000-0000-0000-0000-000000000001", "SKU-1", "Old name");
		ReceiptItemEntity latest = SuggestionItem(receipt.Id, "00000000-0000-0000-0000-000000000002", "SKU-1", "New name");
		ReceiptItemEntity rootOnly = SuggestionItem(receipt.Id, "00000000-0000-0000-0000-000000000003", "SKU-2", "Kibble");
		ReceiptItemEntity untyped = SuggestionItem(receipt.Id, "00000000-0000-0000-0000-000000000004", "SKU-3", "Mystery");
		ReceiptItemEntity unclassified = SuggestionItem(receipt.Id, "00000000-0000-0000-0000-000000000005", "SKU-4", "Cleared");
		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			context.Receipts.Add(receipt);
			context.ReceiptItems.AddRange(older, latest, rootOnly, untyped, unclassified);
			context.TaxonomyNodes.AddRange(
				new TaxonomyNodeEntity { RevisionId = revision, CategoryId = food, Name = "Food" },
				new TaxonomyNodeEntity { RevisionId = revision, CategoryId = dairy, ParentId = food, Name = "Dairy" },
				new TaxonomyNodeEntity { RevisionId = revision, CategoryId = household, Name = "Household" },
				new TaxonomyNodeEntity { RevisionId = revision, CategoryId = cleaning, ParentId = household, Name = "Cleaning" },
				new TaxonomyNodeEntity { RevisionId = revision, CategoryId = sprays, ParentId = cleaning, Name = "Sprays" },
				new TaxonomyNodeEntity { RevisionId = revision, CategoryId = pets, Name = "Pets" });
			AssignSuggestionItem(context, older, revision, dairy);
			AssignSuggestionItem(context, latest, revision, sprays);
			AssignSuggestionItem(context, rootOnly, revision, pets);
			AssignSuggestionItem(context, unclassified, revision, null);
			await context.SaveChangesAsync();
		}

		ReceiptItemRepository repository = new(_contextFactory);

		// Act
		List<Application.Queries.Core.ReceiptItem.GetReceiptItemSuggestions.ReceiptItemSuggestion> actual =
			await repository.GetSuggestionsAsync("sku", null, 10, CancellationToken.None);

		// Assert — top two levels of the latest item's path; "" / null without an item type
		actual.Should().HaveCount(4).And.OnlyContain(s => s.MatchType == "global");
		var sku1 = actual.Single(s => s.ItemCode == "SKU-1");
		sku1.Description.Should().Be("New name");
		sku1.Category.Should().Be("Household");
		sku1.Subcategory.Should().Be("Cleaning");
		var sku2 = actual.Single(s => s.ItemCode == "SKU-2");
		sku2.Category.Should().Be("Pets");
		sku2.Subcategory.Should().BeNull();
		foreach (string code in new[] { "SKU-3", "SKU-4" })
		{
			var none = actual.Single(s => s.ItemCode == code);
			none.Category.Should().BeEmpty();
			none.Subcategory.Should().BeNull();
		}

		_contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSuggestionsAsync_LocationMatches_AlsoUseRecordedPath()
	{
		// Arrange
		Guid revision = Guid.NewGuid(), pets = Guid.NewGuid();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.Location = "Corner Market";
		ReceiptItemEntity item = SuggestionItem(receipt.Id, "00000000-0000-0000-0000-000000000001", "SKU-9", "Kibble");
		using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			context.Receipts.Add(receipt);
			context.ReceiptItems.Add(item);
			context.TaxonomyNodes.Add(new TaxonomyNodeEntity { RevisionId = revision, CategoryId = pets, Name = "Pets" });
			AssignSuggestionItem(context, item, revision, pets);
			await context.SaveChangesAsync();
		}

		ReceiptItemRepository repository = new(_contextFactory);

		// Act
		var actual = await repository.GetSuggestionsAsync("SKU-9", "corner market", 10, CancellationToken.None);

		// Assert
		var suggestion = actual.Should().ContainSingle().Subject;
		suggestion.MatchType.Should().Be("location");
		suggestion.Category.Should().Be("Pets");
		suggestion.Subcategory.Should().BeNull();

		_contextFactory.ResetDatabase();
	}

	private static ReceiptItemEntity SuggestionItem(Guid receiptId, string id, string code, string description)
	{
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receiptId);
		item.Id = Guid.Parse(id);
		item.ReceiptItemCode = code;
		item.Description = description;
		item.Category = "Legacy label";
		item.Subcategory = "Legacy sub";
		return item;
	}

	private static void AssignSuggestionItem(ApplicationDbContext context, ReceiptItemEntity item, Guid revision, Guid? category)
	{
		PurchaseClassificationEntity assignment = new()
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = item.Id,
			CategoryId = category,
			TaxonomyRevisionId = revision,
		};
		context.Classifications.Add(assignment);
		context.CurrentClassifications.Add(new CurrentClassificationEntity { ReceiptItemId = item.Id, AssignmentId = assignment.Id });
	}
}
