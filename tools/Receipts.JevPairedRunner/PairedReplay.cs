using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Classification;
using Application.Classification.Experimental;
using Infrastructure.Jev;
using Microsoft.Extensions.Options;
using Receipts.JevCandidateParity;

namespace Receipts.JevPairedRunner;

// Deliberately no fixture ID, label, expected answer, group, or scoring metadata.
public sealed record PairedInput(CandidatePurchase Purchase, string TaxonomyRevision,
	IReadOnlyList<SuggestionCategory> Taxonomy);
public sealed record ReplayReplies(string Production, IReadOnlyList<string> Shortlist, string? Verification);
public sealed record PairedResult(CategorySuggestionResult Production, CandidateDecision Candidate,
	int ReplayRequests, string? VerificationPayload, string? VerificationSha256);

/// <summary>A frozen single-case plan. Contains purchase evidence; keep private for private inputs.</summary>
public sealed class PairedReplayPlan
{
	public const string Version = "paired-offline-replay-v1";
	public const string BaselineRevision = "e0916a54aae6e44785b2f1c0eea296f7c88b692f";
	public const string CandidateRevision = "16a91add23c25d5b6044500de24dd87de66e6461";
	private readonly CategorySuggestionRequest production;
	private readonly ExhaustiveShortlistPlan shortlist;

	internal PairedReplayPlan(CategorySuggestionRequest production, ExhaustiveShortlistPlan shortlist,
		string payload, string binding)
	{
		this.production = production;
		this.shortlist = shortlist;
		ProductionPayload = payload;
		PlanSha256 = binding;
	}

	public string PlanSha256 { get; }
	public string ProductionPayload { get; }
	public IReadOnlyList<ShortlistBatch> ShortlistBatches => shortlist.Batches;
	public string Model => CandidateWire.SupportedEvaluationModel;
	public bool LiveExecutionEnabled => false;
	public int MaximumNetworkCalls => 0;
	public int MaximumReplayRequests => checked(shortlist.Batches.Count + 2);
	public int Retries => 0;
	public int ProductionQuestionCount => CountQuestions(ProductionPayload);
	public int ShortlistQuestionCount => shortlist.Batches.Sum(batch => batch.QuestionIds.Count);
	public int MaximumVerificationQuestions => 2 + 2 * SupportedCategoryCandidate.MaximumVerificationPaths;
	public long? MaximumInputTokensPerRequest => null;
	public long? MaximumOutputTokensPerRequest => null;
	public long? MaximumTokensPerQuestion => null;

	public CandidateWireRequest? PrepareVerification(IReadOnlyList<string> shortlistReplies)
	{
		ShortlistResult selected = shortlist.Aggregate(shortlistReplies);
		return selected.VerificationPlan is null ? null : CandidateWire.Prepare(selected.VerificationPlan, Model);
	}

	/// <summary>Uses only supplied response bytes. There is no socket handler, configuration or key lookup.</summary>
	public async Task<PairedResult> ReplayAsync(ReplayReplies replies, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(replies);
		cancellationToken.ThrowIfCancellationRequested();
		if (replies.Shortlist is null || replies.Shortlist.Count != shortlist.Batches.Count)
		{
			throw new ArgumentException("Supply exactly one replay reply for every planned shortlist batch.", nameof(replies));
		}
		// Validate the complete supplied shortlist before executing either arm. No partial shortlist is usable.
		ShortlistResult selected = shortlist.Aggregate(replies.Shortlist);
		CandidateWireRequest? verification = selected.VerificationPlan is null ? null :
			CandidateWire.Prepare(selected.VerificationPlan, Model);
		if ((verification is null) != (replies.Verification is null))
		{
			throw new ArgumentException("Verification reply must be present exactly when shortlist permits verification.", nameof(replies));
		}

		CategorySuggestionResult baseline = await ParseProductionAsync(replies.Production, cancellationToken);
		CandidateDecision candidate = verification is null
			? new(null, [], selected.AbstentionReason, selected.Failure)
			: verification.ParseResponse(replies.Verification!).Decision;
		return new(baseline, candidate, 1 + shortlist.Batches.Count + (verification is null ? 0 : 1),
			verification?.Payload, verification?.PlanSha256);
	}

