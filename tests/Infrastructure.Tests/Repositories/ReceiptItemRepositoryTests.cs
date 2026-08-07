using Application.Models;
using FluentAssertions;
using Infrastructure.Entities.Core;
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
		updatedEntities.Should().BeEquivalentTo(entities, opt => opt.Excluding(member => member.Name == nameof(ReceiptItemEntity.Receipt)));

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
			linked.NormalizedDescriptionMatchScore = 0.91;
			context.ReceiptItems.Add(linked);
			await context.SaveChangesAsync(CancellationToken.None);
		}

		ReceiptItemRepository repository = new(_contextFactory);

		List<ReceiptItemEntity> actual = await repository.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None);

		ReceiptItemEntity row = actual.Should().ContainSingle().Subject;
		row.NormalizedDescriptionId.Should().Be(normalizedId);
		row.NormalizedDescriptionMatchScore.Should().Be(0.91);
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
}
