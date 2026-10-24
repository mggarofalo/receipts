using System.Security.Cryptography;
using System.Text;
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
using Microsoft.Extensions.Options;

namespace Infrastructure.NormalizationAi;

public sealed partial class NormalizationAiService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	IChatGptRunner runner,
	INormalizedDescriptionService normalizedDescriptions,
	IOptions<NormalizationAiOptions> options,
	TimeProvider timeProvider,
	ICommittedChangePublisher publisher,
	IEmbeddingService? candidateEmbeddingService = null) : INormalizationAiService
{
	private NormalizationAiOptions Settings => options.Value;
	private static readonly string[] ReservingStates = NormalizationAiDecisionPolicy.ReservingStates;
	private void RequireOwner(string actor)
	{
		if (!Settings.Enabled || string.IsNullOrWhiteSpace(Settings.OwnerUserId)) { throw Error(503, "not_configured", "ChatGPT normalization is not configured for this deployment."); }
		if (!string.Equals(actor, Settings.OwnerUserId, StringComparison.Ordinal)) { throw Error(403, "owner_required", "Only the configured ChatGPT connection owner can use this integration."); }
	}
	private static NormalizationAiException Error(int status, string code, string detail) => new(status, code, detail);
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
	{
		bool configured = Settings.Enabled && !string.IsNullOrWhiteSpace(Settings.OwnerUserId);
		bool owner = configured && actor == Settings.OwnerUserId;
		if (!owner) { return new(configured, false, false, Guid.Empty, []); }
		Guid generation;
		await using (ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken))
		await using (NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken))
		{
			NormalizationAiConnectionEntity connection = await ConnectionAsync(context, cancellationToken);
			generation = connection.Generation;
			if (connection.Changing) { return new(true, true, false, generation, [], Busy: true); }
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

	private static IQueryable<ReceiptItemEntity> Eligible(ApplicationDbContext context)
		=> context.ReceiptItems.IgnoreAutoIncludes().AsNoTracking().Where(item => item.DeletedAt == null
			&& context.Receipts.Any(receipt => receipt.Id == item.ReceiptId && receipt.DeletedAt == null)
			&& (item.NormalizedDescriptionId == null || context.NormalizedDescriptions.Any(target => target.Id == item.NormalizedDescriptionId && target.Status == NormalizedDescriptionStatus.PendingReview))
			&& !context.ItemTemplates.Any(template => template.DeletedAt == null && template.NormalizedDescriptionId != null && template.NormalizedDescriptionId == item.NormalizedDescriptionId)
			&& item.Description.Trim() != ""
			&& !context.NormalizedDescriptions.Any(target => target.Status == NormalizedDescriptionStatus.Rejected && target.CanonicalName.ToLower() == item.Description.Trim().ToLower()));
	private static IQueryable<Guid> Reserved(ApplicationDbContext context) => context.NormalizationAiProposals
		.Where(proposal => proposal.State == "pending" && context.NormalizationAiRuns.Any(run => run.Id == proposal.RunId && ReservingStates.Contains(run.State)))
		.Select(proposal => proposal.ReceiptItemId);

	public async Task<NormalizationAiCandidatePage> ListCandidatesAsync(string actor, string? q, int offset, int limit, CancellationToken cancellationToken)
	{
		RequireOwner(actor);
		if (offset < 0 || limit is < 1 or > 200) { throw Error(400, "invalid_page", "Offset must be nonnegative and limit between 1 and 200."); }
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		IQueryable<Guid> reserved = Reserved(context);
		IQueryable<ReceiptItemEntity> items = Eligible(context).Where(item => !reserved.Contains(item.Id));
		if (!string.IsNullOrWhiteSpace(q)) { string term = q.Trim().ToLowerInvariant(); items = items.Where(item => item.Description.ToLower().Contains(term)); }
		int total = await items.CountAsync(cancellationToken);
		var result = await items.OrderBy(item => item.Id).Skip(offset).Take(limit).Join(context.Receipts,
			item => item.ReceiptId, receipt => receipt.Id,
			(item, receipt) => new NormalizationAiCandidate(item.Id, receipt.Id, item.Description, receipt.Location, receipt.Date)).ToListAsync(cancellationToken);
		return new(result, total);
	}

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

	public async Task<NormalizationAiRun> StartAsync(string actor, Guid clientRequestId, IReadOnlyList<Guid> itemIds, string model, CancellationToken cancellationToken)
	{
		RequireOwner(actor);
		if (clientRequestId == Guid.Empty || itemIds.Count is < 1 or > NormalizationAiPolicy.MaxItems || itemIds.Any(id => id == Guid.Empty)
			|| itemIds.Distinct().Count() != itemIds.Count || string.IsNullOrWhiteSpace(model) || model.Length > 200)
		{ throw Error(400, "invalid_selection", "Select 1–50 distinct items and an available model, with a new request ID."); }
		await RecoverExpiredAsync(cancellationToken);
		Guid[] ids = itemIds.Order().ToArray();
		string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model + "\n" + string.Join(",", ids))));
		await using (ApplicationDbContext priorContext = await contextFactory.CreateDbContextAsync(cancellationToken))
		{
			NormalizationAiRunEntity? prior = await priorContext.NormalizationAiRuns.AsNoTracking().Include(run => run.Proposals).SingleOrDefaultAsync(run => run.OwnerUserId == actor && run.ClientRequestId == clientRequestId, cancellationToken);
			if (prior is not null) { return prior.RequestHash == hash ? ToModel(prior) : throw Error(409, "request_conflict", "This request ID was already used for different input."); }
		}
		NormalizationAiConnection connection = await GetConnectionAsync(actor, cancellationToken);
		if (connection.Busy) { throw Error(409, "provider_busy", "ChatGPT is busy. Wait for the current operation before starting another run."); }
		if (!connection.CredentialsPresent) { throw Error(409, "not_connected", "Sign in to ChatGPT on the deployment before starting a run."); }
		if (!connection.Models.Contains(model, StringComparer.Ordinal)) { throw Error(400, "invalid_model", "Select a model offered by the current ChatGPT runtime."); }
		List<NormalizationAiProposalEntity> snapshots = [];
		await using (ApplicationDbContext read = await contextFactory.CreateDbContextAsync(cancellationToken))
		{
			List<ReceiptItemEntity> items = await Eligible(read).Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
			if (items.Count != ids.Length) { throw Error(409, "source_changed", "Some selected items are no longer available for normalization."); }
			Dictionary<string, string> candidateCache = new(StringComparer.Ordinal);
			foreach (ReceiptItemEntity item in items)
			{
				if (!candidateCache.TryGetValue(item.Description, out string? candidatesJson))
				{
					var match = await normalizedDescriptions.TestMatchAsync(item.Description, 3, null, null, cancellationToken);
					Guid[] candidateIds = match.Candidates.Select(candidate => candidate.NormalizedDescriptionId).ToArray();
					List<NormalizedDescriptionEntity> targets = await read.NormalizedDescriptions.AsNoTracking().Where(target => target.Status == NormalizedDescriptionStatus.Active
						&& (candidateIds.Contains(target.Id) || target.CanonicalName.ToLower() == item.Description.Trim().ToLower())).ToListAsync(cancellationToken);
					candidatesJson = JsonSerializer.Serialize(targets.Select(target => new NormalizationAiTarget(target.Id, target.NormalizationRevision,
						target.CanonicalName, target.DisplayLabel, match.Candidates.FirstOrDefault(candidate => candidate.NormalizedDescriptionId == target.Id)?.CosineSimilarity)).ToArray());
					candidateCache[item.Description] = candidatesJson;
				}
				snapshots.Add(new()
				{
					Id = Guid.NewGuid(),
					ReceiptItemId = item.Id,
					ReceiptId = item.ReceiptId,
					Description = item.Description,
					SourceRevision = item.NormalizationRevision,
					SourceCanonicalId = item.NormalizedDescriptionId,
					SourceCanonicalRevision = item.NormalizedDescriptionId is Guid sourceCanonicalId
						? await read.NormalizedDescriptions.Where(target => target.Id == sourceCanonicalId).Select(target => (Guid?)target.NormalizationRevision).SingleAsync(cancellationToken) : null,
					SourceMatchScore = item.NormalizedDescriptionMatchScore,
					CandidatesJson = candidatesJson
				});
			}
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
			Proposals = snapshots
		};
		foreach (NormalizationAiProposalEntity proposal in snapshots) { proposal.RunId = created.Id; }
		_ = NormalizationAiPolicy.BuildPrompt(Inputs(created));
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
		NormalizationAiRunEntity? existing = await context.NormalizationAiRuns.Include(run => run.Proposals).SingleOrDefaultAsync(run => run.OwnerUserId == actor && run.ClientRequestId == clientRequestId, cancellationToken);
		if (existing is not null) { return existing.RequestHash == hash ? ToModel(existing) : throw Error(409, "request_conflict", "This request ID was already used for different input."); }
		if (await ConnectionUnavailableAsync(context, connection.Generation, cancellationToken)) { throw Error(409, "connection_changed", "The ChatGPT connection changed. Start a new run."); }
		await NormalizationWriteGuard.LockItemsAsync(context, ids, cancellationToken);
		List<ReceiptItemEntity> current = await Eligible(context).Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
		if (current.Count != ids.Length || snapshots.Any(proposal => !current.Any(item => item.Id == proposal.ReceiptItemId && item.NormalizationRevision == proposal.SourceRevision))
			|| await Reserved(context).AnyAsync(id => ids.Contains(id), cancellationToken))
		{ throw Error(409, "source_changed", "Items changed or belong to another pending run. Refresh the selection."); }
		NormalizationAiDecisionPolicy.RequireAdmission(await context.NormalizationAiRuns.CountAsync(run => run.OwnerUserId == actor
			&& ReservingStates.Contains(run.State) && (run.State != "ready" || run.Proposals.Any(proposal => proposal.State == "pending")), cancellationToken), Settings.PendingRunLimit);
		context.NormalizationAiRuns.Add(created);
		await context.SaveChangesAsync(cancellationToken);
		await lease.CommitAsync(cancellationToken);
		return ToModel(created);
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
		NormalizationAiDecisionPolicy.RequireRetryable(run.State);
		if (run.Proposals.Any(proposal => proposal.SourceLocation != null) && run.PromptVersion != NormalizationCatalogAiPolicy.Version)
		{ throw Error(409, "unsupported_protocol", "This saved catalog request uses an older protocol. Cancel it and create a new selection."); }
		if (await ConnectionUnavailableAsync(context, run.ConnectionGeneration, cancellationToken)) { throw Error(409, "connection_changed", "The connection changed. Cancel this run and create a new selection."); }
		Guid[] itemIds = run.Proposals.Select(proposal => proposal.ReceiptItemId).ToArray();
		if (await context.NormalizationAiProposals.AnyAsync(proposal => proposal.RunId != id && proposal.State == "pending" && itemIds.Contains(proposal.ReceiptItemId)
			&& context.NormalizationAiRuns.Any(other => other.Id == proposal.RunId && ReservingStates.Contains(other.State)), cancellationToken))
		{ throw Error(409, "source_changed", "Some items belong to another pending run. Cancel this run and refresh the selection."); }
		IQueryable<ReceiptItemEntity> retrySources = run.PromptVersion == NormalizationCatalogAiPolicy.Version
			? context.ReceiptItems.IgnoreAutoIncludes().Where(item => item.DeletedAt == null
				&& context.Receipts.Any(receipt => receipt.Id == item.ReceiptId && receipt.DeletedAt == null))
			: Eligible(context);
		List<ReceiptItemEntity> items = await retrySources.Where(item => itemIds.Contains(item.Id)).ToListAsync(cancellationToken);
		if (items.Count != itemIds.Length || run.Proposals.Any(proposal => !items.Any(item => item.Id == proposal.ReceiptItemId && item.NormalizationRevision == proposal.SourceRevision)))
		{ throw Error(409, "source_changed", "Source items changed. Cancel this run and select the current items."); }
		if (run.PromptVersion == NormalizationCatalogAiPolicy.Version)
		{
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
		connection.Generation = Guid.NewGuid();
		connection.Changing = true;
		await FenceForRestoreAsync(context, cancellationToken);
		await context.SaveChangesAsync(cancellationToken);
		await lease.CommitAsync(cancellationToken);
		return connection.Generation;
	}

	public async Task CompleteConnectionChangeAsync(Guid changeId, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using NormalizationAiStoreLock lease = await NormalizationAiStoreLock.AcquireAsync(context, cancellationToken);
		NormalizationAiConnectionEntity connection = await ConnectionAsync(context, cancellationToken);
		if (!connection.Changing || connection.Generation != changeId) { return; }
		connection.Generation = Guid.NewGuid();
		connection.Changing = false;
		await FenceForRestoreAsync(context, cancellationToken);
		await context.SaveChangesAsync(cancellationToken);
		await lease.CommitAsync(cancellationToken);
	}

	private static async Task<bool> ConnectionUnavailableAsync(ApplicationDbContext context, Guid generation, CancellationToken cancellationToken)
	{
		NormalizationAiConnectionEntity connection = await ConnectionAsync(context, cancellationToken);
		return connection.Changing || connection.Generation != generation;
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
