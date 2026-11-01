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
/// Drives the persistence chokepoints end to end on InMemory: item saves record the envelope and a
/// classification write (<see cref="EnvelopeWorld.ClassifyAsync"/>) makes default envelopes follow the item type.
/// </summary>
public class ItemEnvelopeWriterTests
{
	private static ReceiptItemRepository Items(EnvelopeWorld world)
		=> new(world.Factory, Options.Create(new ClassificationOptions { WritesEnabled = true }));

	/// <summary>Saves a new item, then gives it an item type through a classification write when one is named.</summary>
	private static async Task<ReceiptItemEntity> CreateItemAsync(EnvelopeWorld world, ReceiptItemEntity item, Guid? itemType = null)
	{
		ReceiptItemEntity created = (await Items(world).CreateAsync([item], CancellationToken.None)).Single();
		if (itemType is not null)
		{
			await world.ClassifyAsync(created.Id, itemType);
		}
		return created;
	}

	[Fact]
	public async Task Create_NoOverride_GetsItemTypeDefaultOnceClassified()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();

		// Act
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Food);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Default);
	}

	[Fact]
	public async Task Create_ChildItemType_UsesNearestAncestorDefault()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();

		// Act
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Produce);

		// Assert
		(await world.ReadItemAsync(created.Id)).EnvelopeId.Should().Be(world.Food);
	}

	[Fact]
	public async Task Create_ItemTypeWithoutDefault_IsUnresolvedDefault()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();

		// Act
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Gifts);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().BeNull();
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Default);
	}

	[Fact]
	public async Task Reclassify_DefaultSourcedItem_FollowsNewItemTypeDefault()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);

		// Act
		await world.ClassifyAsync(created.Id, world.Fuel);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Car);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Default);
	}

	[Fact]
	public async Task Override_IsRecordedAsItemSourceAndSurvivesReclassification()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, envelopeOverride: world.Car), world.Groceries);

		// Act
		await world.ClassifyAsync(created.Id, world.Fuel);
		await world.ClassifyAsync(created.Id, world.Gifts);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Car);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Item);
	}

	[Fact]
	public async Task Edit_WithOverride_ReplacesDefaultWithItemSource()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);

		// Act
		await Items(world).UpdateAsync([EnvelopeWorld.Edit(created, world.Car)], CancellationToken.None);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Car);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Item);
	}

	[Fact]
	public async Task Edit_KeepingAnOverrideWhoseEnvelopeWasArchived_IsAllowed()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, envelopeOverride: world.Car), world.Groceries);
		await ArchiveAsync(world, world.Car);

		// Act
		Func<Task> act = () => Items(world).UpdateAsync([EnvelopeWorld.Edit(created, world.Car)], CancellationToken.None);

		// Assert
		await act.Should().NotThrowAsync();
		(await world.ReadItemAsync(created.Id)).EnvelopeId.Should().Be(world.Car);
	}

	[Fact]
	public async Task ReceiptPaidFrom_NewItemIsReceiptSourcedAndSurvivesReclassification()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync(paidFrom: world.Car);
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);

		// Act
		await world.ClassifyAsync(created.Id, world.Gifts);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Car);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Receipt);
	}

	[Fact]
	public async Task ReceiptPaidFromChosenLater_AppliesToAllButOverrides()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity defaulted = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);
		ReceiptItemEntity overridden = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, envelopeOverride: world.Food), world.Groceries);
		receipt.PaidFromEnvelopeId = world.Car;

		// Act
		await new ReceiptRepository(world.Factory).UpdateAsync([receipt], CancellationToken.None);

		// Assert
		ReceiptItemEntity actualDefaulted = await world.ReadItemAsync(defaulted.Id);
		actualDefaulted.EnvelopeId.Should().Be(world.Car);
		actualDefaulted.EnvelopeSource.Should().Be(EnvelopeSource.Receipt);
		ReceiptItemEntity actualOverridden = await world.ReadItemAsync(overridden.Id);
		actualOverridden.EnvelopeId.Should().Be(world.Food);
		actualOverridden.EnvelopeSource.Should().Be(EnvelopeSource.Item);
	}

	[Fact]
	public async Task ReceiptUpdate_OmittedPaidFrom_KeepsStoredChoice()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync(paidFrom: world.Car);
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);
		receipt.PaidFromEnvelopeId = null;

		// Act
		await new ReceiptRepository(world.Factory).UpdateAsync([receipt], CancellationToken.None);

		// Assert
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		(await db.Receipts.SingleAsync(row => row.Id == receipt.Id)).PaidFromEnvelopeId.Should().Be(world.Car);
		(await world.ReadItemAsync(created.Id)).EnvelopeSource.Should().Be(EnvelopeSource.Receipt);
	}

	[Fact]
	public async Task Create_OnReceiptWhosePaidFromAndOccasionWereArchived_GetsItemTypeDefaultAndNoOccasion()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		Guid occasion = Guid.NewGuid();
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			db.Occasions.Add(new() { Id = occasion, Name = "Birthday", IsActive = true });
			await db.SaveChangesAsync();
		}
		ReceiptEntity receipt = await world.AddReceiptAsync(paidFrom: world.Car, occasion: occasion);
		await ArchiveAsync(world, world.Car);
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			(await db.Occasions.SingleAsync(row => row.Id == occasion)).IsActive = false;
			await db.SaveChangesAsync();
		}

		// Act
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Food);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Default);
		actual.OccasionId.Should().BeNull();
	}

	[Fact]
	public async Task PaidFromCleared_ReceiptSourcedItemsReturnToTheirItemTypeDefault()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync(paidFrom: world.Car);
		ReceiptItemEntity receiptSourced = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);
		ReceiptItemEntity overridden = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, envelopeOverride: world.Food), world.Fuel);

		// Act
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			ReceiptEntity stored = await db.Receipts.SingleAsync(row => row.Id == receipt.Id);
			stored.PaidFromEnvelopeId = null;
			await ItemEnvelopeWriter.AfterReceiptChoiceChangeAsync(db, stored, world.Car, null, CancellationToken.None);
			await db.SaveChangesAsync();
		}

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(receiptSourced.Id);
		actual.EnvelopeId.Should().Be(world.Food);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Default);
		ReceiptItemEntity actualOverride = await world.ReadItemAsync(overridden.Id);
		actualOverride.EnvelopeId.Should().Be(world.Food);
		actualOverride.EnvelopeSource.Should().Be(EnvelopeSource.Item);
	}

	[Fact]
	public async Task Reclassify_HistoricalItemWithEnvelope_KeepsIt()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);
		await world.SetStoredEnvelopeAsync(created.Id, world.Retired, EnvelopeSource.Historical);

		// Act
		await world.ClassifyAsync(created.Id, world.Fuel);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Retired);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Historical);
	}

	[Fact]
	public async Task Reclassify_HistoricalItemWithoutEnvelope_FollowsNewItemType()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);
		await world.SetStoredEnvelopeAsync(created.Id, null, EnvelopeSource.Historical);

		// Act
		await world.ClassifyAsync(created.Id, world.Fuel);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Car);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Default);
	}

	[Fact]
	public async Task OrdinaryEdit_WithoutTypeChange_KeepsRecordedEnvelope()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			db.ItemTypeEnvelopeDefaults.Single(row => row.CategoryId == world.Groceries).EnvelopeId = world.Car;
			await db.SaveChangesAsync();
		}
		ReceiptItemEntity edit = EnvelopeWorld.Edit(created);
		edit.Description = "Renamed";

		// Act
		await Items(world).UpdateAsync([edit], CancellationToken.None);

		// Assert
		(await world.ReadItemAsync(created.Id)).EnvelopeId.Should().Be(world.Food, "history does not move when defaults change");
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Create_ArchivedOrMissingOverride_IsRejectedWith400(bool archived)
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		Guid choice = archived ? world.Retired : Guid.NewGuid();

		// Act
		Func<Task> act = () => CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, envelopeOverride: choice), world.Groceries);

		// Assert
		(await act.Should().ThrowAsync<EnvelopeException>()).Which.Status.Should().Be(400);
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		(await db.ReceiptItems.AnyAsync()).Should().BeFalse();
	}

	[Fact]
	public async Task Edit_NewArchivedOverride_IsRejectedWith400()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);

		// Act
		Func<Task> act = () => Items(world).UpdateAsync([EnvelopeWorld.Edit(created, world.Retired)], CancellationToken.None);

		// Assert
		(await act.Should().ThrowAsync<EnvelopeException>()).Which.Status.Should().Be(400);
		(await world.ReadItemAsync(created.Id)).EnvelopeId.Should().Be(world.Food);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Receipt_ArchivedOrMissingPaidFrom_IsRejectedWith400(bool onCreate)
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptRepository receipts = new(world.Factory);
		ReceiptEntity receipt = onCreate ? SampleData.Entities.ReceiptEntityGenerator.Generate() : await world.AddReceiptAsync();
		receipt.PaidFromEnvelopeId = onCreate ? world.Retired : Guid.NewGuid();

		// Act
		Func<Task> act = () => onCreate
			? receipts.CreateAsync([receipt], CancellationToken.None)
			: receipts.UpdateAsync([receipt], CancellationToken.None);

		// Assert
		(await act.Should().ThrowAsync<EnvelopeException>()).Which.Status.Should().Be(400);
	}

	[Fact]
	public async Task Create_ArchivedOccasion_IsRejectedWith400()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		Guid occasion = Guid.NewGuid();
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			db.Occasions.Add(new() { Id = occasion, Name = "Past", IsActive = false });
			await db.SaveChangesAsync();
		}
		ReceiptItemEntity item = EnvelopeWorld.NewItem(receipt.Id);
		item.OccasionId = occasion;

		// Act
		Func<Task> act = () => CreateItemAsync(world, item);

		// Assert
		(await act.Should().ThrowAsync<EnvelopeException>()).Which.Message.Should().Contain("Occasion");
	}

	[Fact]
	public async Task Create_InheritsReceiptOccasion()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		Guid occasion = Guid.NewGuid();
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			db.Occasions.Add(new() { Id = occasion, Name = "Birthday", IsActive = true });
			await db.SaveChangesAsync();
		}
		ReceiptEntity receipt = await world.AddReceiptAsync(occasion: occasion);

		// Act
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);

		// Assert
		(await world.ReadItemAsync(created.Id)).OccasionId.Should().Be(occasion);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Create_ItemTypeDefaultEnvelopeArchivedOrDeleted_IsUnresolvedDefault(bool archived)
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		if (archived)
		{
			await ArchiveAsync(world, world.Food);
		}
		else
		{
			await SoftDeleteAsync(world, world.Food);
		}

		// Act
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().BeNull();
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Default);
	}

	[Fact]
	public async Task Create_ChildDefaultEnvelopeArchived_FallsBackToAncestorDefault()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			db.ItemTypeEnvelopeDefaults.Add(new() { Id = Guid.NewGuid(), CategoryId = world.Produce, EnvelopeId = world.Retired });
			await db.SaveChangesAsync();
		}

		// Act
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Produce);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Food);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Default);
	}

	[Fact]
	public async Task Create_AfterDefaultEnvelopeRestored_GetsItAgain()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		await ArchiveAsync(world, world.Food);
		ReceiptItemEntity whileArchived = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			(await db.Envelopes.SingleAsync(row => row.Id == world.Food)).IsActive = true;
			await db.SaveChangesAsync();
		}

		// Act
		ReceiptItemEntity afterRestore = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);

		// Assert
		(await world.ReadItemAsync(whileArchived.Id)).EnvelopeId.Should().BeNull("restoring does not move recorded history");
		ReceiptItemEntity actual = await world.ReadItemAsync(afterRestore.Id);
		actual.EnvelopeId.Should().Be(world.Food);
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Default);
	}

	[Fact]
	public async Task Create_WithoutACategoryChoice_IsUnresolvedDefault()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();

		// Act
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id));

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().BeNull();
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Default);
	}

	[Fact]
	public async Task Edit_CarryingDifferentLegacyLabels_DoesNotChangeItemTypeOrEnvelope()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id), world.Groceries);
		ReceiptItemEntity edit = EnvelopeWorld.Edit(created);
		edit.Category = "Fuel";

		// Act
		await Items(world).UpdateAsync([edit], CancellationToken.None);

		// Assert
		ReceiptItemEntity actual = await world.ReadItemAsync(created.Id);
		actual.EnvelopeId.Should().Be(world.Food, "labels no longer reclassify an item");
		actual.EnvelopeSource.Should().Be(EnvelopeSource.Default);
		actual.Category.Should().BeEmpty();
	}

	private static async Task ArchiveAsync(EnvelopeWorld world, Guid envelopeId)
	{
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		(await db.Envelopes.SingleAsync(row => row.Id == envelopeId)).IsActive = false;
		await db.SaveChangesAsync();
	}

	private static async Task SoftDeleteAsync(EnvelopeWorld world, Guid envelopeId)
	{
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		(await db.Envelopes.SingleAsync(row => row.Id == envelopeId)).DeletedAt = DateTimeOffset.UtcNow;
		await db.SaveChangesAsync();
	}
}
