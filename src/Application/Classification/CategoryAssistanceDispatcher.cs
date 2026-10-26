using Application.Normalization;

namespace Application.Classification;

/// <summary>One exact source identity. A hash lookup must also compare all three normalized fields.</summary>
public sealed record CategoryAssistanceSource(NormalizationSourceKey Key, string Hash, Guid Revision);

/// <summary>Durable work created from an authorized post-consent save, never from a history sweep.</summary>
public sealed record CategoryAssistanceQueuedWork(Guid AttemptId, Guid QueueClaimToken, Guid RequestId,
	Guid ReceiptId, Guid ReceiptRevision, Guid ItemId, Guid ItemRevision,
	CategoryAssistanceSource Source, CategoryAssistanceFence Fence, string Provider, string Model,
	string QuestionPolicyVersion, CategorySuggestionRequest Request);

/// <summary>A dispatch-start commit has consumed one global UTC-day debit and owns this token.</summary>
public sealed record CategoryAssistanceDispatchClaim(CategoryAssistanceQueuedWork Work, Guid ClaimToken);

public enum CategoryAssistanceAttemptOutcome { Suggested, Abstained, Failed, Stale, Indeterminate }
public sealed record CategoryAssistanceCompletion(CategoryAssistanceAttemptOutcome Outcome,
	CategorySuggestion? Suggestion = null, SuggestionFailure? Failure = null);
public enum CategoryAssistanceQueuedDeferral { CapabilityUnavailable, StartRejected }

public static class CategoryAssistanceQueuePolicy
{
	public static readonly TimeSpan QueuedClaimLease = TimeSpan.FromMinutes(2);
	public const int MaximumDeferrals = 6;

	/// <summary>One-based deferral count; persistence must terminalize after the cap.</summary>
	public static TimeSpan Backoff(int deferrals) => TimeSpan.FromSeconds(
		Math.Min(600, 30 * (1 << Math.Clamp(deferrals - 1, 0, MaximumDeferrals - 1))));
}

/// <summary>
/// Persistence owns the transaction and writer coordination. ClaimQueued takes one unbilled queued
/// attempt under the pending limit and return its unique queued claim token. It must deduplicate automatic work by restore generation,
/// exact normalized source tuple (hash plus tuple equality), taxonomy revision, provider, model,
/// and question-policy version. Repeated observations attach to the same source attempt.
/// </summary>
public interface ICategoryAssistanceDispatchStore
{
	Task<CategoryAssistanceQueuedWork?> ClaimQueuedAsync(CancellationToken ct);

	/// <summary>Release only the matching unbilled queued claim. Persist a finite lease and bounded
	/// backoff from CategoryAssistanceQueuePolicy before reclaiming the same attempt and request ID.
	/// After MaximumDeferrals, exhausted/stale queued work is
	/// terminalized without a debit. Expired dispatching claims are indeterminate and never resent.</summary>
	Task DeferQueuedAsync(CategoryAssistanceQueuedWork work, CategoryAssistanceQueuedDeferral reason,
		DateTimeOffset now, CancellationToken ct);

	/// <summary>
	/// In one transaction with classification/source writers: requery consent, configured provider
	/// and model, connection/restore generations, source tuple and revision, receipt/item liveness
	/// and captured receipt/item revisions,
	/// assignment, mapping, taxonomy and eligibility. Compare all captured fences and claim ownership.
	/// Match the queued claim token. Atomically enforce the pending limit and deployment-global UTC-day ledger, debit one attempt,
	/// transition to dispatching and return the current durable claim. This commit is the
	/// authorization linearization point: later revocation cannot unsend an in-flight call,
	/// but completion must discard its stale result. No daily debit on rejection.
	/// Settings changes invalidate claims but never erase dedup or daily usage. Expired dispatching
	/// claims become indeterminate with their debit retained and must never be automatically resent.
	/// </summary>
	Task<CategoryAssistanceDispatchClaim?> TryStartDispatchAsync(CategoryAssistanceQueuedWork work,
		CategoryAssistanceCapability capability, DateTimeOffset now, CancellationToken ct);

