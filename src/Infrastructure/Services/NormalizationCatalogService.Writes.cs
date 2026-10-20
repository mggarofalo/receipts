using Application.Models.CommittedChanges;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.NormalizationAi;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public sealed partial class NormalizationCatalogService
{
	public async Task<NormalizationCatalogOperation> ApproveAsync(string actor, Guid id, Guid expectedRevision, CancellationToken cancellationToken)
	{
		NormalizationCatalogOperation result;
		await using (ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken))
		await using (NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken))
		{
			await AuthorizeAsync(context, actor, cancellationToken);
			var operation = await OperationAsync(context, id, cancellationToken);
			if (operation.State is "applying" or "completed") { return await ModelAsync(context, operation, cancellationToken); }
			RequireRevision(operation, expectedRevision);
			NormalizationCatalogPolicy.RequireAction(operation.State, "approve");
			await NormalizationWriteGuard.LockTargetsAsync(context, operation.TargetId is Guid existingTarget ? [existingTarget] : [], cancellationToken);
			await ValidateTargetAsync(context, operation, cancellationToken);
			await ValidateSuggestionAsync(context, actor, operation, cancellationToken);
			string hash = NormalizationCatalogPolicy.Hash(Key(operation));
			if (await context.NormalizationSourceMappings.AnyAsync(mapping => mapping.SourceHash == hash && mapping.State == "active", cancellationToken))
			{ throw Error(409, "mapping_exists", "This source already has an approved mapping. Revoke it before approving a fresh historical operation; each operation owns its own mapping."); }
			if (!operation.Exclude && !operation.TargetId.HasValue)
			{
				NormalizedDescriptionEntity target = new()
				{
					Id = Guid.NewGuid(),
					CanonicalName = operation.NewCanonicalName!,
					Status = NormalizedDescriptionStatus.Active,
					CreatedAt = timeProvider.GetUtcNow()
				};
				context.NormalizedDescriptions.Add(target);
				operation.TargetId = target.Id;
			}
			NormalizationSourceMappingEntity mapping = new()
			{
				Id = Guid.NewGuid(),
				SourceHash = hash,
				Location = operation.Location,
				Description = operation.Description,
				ItemCode = operation.ItemCode,
				TargetId = operation.TargetId,
				Exclude = operation.Exclude,
				ApprovedBy = actor,
				ApprovedAt = timeProvider.GetUtcNow()
			};
			context.NormalizationSourceMappings.Add(mapping);
			operation.MappingId = mapping.Id;
			operation.State = "applying";
			if (operation.SuggestionRunId is Guid runId)
			{
				var run = await context.NormalizationAiRuns.Include(run => run.Proposals).SingleAsync(run => run.Id == runId, cancellationToken);
				var proposal = run.Proposals.Single(proposal => proposal.Id == operation.SuggestionProposalId);
				bool acceptedIdentity = !operation.Exclude && (proposal.Action == "reuse"
						? proposal.TargetId == operation.TargetId && operation.NewCanonicalName is null
						: proposal.Action == "create" && proposal.CanonicalName == operation.NewCanonicalName);
				proposal.State = acceptedIdentity ? "applied" : "overridden";
				if (run.Proposals.All(proposal => proposal.State != "pending"))
				{
					run.State = "applied"; run.CompletedAt = timeProvider.GetUtcNow(); run.Revision = Guid.NewGuid();
				}
				// A sibling approval does not invalidate other unchanged proposal previews.
			}
			await context.SaveChangesAsync(cancellationToken);
			await ApplyChunkAsync(context, operation, cancellationToken);
			await lease.CommitAsync(cancellationToken);
			result = await ModelAsync(context, operation, cancellationToken);
		}
		await PublishAsync();
		return result;
	}

	public async Task<NormalizationCatalogOperation> ResumeAsync(string actor, Guid id, CancellationToken cancellationToken)
	{
		NormalizationCatalogOperation result;
		await using (ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken))
		await using (NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken))
		{
			await AuthorizeAsync(context, actor, cancellationToken);
			var operation = await OperationAsync(context, id, cancellationToken);
			NormalizationCatalogPolicy.RequireAction(operation.State, "resume");
			if (operation.State == "paused") { operation.State = "applying"; }
			if (operation.State == "undo_paused") { operation.State = "undoing"; }
			operation.ErrorCode = null; operation.ErrorDetail = null;
			if (operation.State == "applying") { await ApplyChunkAsync(context, operation, cancellationToken); }
			else if (operation.State == "undoing") { await UndoChunkAsync(context, operation, cancellationToken); }
			else if (operation.State is not ("completed" or "undone")) { throw Error(409, "operation_state", "This operation cannot be resumed."); }
			await lease.CommitAsync(cancellationToken);
			result = await ModelAsync(context, operation, cancellationToken);
		}
		await PublishAsync();
		return result;
	}

	private static async Task<NormalizationCatalogOperationEntity> OperationAsync(ApplicationDbContext context, Guid id, CancellationToken ct)
		=> await context.NormalizationCatalogOperations.SingleOrDefaultAsync(operation => operation.Id == id, ct)
			?? throw Error(404, "operation_not_found", "The catalog operation was not found.");

	private static async Task LockReceiptsAsync(ApplicationDbContext context, Guid[] ids, CancellationToken ct)
	{
		if (!context.Database.IsNpgsql() || ids.Length == 0) { return; }
		await context.Database.SqlQuery<Guid>($"""
			SELECT "Id" AS "Value" FROM receipts."Receipts" WHERE "Id" = ANY ({ids}) ORDER BY "Id" FOR NO KEY UPDATE
			""").ToListAsync(ct);
	}

	private static async Task ApplyChunkAsync(ApplicationDbContext context, NormalizationCatalogOperationEntity operation, CancellationToken ct)
	{
		var mapping = await context.NormalizationSourceMappings.SingleAsync(value => value.Id == operation.MappingId, ct);
		if (mapping.State != "active") { throw Error(409, "mapping_revoked", "This mapping was revoked. Cancel remaining historical work."); }
		var members = await context.NormalizationCatalogMembers.Where(member => member.OperationId == operation.Id && member.State == "pending")
			.OrderBy(member => member.ItemId).Take(NormalizationCatalogPolicy.ChunkSize).ToListAsync(ct);
		Guid[] ids = members.Select(member => member.ItemId).ToArray();
		await NormalizationWriteGuard.LockTargetsAsync(context, mapping.TargetId is Guid targetId ? [targetId] : [], ct);
		if (mapping.TargetId != operation.TargetId || mapping.Exclude != operation.Exclude)
		{
			operation.State = "cancelled";
			operation.Revision = Guid.NewGuid();
			operation.ErrorCode = "target_changed";
			operation.ErrorDetail = "The reusable mapping changed identity. Revoke the current mapping, then review a fresh preview before continuing historical work.";
			await context.SaveChangesAsync(ct);
			return;
		}
		if (!mapping.Exclude && !await context.NormalizedDescriptions.AnyAsync(target => target.Id == mapping.TargetId && target.Status == NormalizedDescriptionStatus.Active, ct))
		{ throw Error(409, "target_changed", "The approved target is no longer active. Cancel and review a new decision."); }
		await LockReceiptsAsync(context, members.Select(member => member.ReceiptId).Distinct().ToArray(), ct);
		await NormalizationWriteGuard.LockItemsAsync(context, ids, ct);
		var items = await context.ReceiptItems.IgnoreAutoIncludes().IgnoreQueryFilters().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, ct);
		Guid[] receiptIds = members.Select(member => member.ReceiptId).Distinct().ToArray();
		var parents = await context.Receipts.IgnoreQueryFilters().AsNoTracking().Where(receipt => receiptIds.Contains(receipt.Id))
			.Select(receipt => new { receipt.Id, receipt.DeletedAt, receipt.NormalizationRevision, receipt.Location }).ToDictionaryAsync(receipt => receipt.Id, ct);
		var decisions = await ReadDecisionsAsync(context, ids, operation.IncludeLegacyUnknown, ct);
		foreach (var member in members)
		{
			ReceiptItemEntity? item = items.GetValueOrDefault(member.ItemId);
			bool parentCurrent = parents.TryGetValue(member.ReceiptId, out var parent) && parent.DeletedAt == null
				&& parent.NormalizationRevision == member.ReceiptRevision && parent.Location.Trim(' ') == operation.Location;
			if (item is null || item.DeletedAt != null || item.NormalizationRevision != member.SourceRevision || !parentCurrent
				|| !decisions[item.Id].Eligible)
			{ member.State = "conflicted"; continue; }
			item.NormalizedDescriptionId = operation.TargetId;
			item.NormalizedDescriptionMatchScore = null;
			item.NormalizationOrigin = operation.Exclude ? "excluded" : "mapped";
			item.NormalizationMappingId = mapping.Id;
			item.NormalizationAssignmentId = Guid.NewGuid();
			member.AssignmentId = item.NormalizationAssignmentId;
			member.State = "applied";
		}
		await context.SaveChangesAsync(ct);
		// Re-read trigger-generated revisions in one projection after the chunk save.
		var appliedRevisions = await context.ReceiptItems.AsNoTracking().Where(item => ids.Contains(item.Id))
			.ToDictionaryAsync(item => item.Id, item => item.NormalizationRevision, ct);
		foreach (var member in members.Where(member => member.State == "applied"))
		{
			member.AppliedRevision = appliedRevisions[member.ItemId];
		}
		operation.State = await context.NormalizationCatalogMembers.AnyAsync(member => member.OperationId == operation.Id && member.State == "pending", ct)
			? "applying" : "completed";
		operation.Revision = Guid.NewGuid();
		await context.SaveChangesAsync(ct);
	}

	public async Task<NormalizationCatalogOperation> CancelAsync(string actor, Guid id, Guid expectedRevision, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
		await AuthorizeAsync(context, actor, cancellationToken);
		var operation = await OperationAsync(context, id, cancellationToken);
		if (operation.State == "cancelled") { return await ModelAsync(context, operation, cancellationToken); }
		RequireRevision(operation, expectedRevision);
		NormalizationCatalogPolicy.RequireAction(operation.State, "cancel");
		await RevokeOperationMappingAsync(context, operation, cancellationToken);
		operation.State = "cancelled"; operation.Revision = Guid.NewGuid();
		await context.SaveChangesAsync(cancellationToken);
		await lease.CommitAsync(cancellationToken);
		return await ModelAsync(context, operation, cancellationToken);
	}

	public async Task<NormalizationSourceMapping> RevokeAsync(string actor, Guid id, Guid expectedRevision, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
		await AuthorizeAsync(context, actor, cancellationToken);
		var mapping = await context.NormalizationSourceMappings.SingleOrDefaultAsync(value => value.Id == id, cancellationToken)
			?? throw Error(404, "mapping_not_found", "The mapping was not found.");
		if (mapping.State != "revoked")
		{
			if (mapping.Revision != expectedRevision) { throw Error(409, "mapping_changed", "The mapping changed. Refresh before revoking it."); }
			mapping.State = "revoked"; mapping.Revision = Guid.NewGuid();
			await context.SaveChangesAsync(cancellationToken);
			await lease.CommitAsync(cancellationToken);
		}
		return await MappingModelAsync(context, mapping, cancellationToken);
	}
	private static async Task RevokeOperationMappingAsync(ApplicationDbContext context, NormalizationCatalogOperationEntity operation, CancellationToken ct)
	{
		if (operation.MappingId is not Guid mappingId) { return; }
		var mapping = await context.NormalizationSourceMappings.SingleAsync(value => value.Id == mappingId, ct);
		if (mapping.State == "active") { mapping.State = "revoked"; mapping.Revision = Guid.NewGuid(); }
	}

	public async Task<NormalizationCatalogOperation> UndoAsync(string actor, Guid id, Guid expectedRevision, CancellationToken cancellationToken)
	{
		NormalizationCatalogOperation result;
		await using (ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken))
		await using (NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken))
		{
			await AuthorizeAsync(context, actor, cancellationToken);
			var operation = await OperationAsync(context, id, cancellationToken);
			if (operation.State == "undone") { return await ModelAsync(context, operation, cancellationToken); }
			RequireRevision(operation, expectedRevision);
			NormalizationCatalogPolicy.RequireAction(operation.State, "undo");
			if (operation.MappingId is null) { throw Error(409, "operation_state", "Only an approved operation can be undone."); }
			await RevokeOperationMappingAsync(context, operation, cancellationToken);
			operation.State = "undoing";
			await UndoChunkAsync(context, operation, cancellationToken);
			await lease.CommitAsync(cancellationToken);
			result = await ModelAsync(context, operation, cancellationToken);
		}
		await PublishAsync();
		return result;
	}
	private static async Task UndoChunkAsync(ApplicationDbContext context, NormalizationCatalogOperationEntity operation, CancellationToken ct)
	{
		var members = await context.NormalizationCatalogMembers.Where(member => member.OperationId == operation.Id && member.State == "applied")
			.OrderBy(member => member.ItemId).Take(NormalizationCatalogPolicy.ChunkSize).ToListAsync(ct);
		Guid[] ids = members.Select(member => member.ItemId).ToArray();
		await NormalizationWriteGuard.LockTargetsAsync(context, members.Where(member => member.PreviousTargetId.HasValue).Select(member => member.PreviousTargetId!.Value).Distinct().ToArray(), ct);
		await LockReceiptsAsync(context, members.Select(member => member.ReceiptId).Distinct().ToArray(), ct);
		await NormalizationWriteGuard.LockItemsAsync(context, ids, ct);
		var items = await context.ReceiptItems.IgnoreAutoIncludes().IgnoreQueryFilters().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, ct);
		Guid[] receiptIds = members.Select(member => member.ReceiptId).Distinct().ToArray();
		var parents = await context.Receipts.IgnoreQueryFilters().AsNoTracking().Where(receipt => receiptIds.Contains(receipt.Id))
			.Select(receipt => new { receipt.Id, receipt.DeletedAt, receipt.NormalizationRevision }).ToDictionaryAsync(receipt => receipt.Id, ct);
		Guid[] previousTargets = members.Where(member => member.PreviousTargetId.HasValue).Select(member => member.PreviousTargetId!.Value).Distinct().ToArray();
		var validTargets = (await context.NormalizedDescriptions.Where(target => previousTargets.Contains(target.Id)
			&& target.Status != NormalizedDescriptionStatus.Rejected).Select(target => target.Id).ToListAsync(ct)).ToHashSet();
		foreach (var member in members)
		{
			var item = items.GetValueOrDefault(member.ItemId);
			bool parentCurrent = parents.TryGetValue(member.ReceiptId, out var parent) && parent.DeletedAt == null
				&& parent.NormalizationRevision == member.ReceiptRevision;
			bool targetValid = member.PreviousTargetId is null || validTargets.Contains(member.PreviousTargetId.Value);
			if (item is null || item.DeletedAt != null || item.NormalizationRevision != member.AppliedRevision
				|| item.NormalizationAssignmentId != member.AssignmentId || !parentCurrent || !targetValid)
			{ member.State = "conflicted"; continue; }
			item.NormalizedDescriptionId = member.PreviousTargetId;
			item.NormalizedDescriptionMatchScore = member.PreviousScore;
			item.NormalizationOrigin = member.PreviousOrigin;
			item.NormalizationMappingId = member.PreviousMappingId;
			// A new assignment token ensures an old operation cannot reclaim this restored state.
			item.NormalizationAssignmentId = Guid.NewGuid();
			member.State = "undone";
		}
		await context.SaveChangesAsync(ct);
		operation.State = await context.NormalizationCatalogMembers.AnyAsync(member => member.OperationId == operation.Id && member.State == "applied", ct)
			? "undoing" : "undone";
		operation.Revision = Guid.NewGuid();
		await context.SaveChangesAsync(ct);
	}
	private async Task PublishAsync()
	{
		await publisher.PublishAsync(new(CommittedEntityType.ReceiptItem, CommittedChangeType.Updated, SuppressToast: true));
		await publisher.PublishAsync(new(CommittedEntityType.NormalizedDescription, CommittedChangeType.Updated, SuppressToast: true));
	}
}
