using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.Classification;
using Infrastructure.Jev;

namespace Receipts.JevProductionParity;

/// <summary>What production would have sent, decided and written for one recorded round-4 case.</summary>
public sealed record ProductionCase(string Id, bool Stage1BytesMatch, bool Stage2BytesMatch, bool Stage2RootsMatch,
	string Outcome, string? AbstentionReason, bool Stage2Sent, CategorySuggestion? Suggestion, CategoryAutoWriteDecision? AutoWrite,
	CategoryAutoWriteDecision? AutoWriteWithLeaves);

public sealed record ProductionReplay(int Cases, int Stage1BytesMatched, int Stage2BytesMatched, int Stage2RootsMatched,
	int Suggested, int AbstainedNoFit, int AbstainedUncertainFit, int Failed, int Stage2Requests,
	int AutoWriteRoot, int AutoWriteLeaf, int AutoWriteWithLeavesRoot, int AutoWriteWithLeavesLeaf, int SuggestionOnly,
	CategoryAutoWriteOptions Options, IReadOnlyList<ProductionCase> Outcomes);

/// <summary>
/// Offline production parity for round 4: rebuilds every recorded request with the production builder, replays the
/// recorded responses through the production parser, decision and auto-write policy, and counts what would be written.
/// No provider, credential or transport. Production skips stage 2 after a stage-1 abstention; the recorded stage-2
/// answer is then only used to check request bytes.
/// </summary>
public static class ProductionParity
{
	public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

	public static ProductionReplay Replay(TwoStagePacket packet, IReadOnlyList<ReplayExchange> exchanges, CategoryAutoWriteOptions options)
	{
		ArgumentNullException.ThrowIfNull(packet);
		ArgumentNullException.ThrowIfNull(exchanges);
		ArgumentNullException.ThrowIfNull(options);
		if (exchanges.Count != 2 * packet.Cases.Count || exchanges.Any(e => e is null || e.StatusCode is < 200 or >= 300))
		{
			throw new ArgumentException("Production parity needs one successful stage-1 and stage-2 exchange per case.");
		}

		CategoryAutoWriteOptions withLeaves = new()
		{
			RootAutoWriteThreshold = options.RootAutoWriteThreshold,
			LeafAutoWriteThreshold = options.LeafAutoWriteThreshold,
			LeafAutoWriteEnabled = true
		};
		List<ProductionCase> outcomes = [];
		for (int c = 0; c < packet.Cases.Count; c++)
		{
			ReplayExchange first = exchanges[2 * c];
			ReplayExchange second = exchanges[(2 * c) + 1];
			CategorySuggestionRequest request = TwoStageQuestions.Request(packet.Cases[c], packet.Taxonomy);
			bool stage1Bytes = Hashing.Hash(JevTwoStageQuestions.Stage1(request)) == first.RequestSha256;
			CategoryStageJudgment? judgment = JevTwoStageQuestions.ParseStage1(Encoding.UTF8.GetBytes(first.Body), request);
			CategorySuggestionResult stage1 = judgment is null ? CategorySuggestionResult.Failed(SuggestionFailure.InvalidResponse)
				: CategorySuggestionPolicy.DecideStage1(request, judgment, "jev");
			// The evaluation sent stage 2 for every case, over its own top-3 ranking.
			IReadOnlyList<string> evaluationRoots = judgment is null ? []
				: TwoStageQuestions.TopRootIds(packet.Taxonomy, judgment.Probabilities);
			bool stage2Bytes = evaluationRoots.Count > 0 &&
				Hashing.Hash(JevTwoStageQuestions.Stage2(request, evaluationRoots)) == second.RequestSha256;
			if (stage1.Suggestion is not { } baseline)
			{
				outcomes.Add(new(packet.Cases[c].Id, stage1Bytes, stage2Bytes, false, "failed", null, false, null, null, null));
				continue;
			}

			if (baseline.CategoryId is null)
			{
				outcomes.Add(new(packet.Cases[c].Id, stage1Bytes, stage2Bytes, true, "abstained", baseline.AbstentionReason, false,
					baseline, null, null));
				continue;
			}

			IReadOnlyList<string> roots = CategorySuggestionPolicy.Stage2RootIds(baseline);
			CategoryStageJudgment? leaf = JevTwoStageQuestions.ParseStage2(Encoding.UTF8.GetBytes(second.Body), request, roots);
			CategorySuggestion? final = leaf is null ? null : CategorySuggestionPolicy.DecideStage2(request, baseline, leaf).Suggestion;
			outcomes.Add(new(packet.Cases[c].Id, stage1Bytes, stage2Bytes, roots.SequenceEqual(evaluationRoots),
				final is null ? "failed" : "suggested", null, true, final ?? baseline,
				CategoryAutoWritePolicy.Decide(request, final, options), CategoryAutoWritePolicy.Decide(request, final, withLeaves)));
		}

		return new(outcomes.Count, outcomes.Count(o => o.Stage1BytesMatch), outcomes.Count(o => o.Stage2BytesMatch),
			outcomes.Count(o => o.Stage2RootsMatch), outcomes.Count(o => o.Outcome == "suggested"),
			outcomes.Count(o => o.AbstentionReason == "no-fit"), outcomes.Count(o => o.AbstentionReason == "uncertain-fit"),
			outcomes.Count(o => o.Outcome == "failed"), outcomes.Count(o => o.Stage2Sent),
			outcomes.Count(o => o.AutoWrite?.Level == CategoryAutoWriteLevel.Root), outcomes.Count(o => o.AutoWrite?.Level == CategoryAutoWriteLevel.Leaf),
			outcomes.Count(o => o.AutoWriteWithLeaves?.Level == CategoryAutoWriteLevel.Root),
			outcomes.Count(o => o.AutoWriteWithLeaves?.Level == CategoryAutoWriteLevel.Leaf),
			outcomes.Count(o => o.Outcome == "suggested" && o.AutoWrite is null), options, outcomes);
	}

	private static JsonSerializerOptions CreateJsonOptions()
	{
		JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
		options.Converters.Add(new JsonStringEnumConverter());
		options.MakeReadOnly(populateMissingResolver: true);
		return options;
	}
}
