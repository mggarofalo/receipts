using Application.Classification;
using Infrastructure.Jev;

namespace Receipts.JevProductionParity;

public sealed record TwoStageLeaf(string Id, string Name, string Definition);
public sealed record TwoStageRoot(string Id, string Name, string Definition, IReadOnlyList<TwoStageLeaf> Leaves);

/// <summary>Item roots only, in a fixed order, with placeholder leaves already removed by the packet builder.</summary>
public sealed record TwoStageTaxonomy(string Revision, IReadOnlyList<TwoStageRoot> Roots);

/// <summary>What a person would see on the receipt line: the item text and the store it came from.</summary>
public sealed record TwoStageCase(string Id, string Description, string? Store);

/// <summary>A parsed Choice: probabilities keyed by category id, plus billed usage.</summary>
public sealed record TwoStageChoice(IReadOnlyDictionary<string, double> Probabilities, double? Fit, long InputTokens, long OutputTokens);

/// <summary>
/// Packet adapter over the production two-stage request (<see cref="JevTwoStageQuestions"/>): the evaluation sends and
/// parses exactly what production does. Unlike production, an evaluation asks stage 2 for every case, so it can score
/// stage 2 even where production would abstain after stage 1.
/// </summary>
public static class TwoStageQuestions
{
	public const string Model = CategorySuggestionPolicy.Model;
	// Packet identity of round 4; production names the same wording CategorySuggestionPolicy.Version.
	public const string QuestionPolicy = "two-stage-v1";
	public const int TopRoots = CategorySuggestionPolicy.TopRoots;
	public const int MaximumOptions = CategorySuggestionPolicy.MaximumOptions;
	public const string Separator = JevTwoStageQuestions.Separator;

	/// <summary>The production request for one case: roots then their leaves, in packet order.</summary>
	public static CategorySuggestionRequest Request(TwoStageCase item, TwoStageTaxonomy taxonomy)
	{
		ArgumentNullException.ThrowIfNull(item);
		ArgumentNullException.ThrowIfNull(taxonomy);
		return new(item.Description, taxonomy.Revision,
			[.. taxonomy.Roots.Select(r => new SuggestionCategory(r.Id, r.Name, r.Definition, null)),
			 .. taxonomy.Roots.SelectMany(r => r.Leaves.Select(l => new SuggestionCategory(l.Id, l.Name, l.Definition, r.Id)))],
			item.Store);
	}

	public static byte[] Stage1(TwoStageCase item, TwoStageTaxonomy taxonomy) => JevTwoStageQuestions.Stage1(Request(item, taxonomy));

	public static byte[] Stage2(TwoStageCase item, TwoStageTaxonomy taxonomy, IReadOnlyCollection<string> rootIds) =>
		JevTwoStageQuestions.Stage2(Request(item, taxonomy), rootIds);

	public static IReadOnlyList<JevTwoStageQuestions.Option> Stage1Options(TwoStageTaxonomy taxonomy) =>
		JevTwoStageQuestions.Stage1Options(Request(Placeholder, taxonomy));

	public static IReadOnlyList<JevTwoStageQuestions.Option> Stage2Options(TwoStageTaxonomy taxonomy, IReadOnlyCollection<string> rootIds) =>
		JevTwoStageQuestions.Stage2Options(Request(Placeholder, taxonomy), rootIds);

	/// <summary>Top roots by stage-1 probability; ties keep taxonomy order (the production ranking).</summary>
	public static IReadOnlyList<string> TopRootIds(TwoStageTaxonomy taxonomy, IReadOnlyDictionary<string, double> rootProbabilities)
	{
		ArgumentNullException.ThrowIfNull(taxonomy);
		ArgumentNullException.ThrowIfNull(rootProbabilities);
		return [.. taxonomy.Roots.Select((r, i) => (r.Id, i)).OrderByDescending(x => rootProbabilities[x.Id]).ThenBy(x => x.i)
			.Take(TopRoots).Select(x => x.Id)];
	}

	/// <summary>Parses a stage-1 response with the production parser and judgment checks, or null when it is invalid.</summary>
	public static TwoStageChoice? ParseStage1(string body, TwoStageTaxonomy taxonomy)
	{
		CategorySuggestionRequest request = Request(Placeholder, taxonomy);
		CategoryStageJudgment? judgment = JevTwoStageQuestions.ParseStage1(System.Text.Encoding.UTF8.GetBytes(body), request);
		return judgment is { FitProbability: double fit } && double.IsFinite(fit) && fit is >= 0 and <= 1 &&
			CategorySuggestionPolicy.IsValidJudgment(judgment, CategorySuggestionPolicy.Roots(request))
			? new(judgment.Probabilities, fit, judgment.InputTokens, judgment.OutputTokens) : null;
	}

	public static TwoStageChoice? ParseStage2(string body, TwoStageTaxonomy taxonomy, IReadOnlyCollection<string> rootIds)
	{
		CategorySuggestionRequest request = Request(Placeholder, taxonomy);
		CategoryStageJudgment? judgment = JevTwoStageQuestions.ParseStage2(System.Text.Encoding.UTF8.GetBytes(body), request, rootIds);
		return judgment is not null && CategorySuggestionPolicy.IsValidJudgment(judgment, CategorySuggestionPolicy.Stage2Options(request, rootIds))
			? new(judgment.Probabilities, null, judgment.InputTokens, judgment.OutputTokens) : null;
	}

	// Options and parsing depend only on the taxonomy; the purchase text is never read.
	private static readonly TwoStageCase Placeholder = new("options", "options", null);
}
