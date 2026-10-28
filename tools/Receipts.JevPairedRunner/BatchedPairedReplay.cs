using System.Security.Cryptography;
using System.Text.Json;
using Application.Classification;
using Application.Classification.Experimental;
using Receipts.JevCandidateParity;

namespace Receipts.JevPairedRunner;

public sealed record BatchedReplayReplies(string Production, IReadOnlyList<string> Shortlist,
	IReadOnlyList<string> Verification);
public sealed record BatchedPairedResult(CategorySuggestionResult Production, CandidateDecision Candidate,
	int ReplayRequests, string? VerificationSha256);

/// <summary>Opt-in offline paired execution. Every shortlist reply precedes verification planning.</summary>
public sealed class BatchedPairedReplayPlan
{
	public const string Version = "paired-offline-batched-replay-v1";
	private readonly PairedReplayPlan baseline;
	private readonly ExhaustiveShortlistPlan shortlist;

	public BatchedPairedReplayPlan(PairedInput input)
	{
		baseline = PairedReplay.Prepare(input);
		shortlist = ExhaustiveShortlist.PrepareForBatchedVerification(input.Purchase,
			input.TaxonomyRevision, input.Taxonomy, baseline.Model);
		PlanSha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
		{
			Version,
			baseline.PlanSha256,
			shortlist = shortlist.PlanSha256,
			verifier = BatchedVerification.Version,
			globalCoverageLast = true
		})));
	}

	public string PlanSha256 { get; }
	public string ProductionPayload => baseline.ProductionPayload;
	public IReadOnlyList<ShortlistBatch> ShortlistBatches => shortlist.Batches;
	public int MaximumNetworkCalls => 0;
	public int Retries => 0;

	public BatchedVerificationPlan? PrepareVerification(IReadOnlyList<string> replies)
	{
		ShortlistResult selected = shortlist.Aggregate(replies);
		if (selected.Failure is not null)
		{
			throw new ArgumentException("Complete valid ordered shortlist required.", nameof(replies));
		}
		return selected.VerificationPlan is null ? null : BatchedVerification.Prepare(
			selected.VerificationPlan, baseline.Model, globalCoverageLast: true);
	}

	/// <summary>Actual parsers validate an ordered response prefix before a journal may mark success.</summary>
	public async Task ValidateAsync(BatchedReplayReplies replies)
	{
		ArgumentNullException.ThrowIfNull(replies);
		if (replies.Shortlist is null || replies.Verification is null ||
			(await baseline.ParseProductionAsync(replies.Production)).Failure is not null ||
			shortlist.Aggregate(replies.Shortlist, allowPrefix: true).Failure is not null)
		{
			throw new ArgumentException("Invalid replay prefix.", nameof(replies));
		}
		if (replies.Verification.Count != 0 &&
			!(PrepareVerification(replies.Shortlist)?.ValidatePrefix(replies.Verification) ?? false))
		{
			throw new ArgumentException("Invalid verification prefix.", nameof(replies));
		}
	}

	public async Task<BatchedPairedResult> ReplayAsync(BatchedReplayReplies replies,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(replies);
		cancellationToken.ThrowIfCancellationRequested();
		if (replies.Verification is null)
		{
			throw new ArgumentException("Ordered verification replies required.", nameof(replies));
		}
		BatchedVerificationPlan? verification = PrepareVerification(replies.Shortlist);
		ShortlistResult selected = shortlist.Aggregate(replies.Shortlist);
		if (verification is null && replies.Verification.Count != 0)
		{
			throw new ArgumentException("Abstention has no verification requests.", nameof(replies));
		}
		CategorySuggestionResult production = await baseline.ParseProductionAsync(replies.Production, cancellationToken);
		CandidateDecision candidate = verification is null
			? new(null, [], selected.AbstentionReason, selected.Failure)
			: verification.Aggregate(replies.Verification).Decision;
		return new(production, candidate, 1 + shortlist.Batches.Count + replies.Verification.Count,
			verification?.PlanSha256);
	}
}
