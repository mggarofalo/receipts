using System.Text.Json;
using Application.Models.CommittedChanges;
using Application.NormalizationAi;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.NormalizationAi;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.NormalizationAi;

public sealed partial class NormalizationAiService
{
	public async Task<NormalizationAiRun> ApplyAsync(string actor, Guid id, IReadOnlyList<Guid> proposalIds, Guid expectedRevision, CancellationToken cancellationToken)
	{
		RequireOwner(actor);
		if (proposalIds.Count is < 1 or > 50 || proposalIds.Distinct().Count() != proposalIds.Count || expectedRevision == Guid.Empty)
		{ throw Error(400, "invalid_selection", "Select 1–50 distinct proposals and the current run revision."); }
		NormalizationAiRun result;
		await using (ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken))
		await using (NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken))
		{
			NormalizationAiRunEntity run = await OwnedRunAsync(context, actor, id, cancellationToken);
			List<NormalizationAiProposalEntity> selected = run.Proposals.Where(proposal => proposalIds.Contains(proposal.Id)).ToList();
			if (selected.Count != proposalIds.Count) { throw Error(400, "invalid_selection", "A selected proposal does not belong to this run."); }
			if (selected.All(proposal => proposal.State == "applied")) { return ToModel(run); }
			NormalizationAiDecisionPolicy.RequireReviewable(run.State, run.Revision, expectedRevision,
				selected.Select(proposal => new NormalizationAiReviewState(proposal.State, proposal.Action)).ToArray());
			if (await ConnectionUnavailableAsync(context, run.ConnectionGeneration, cancellationToken))
			{ throw Error(409, "connection_changed", "The ChatGPT connection changed; create a new run."); }
			Guid[] itemIds = selected.Select(proposal => proposal.ReceiptItemId).ToArray();
			Guid[] targetIds = selected.Where(proposal => proposal.TargetId.HasValue).Select(proposal => proposal.TargetId!.Value)
				.Concat(selected.Where(proposal => proposal.SourceCanonicalId.HasValue).Select(proposal => proposal.SourceCanonicalId!.Value)).Distinct().Order().ToArray();
			await NormalizationWriteGuard.LockTargetsAsync(context, targetIds, cancellationToken);
			await NormalizationWriteGuard.LockItemsAsync(context, itemIds, cancellationToken);
			Dictionary<Guid, ReceiptItemEntity> items = await context.ReceiptItems.IgnoreAutoIncludes().IgnoreQueryFilters().Where(item => itemIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
			Dictionary<Guid, NormalizedDescriptionEntity> targets = await context.NormalizedDescriptions.Where(target => targetIds.Contains(target.Id)).ToDictionaryAsync(target => target.Id, cancellationToken);
			Guid[] receiptIds = selected.Select(proposal => proposal.ReceiptId).Distinct().ToArray();
			HashSet<Guid> liveReceipts = (await context.Receipts.IgnoreQueryFilters().Where(receipt => receiptIds.Contains(receipt.Id) && receipt.DeletedAt == null).Select(receipt => receipt.Id).ToListAsync(cancellationToken)).ToHashSet();
			string[] sourceNames = selected.Select(proposal => proposal.Description.Trim().ToLowerInvariant()).ToArray();
			Guid[] sourceCanonicalIds = selected.Where(proposal => proposal.SourceCanonicalId.HasValue).Select(proposal => proposal.SourceCanonicalId!.Value).ToArray();
			HashSet<Guid> protectedSources = (await context.ItemTemplates.Where(template => template.DeletedAt == null
				&& template.NormalizedDescriptionId != null && sourceCanonicalIds.Contains(template.NormalizedDescriptionId.Value))
				.Select(template => template.NormalizedDescriptionId!.Value).ToListAsync(cancellationToken)).ToHashSet();
			HashSet<string> rejectedSources = (await context.NormalizedDescriptions.Where(target => target.Status == NormalizedDescriptionStatus.Rejected
				&& sourceNames.Contains(target.CanonicalName.ToLower())).Select(target => target.CanonicalName.ToLower()).ToListAsync(cancellationToken)).ToHashSet();
			string[] newNames = selected.Where(proposal => proposal.Action == "create")
				.Select(proposal => proposal.CanonicalName!.ToLowerInvariant()).Distinct().ToArray();
			var collisions = await context.NormalizedDescriptions.Where(target => newNames.Contains(target.CanonicalName.ToLower())
				|| newNames.Contains((target.DisplayLabel ?? target.CanonicalName).ToLower()))
				.Select(target => new { target.CanonicalName, target.DisplayLabel }).ToListAsync(cancellationToken);
			HashSet<string> occupiedNames = collisions.SelectMany(target => new[] { target.CanonicalName.ToLowerInvariant(),
				(target.DisplayLabel ?? target.CanonicalName).ToLowerInvariant() }).ToHashSet();
			foreach (NormalizationAiProposalEntity proposal in selected)
			{
				NormalizationAiSourceState expected = new(proposal.SourceRevision, proposal.Description, proposal.SourceCanonicalId,
					proposal.SourceCanonicalRevision, proposal.SourceCanonicalId.HasValue ? NormalizedDescriptionStatus.PendingReview : null, proposal.SourceMatchScore);
				NormalizationAiSourceState? current = null;
				if (items.TryGetValue(proposal.ReceiptItemId, out ReceiptItemEntity? item))
				{
					NormalizedDescriptionEntity? source = item.NormalizedDescriptionId is Guid sourceId ? targets.GetValueOrDefault(sourceId) : null;
					current = new(item.NormalizationRevision, item.Description, item.NormalizedDescriptionId, source?.NormalizationRevision,
						source?.Status, item.NormalizedDescriptionMatchScore, item.DeletedAt is null, liveReceipts.Contains(item.ReceiptId),
						source is not null && protectedSources.Contains(source.Id), rejectedSources.Contains(item.Description.Trim().ToLowerInvariant()));
				}
				NormalizationAiDecisionPolicy.RequireUnchangedSource(expected, current);
				NormalizedDescriptionEntity? target = proposal.TargetId is Guid targetId ? targets.GetValueOrDefault(targetId) : null;
				NormalizationAiDecisionPolicy.RequireValidTarget(proposal.Action, proposal.CanonicalName, proposal.TargetId, proposal.TargetRevision,
					target is null ? null : new(target.Id, target.NormalizationRevision, target.Status),
					proposal.CanonicalName is not null && occupiedNames.Contains(proposal.CanonicalName.ToLowerInvariant()));
			}
			Dictionary<string, NormalizedDescriptionEntity> createdTargets = new(StringComparer.OrdinalIgnoreCase);
			// All validation precedes all tracked business mutations. New targets and the reviewed
			// item links share one SaveChanges transaction with the automatic and semantic audit.
			foreach (NormalizationAiProposalEntity proposal in selected)
			{
				NormalizedDescriptionEntity target;
				if (proposal.Action == "reuse") { target = targets[proposal.TargetId!.Value]; }
				else if (!createdTargets.TryGetValue(proposal.CanonicalName!, out target!))
				{
					target = new() { Id = Guid.NewGuid(), CanonicalName = proposal.CanonicalName!, Status = NormalizedDescriptionStatus.Active, CreatedAt = timeProvider.GetUtcNow() };
					createdTargets.Add(target.CanonicalName, target);
					context.NormalizedDescriptions.Add(target);
				}
				ReceiptItemEntity item = items[proposal.ReceiptItemId];
				item.NormalizedDescriptionId = target.Id;
				item.NormalizedDescriptionMatchScore = proposal.Action == "reuse" ? proposal.MatchScore : null;
				proposal.TargetId = target.Id;
				proposal.State = "applied";
			}
			// Approval is a single reviewed selection. Unselected suggestions are retained for
			// history but discarded, so they neither reserve items nor silently acquire new targets.
			foreach (NormalizationAiProposalEntity unselected in run.Proposals.Where(proposal => proposal.State == "pending"))
			{
				unselected.State = "discarded";
			}
			Finish(run, "applied");
			context.AuditLogs.Add(new AuditLogEntity
			{
				Id = Guid.NewGuid(),
				EntityType = "NormalizationAiRun",
				EntityId = run.Id.ToString(),
				Action = AuditAction.Update,
				ChangedByUserId = actor,
				ChangedAt = timeProvider.GetUtcNow(),
				ChangesJson = JsonSerializer.Serialize(new[] { new FieldChange { FieldName = "ReviewedProposals", NewValue = string.Join(",", selected.Select(proposal => proposal.Id)) } })
			});
			try { await context.SaveChangesAsync(cancellationToken); await lease.CommitAsync(cancellationToken); }
			catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
			{ throw Error(409, "target_changed", "A canonical name collided with another change. Nothing was applied."); }
			result = ToModel(run);
		}
		await publisher.PublishAsync(new(CommittedEntityType.ReceiptItem, CommittedChangeType.Updated, SuppressToast: true));
		await publisher.PublishAsync(new(CommittedEntityType.NormalizedDescription, CommittedChangeType.Updated, SuppressToast: true));
		return result;
	}
}
