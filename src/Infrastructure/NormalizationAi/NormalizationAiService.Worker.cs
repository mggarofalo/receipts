using System.Text.Json;
using Application.NormalizationAi;
using Infrastructure.Entities.NormalizationAi;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.NormalizationAi;

public sealed partial class NormalizationAiService
{
	public async Task RecoverExpiredAsync(CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
		DateTimeOffset now = timeProvider.GetUtcNow();
		foreach (NormalizationAiRunEntity run in await context.NormalizationAiRuns.Where(run => run.State == "running" && run.LeaseExpiresAt <= now).ToListAsync(cancellationToken))
		{ Finish(run, "unknown", "unknown_outcome", "The worker stopped before a durable result. Usage may have occurred; retry only deliberately."); }
		await ExpireUnacceptedAsync(context, cancellationToken);
		await context.SaveChangesAsync(cancellationToken);
		await lease.CommitAsync(cancellationToken);
	}

	private async Task ExpireUnacceptedAsync(ApplicationDbContext context, CancellationToken cancellationToken)
	{
		DateTimeOffset cutoff = NormalizationAiDecisionPolicy.UnacceptedRetentionCutoff(timeProvider.GetUtcNow(), Settings.UnacceptedRetentionDays);
		// Accepted provenance survives, but discarded/abstained siblings have no audit retention need.
		foreach (NormalizationAiRunEntity accepted in await context.NormalizationAiRuns.Include(run => run.Proposals)
			.Where(run => run.State == "applied" && run.CreatedAt < cutoff && run.Proposals.Any(proposal => proposal.State != "applied"))
			.ToListAsync(cancellationToken))
		{
			NormalizationAiProposalEntity[] discarded = accepted.Proposals.Where(proposal => proposal.State != "applied").ToArray();
			context.NormalizationAiProposals.RemoveRange(discarded);
			foreach (NormalizationAiProposalEntity proposal in discarded) { accepted.Proposals.Remove(proposal); }
			accepted.Revision = Guid.NewGuid();
		}
		foreach (NormalizationAiRunEntity run in await context.NormalizationAiRuns.Include(run => run.Proposals)
			.Where(run => run.State != "applied" && run.State != "expired" && run.CreatedAt < cutoff).ToListAsync(cancellationToken))
		{
			// Keep the non-payload request tombstone so a delayed retry of an old HTTP request
			// cannot incur usage again after its source text and candidates have been purged.
			context.NormalizationAiProposals.RemoveRange(run.Proposals);
			run.Proposals.Clear();
			Finish(run, "expired", "expired", "Unaccepted suggestions expired and their item payloads were removed. Select current items to create a new request.");
		}
	}

