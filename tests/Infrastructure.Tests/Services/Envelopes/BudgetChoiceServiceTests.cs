using Application.Envelopes;
using Application.Taxonomy;
using Domain.Envelopes;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Services.Envelopes;

/// <summary>
/// Explicit receipt and item budget choices on InMemory: unlike ordinary edits, null means automatic
/// (envelope) or none (occasion), and only new choices must be selectable.
/// </summary>
public class BudgetChoiceServiceTests
{
	private static BudgetChoiceService Service(EnvelopeWorld world) => new(world.Factory);

	private static async Task<ReceiptItemEntity> CreateItemAsync(EnvelopeWorld world, ReceiptItemEntity item)
		=> (await new ReceiptItemRepository(world.Factory, Options.Create(new ClassificationOptions { WritesEnabled = true }))
			.CreateAsync([item], CancellationToken.None)).Single();

	// --- Receipt choices ---

	[Fact]
	public async Task SetReceipt_PaidFrom_AppliesToAllButOverrides()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity defaulted = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));
		ReceiptItemEntity overridden = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries", envelopeOverride: world.Food));

		// Act
		bool actual = await Service(world).SetReceiptChoicesAsync(receipt.Id, world.Car, null, CancellationToken.None);

		// Assert
		actual.Should().BeTrue();
		(await ReadReceiptAsync(world, receipt.Id)).PaidFromEnvelopeId.Should().Be(world.Car);
		ReceiptItemEntity actualDefaulted = await world.ReadItemAsync(defaulted.Id);
		actualDefaulted.EnvelopeId.Should().Be(world.Car);
		actualDefaulted.EnvelopeSource.Should().Be(EnvelopeSource.Receipt);
		ReceiptItemEntity actualOverridden = await world.ReadItemAsync(overridden.Id);
		actualOverridden.EnvelopeId.Should().Be(world.Food);
		actualOverridden.EnvelopeSource.Should().Be(EnvelopeSource.Item);
	}

	[Fact]
	public async Task SetReceipt_NullPaidFrom_ClearsItAndReceiptSourcedItemsReturnToDefault()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync(paidFrom: world.Car);
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));

		// Act
		bool actual = await Service(world).SetReceiptChoicesAsync(receipt.Id, null, null, CancellationToken.None);

		// Assert
		actual.Should().BeTrue();
		(await ReadReceiptAsync(world, receipt.Id)).PaidFromEnvelopeId.Should().BeNull();
		ReceiptItemEntity actualItem = await world.ReadItemAsync(created.Id);
		actualItem.EnvelopeId.Should().Be(world.Food);
		actualItem.EnvelopeSource.Should().Be(EnvelopeSource.Default);
	}

	[Fact]
	public async Task SetReceipt_MissingReceipt_ReturnsFalse()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();

		// Act
		bool actual = await Service(world).SetReceiptChoicesAsync(Guid.NewGuid(), world.Car, null, CancellationToken.None);

		// Assert
		actual.Should().BeFalse();
	}

	[Fact]
	public async Task SetReceipt_NewArchivedPaidFrom_IsRejectedWith400()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync(paidFrom: world.Car);

		// Act
		Func<Task> act = () => Service(world).SetReceiptChoicesAsync(receipt.Id, world.Retired, null, CancellationToken.None);

		// Assert
		(await act.Should().ThrowAsync<EnvelopeException>()).Which.Status.Should().Be(400);
		(await ReadReceiptAsync(world, receipt.Id)).PaidFromEnvelopeId.Should().Be(world.Car);
	}

	[Fact]
	public async Task SetReceipt_KeepingStoredArchivedPaidFrom_IsAllowed()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		Guid occasion = await AddOccasionAsync(world, isActive: true);
		ReceiptEntity receipt = await world.AddReceiptAsync(paidFrom: world.Retired);

		// Act
		Func<Task> act = () => Service(world).SetReceiptChoicesAsync(receipt.Id, world.Retired, occasion, CancellationToken.None);

		// Assert
		await act.Should().NotThrowAsync();
		ReceiptEntity actual = await ReadReceiptAsync(world, receipt.Id);
		actual.PaidFromEnvelopeId.Should().Be(world.Retired);
		actual.OccasionId.Should().Be(occasion);
	}

	[Fact]
	public async Task SetReceipt_NewArchivedOccasion_IsRejectedWith400()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		Guid occasion = await AddOccasionAsync(world, isActive: false);
		ReceiptEntity receipt = await world.AddReceiptAsync();

		// Act
		Func<Task> act = () => Service(world).SetReceiptChoicesAsync(receipt.Id, null, occasion, CancellationToken.None);

		// Assert
		(await act.Should().ThrowAsync<EnvelopeException>()).Which.Status.Should().Be(400);
		(await ReadReceiptAsync(world, receipt.Id)).OccasionId.Should().BeNull();
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SetReceipt_OccasionChange_ReplacesOnlyItemsCarryingThePreviousOccasion(bool cleared)
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		Guid previous = await AddOccasionAsync(world, isActive: true);
		Guid own = await AddOccasionAsync(world, isActive: true);
		Guid? next = cleared ? null : await AddOccasionAsync(world, isActive: true);
		ReceiptEntity receipt = await world.AddReceiptAsync(occasion: previous);
		ReceiptItemEntity inherited = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));
		ReceiptItemEntity tagged = EnvelopeWorld.NewItem(receipt.Id, "Groceries");
		tagged.OccasionId = own;
		tagged = await CreateItemAsync(world, tagged);

		// Act
		await Service(world).SetReceiptChoicesAsync(receipt.Id, null, next, CancellationToken.None);

		// Assert
		(await ReadReceiptAsync(world, receipt.Id)).OccasionId.Should().Be(next);
		(await world.ReadItemAsync(inherited.Id)).OccasionId.Should().Be(next);
		(await world.ReadItemAsync(tagged.Id)).OccasionId.Should().Be(own);
	}

	[Fact]
	public async Task SetReceipt_ChoiceChange_AppliesToSoftDeletedItemsButNotTheirOverrides()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		Guid previous = await AddOccasionAsync(world, isActive: true);
		Guid next = await AddOccasionAsync(world, isActive: true);
		ReceiptEntity receipt = await world.AddReceiptAsync(paidFrom: world.Food, occasion: previous);
		ReceiptItemEntity receiptSourced = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Fuel"));
		ReceiptItemEntity overridden = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Fuel", envelopeOverride: world.Food));
		await SoftDeleteItemAsync(world, receiptSourced.Id);
		await SoftDeleteItemAsync(world, overridden.Id);

		// Act
		await Service(world).SetReceiptChoicesAsync(receipt.Id, world.Car, next, CancellationToken.None);

		// Assert
		ReceiptItemEntity actualReceiptSourced = await world.ReadItemAsync(receiptSourced.Id);
		actualReceiptSourced.EnvelopeId.Should().Be(world.Car);
		actualReceiptSourced.EnvelopeSource.Should().Be(EnvelopeSource.Receipt);
		actualReceiptSourced.OccasionId.Should().Be(next);
		ReceiptItemEntity actualOverridden = await world.ReadItemAsync(overridden.Id);
		actualOverridden.EnvelopeId.Should().Be(world.Food);
		actualOverridden.EnvelopeSource.Should().Be(EnvelopeSource.Item);
	}

	// --- Item choices ---

	[Fact]
	public async Task SetItem_Envelope_IsRecordedAsItemOverride()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));

		// Act
		bool actual = await Service(world).SetItemChoicesAsync(created.Id, world.Car, null, CancellationToken.None);

		// Assert
		actual.Should().BeTrue();
		ReceiptItemEntity actualItem = await world.ReadItemAsync(created.Id);
		actualItem.EnvelopeId.Should().Be(world.Car);
		actualItem.EnvelopeSource.Should().Be(EnvelopeSource.Item);
	}

	[Fact]
	public async Task SetItem_NullOnOverride_ReturnsToReceiptPaidFrom()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync(paidFrom: world.Car);
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries", envelopeOverride: world.Food));

		// Act
		await Service(world).SetItemChoicesAsync(created.Id, null, null, CancellationToken.None);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Car);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Receipt);
	}

	[Fact]
	public async Task SetItem_NullOnOverrideWithoutPaidFrom_ReturnsToItemTypeDefault()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries", envelopeOverride: world.Car));

		// Act
		await Service(world).SetItemChoicesAsync(created.Id, null, null, CancellationToken.None);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Food);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Default);
	}

	[Fact]
	public async Task SetItem_NullOnOverrideWhenReceiptPaidFromIsArchived_ReturnsToItemTypeDefault()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync(paidFrom: world.Retired);
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries", envelopeOverride: world.Car));

		// Act
		await Service(world).SetItemChoicesAsync(created.Id, null, null, CancellationToken.None);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Food);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Default);
	}

	[Theory]
	[InlineData(EnvelopeSource.Default)]
	[InlineData(EnvelopeSource.Receipt)]
	[InlineData(EnvelopeSource.Historical)]
	public async Task SetItem_NullOnNonOverriddenItem_KeepsRecordedEnvelope(EnvelopeSource source)
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync(paidFrom: world.Car);
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));
		await world.SetStoredEnvelopeAsync(created.Id, world.Retired, source);

		// Act
		await Service(world).SetItemChoicesAsync(created.Id, null, null, CancellationToken.None);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Retired);
		actual.EnvelopeSource.Should().Be(source);
	}

	[Fact]
	public async Task SetItem_Occasion_IsSetThenClearedByNull()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		Guid occasion = await AddOccasionAsync(world, isActive: true);
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));
		BudgetChoiceService service = Service(world);

		// Act
		await service.SetItemChoicesAsync(created.Id, null, occasion, CancellationToken.None);
		Guid? afterSet = (await world.ReadItemAsync(created.Id)).OccasionId;
		await service.SetItemChoicesAsync(created.Id, null, null, CancellationToken.None);

		// Assert
		afterSet.Should().Be(occasion);
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.OccasionId.Should().BeNull();
		actual.EnvelopeId.Should().Be(world.Food, "an occasion change leaves a default envelope alone");
	}

	[Fact]
	public async Task SetItem_MissingItem_ReturnsFalse()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();

		// Act
		bool actual = await Service(world).SetItemChoicesAsync(Guid.NewGuid(), world.Car, null, CancellationToken.None);

		// Assert
		actual.Should().BeFalse();
	}

	[Fact]
	public async Task SetItem_NewArchivedOverride_IsRejectedWith400()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));

		// Act
		Func<Task> act = () => Service(world).SetItemChoicesAsync(created.Id, world.Retired, null, CancellationToken.None);

		// Assert
		(await act.Should().ThrowAsync<EnvelopeException>()).Which.Status.Should().Be(400);
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Food);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Default);
	}

	[Fact]
	public async Task SetItem_KeepingStoredArchivedOverride_IsAllowed()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));
		await world.SetStoredEnvelopeAsync(created.Id, world.Retired, EnvelopeSource.Item);

		// Act
		Func<Task> act = () => Service(world).SetItemChoicesAsync(created.Id, world.Retired, null, CancellationToken.None);

		// Assert
		await act.Should().NotThrowAsync();
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Retired);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Item);
	}

	private static async Task<Guid> AddOccasionAsync(EnvelopeWorld world, bool isActive)
	{
		Guid id = Guid.NewGuid();
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		db.Occasions.Add(new() { Id = id, Name = $"Occasion {id:N}", IsActive = isActive });
		await db.SaveChangesAsync();
		return id;
	}

	private static async Task SoftDeleteItemAsync(EnvelopeWorld world, Guid itemId)
	{
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		(await db.ReceiptItems.IgnoreAutoIncludes().SingleAsync(row => row.Id == itemId)).DeletedAt = DateTimeOffset.UtcNow;
		await db.SaveChangesAsync();
	}

	private static async Task<ReceiptEntity> ReadReceiptAsync(EnvelopeWorld world, Guid id)
	{
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		return await db.Receipts.AsNoTracking().SingleAsync(row => row.Id == id);
	}
}
