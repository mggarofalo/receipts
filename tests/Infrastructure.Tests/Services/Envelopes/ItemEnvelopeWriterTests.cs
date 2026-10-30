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
/// Drives the persistence chokepoints end to end on InMemory: item saves record the envelope and the
/// legacy classification write (ClassificationService.AppendAsync) makes default envelopes follow the item type.
/// </summary>
public class ItemEnvelopeWriterTests
{
	private static ReceiptItemRepository Items(EnvelopeWorld world)
		=> new(world.Factory, Options.Create(new ClassificationOptions { WritesEnabled = true }));

	private static async Task<ReceiptItemEntity> CreateItemAsync(EnvelopeWorld world, ReceiptItemEntity item)
		=> (await Items(world).CreateAsync([item], CancellationToken.None)).Single();

	[Fact]
	public async Task Create_NoOverride_GetsItemTypeDefaultOnceClassified()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync();

		// Act
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));

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
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries", "Produce"));

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
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Gifts"));

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
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));

		// Act
		await Items(world).UpdateAsync([EnvelopeWorld.Edit(created, "Fuel")], CancellationToken.None);

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
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries", envelopeOverride: world.Car));

		// Act
		await Items(world).UpdateAsync([EnvelopeWorld.Edit(created, "Fuel")], CancellationToken.None);
		await Items(world).UpdateAsync([EnvelopeWorld.Edit(created, "Gifts")], CancellationToken.None);

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
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));

		// Act
		await Items(world).UpdateAsync([EnvelopeWorld.Edit(created, "Groceries", world.Car)], CancellationToken.None);

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
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries", envelopeOverride: world.Car));
		await ArchiveAsync(world, world.Car);

		// Act
		Func<Task> act = () => Items(world).UpdateAsync([EnvelopeWorld.Edit(created, "Groceries", world.Car)], CancellationToken.None);

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
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));

		// Act
		await Items(world).UpdateAsync([EnvelopeWorld.Edit(created, "Gifts")], CancellationToken.None);

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
		ReceiptItemEntity defaulted = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));
		ReceiptItemEntity overridden = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries", envelopeOverride: world.Food));
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
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));
		receipt.PaidFromEnvelopeId = null;

		// Act
		await new ReceiptRepository(world.Factory).UpdateAsync([receipt], CancellationToken.None);

		// Assert
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		(await db.Receipts.SingleAsync(row => row.Id == receipt.Id)).PaidFromEnvelopeId.Should().Be(world.Car);
		(await world.ReadItemAsync(created.Id)).EnvelopeSource.Should().Be(EnvelopeSource.Receipt);
	}

	[Fact]
	public async Task PaidFromCleared_ReceiptSourcedItemsReturnToTheirItemTypeDefault()
	{
		// Arrange
		EnvelopeWorld world = await EnvelopeWorld.CreateAsync();
		ReceiptEntity receipt = await world.AddReceiptAsync(paidFrom: world.Car);
		ReceiptItemEntity receiptSourced = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));
		ReceiptItemEntity overridden = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Fuel", envelopeOverride: world.Food));

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
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));
		await world.SetStoredEnvelopeAsync(created.Id, world.Retired, EnvelopeSource.Historical);

		// Act
		await Items(world).UpdateAsync([EnvelopeWorld.Edit(created, "Fuel")], CancellationToken.None);

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
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));
		await world.SetStoredEnvelopeAsync(created.Id, null, EnvelopeSource.Historical);

		// Act
		await Items(world).UpdateAsync([EnvelopeWorld.Edit(created, "Fuel")], CancellationToken.None);

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
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));
		await using (ApplicationDbContext db = world.Factory.CreateDbContext())
		{
			db.ItemTypeEnvelopeDefaults.Single(row => row.CategoryId == world.Groceries).EnvelopeId = world.Car;
			await db.SaveChangesAsync();
		}
		ReceiptItemEntity edit = EnvelopeWorld.Edit(created, "Groceries");
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
		Func<Task> act = () => CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries", envelopeOverride: choice));

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
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));

		// Act
		Func<Task> act = () => Items(world).UpdateAsync([EnvelopeWorld.Edit(created, "Groceries", world.Retired)], CancellationToken.None);

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
		ReceiptItemEntity item = EnvelopeWorld.NewItem(receipt.Id, "Groceries");
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
		ReceiptItemEntity created = await CreateItemAsync(world, EnvelopeWorld.NewItem(receipt.Id, "Groceries"));

		// Assert
		(await world.ReadItemAsync(created.Id)).OccasionId.Should().Be(occasion);
	}

	private static async Task ArchiveAsync(EnvelopeWorld world, Guid envelopeId)
	{
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		(await db.Envelopes.SingleAsync(row => row.Id == envelopeId)).IsActive = false;
		await db.SaveChangesAsync();
	}
}