	/// <summary>
	/// In one transaction with classification/source writers: match attempt, token and every fence;
	/// recheck current consent, capability, source and taxonomy, then insert a valid result or terminal
	/// failure. On any stale fact persist Stale without a visible suggestion. Release pending capacity.
	/// Never write a category assignment or an approved mapping here.
	/// </summary>
	Task CompleteAsync(CategoryAssistanceDispatchClaim claim, CategoryAssistanceCompletion completion,
		CategoryAssistanceCapability capability, CancellationToken ct);

	/// <summary>Terminalize invalid queued work only on a matching queued claim token, without a daily debit.</summary>
	Task RejectQueuedAsync(CategoryAssistanceQueuedWork work, SuggestionFailure failure, CancellationToken ct);
}

/// <summary>Readiness is current provider capability, independent of an administrative connection check.</summary>
public interface ICategoryAssistanceCapabilitySource
{
	Task<CategoryAssistanceCapability> GetAsync(CancellationToken ct);
}

/// <summary>One selected provider, model and exact transport serializer. The implementation must
/// bind the captured connection generation when sending and never silently switch credentials.</summary>
public interface ICategoryAssistanceSelectedProvider
{
	string Provider { get; }
	string Model { get; }
	bool IsAvailable { get; }
	/// <summary>Measures the bytes that this same transport will send.</summary>
	bool FitsPayload(CategorySuggestionRequest request);
	/// <summary>Send with exactly the captured connection generation, or fail closed if it changed.</summary>
	Task<CategorySuggestionResult> SuggestAsync(CategorySuggestionRequest request,
		Guid connectionGeneration, CancellationToken ct);
}

