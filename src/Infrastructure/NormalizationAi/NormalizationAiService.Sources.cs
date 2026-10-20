using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Normalization;
using Application.NormalizationAi;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.NormalizationAi;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.NormalizationAi;

public sealed partial class NormalizationAiService
{
	public async Task<NormalizationAiRun> StartSourcesAsync(string actor, Guid clientRequestId,
		IReadOnlyList<NormalizationAiSourceSelection> sources, string model, CancellationToken cancellationToken)
	{
		RequireOwner(actor);
		if (clientRequestId == Guid.Empty || sources.Count is < 1 or > 50 || string.IsNullOrWhiteSpace(model) || model.Length > 200)
		{ throw Error(400, "invalid_selection", "Choose 1–50 distinct source groups, an available model, and a new request ID."); }
		var selections = sources.Select(selection => selection with { Source = NormalizationCatalogPolicy.Normalize(selection.Source) })
			.OrderBy(selection => NormalizationCatalogPolicy.Hash(selection.Source), StringComparer.Ordinal).ToArray();
		if (selections.Select(selection => NormalizationCatalogPolicy.Hash(selection.Source)).Distinct().Count() != selections.Length)
		{ throw Error(400, "invalid_selection", "Each source group may appear only once."); }
		string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { model, selections, version = NormalizationCatalogAiPolicy.Version }))));
		await RecoverExpiredAsync(cancellationToken);
		await using (ApplicationDbContext prior = await contextFactory.CreateDbContextAsync(cancellationToken))
		{
			var existing = await prior.NormalizationAiRuns.Include(run => run.Proposals).SingleOrDefaultAsync(run => run.OwnerUserId == actor && run.ClientRequestId == clientRequestId, cancellationToken);
			if (existing is not null) { return existing.RequestHash == hash ? ToModel(existing) : throw Error(409, "request_conflict", "This request ID was used for different inputs."); }
		}
		var connection = await GetConnectionAsync(actor, cancellationToken);
		if (connection.Busy) { throw Error(409, "provider_busy", "Wait for the current ChatGPT operation."); }
		if (!connection.CredentialsPresent) { throw Error(409, "not_connected", "Sign in to ChatGPT before requesting suggestions."); }
		if (!connection.Models.Contains(model, StringComparer.Ordinal)) { throw Error(400, "invalid_model", "Choose an available model."); }
		var catalog = new NormalizationCatalogService(contextFactory, timeProvider, publisher, candidateEmbeddingService);
		List<NormalizationAiProposalEntity> proposals = [];
		foreach (var selection in selections)
		{
			await using ApplicationDbContext read = await contextFactory.CreateDbContextAsync(cancellationToken);
			ReceiptItemEntity item;
			IReadOnlyList<NormalizationCatalogCandidate> candidates;
			NormalizedDescriptionEntity? labelTarget = null;
			if (selection.LabelOnly)
			{
				labelTarget = await ReadLabelSourceTargetAsync(read, selection.Source, cancellationToken);
				item = await NormalizationCatalogService.SourceItems(read, selection.Source)
					.Where(item => item.NormalizedDescriptionId == labelTarget.Id).OrderBy(item => item.Id).FirstAsync(cancellationToken);
				candidates = [new(labelTarget.Id, labelTarget.NormalizationRevision, labelTarget.CanonicalName, labelTarget.DisplayLabel, ["current_identity"], [])];
			}
			else
			{
				item = await NormalizationCatalogService.SourceItems(read, selection.Source)
					.Where(item => item.NormalizationOrigin == "unassigned" || item.NormalizationOrigin == "automatic"
						|| (selection.IncludeLegacyUnknown && item.NormalizationOrigin == "legacy_unknown")
						|| ((item.NormalizationOrigin == "mapped" || item.NormalizationOrigin == "excluded") && item.NormalizationAssignmentId != null
							&& read.NormalizationSourceMappings.Any(mapping => mapping.Id == item.NormalizationMappingId && mapping.State == "revoked" && mapping.TargetId == item.NormalizedDescriptionId)))
					.Where(item => !read.ItemTemplates.Any(template => template.DeletedAt == null && template.NormalizedDescriptionId != null && template.NormalizedDescriptionId == item.NormalizedDescriptionId)
						&& !read.NormalizedDescriptions.Any(target => target.Status == Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Rejected
							&& (target.Id == item.NormalizedDescriptionId || target.CanonicalName.ToLower() == item.Description.Trim(' ').ToLower())))
					.OrderBy(item => item.Id).FirstOrDefaultAsync(cancellationToken)
					?? throw Error(409, "source_changed", "A selected source has no eligible purchases. Refresh the source groups.");
				if (!(await NormalizationCatalogService.ReadDecisionAsync(read, item, selection.IncludeLegacyUnknown, cancellationToken)).Eligible)
				{ throw Error(409, "source_changed", "A selected source is protected. Refresh the source groups."); }
				candidates = await catalog.GetCandidatesAsync(actor, selection.Source, cancellationToken);
			}
			Guid[] candidateIds = candidates.Select(candidate => candidate.Id).ToArray();
			var exampleRows = await read.NormalizedDescriptions.Where(target => candidateIds.Contains(target.Id)).Select(target => new
			{
				target.Id,
				Examples = read.NormalizationSourceMappings.Where(mapping => mapping.State == "active" && !mapping.Exclude && mapping.TargetId == target.Id)
					.OrderByDescending(mapping => mapping.Location == selection.Source.Location && mapping.ItemCode == selection.Source.ItemCode)
					.ThenBy(mapping => mapping.Description).Take(3)
					.Select(mapping => new NormalizationApprovedExample(mapping.Description,
						mapping.Location == selection.Source.Location && mapping.ItemCode == selection.Source.ItemCode)).ToList()
			}).ToListAsync(cancellationToken);
			var examplesByTarget = exampleRows.ToDictionary(row => row.Id, row => row.Examples);
			List<NormalizationAiTarget> evidence = candidates.Select(candidate => new NormalizationAiTarget(candidate.Id, candidate.Revision,
				candidate.CanonicalName, candidate.DisplayLabel, null, candidate.ApprovedExamples, candidate.Channels,
				examplesByTarget.GetValueOrDefault(candidate.Id) ?? [])).ToList();
			proposals.Add(new()
			{
				Id = Guid.NewGuid(),
				ReceiptItemId = item.Id,
				ReceiptId = item.ReceiptId,
				SourceRevision = item.NormalizationRevision,
				Description = selection.Source.Description,
				SourceLocation = selection.Source.Location,
				SourceItemCode = selection.Source.ItemCode,
				SourceReceiptRevision = await read.Receipts.Where(receipt => receipt.Id == item.ReceiptId).Select(receipt => receipt.NormalizationRevision).SingleAsync(cancellationToken),
				IncludeLegacyUnknown = selection.IncludeLegacyUnknown,
				LabelOnly = selection.LabelOnly,
				SourceCanonicalId = item.NormalizedDescriptionId,
				SourceCanonicalRevision = labelTarget?.NormalizationRevision,
				CandidatesJson = JsonSerializer.Serialize(evidence)
			});
		}
		NormalizationAiRunEntity created = new()
		{
			Id = Guid.NewGuid(),
			ClientRequestId = clientRequestId,
			OwnerUserId = actor,
			ConnectionGeneration = connection.Generation,
			RequestHash = hash,
			Model = model,
			CreatedAt = timeProvider.GetUtcNow(),
			Proposals = proposals,
			PromptVersion = NormalizationCatalogAiPolicy.Version
		};
		foreach (var proposal in proposals) { proposal.RunId = created.Id; }
		_ = NormalizationCatalogAiPolicy.BuildPrompt(Inputs(created));
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
		var priorRun = await context.NormalizationAiRuns.Include(run => run.Proposals).SingleOrDefaultAsync(run => run.OwnerUserId == actor && run.ClientRequestId == clientRequestId, cancellationToken);
		if (priorRun is not null) { return priorRun.RequestHash == hash ? ToModel(priorRun) : throw Error(409, "request_conflict", "This request ID was used for different inputs."); }
		if (!await OwnerAuthorizedAsync(context, actor, cancellationToken) || await ConnectionUnavailableAsync(context, connection.Generation, cancellationToken))
		{ throw Error(409, "connection_changed", "The connection or administrator access changed."); }
		NormalizationAiDecisionPolicy.RequireAdmission(await context.NormalizationAiRuns.CountAsync(run => run.OwnerUserId == actor && ReservingStates.Contains(run.State) && (run.State != "ready" || run.Proposals.Any(proposal => proposal.State == "pending")), cancellationToken), Settings.PendingRunLimit);
		Guid[] itemIds = proposals.Select(proposal => proposal.ReceiptItemId).ToArray();
		if (await Reserved(context).AnyAsync(id => itemIds.Contains(id), cancellationToken)) { throw Error(409, "source_reserved", "A source group already has pending suggestions."); }
		foreach (var selection in selections)
		{
			var source = selection.Source;
			if (await context.NormalizationAiProposals.AnyAsync(proposal => proposal.SourceLocation == source.Location
				&& proposal.Description == source.Description && proposal.SourceItemCode == source.ItemCode && proposal.State == "pending"
				&& context.NormalizationAiRuns.Any(run => run.Id == proposal.RunId && ReservingStates.Contains(run.State)), cancellationToken))
			{ throw Error(409, "source_reserved", "A selected source group already has pending suggestions."); }
		}
		foreach (var proposal in proposals.Where(proposal => proposal.LabelOnly))
		{
			var currentTarget = await ReadLabelSourceTargetAsync(context, new(proposal.SourceLocation!, proposal.Description, proposal.SourceItemCode), cancellationToken);
			if (currentTarget.Id != proposal.SourceCanonicalId || currentTarget.NormalizationRevision != proposal.SourceCanonicalRevision
				|| !await context.ReceiptItems.AnyAsync(item => item.Id == proposal.ReceiptItemId && item.NormalizationRevision == proposal.SourceRevision, cancellationToken)
				|| !await context.Receipts.AnyAsync(receipt => receipt.Id == proposal.ReceiptId && receipt.NormalizationRevision == proposal.SourceReceiptRevision, cancellationToken))
			{ throw Error(409, "source_changed", "The source identity changed before the label request could start. Refresh and select it again."); }
		}
		context.NormalizationAiRuns.Add(created);
		await context.SaveChangesAsync(cancellationToken);
		await lease.CommitAsync(cancellationToken);
		return ToModel(created);
	}
	private static async Task<NormalizedDescriptionEntity> ReadLabelSourceTargetAsync(ApplicationDbContext context, NormalizationSourceKey source, CancellationToken ct)
	{
		Guid[] targets = await NormalizationCatalogService.SourceItems(context, source).Where(item => item.NormalizedDescriptionId != null)
			.Select(item => item.NormalizedDescriptionId!.Value).Distinct().Take(2).ToArrayAsync(ct);
		if (targets.Length != 1)
		{ throw Error(409, "label_source_ambiguous", "A display-label suggestion needs one unambiguous existing product identity for this source. Review its identities first."); }
		return await context.NormalizedDescriptions.SingleOrDefaultAsync(target => target.Id == targets[0] && target.Status == NormalizedDescriptionStatus.Active, ct)
			?? throw Error(409, "label_source_ambiguous", "The source must refer to an active product identity before requesting a display label.");
	}

}
