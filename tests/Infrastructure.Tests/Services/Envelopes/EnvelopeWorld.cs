using Infrastructure.Entities.Core;
using Infrastructure.Entities.Envelopes;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.Tests.Services.Envelopes;

/// <summary>
/// An InMemory database with a migrated taxonomy (Groceries › Produce, Fuel, Gifts), three envelopes
/// (Food and Car active, Retired archived) and item-type defaults Groceries → Food and Fuel → Car.
/// </summary>
internal sealed class EnvelopeWorld
{
	public Guid Revision { get; } = Guid.NewGuid();
	public Guid Groceries { get; } = Guid.NewGuid();
	public Guid Produce { get; } = Guid.NewGuid();
	public Guid Fuel { get; } = Guid.NewGuid();
	public Guid Gifts { get; } = Guid.NewGuid();
	public Guid Food { get; } = Guid.NewGuid();
	public Guid Car { get; } = Guid.NewGuid();
	public Guid Retired { get; } = Guid.NewGuid();
	public IDbContextFactory<ApplicationDbContext> Factory { get; }

	private EnvelopeWorld()
	{
		(Factory, MockCurrentUserAccessor accessor) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		accessor.UserId = "envelope-tester";
	}

	public static async Task<EnvelopeWorld> CreateAsync(bool withEnvelopes = true)
	{
		EnvelopeWorld world = new();
		await using ApplicationDbContext db = world.Factory.CreateDbContext();
		db.TaxonomyHeads.Add(new() { Id = 1, RevisionId = world.Revision });
		db.TaxonomyRevisions.Add(new() { Id = world.Revision, Sequence = 1, Actor = "seed", Reason = "seed", OperationId = Guid.NewGuid() });
		db.LegacyMigrations.Add(new()
		{
			Actor = "seed",
			OperationId = Guid.NewGuid(),
			OperationKind = LegacyTaxonomyMigrationService.InitialOperationKind,
			RevisionId = world.Revision,
		});
		foreach (Guid id in new[] { world.Groceries, world.Produce, world.Fuel, world.Gifts })
		{
			db.TaxonomyIdentities.Add(new() { Id = id });
		}
		db.TaxonomyNodes.AddRange(
			new TaxonomyNodeEntity { RevisionId = world.Revision, CategoryId = world.Groceries, Name = "Groceries" },
			new TaxonomyNodeEntity { RevisionId = world.Revision, CategoryId = world.Produce, ParentId = world.Groceries, Name = "Produce" },
			new TaxonomyNodeEntity { RevisionId = world.Revision, CategoryId = world.Fuel, Name = "Fuel" },
			new TaxonomyNodeEntity { RevisionId = world.Revision, CategoryId = world.Gifts, Name = "Gifts" });
		if (withEnvelopes)
		{
			db.Envelopes.AddRange(
				new EnvelopeEntity { Id = world.Food, Name = "Food", IsActive = true },
				new EnvelopeEntity { Id = world.Car, Name = "Car", IsActive = true },
				new EnvelopeEntity { Id = world.Retired, Name = "Retired", IsActive = false });
			db.ItemTypeEnvelopeDefaults.AddRange(
				new ItemTypeEnvelopeDefaultEntity { Id = Guid.NewGuid(), CategoryId = world.Groceries, EnvelopeId = world.Food },
				new ItemTypeEnvelopeDefaultEntity { Id = Guid.NewGuid(), CategoryId = world.Fuel, EnvelopeId = world.Car });
		}
		await db.SaveChangesAsync();
		return world;
	}

	public async Task<ReceiptEntity> AddReceiptAsync(Guid? paidFrom = null, Guid? occasion = null)
	{
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.PaidFromEnvelopeId = paidFrom;
		receipt.OccasionId = occasion;
		await using ApplicationDbContext db = Factory.CreateDbContext();
		db.Receipts.Add(receipt);
		await db.SaveChangesAsync();
		return receipt;
	}

	public static ReceiptItemEntity NewItem(Guid receiptId, string category, string? subcategory = null, Guid? envelopeOverride = null)
	{
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receiptId, subcategory: subcategory);
		item.Category = category;
		item.EnvelopeOverrideId = envelopeOverride;
		return item;
	}

	/// <summary>An incoming edit of a stored item, as the API mapper would build it.</summary>
	public static ReceiptItemEntity Edit(ReceiptItemEntity stored, string category, Guid? envelopeOverride = null) => new()
	{
		Id = stored.Id,
		ReceiptId = stored.ReceiptId,
		ReceiptItemCode = stored.ReceiptItemCode,
		Description = stored.Description,
		Quantity = stored.Quantity,
		UnitPrice = stored.UnitPrice,
		UnitPriceCurrency = stored.UnitPriceCurrency,
		TotalAmount = stored.TotalAmount,
		TotalAmountCurrency = stored.TotalAmountCurrency,
		Category = category,
		Subcategory = null,
		EnvelopeOverrideId = envelopeOverride,
	};

	public async Task<ReceiptItemEntity> ReadItemAsync(Guid id)
	{
		await using ApplicationDbContext db = Factory.CreateDbContext();
		return await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking().SingleAsync(item => item.Id == id);
	}

	public async Task SetStoredEnvelopeAsync(Guid itemId, Guid? envelopeId, Domain.Envelopes.EnvelopeSource source)
	{
		await using ApplicationDbContext db = Factory.CreateDbContext();
		ReceiptItemEntity item = await db.ReceiptItems.IgnoreAutoIncludes().SingleAsync(row => row.Id == itemId);
		item.EnvelopeId = envelopeId;
		item.EnvelopeSource = source;
		await db.SaveChangesAsync();
	}
}
