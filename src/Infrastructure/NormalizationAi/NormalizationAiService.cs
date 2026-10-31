using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.NormalizationAi;
using Common;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.NormalizationAi;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.NormalizationAi;

public sealed partial class NormalizationAiService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	IChatGptRunner runner,
	IOptions<NormalizationAiOptions> options,
	TimeProvider timeProvider,
	ICommittedChangePublisher publisher,
	IEmbeddingService? candidateEmbeddingService = null,
		IOptions<PiNormalizationRunnerOptions>? runnerOptions = null,
	ILoggerFactory? loggerFactory = null) : INormalizationAiService
{
	private NormalizationAiOptions Settings => options.Value;
	private ChatGptCredentialBinding? Binding => runnerOptions is null ? null : new(runnerOptions.Value);
	private bool IsBound(Guid generation) => Binding?.Matches(Settings.OwnerUserId, generation) == true;
	private async Task<bool> EnsureBoundAsync(ApplicationDbContext context, NormalizationAiConnectionEntity connection, CancellationToken token)
	{
		if (IsBound(connection.Generation)) { return true; }
		// Reconcile observed owner/session mismatches while holding both fences. Busy is unavailable.
		if (!connection.Changing && Binding is { } binding && (binding.Exists
			|| await context.NormalizationAssistancePreferences.AnyAsync(p => p.Enabled, token)))
		{
			try
			{
				using ChatGptOperatorLease operatorLease = ChatGptOperatorLease.Acquire(runnerOptions!.Value);
				binding.Invalidate();
				await FenceForRestoreAsync(context, token);
				await context.SaveChangesAsync(token);
			}
			catch (ChatGptRunnerException error) when (error.Code == "operator_busy") { }
		}
		return false;
	}
	private static readonly string[] ReservingStates = NormalizationAiDecisionPolicy.ReservingStates;
	private void RequireOwner(string actor)
	{
		if (!Settings.Enabled || string.IsNullOrWhiteSpace(Settings.OwnerUserId)) { throw Error(503, "not_configured", "ChatGPT normalization is not configured for this deployment."); }
		if (!string.Equals(actor, Settings.OwnerUserId, StringComparison.Ordinal)) { throw Error(403, "owner_required", "Only the configured ChatGPT connection owner can use this integration."); }
	}
	private static NormalizationAiException Error(int status, string code, string detail) => new(status, code, detail);
	// Per-purchase runs (no source key) were retired in favour of catalog source runs; existing rows stay readable.
	private static bool IsPerPurchase(NormalizationAiRunEntity run) => run.PromptVersion == "receipts-normalization-v1"
		&& run.Proposals.All(proposal => proposal.SourceLocation == null);
	private static NormalizationAiException WorkflowRetired() => Error(409, "workflow_retired",
		"Per-purchase ChatGPT suggestions are retired. Discard this run and request suggestions from Catalog.");
	private static NormalizationAiRun ToModel(NormalizationAiRunEntity run) => new(run.Id, run.Revision, run.ClientRequestId, run.State, run.Model,
		run.CreatedAt, run.CompletedAt, run.ErrorCode, run.ErrorDetail,
		run.Proposals.OrderBy(proposal => proposal.Id).Select(proposal => new NormalizationAiProposal(proposal.Id, proposal.ReceiptItemId,
			proposal.Description, proposal.Action, proposal.TargetId, proposal.CanonicalName, proposal.DisplayLabel, proposal.Reason, proposal.State,
			proposal.SourceLocation is null ? null : new Application.Normalization.NormalizationSourceKey(proposal.SourceLocation, proposal.Description, proposal.SourceItemCode),
			proposal.TargetRevision, proposal.IncludeLegacyUnknown, proposal.LabelOnly, proposal.SuggestedDisplayLabel)).ToArray(),
		run.UsageJson is null ? null : JsonSerializer.Deserialize<ChatGptUsage>(run.UsageJson));
	private static IReadOnlyList<NormalizationAiInput> Inputs(NormalizationAiRunEntity run) => run.Proposals.OrderBy(proposal => proposal.Id)
		.Select(proposal => new NormalizationAiInput(proposal.Id, proposal.Description,
			JsonSerializer.Deserialize<List<NormalizationAiTarget>>(proposal.CandidatesJson) ?? [], proposal.LabelOnly)).ToArray();
	private static async Task<NormalizationAiConnectionEntity> ConnectionAsync(ApplicationDbContext context, CancellationToken cancellationToken)
	{
		NormalizationAiConnectionEntity? connection = await context.NormalizationAiConnection.SingleOrDefaultAsync(cancellationToken);
		if (connection is null) { connection = new(); context.NormalizationAiConnection.Add(connection); await context.SaveChangesAsync(cancellationToken); }
		return connection;
	}
	private static async Task<NormalizationAiRunEntity> OwnedRunAsync(ApplicationDbContext context, string actor, Guid id, CancellationToken cancellationToken)
		=> await context.NormalizationAiRuns.Include(run => run.Proposals).SingleOrDefaultAsync(run => run.Id == id && run.OwnerUserId == actor, cancellationToken)
			?? throw Error(404, "run_not_found", "The normalization run was not found.");

	public async Task<NormalizationAiConnection> GetConnectionAsync(string actor, CancellationToken cancellationToken)
		=> await GetConnectionCoreAsync(actor, cancellationToken, admission: false);

	private async Task<NormalizationAiConnection> GetConnectionCoreAsync(string actor, CancellationToken cancellationToken, bool admission)
	{
		bool configured = Settings.Enabled && !string.IsNullOrWhiteSpace(Settings.OwnerUserId);
		bool owner = configured && actor == Settings.OwnerUserId;
		if (!owner) { return new(configured, false, false, Guid.Empty, []); }
		// Every status/admission retry also repairs failed finalization in a still-running host.
		if (!await RecoverConnectionChangeAsync(cancellationToken)) { return new(true, true, false, Guid.Empty, [], Busy: true); }
		Guid generation;
		await using (ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken))
		await using (NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken))
		{
			NormalizationAiConnectionEntity connection = await ConnectionAsync(context, cancellationToken);
			generation = connection.Generation;
			if (connection.Changing) { return new(true, true, false, generation, [], Busy: true); }
			if (!await EnsureBoundAsync(context, connection, cancellationToken)) { await lease.CommitAsync(cancellationToken); return new(true, true, false, connection.Generation, []); }
			// Once work is reserved, refreshes must not take the rotating-credential lock.
			// An already-started read may overlap admission; the worker safely backs off.
			if (!admission && await context.NormalizationAiRuns.AnyAsync(run => run.State == "queued" || run.State == "running", cancellationToken))
			{ return new(true, true, false, generation, [], Busy: true); }
			await lease.CommitAsync(cancellationToken);
		}
		try
		{
			ChatGptRunnerStatus status = await runner.GetStatusAsync(cancellationToken);
			if (status.Busy) { return new(true, true, false, generation, [], Busy: true); }
			return new(true, true, status.CredentialsPresent, generation, status.CredentialsPresent ? await runner.GetModelsAsync(cancellationToken) : []);
		}
		catch (ChatGptRunnerException exception) when (exception.Code == "busy")
		{
			// A request can acquire the runtime between the status and catalog commands.
			return new(true, true, false, generation, [], Busy: true);
		}
		catch (ChatGptRunnerException) { throw Error(503, "provider_unavailable", "The ChatGPT runtime is unavailable. Check the deployment runtime and sign-in status."); }
	}

	private static IQueryable<Guid> Reserved(ApplicationDbContext context) => context.NormalizationAiProposals
		.Where(proposal => proposal.State == "pending" && context.NormalizationAiRuns.Any(run => run.Id == proposal.RunId && ReservingStates.Contains(run.State)))
		.Select(proposal => proposal.ReceiptItemId);

	public async Task<IReadOnlyList<NormalizationAiRun>> ListRunsAsync(string actor, CancellationToken cancellationToken)
	{
		RequireOwner(actor);
		await RecoverExpiredAsync(cancellationToken);
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		return (await context.NormalizationAiRuns.AsNoTracking().Include(run => run.Proposals).Where(run => run.OwnerUserId == actor)
			.OrderByDescending(run => run.CreatedAt).Take(50).ToListAsync(cancellationToken)).Select(ToModel).ToArray();
	}
	public async Task<NormalizationAiRun?> GetRunAsync(string actor, Guid id, CancellationToken cancellationToken)
	{
		RequireOwner(actor);
		await RecoverExpiredAsync(cancellationToken);
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		NormalizationAiRunEntity? run = await context.NormalizationAiRuns.AsNoTracking().Include(run => run.Proposals).SingleOrDefaultAsync(run => run.Id == id && run.OwnerUserId == actor, cancellationToken);
		return run is null ? null : ToModel(run);
	}

	public async Task<NormalizationAiRun> CancelAsync(string actor, Guid id, CancellationToken cancellationToken)
	{
		RequireOwner(actor);
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
		NormalizationAiRunEntity run = await OwnedRunAsync(context, actor, id, cancellationToken);
		NormalizationAiDecisionPolicy.RequireCancellable(run.State);
		if (run.State != "cancelled") { Finish(run, "cancelled", "cancelled", "Cancelled. Any usage already incurred cannot be reversed."); await context.SaveChangesAsync(cancellationToken); }
		await lease.CommitAsync(cancellationToken);
		return ToModel(run);
	}

	public async Task<NormalizationAiRun> RetryAsync(string actor, Guid id, Guid clientRequestId, CancellationToken cancellationToken)
	{
		RequireOwner(actor);
		if (clientRequestId == Guid.Empty) { throw Error(400, "invalid_request", "A retry request ID is required."); }
		await RecoverExpiredAsync(cancellationToken);
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
		NormalizationAiRunEntity run = await OwnedRunAsync(context, actor, id, cancellationToken);
		string hash = "retry:" + id;
		NormalizationAiRunEntity? prior = await context.NormalizationAiRuns.Include(value => value.Proposals).SingleOrDefaultAsync(value => value.OwnerUserId == actor && value.ClientRequestId == clientRequestId, cancellationToken);
		if (prior is not null) { return prior.RequestHash == hash ? ToModel(prior) : throw Error(409, "request_conflict", "This request ID was used for a different operation."); }
		if (IsPerPurchase(run)) { throw WorkflowRetired(); }
		NormalizationAiDecisionPolicy.RequireRetryable(run.State);
		if (run.PromptVersion != NormalizationCatalogAiPolicy.Version)
		{ throw Error(409, "unsupported_protocol", "This saved catalog request uses an older protocol. Cancel it and create a new selection."); }
		if (await ConnectionUnavailableAsync(context, run.ConnectionGeneration, cancellationToken)) { throw Error(409, "connection_changed", "The connection changed. Cancel this run and create a new selection."); }
		Guid[] itemIds = run.Proposals.Select(proposal => proposal.ReceiptItemId).ToArray();
		if (await context.NormalizationAiProposals.AnyAsync(proposal => proposal.RunId != id && proposal.State == "pending" && itemIds.Contains(proposal.ReceiptItemId)
			&& context.NormalizationAiRuns.Any(other => other.Id == proposal.RunId && ReservingStates.Contains(other.State)), cancellationToken))
		{ throw Error(409, "source_changed", "Some items belong to another pending run. Cancel this run and refresh the selection."); }
		List<ReceiptItemEntity> items = await context.ReceiptItems.IgnoreAutoIncludes().Where(item => item.DeletedAt == null
			&& context.Receipts.Any(receipt => receipt.Id == item.ReceiptId && receipt.DeletedAt == null)
			&& itemIds.Contains(item.Id)).ToListAsync(cancellationToken);
		if (items.Count != itemIds.Length || run.Proposals.Any(proposal => !items.Any(item => item.Id == proposal.ReceiptItemId && item.NormalizationRevision == proposal.SourceRevision)))
		{ throw Error(409, "source_changed", "Source items changed. Cancel this run and select the current items."); }
		foreach (var proposal in run.Proposals)
		{
			if (proposal.LabelOnly)
			{
				var labelTarget = await ReadLabelSourceTargetAsync(context, new(proposal.SourceLocation!, proposal.Description, proposal.SourceItemCode), cancellationToken);
				if (labelTarget.Id != proposal.SourceCanonicalId || labelTarget.NormalizationRevision != proposal.SourceCanonicalRevision)
				{ throw Error(409, "source_changed", "The label suggestion's product identity changed. Create a fresh request."); }
			}
			if (!proposal.LabelOnly && !(await Infrastructure.Services.NormalizationCatalogService.ReadDecisionAsync(context,
				items.Single(item => item.Id == proposal.ReceiptItemId), proposal.IncludeLegacyUnknown, cancellationToken)).Eligible)
			{ throw Error(409, "source_changed", "A source group is now protected. Refresh before requesting suggestions."); }
			if (await context.NormalizationAiProposals.AnyAsync(other => other.RunId != id && other.State == "pending"
				&& other.SourceLocation == proposal.SourceLocation && other.Description == proposal.Description && other.SourceItemCode == proposal.SourceItemCode
				&& context.NormalizationAiRuns.Any(otherRun => otherRun.Id == other.RunId && ReservingStates.Contains(otherRun.State)), cancellationToken))
			{ throw Error(409, "source_reserved", "A source group already has another pending run. Cancel this run and refresh."); }
			if (!await context.Receipts.AnyAsync(receipt => receipt.Id == proposal.ReceiptId && receipt.NormalizationRevision == proposal.SourceReceiptRevision, cancellationToken))
			{ throw Error(409, "source_changed", "A source receipt changed. Select its current source group."); }
		}
		NormalizationAiDecisionPolicy.RequireAdmission(await context.NormalizationAiRuns.CountAsync(other => other.Id != id
			&& other.OwnerUserId == actor && ReservingStates.Contains(other.State) && (other.State != "ready" || other.Proposals.Any(proposal => proposal.State == "pending")), cancellationToken), Settings.PendingRunLimit);
		NormalizationAiRunEntity retry = new()
		{
			Id = Guid.NewGuid(),
			ClientRequestId = clientRequestId,
			OwnerUserId = actor,
			ConnectionGeneration = run.ConnectionGeneration,
			RequestHash = hash,
			Model = run.Model,
			PromptVersion = run.PromptVersion,
			CreatedAt = timeProvider.GetUtcNow(),
			Proposals = run.Proposals.Select(proposal => new NormalizationAiProposalEntity
			{
				Id = Guid.NewGuid(),
				ReceiptItemId = proposal.ReceiptItemId,
				ReceiptId = proposal.ReceiptId,
				SourceRevision = proposal.SourceRevision,
				SourceLocation = proposal.SourceLocation,
				SourceItemCode = proposal.SourceItemCode,
				SourceReceiptRevision = proposal.SourceReceiptRevision,
				IncludeLegacyUnknown = proposal.IncludeLegacyUnknown,
				LabelOnly = proposal.LabelOnly,
				SourceCanonicalId = proposal.SourceCanonicalId,
				SourceCanonicalRevision = proposal.SourceCanonicalRevision,
				SourceMatchScore = proposal.SourceMatchScore,
				Description = proposal.Description,
				CandidatesJson = proposal.CandidatesJson
			}).ToList()
		};
		Finish(run, "cancelled", "retried", "An explicit retry created a new run; earlier usage may still have been incurred.");
		context.NormalizationAiRuns.Add(retry);
		var automaticAttempt = await context.NormalizationAssistanceAttempts.SingleOrDefaultAsync(attempt => attempt.RunId == id, cancellationToken);
		if (automaticAttempt is not null)
		{
			// Explicit retries retain inbox ownership; the scheduler still cannot replay inference.
			automaticAttempt.RunId = retry.Id; automaticAttempt.Revision = Guid.NewGuid();
		}
		await context.SaveChangesAsync(cancellationToken);
		await lease.CommitAsync(cancellationToken);
		return ToModel(retry);
	}

	public async Task<Guid> BeginConnectionChangeAsync(CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
		NormalizationAiConnectionEntity connection = await ConnectionAsync(context, cancellationToken);
		Binding?.Invalidate();
		connection.Generation = Guid.NewGuid();
		connection.Changing = true;
		await FenceForRestoreAsync(context, cancellationToken);
		await context.SaveChangesAsync(cancellationToken);
		await lease.CommitAsync(cancellationToken);
		return connection.Generation;
	}

	public Task CompleteConnectionChangeAsync(Guid changeId, CancellationToken cancellationToken)
			=> CompleteConnectionCoreAsync(changeId, null, cancellationToken);

	public Task BindConnectionChangeAsync(Guid changeId, string consentingOwner, CancellationToken cancellationToken)
	{
		RequireOwner(consentingOwner);
		if (Binding is null) { throw Error(503, "not_configured", "Credential binding storage is unavailable."); }
		return CompleteConnectionCoreAsync(changeId, consentingOwner, cancellationToken);
	}

	private async Task CompleteConnectionCoreAsync(Guid changeId, string? consentingOwner, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
		NormalizationAiConnectionEntity connection = await ConnectionAsync(context, cancellationToken);
		if (!connection.Changing || connection.Generation != changeId)
		{
			if (consentingOwner is not null) { throw Error(409, "connection_changed", "The connection changed before consent could be saved."); }
			return;
		}
		connection.Generation = Guid.NewGuid();
		connection.Changing = false;
		await FenceForRestoreAsync(context, cancellationToken);
		await context.SaveChangesAsync(cancellationToken);
		await lease.CommitAsync(cancellationToken);
		// Failure here leaves an unavailable, unbound session. Never publish before DB finalization.
		if (consentingOwner is not null) { Binding!.Publish(consentingOwner, connection.Generation, timeProvider.GetUtcNow()); }
	}

	private async Task<bool> ConnectionUnavailableAsync(ApplicationDbContext context, Guid generation, CancellationToken cancellationToken)
	{
		NormalizationAiConnectionEntity connection = await ConnectionAsync(context, cancellationToken);
		return connection.Changing || connection.Generation != generation || !IsBound(generation);
	}

	private Task<bool> OwnerAuthorizedAsync(ApplicationDbContext context, string owner, CancellationToken cancellationToken)
	{
		DateTimeOffset now = timeProvider.GetUtcNow();
		return context.Users.AnyAsync(user => user.Id == owner && !(user.LockoutEnabled && user.LockoutEnd > now)
			&& context.UserRoles.Any(membership => membership.UserId == user.Id
				&& context.Roles.Any(role => role.Id == membership.RoleId && role.Name == AppRoles.Admin)), cancellationToken);
	}

	internal static async Task FenceForRestoreAsync(ApplicationDbContext context, CancellationToken cancellationToken)
	{
		await NormalizationWriteGuard.AcquireWriteGateAsync(context, cancellationToken);
		// Generation changes only at credential/context fences, never model selection or settings reads.
		NormalizationAiConnectionEntity connection = await ConnectionAsync(context, cancellationToken);
		connection.Generation = Guid.NewGuid();
		var preference = await context.NormalizationAssistancePreferences.SingleOrDefaultAsync(cancellationToken);
		if (preference is not null) { preference.Enabled = false; preference.Revision = Guid.NewGuid(); }
		await Infrastructure.Services.NormalizationReviewInboxService.CancelOutstandingAsync(context, "context_changed", DateTimeOffset.UtcNow, cancellationToken);
		foreach (var operation in await context.NormalizationCatalogOperations.Where(operation => operation.State == "preview"
			|| operation.State == "applying" || operation.State == "undoing" || operation.State == "paused" || operation.State == "undo_paused").ToListAsync(cancellationToken))
		{
			operation.State = "restored"; operation.Revision = Guid.NewGuid();
			operation.ErrorCode = "context_changed"; operation.ErrorDetail = "A backup restore changed source observations. Create a new preview.";
		}
		foreach (NormalizationAiRunEntity run in await context.NormalizationAiRuns.Where(run => ReservingStates.Contains(run.State)).ToListAsync(cancellationToken))
		{
			run.State = "cancelled"; run.Revision = Guid.NewGuid(); run.ClaimToken = null; run.LeaseExpiresAt = null;
			run.CompletedAt = DateTimeOffset.UtcNow; run.ErrorCode = "context_changed"; run.ErrorDetail = "Data or connection context changed; create a new run.";
		}
	}
	private void Finish(NormalizationAiRunEntity run, string state, string? code = null, string? detail = null)
	{
		run.State = state; run.Revision = Guid.NewGuid(); run.ClaimToken = null; run.LeaseExpiresAt = null;
		run.CompletedAt = timeProvider.GetUtcNow(); run.ErrorCode = code; run.ErrorDetail = detail;
	}
}