	public async Task ValidateAsync(ReplayReplies replies)
	{
		if ((await ParseProductionAsync(replies.Production)).Failure is not null ||
			shortlist.Aggregate(replies.Shortlist, allowPrefix: true).Failure is not null)
		{
			throw new ArgumentException();
		}
		if (replies.Verification is not null)
		{
			var verification = PrepareVerification(replies.Shortlist) ?? throw new ArgumentException();
			if (verification.ParseResponse(replies.Verification).Decision.Failure is not null)
			{
				throw new ArgumentException();
			}
		}
	}

	internal async Task<CategorySuggestionResult> ParseProductionAsync(string response, CancellationToken cancellationToken = default)
	{
		using ReplayHandler handler = new(ProductionPayload, response);
		using HttpClient client = new(handler);
		using JevSuggestionConcurrency concurrency = new();
		JevSuggestionProvider provider = new(client, new ReplayOptions(), concurrency);
		return await provider.SuggestAsync(production, cancellationToken);
	}

	internal static int CountQuestions(string payload)
	{
		using JsonDocument document = JsonDocument.Parse(payload);
		return document.RootElement.GetProperty("questions").EnumerateObject().Count();
	}

	// This sealed handler cannot delegate to HttpClientHandler or SocketsHttpHandler.
	private sealed class ReplayHandler(string expected, string response) : HttpMessageHandler
	{
		public int Calls { get; private set; }
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (++Calls != 1 || await request.Content!.ReadAsStringAsync(cancellationToken) != expected)
			{
				throw new InvalidOperationException("Production request drift or duplicate replay attempt.");
			}
			return new(HttpStatusCode.OK) { Content = new StringContent(response) };
		}
	}

	private sealed class ReplayOptions : IOptionsMonitor<JevOptions>
	{
		// Literal fake credential satisfies the real provider's guard; never loaded from the environment.
		public JevOptions CurrentValue => new() { Enabled = true, InferenceEnabled = true, ApiKey = "offline-replay-only" };
		public JevOptions Get(string? name) => CurrentValue;
		public IDisposable? OnChange(Action<JevOptions, string?> listener) => null;
	}
}

public static class PairedReplay
{
	public static PairedReplayPlan Prepare(PairedInput input)
	{
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(input.Purchase);
		ArgumentNullException.ThrowIfNull(input.Taxonomy);
		if (CategorySuggestionPolicy.Model != CandidateWire.SupportedEvaluationModel)
		{
			throw new InvalidOperationException("Pinned model drift.");
		}
		SuggestionCategory[] taxonomy = input.Taxonomy.ToArray();
		CategorySuggestionRequest production = new(input.Purchase.Description, input.TaxonomyRevision,
			Array.AsReadOnly(taxonomy));
		if (!CategorySuggestionPolicy.IsValid(production))
		{
			throw new ArgumentException("Input is not eligible for both evaluation arms.", nameof(input));
		}
		byte[] bytes = JevSuggestionQuestions.Serialize(production);
		if (bytes.Length > CategorySuggestionPolicy.MaximumPayloadBytes)
		{
			throw new ArgumentException("Production payload limit exceeded.", nameof(input));
		}
		ExhaustiveShortlistPlan shortlist = ExhaustiveShortlist.Prepare(input.Purchase, input.TaxonomyRevision,
			taxonomy, CandidateWire.SupportedEvaluationModel);
		string payload = Encoding.UTF8.GetString(bytes);
		byte[] binding = JsonSerializer.SerializeToUtf8Bytes(new
		{
			version = PairedReplayPlan.Version,
			baselineRevision = PairedReplayPlan.BaselineRevision,
			candidateRevision = PairedReplayPlan.CandidateRevision,
			baselinePolicy = CategorySuggestionPolicy.Version,
			candidatePolicy = SupportedCategoryCandidate.Version,
			wireFormat = CandidateWire.Format,
			shortlistVersion = ExhaustiveShortlist.Version,
			model = CandidateWire.SupportedEvaluationModel,
			productionPayload = payload,
			shortlist = shortlist.PlanSha256,
			shortlistPayloads = shortlist.Batches.Select(batch => batch.Payload).ToArray(),
			maximumReplayRequests = shortlist.Batches.Count + 2,
			maximumNetworkCalls = 0,
			retries = 0,
			maximumVerificationQuestions = 2 + 2 * SupportedCategoryCandidate.MaximumVerificationPaths,
			inputTokensPerRequest = (long?)null,
			outputTokensPerRequest = (long?)null,
			tokensPerQuestion = (long?)null
		});
		return new(production, shortlist, payload, Convert.ToHexStringLower(SHA256.HashData(binding)));
	}
}
