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

	/// <summary>A new item as the API mapper builds it: request labels are retired, so it carries none.</summary>
	public static ReceiptItemEntity NewItem(Guid receiptId, Guid? envelopeOverride = null)
	{
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receiptId, subcategory: null);
		item.Category = "";
		item.EnvelopeOverrideId = envelopeOverride;
		return item;
	}

	/// <summary>An incoming edit of a stored item, as the API mapper would build it.</summary>
	public static ReceiptItemEntity Edit(ReceiptItemEntity stored, Guid? envelopeOverride = null) => new()
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
		Category = "",
		Subcategory = null,
		EnvelopeOverrideId = envelopeOverride,
	};

	/// <summary>
	/// Records a classification write the way every assignment chokepoint does: append the assignment, move the
	/// current pointer and let <see cref="ItemEnvelopeWriter.AfterItemTypeChangeAsync"/> make the envelope follow.
	/// An item's type changes only through classification; the explicit-intent path locks rows with PostgreSQL SQL,
	/// so this InMemory world stands in for it.
	/// </summary>
	public async Task ClassifyAsync(Guid itemId, Guid? categoryId)
	{
		await using ApplicationDbContext db = Factory.CreateDbContext();
		CurrentClassificationEntity? current = await db.CurrentClassifications.SingleOrDefaultAsync(row => row.ReceiptItemId == itemId);
		long sequence = current is null ? 1 : 1 + await db.Classifications.Where(row => row.Id == current.AssignmentId).Select(row => row.Sequence).SingleAsync();
		PurchaseClassificationEntity assignment = new()
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = itemId,
			Sequence = sequence,
			PreviousAssignmentId = current?.AssignmentId,
			CategoryId = categoryId,
			TaxonomyRevisionId = Revision,
			RecordedAt = DateTimeOffset.UtcNow,
			Actor = "envelope-tester",
			Source = "manual",
			Reason = "test",
			OperationId = Guid.NewGuid(),
		};
		db.Classifications.Add(assignment);
		if (current is null)
		{
			db.CurrentClassifications.Add(new() { ReceiptItemId = itemId, AssignmentId = assignment.Id });
		}
		else
		{
			current.AssignmentId = assignment.Id;
		}
		await ItemEnvelopeWriter.AfterItemTypeChangeAsync(db, itemId, categoryId, Revision, CancellationToken.None);
		await db.SaveChangesAsync();
	}

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