/// <summary>Runs at most one claimed source attempt; scheduling and persistence live outside Application.</summary>
public sealed class CategoryAssistanceDispatcher(
	ICategoryAssistanceDispatchStore store,
	ICategoryAssistanceCapabilitySource capabilitySource,
	ICategoryAssistanceSelectedProvider provider,
	TimeProvider clock)
{
	public async Task<bool> DispatchOneAsync(CancellationToken ct = default)
	{
		CategoryAssistanceQueuedWork? work = await store.ClaimQueuedAsync(ct);
		if (work is null)
		{
			return false;
		}

		if (!ValidQueuedWork(work) || provider.Provider != work.Provider || provider.Model != work.Model
			|| !provider.FitsPayload(work.Request))
		{
			await store.RejectQueuedAsync(work, SuggestionFailure.InvalidInput, ct);
			return true;
		}

		CategoryAssistanceCapability capability = await capabilitySource.GetAsync(ct);
		// The durable store performs the authoritative consent and eligibility recheck and debit.
		// Provider availability alone can never authorize inference.
		if (capability is null || capability.State != CategoryAssistanceCapabilityState.Ready
			|| capability.Provider != work.Provider || capability.Models is null
			|| !capability.Models.Contains(work.Model, StringComparer.Ordinal)
			|| !provider.IsAvailable)
		{
			await store.DeferQueuedAsync(work, CategoryAssistanceQueuedDeferral.CapabilityUnavailable,
				clock.GetUtcNow(), ct);
			return true;
		}

		CategoryAssistanceDispatchClaim? claim = await store.TryStartDispatchAsync(work, capability,
			clock.GetUtcNow(), ct);
		if (claim is null)
		{
			await store.DeferQueuedAsync(work, CategoryAssistanceQueuedDeferral.StartRejected,
				clock.GetUtcNow(), ct);
			return true;
		}

		CategoryAssistanceCompletion completion;
		try
		{
			ct.ThrowIfCancellationRequested();
			CategorySuggestionResult result = await provider.SuggestAsync(claim.Work.Request,
				claim.Work.Fence.ConnectionGeneration, ct);
			completion = ValidateResult(claim.Work, result);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			// The call may have reached the provider. Recovery must mark an expired dispatching
			// claim indeterminate; do not retry or refund its debit.
			throw;
		}
		catch (Exception)
		{
			completion = new(CategoryAssistanceAttemptOutcome.Failed, Failure: SuggestionFailure.Unavailable);
		}

		await store.CompleteAsync(claim, completion, capability, ct);
		return true;
	}

	private static bool ValidQueuedWork(CategoryAssistanceQueuedWork work)
	{
		try { return ValidQueuedWorkCore(work); }
		catch (Exception exception) when (exception is ArgumentException or NullReferenceException
			or NormalizationCatalogException)
		{ return false; }
	}

	private static bool ValidQueuedWorkCore(CategoryAssistanceQueuedWork work) =>
		work.AttemptId != Guid.Empty && work.QueueClaimToken != Guid.Empty
		&& work.RequestId != Guid.Empty && work.ReceiptId != Guid.Empty
		&& work.ReceiptRevision != Guid.Empty && work.ItemId != Guid.Empty
		&& work.ItemRevision != Guid.Empty && work.Source is not null && work.Fence is not null
		&& work.Request is not null && work.Source.Key is not null && work.Source.Revision != Guid.Empty
		&& work.Source.Key == NormalizationCatalogPolicy.Normalize(work.Source.Key)
		&& work.Source.Hash == NormalizationCatalogPolicy.Hash(work.Source.Key)
		&& work.Fence.SourceHash == work.Source.Hash && work.Fence.SourceRevision == work.Source.Revision
		&& work.Fence.SettingsRevision != Guid.Empty && work.Fence.ConnectionGeneration != Guid.Empty
		&& work.Fence.RestoreGeneration != Guid.Empty && work.Fence.TaxonomyRevision != Guid.Empty
		&& !string.IsNullOrWhiteSpace(work.Provider) && work.Model == CategorySuggestionPolicy.Model
		&& work.QuestionPolicyVersion == CategorySuggestionPolicy.Version
		&& work.Request.TaxonomyRevision == work.Fence.TaxonomyRevision.ToString("D")
		&& CategorySuggestionPolicy.IsValid(work.Request);

	private static CategoryAssistanceCompletion ValidateResult(CategoryAssistanceQueuedWork work,
		CategorySuggestionResult result)
	{
		if (result is null || (result.Suggestion is null) == (result.Failure is null))
		{
			return new(CategoryAssistanceAttemptOutcome.Failed, Failure: SuggestionFailure.InvalidResponse);
		}

		if (result.Failure is { } failure)
		{
			return new(CategoryAssistanceAttemptOutcome.Failed,
				Failure: Enum.IsDefined(failure) ? failure : SuggestionFailure.InvalidResponse);
		}

		CategorySuggestion suggestion = result.Suggestion!;
		if (suggestion.Provenance is null || suggestion.Provenance.TaxonomyRevision != work.Request.TaxonomyRevision
			|| suggestion.Provenance.QuestionPolicy != work.QuestionPolicyVersion
			|| suggestion.Provenance.Model != work.Model || suggestion.Provenance.Provider != work.Provider
			|| suggestion.Provenance.InputTokens is < 0 or > 9007199254740991
			|| suggestion.Provenance.OutputTokens is < 0 or > 9007199254740991
			|| suggestion.Path is null || work.Request.Categories is null
			|| !Probability(suggestion.FitProbability) || !Probability(suggestion.CategoryProbability)
			|| !Probability(suggestion.Confidence))
		{
			return new(CategoryAssistanceAttemptOutcome.Failed, Failure: SuggestionFailure.InvalidResponse);
		}

		if (suggestion.CategoryId is null)
		{
			return suggestion.Path.Count == 0
				&& (suggestion.AbstentionReason == "no-fit" && suggestion.FitProbability <= 0.3
					|| suggestion.AbstentionReason == "uncertain-fit"
						&& suggestion.FitProbability is > 0.3 and < 0.7)
				? new(CategoryAssistanceAttemptOutcome.Abstained, suggestion)
				: new(CategoryAssistanceAttemptOutcome.Failed, Failure: SuggestionFailure.InvalidResponse);
		}

		if (suggestion.FitProbability < 0.7 || suggestion.AbstentionReason is not null
			|| !work.Request.Categories.Any(x => x.Id == suggestion.CategoryId)
			|| !suggestion.Path.SequenceEqual(CategorySuggestionPolicy.PathFor(work.Request, suggestion.CategoryId)))
		{
			return new(CategoryAssistanceAttemptOutcome.Failed, Failure: SuggestionFailure.InvalidResponse);
		}

		return new(CategoryAssistanceAttemptOutcome.Suggested, suggestion);
	}

	private static bool Probability(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}