	public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
	{
		if (!Settings.Enabled || string.IsNullOrWhiteSpace(Settings.OwnerUserId)) { return false; }
		await RecoverExpiredAsync(cancellationToken);
		NormalizationAiRunEntity? claimed;
		Guid claim = Guid.NewGuid();
		await using (ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken))
		await using (NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken))
		{
			// One bounded inference at a time per deployment/connection, even with API replicas.
			if (await context.NormalizationAiRuns.AnyAsync(run => run.State == "running", cancellationToken)) { return false; }
			claimed = await context.NormalizationAiRuns.Include(run => run.Proposals).Where(run => run.State == "queued").OrderBy(run => run.CreatedAt).FirstOrDefaultAsync(cancellationToken);
			if (claimed is null) { return false; }
			if (claimed.OwnerUserId != Settings.OwnerUserId || await ConnectionUnavailableAsync(context, claimed.ConnectionGeneration, cancellationToken)
				|| !await OwnerAuthorizedAsync(context, claimed.OwnerUserId, cancellationToken))
			{ Finish(claimed, "cancelled", "connection_changed", "The connection owner or generation changed."); await context.SaveChangesAsync(cancellationToken); await lease.CommitAsync(cancellationToken); return true; }
			claimed.State = "running"; claimed.ClaimToken = claim; claimed.Revision = Guid.NewGuid();
			claimed.LeaseExpiresAt = timeProvider.GetUtcNow() + Settings.LeaseDuration;
			await context.SaveChangesAsync(cancellationToken);
			await lease.CommitAsync(cancellationToken);
		}
		IReadOnlyList<NormalizationAiDecision>? decisions = null;
		ChatGptUsage? usage = null;
		string outcome = "ready";
		string? code = null;
		string? detail = null;
		Task<ChatGptInferenceResult>? inference = null;
		using CancellationTokenSource inferenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		inferenceCancellation.CancelAfter(Settings.LeaseDuration);
		try
		{
			IReadOnlyList<NormalizationAiInput> inputs = Inputs(claimed);
			inference = runner.InferAsync(new(claimed.Model, NormalizationAiPolicy.Instructions,
				NormalizationAiPolicy.BuildPrompt(inputs), NormalizationAiPolicy.Schema), inferenceCancellation.Token);
			while (!inference.IsCompleted)
			{
				await Task.WhenAny(inference, Task.Delay(TimeSpan.FromMilliseconds(250), inferenceCancellation.Token));
				if (inference.IsCompleted) { break; }
				await using ApplicationDbContext polling = await contextFactory.CreateDbContextAsync(inferenceCancellation.Token);
				bool active = await polling.NormalizationAiRuns.AsNoTracking().AnyAsync(run => run.Id == claimed.Id && run.State == "running" && run.ClaimToken == claim, inferenceCancellation.Token);
				if (!active) { inferenceCancellation.Cancel(); break; }
			}
			ChatGptInferenceResult response = await inference;
			usage = response.Usage;
			decisions = NormalizationAiPolicy.Validate(response.Text, inputs);
		}
		catch (NormalizationAiException exception) { outcome = "failed"; code = exception.Code; detail = exception.Message; }
		catch (ChatGptRunnerException exception)
		{
			// A provider error does not prove that a request incurred no usage. Never replay automatically.
			outcome = "unknown"; code = "unknown_outcome";
			detail = "The provider did not return a validated result (" + SafeCode(exception.Code) + "). Usage may have occurred.";
		}
		catch (OperationCanceledException) { outcome = "unknown"; code = "unknown_outcome"; detail = "Inference was interrupted. Usage may have occurred; retry only deliberately."; }
		catch (Exception) { outcome = "unknown"; code = "unknown_outcome"; detail = "The runtime stopped before a validated result. Usage may have occurred."; }
		finally
		{
			// A polling/database failure must stop and observe the provider process before releasing this worker.
			await inferenceCancellation.CancelAsync();
			if (inference is not null) { try { await inference; } catch (Exception) { /* Outcome is classified above. */ } }
		}
		// This short finalization survives host cancellation; a claim/generation fence prevents
		// a cancelled, restored, switched or expired run from publishing its late result.
		using CancellationTokenSource completionTimeout = new(TimeSpan.FromSeconds(10));
		CancellationToken completionToken = completionTimeout.Token;
		await using ApplicationDbContext final = await contextFactory.CreateDbContextAsync(completionToken);
		await using NormalizationAiStoreLock finalLease = await NormalizationAiStoreLock.AcquireAsync(final, completionToken);
		NormalizationAiRunEntity? runResult = await final.NormalizationAiRuns.Include(run => run.Proposals).SingleOrDefaultAsync(run => run.Id == claimed.Id, completionToken);
		if (runResult is null || runResult.State != "running" || runResult.ClaimToken != claim || runResult.LeaseExpiresAt <= timeProvider.GetUtcNow()
			|| await ConnectionUnavailableAsync(final, runResult.ConnectionGeneration, completionToken)) { return true; }
		if (!await OwnerAuthorizedAsync(final, runResult.OwnerUserId, completionToken))
		{
			Finish(runResult, "cancelled", "owner_changed", "The connection owner is no longer an enabled administrator.");
			await final.SaveChangesAsync(completionToken);
			await finalLease.CommitAsync(completionToken);
			return true;
		}
		if (decisions is not null)
		{
			foreach (NormalizationAiDecision decision in decisions)
			{
				NormalizationAiProposalEntity proposal = runResult.Proposals.Single(value => value.Id == decision.Id);
				proposal.Action = decision.Action; proposal.Reason = decision.Reason; proposal.TargetId = decision.TargetId;
				if (decision.Action == "reuse")
				{
					NormalizationAiTarget target = JsonSerializer.Deserialize<List<NormalizationAiTarget>>(proposal.CandidatesJson)!.Single(value => value.Id == decision.TargetId);
					proposal.TargetRevision = target.Revision; proposal.MatchScore = target.Score; proposal.CanonicalName = target.Name; proposal.DisplayLabel = target.DisplayLabel;
				}
				else if (decision.Action == "create") { proposal.CanonicalName = decision.DisplayLabel; }
				else { proposal.State = "abstained"; }
			}
		}
		runResult.UsageJson = usage is null ? null : JsonSerializer.Serialize(usage);
		Finish(runResult, outcome, code, detail);
		await final.SaveChangesAsync(completionToken);
		await finalLease.CommitAsync(completionToken);
		return true;
	}
	private static string SafeCode(string code) => code.Length <= 64 && code.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') ? code : "provider_error";
}
