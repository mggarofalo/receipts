using Application.Envelopes;
using Domain.Envelopes;
using Domain.Taxonomy;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Envelopes;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public sealed record EnvelopeBackfillResult(int Items, int ItemsWithEnvelope, int Envelopes, int Defaults);

/// <summary>
/// Applies <see cref="EnvelopeBackfillPlanner"/> to the database: historical items get the envelope
/// their recorded classification pushes to today, and node mappings seed item-type defaults.
/// The caller owns the transaction.
/// </summary>
internal static class EnvelopeBackfill
{
	/// <summary>
	/// Deploy data step: runs once per database that predates envelopes, recorded by the singleton
	/// marker. A database with no historical purchases or node mappings has nothing to derive, so it
	/// is left untouched (a fresh install stays a pristine restore target).
	/// </summary>
	public static async Task<EnvelopeBackfillResult?> RunIfPendingAsync(ApplicationDbContext db, TimeProvider clock, CancellationToken ct)
	{
		bool pending = !await db.EnvelopeBackfills.AnyAsync(ct)
			&& (await db.ReceiptItems.IgnoreQueryFilters().AnyAsync(item => item.EnvelopeSource == EnvelopeSource.Historical, ct)
				|| await db.YnabCategoryMappings.AnyAsync(ct));
		return pending ? await RunAsync(db, clock, ct) : null;
	}

	public static async Task<EnvelopeBackfillResult> RunAsync(ApplicationDbContext db, TimeProvider clock, CancellationToken ct)
	{
		if (await db.Envelopes.IgnoreQueryFilters().AnyAsync(ct) || await db.ItemTypeEnvelopeDefaults.AnyAsync(ct))
		{
			throw new InvalidOperationException("The envelope backfill requires a database without envelopes or item-type defaults.");
		}

		List<ReceiptItemEntity> items = await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes()
			.Where(item => item.EnvelopeSource == EnvelopeSource.Historical && item.EnvelopeId == null)
			.ToListAsync(ct);
		await CurrentClassificationReader.PopulateAsync(db, items, ct);
		List<LegacyYnabCategoryMapping> mappings = await LoadLegacyMappingsAsync(db, ct);
		Guid? revisionId = await db.TaxonomyHeads.AsNoTracking().Select(head => (Guid?)head.RevisionId).SingleOrDefaultAsync(ct);
		List<TaxonomyNode> nodes = revisionId is Guid revision
			? await db.TaxonomyNodes.AsNoTracking().Where(node => node.RevisionId == revision)
				.Select(node => new TaxonomyNode(node.CategoryId, node.ParentId, node.Name, node.Definition, node.Archived)).ToListAsync(ct)
			: [];
		string? selectedBudget = await db.YnabSelectedBudgets.AsNoTracking().OrderByDescending(row => row.UpdatedAt)
			.Select(row => row.BudgetId).FirstOrDefaultAsync(ct);

		EnvelopeBackfillPlan plan = EnvelopeBackfillPlanner.Plan(
			items.Select(item => new EnvelopeBackfillItem(item.Id, item.CurrentClassification)).ToList(),
			mappings, revisionId ?? Guid.Empty, nodes, selectedBudget, Guid.NewGuid);

		DateTimeOffset now = clock.GetUtcNow();
		foreach (PlannedEnvelope envelope in plan.Envelopes)
		{
			db.Envelopes.Add(new() { Id = envelope.Id, Name = envelope.Name, IsActive = envelope.IsActive });
			db.YnabEnvelopeMappings.AddRange(envelope.Links.Select(link => new YnabEnvelopeMappingEntity
			{
				Id = Guid.NewGuid(),
				EnvelopeId = envelope.Id,
				YnabBudgetId = link.BudgetId,
				YnabCategoryId = link.YnabCategoryId,
				YnabCategoryName = link.YnabCategoryName,
				YnabCategoryGroupName = link.YnabCategoryGroupName,
				CreatedAt = now,
				UpdatedAt = now,
			}));
		}
		db.ItemTypeEnvelopeDefaults.AddRange(plan.Defaults.Select(pair => new ItemTypeEnvelopeDefaultEntity
		{
			Id = Guid.NewGuid(),
			CategoryId = pair.Key,
			EnvelopeId = pair.Value,
			CreatedAt = now,
			UpdatedAt = now,
		}));
		foreach (ReceiptItemEntity item in items)
		{
			item.EnvelopeId = plan.ItemEnvelopes[item.Id];
		}
		int itemsWithEnvelope = items.Count(item => item.EnvelopeId is not null);
		EnvelopeBackfillEntity? marker = await db.EnvelopeBackfills.SingleOrDefaultAsync(ct);
		if (marker is null)
		{
			marker = new() { Id = 1 };
			db.EnvelopeBackfills.Add(marker);
		}
		marker.CompletedAt = now;
		marker.ItemCount = items.Count;
		marker.EnvelopeCount = plan.Envelopes.Count;
		marker.DefaultCount = plan.Defaults.Count;
		await db.SaveChangesAsync(ct);
		return new(items.Count, itemsWithEnvelope, plan.Envelopes.Count, plan.Defaults.Count);
	}

	internal static async Task<List<LegacyYnabCategoryMapping>> LoadLegacyMappingsAsync(ApplicationDbContext db, CancellationToken ct)
		=> await db.YnabCategoryMappings.AsNoTracking()
			.Select(row => new LegacyYnabCategoryMapping(row.Id, row.ReceiptsCategoryId, row.YnabBudgetId,
				row.YnabCategoryId, row.YnabCategoryName, row.YnabCategoryGroupName, row.UpdatedAt))
			.ToListAsync(ct);
}

/// <summary>Loads the whole database (including trash) for <see cref="EnvelopeParity"/>.</summary>
public static class EnvelopeParityReader
{
	public static async Task<EnvelopeParityReport> CheckAsync(ApplicationDbContext db, CancellationToken ct)
	{
		List<ReceiptItemEntity> items = await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking().ToListAsync(ct);
		await CurrentClassificationReader.PopulateAsync(db, items, ct);
		List<LegacyYnabCategoryMapping> mappings = await EnvelopeBackfill.LoadLegacyMappingsAsync(db, ct);
		Dictionary<(Guid, string), string> links = await db.YnabEnvelopeMappings.AsNoTracking()
			.ToDictionaryAsync(row => (row.EnvelopeId, row.YnabBudgetId), row => row.YnabCategoryId, ct);
		int envelopes = await db.Envelopes.IgnoreQueryFilters().CountAsync(ct);
		return EnvelopeParity.Check(items.Select(item => new EnvelopeParityItem(item.Id, item.CurrentClassification, item.EnvelopeId)).ToList(),
			mappings, envelopes, links);
	}

	/// <summary>Every item's YNAB category through its envelope, for <see cref="EnvelopeBaseline"/>.</summary>
	public static async Task<IReadOnlyList<EnvelopeYnabRoute>> RoutesAsync(ApplicationDbContext db, CancellationToken ct)
	{
		var items = await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
			.Select(item => new { item.Id, item.EnvelopeId }).ToListAsync(ct);
		Dictionary<(Guid, string), string> links = await db.YnabEnvelopeMappings.AsNoTracking()
			.ToDictionaryAsync(row => (row.EnvelopeId, row.YnabBudgetId), row => row.YnabCategoryId, ct);
		return EnvelopeBaseline.Routes(items.Select(item => (item.Id, item.EnvelopeId)), links);
	}
}
