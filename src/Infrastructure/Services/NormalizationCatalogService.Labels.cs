using Application.Normalization;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.NormalizationAi;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public sealed partial class NormalizationCatalogService
{
	public async Task<NormalizationCatalogLabelReview> PreviewLabelAsync(string actor, Guid targetId, string? displayLabel, CancellationToken cancellationToken)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await AuthorizeAsync(context, actor, cancellationToken);
		var target = await LabelTargetAsync(context, targetId, cancellationToken);
		string? proposed = await PrepareLabelAsync(context, target, displayLabel, cancellationToken);
		int affected = await LabelImpactAsync(context, targetId, cancellationToken);
		return new(target.Id, target.NormalizationRevision, target.CanonicalName, target.DisplayLabel, proposed, affected);
	}

	public async Task<NormalizationCatalogLabelReview> ApproveLabelAsync(string actor, Guid targetId, NormalizationCatalogLabelApproval request, CancellationToken cancellationToken)
	{
		NormalizationCatalogLabelReview result;
		await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
		await using (var lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken))
		{
			await AuthorizeAsync(context, actor, cancellationToken);
			await NormalizationWriteGuard.LockTargetsAsync(context, [targetId], cancellationToken);
			var target = await LabelTargetAsync(context, targetId, cancellationToken);
			if (request.ExpectedTargetRevision != target.NormalizationRevision)
			{ throw Error(409, "target_changed", "The product identity or display label changed. Review its current label again."); }
			int affected = await LabelImpactAsync(context, targetId, cancellationToken);
			if (request.ExpectedAffectedItemCount != affected)
			{ throw Error(409, "label_impact_changed", "The number of affected purchases changed. Review the global label impact again."); }
			string? proposed = await PrepareLabelAsync(context, target, request.DisplayLabel, cancellationToken);
			await ConsumeLabelSuggestionAsync(context, actor, targetId, request, cancellationToken);
			target.DisplayLabel = proposed;
			try { await context.SaveChangesAsync(cancellationToken); }
			catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
			{ throw Error(409, "display_name_taken", "Another product already uses this display name."); }
			await lease.CommitAsync(cancellationToken);
			result = new(target.Id, target.NormalizationRevision, target.CanonicalName, target.DisplayLabel, proposed, affected);
		}
		await PublishAsync();
		return result;
	}

	private async Task ConsumeLabelSuggestionAsync(ApplicationDbContext context, string actor, Guid targetId, NormalizationCatalogLabelApproval request, CancellationToken ct)
	{
		int references = (request.SuggestionRunId.HasValue ? 1 : 0) + (request.SuggestionProposalId.HasValue ? 1 : 0)
			+ (request.SuggestionRunRevision.HasValue ? 1 : 0);
		if (references == 0) { return; }
		if (references != 3) { throw Error(400, "invalid_suggestion", "Supply all three suggestion references together."); }
		var run = await context.NormalizationAiRuns.Include(run => run.Proposals).SingleOrDefaultAsync(run => run.Id == request.SuggestionRunId, ct);
		var proposal = run?.Proposals.SingleOrDefault(proposal => proposal.Id == request.SuggestionProposalId);
		if (run is null || run.OwnerUserId != actor || run.State != "ready" || run.Revision != request.SuggestionRunRevision
			|| proposal is null || !proposal.LabelOnly || proposal.State != "pending" || proposal.TargetId != targetId
			|| !await context.NormalizationAiConnection.AnyAsync(connection => !connection.Changing && connection.Generation == run.ConnectionGeneration, ct))
		{ throw Error(409, "suggestion_changed", "The label suggestion or connection changed. Refresh before approving its label."); }
		proposal.State = string.Equals(proposal.SuggestedDisplayLabel, request.DisplayLabel?.Trim(), StringComparison.Ordinal) ? "applied" : "overridden";
		if (run.Proposals.All(proposal => proposal.State != "pending"))
		{
			run.State = "applied";
			run.CompletedAt = timeProvider.GetUtcNow();
			run.Revision = Guid.NewGuid();
		}
	}

	private static async Task<NormalizedDescriptionEntity> LabelTargetAsync(ApplicationDbContext context, Guid id, CancellationToken ct)
	{
		var target = await context.NormalizedDescriptions.SingleOrDefaultAsync(target => target.Id == id, ct)
			?? throw Error(404, "target_not_found", "The product identity was not found.");
		if (target.Status != NormalizedDescriptionStatus.Active)
		{ throw Error(409, "target_changed", "Only an active product identity can receive a reviewed display label."); }
		return target;
	}

	private static Task<int> LabelImpactAsync(ApplicationDbContext context, Guid id, CancellationToken ct)
		=> Live(context).CountAsync(item => item.NormalizedDescriptionId == id, ct);

	private static async Task<string?> PrepareLabelAsync(ApplicationDbContext context, NormalizedDescriptionEntity target, string? label, CancellationToken ct)
	{
		try { return await NormalizedDescriptionService.PrepareDisplayLabelAsync(context, target, label, ct); }
		catch (ArgumentException exception) { throw Error(400, "invalid_display_label", exception.Message); }
		catch (InvalidOperationException exception) when (exception.Message == NormalizedDescriptionService.DisplayNameAlreadyTaken)
		{ throw Error(409, "display_name_taken", "Another product already uses this display name."); }
	}
}
