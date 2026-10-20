using Application.Normalization;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.NormalizationAi;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>Deterministic approved-source resolution; never calls a model.</summary>
internal static class NormalizationCatalogResolver
{
	internal readonly record struct Result(int Linked, int Attempted);
	internal static async Task<Result> ResolveAsync(IDbContextFactory<ApplicationDbContext> factory, CancellationToken ct)
	{
		await using ApplicationDbContext context = await factory.CreateDbContextAsync(ct);
		var catalogReservations = NormalizationCatalogReservations.ItemIds(context);
		var candidates = from item in context.ReceiptItems.IgnoreAutoIncludes()
						 join receipt in context.Receipts on item.ReceiptId equals receipt.Id
						 where item.DeletedAt == null && receipt.DeletedAt == null && item.NormalizedDescriptionId == null
						 && item.NormalizationOrigin == "unassigned" && item.NormalizationReviewReason != "deferred"
						 && !catalogReservations.Contains(item.Id)
						 && !context.NormalizationAiProposals.Any(proposal => proposal.ReceiptItemId == item.Id && proposal.State == "pending"
							&& context.NormalizationAiRuns.Any(run => run.Id == proposal.RunId && (run.State == "queued" || run.State == "running" || run.State == "ready" || run.State == "unknown")))
						 && (context.NormalizationSourceMappings.Any(mapping => mapping.State == "active"
							&& (mapping.Exclude || context.NormalizedDescriptions.Any(target => target.Id == mapping.TargetId && target.Status == NormalizedDescriptionStatus.Active))
							&& mapping.Description == item.Description.Trim(' ')
							&& mapping.Location == receipt.Location.Trim(' ')
							&& mapping.ItemCode == (item.ReceiptItemCode == null || item.ReceiptItemCode.Trim(' ') == "" ? null : item.ReceiptItemCode.Trim(' '))))
						 orderby item.Id
						 select item;
		var items = await candidates.AsNoTracking().Take(NormalizationCatalogPolicy.ChunkSize).ToListAsync(ct);
		await using NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, ct);
		string[] names = items.Select(item => item.Description.Trim(' ').ToLowerInvariant()).Distinct().ToArray();
		Guid[] targets = await context.NormalizedDescriptions.Where(target => names.Contains(target.CanonicalName.ToLower())
			|| context.NormalizationSourceMappings.Any(mapping => mapping.State == "active" && mapping.TargetId == target.Id && names.Contains(mapping.Description.ToLower())))
			.Select(target => target.Id).ToArrayAsync(ct);
		await NormalizationWriteGuard.LockTargetsAsync(context, targets, ct);
		if (context.Database.IsNpgsql())
		{
			Guid[] receiptIds = items.Select(item => item.ReceiptId).Distinct().Order().ToArray();
			await context.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM receipts."Receipts" WHERE "Id" = ANY ({receiptIds}) ORDER BY "Id" FOR NO KEY UPDATE""").ToListAsync(ct);
		}
		await NormalizationWriteGuard.LockItemsAsync(context, items.Select(item => item.Id).ToArray(), ct);
		Guid[] itemIds = items.Select(item => item.Id).ToArray();
		Guid[] parentIds = items.Select(item => item.ReceiptId).Distinct().ToArray();
		var observedRevisions = items.ToDictionary(item => item.Id, item => item.NormalizationRevision);
		var currentItems = await context.ReceiptItems.IgnoreAutoIncludes().IgnoreQueryFilters().Where(item => itemIds.Contains(item.Id)).ToListAsync(ct);
		var parents = await context.Receipts.AsNoTracking().Where(receipt => parentIds.Contains(receipt.Id) && receipt.DeletedAt == null)
			.ToDictionaryAsync(receipt => receipt.Id, ct);
		var reserved = (await context.NormalizationAiProposals.Where(proposal => itemIds.Contains(proposal.ReceiptItemId) && proposal.State == "pending"
			&& context.NormalizationAiRuns.Any(run => run.Id == proposal.RunId && (run.State == "queued" || run.State == "running" || run.State == "ready" || run.State == "unknown")))
			.Select(proposal => proposal.ReceiptItemId).ToListAsync(ct)).ToHashSet();
		reserved.UnionWith(await NormalizationCatalogReservations.ItemIds(context).Where(id => itemIds.Contains(id)).ToListAsync(ct));
		var sourceHashes = currentItems.Where(item => parents.ContainsKey(item.ReceiptId) && item.NormalizationRevision == observedRevisions[item.Id]
			&& item.DeletedAt == null && item.NormalizedDescriptionId == null && item.NormalizationOrigin == "unassigned" && item.NormalizationReviewReason != "deferred").Select(item => NormalizationCatalogPolicy.Hash(
			NormalizationCatalogPolicy.Normalize(new(parents[item.ReceiptId].Location, item.Description, item.ReceiptItemCode)))).Distinct().ToArray();
		var mappings = await context.NormalizationSourceMappings.Where(mapping => sourceHashes.Contains(mapping.SourceHash) && mapping.State == "active")
			.ToDictionaryAsync(mapping => mapping.SourceHash, ct);
		var activeTargets = await context.NormalizedDescriptions.Where(target => target.Status == NormalizedDescriptionStatus.Active && targets.Contains(target.Id))
			.Select(target => new { target.Id, Name = target.CanonicalName.ToLower() }).ToListAsync(ct);
		var activeIds = activeTargets.Select(target => target.Id).ToHashSet();
		int linked = 0;
		foreach (ReceiptItemEntity item in currentItems)
		{
			if (item.NormalizationRevision != observedRevisions[item.Id] || item.DeletedAt != null || item.NormalizedDescriptionId != null || item.NormalizationOrigin != "unassigned" || item.NormalizationReviewReason == "deferred") { continue; }
			if (reserved.Contains(item.Id) || !parents.TryGetValue(item.ReceiptId, out var receipt)) { continue; }
			NormalizationSourceKey key = NormalizationCatalogPolicy.Normalize(new(receipt.Location, item.Description, item.ReceiptItemCode));
			string hash = NormalizationCatalogPolicy.Hash(key);
			var mapping = mappings.GetValueOrDefault(hash);
			if (mapping is not null)
			{
				if (!mapping.Exclude && (mapping.TargetId is not Guid mappedTarget || !activeIds.Contains(mappedTarget))) { continue; }
				item.NormalizedDescriptionId = mapping.TargetId;
				item.NormalizationOrigin = mapping.Exclude ? "excluded" : "mapped";
				item.NormalizationMappingId = mapping.Id;
				item.NormalizedDescriptionMatchScore = null;
			}
			else { continue; }
			item.NormalizationAssignmentId = Guid.NewGuid();
			linked++;
		}
		await context.SaveChangesAsync(ct);
		await lease.CommitAsync(ct);
		return new(linked, items.Count);
	}
}
