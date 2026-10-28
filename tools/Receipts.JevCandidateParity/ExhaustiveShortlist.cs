using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Classification;
using Application.Classification.Experimental;

namespace Receipts.JevCandidateParity;

public sealed record ShortlistBatch(int Index, string Payload, IReadOnlyList<string> QuestionIds);
public sealed record ShortlistResult(CandidateVerificationPlan? VerificationPlan, string? AbstentionReason, string? Failure);

/// <summary>Offline exhaustive planning and reduction only. No inference or assignment authorization.</summary>
public sealed class ExhaustiveShortlistPlan
{
	private readonly CandidatePurchase purchase;
	private readonly string revision;
	private readonly SuggestionCategory[] taxonomy;
	private readonly CandidateVerificationPath[] paths;
	private readonly string model;
	private readonly IReadOnlyDictionary<string, string> categoriesByQuestion;

	internal ExhaustiveShortlistPlan(CandidatePurchase purchase, string revision, SuggestionCategory[] taxonomy,
		CandidateVerificationPath[] paths, string model, string binding, ShortlistBatch[] batches,
		IReadOnlyDictionary<string, string> categoriesByQuestion)
	{
		this.purchase = purchase;
		this.revision = revision;
		this.taxonomy = taxonomy;
		this.paths = paths;
		this.model = model;
		this.categoriesByQuestion = categoriesByQuestion;
		PlanSha256 = binding;
		Batches = Array.AsReadOnly(batches);
	}

	public string PlanSha256 { get; }
	public IReadOnlyList<ShortlistBatch> Batches { get; }

	/// <summary>Responses must be in batch order, with exactly one valid response for every batch.</summary>
	public ShortlistResult Aggregate(IReadOnlyList<string> responses)
	{
		if (responses is null || responses.Count != Batches.Count)
		{
			return Failed();
		}
		Dictionary<string, double> values = new(StringComparer.Ordinal);
		try
		{
			for (int i = 0; i < Batches.Count; i++)
			{
				string raw = responses[i];
				if (raw is null || raw.Length > CandidateWire.MaximumResponseBytes ||
					Encoding.UTF8.GetByteCount(raw) > CandidateWire.MaximumResponseBytes)
				{
					return Failed();
				}
				using JsonDocument document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
				JsonElement root = document.RootElement;
				RequireObject(root, ["model", "answers", "usage"]);
				if (root.GetProperty("model").GetString() != model)
				{
					return Failed();
				}
				JsonElement answers = root.GetProperty("answers");
				RequireObject(answers, Batches[i].QuestionIds);
				foreach (string id in Batches[i].QuestionIds)
				{
					JsonElement answer = answers.GetProperty(id);
					RequireObject(answer, ["type", "noul"]);
					if (answer.GetProperty("type").GetString() != "noul" ||
						!answer.GetProperty("noul").TryGetDouble(out double value) ||
						!double.IsFinite(value) || value is < 0 or > 1)
					{
						return Failed();
					}
					values.Add(categoriesByQuestion[id], value);
				}
				JsonElement usage = root.GetProperty("usage");
				RequireObject(usage, ["input_tokens", "output_tokens"]);
				if (usage.GetProperty("input_tokens").GetInt64() < 0 || usage.GetProperty("output_tokens").GetInt64() < 0)
				{
					return Failed();
				}
			}
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException or
			FormatException or OverflowException or ArgumentException)
		{
			return Failed();
		}

		// Independent judgments never prune another branch. Uncertain answers remain competitors.
		HashSet<string> retained = new(values.Where(x => x.Value > SupportedCategoryCandidate.RejectedThreshold)
			.Select(x => x.Key), StringComparer.Ordinal);
		if (retained.Count == 0)
		{
			return new(null, "no-plausible-path", null);
		}
		HashSet<string> ancestors = new(paths.Where(path => retained.Contains(path.CategoryId))
			.SelectMany(path => path.Categories.SkipLast(1)).Select(category => category.Id), StringComparer.Ordinal);
		string[] terminals = retained.Where(id => !ancestors.Contains(id)).Order(StringComparer.Ordinal).ToArray();
		int closure = paths.Where(path => terminals.Contains(path.CategoryId, StringComparer.Ordinal))
			.SelectMany(path => path.Categories).Select(category => category.Id).Distinct(StringComparer.Ordinal).Count();
		if (terminals.Length > SupportedCategoryCandidate.MaximumShortlistSize ||
			closure > SupportedCategoryCandidate.MaximumVerificationPaths)
		{
			return new(null, "shortlist-overflow", null);
		}
		return new(SupportedCategoryCandidate.Plan(purchase, revision, taxonomy, terminals), null, null);
	}

	private static ShortlistResult Failed() => new(null, null, "invalid-or-incomplete-shortlist-response");

	private static void RequireObject(JsonElement element, IEnumerable<string> expected)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			throw new JsonException();
		}
		HashSet<string> remaining = new(expected, StringComparer.Ordinal);
		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (!remaining.Remove(property.Name))
			{
				throw new JsonException();
			}
		}
		if (remaining.Count != 0)
		{
			throw new JsonException();
		}
	}
}

public static class ExhaustiveShortlist
{
	public const string Version = "offline-exhaustive-shortlist-v1";
	public const int MaximumQuestionsPerBatch = 16;
	public const int MaximumBatchBytes = 64 * 1024;

	public static CandidateNoulQuestion PlausibilityQuestion { get; } = new(
		"Could the purchased goods or service belong to this full category path based on purchase evidence?",
		"The supplied evidence is compatible with the terminal category within its ancestor definitions; details may be unresolved.",
		"The supplied evidence identifies goods or services outside this path's meaning.");

	public static ExhaustiveShortlistPlan Prepare(CandidatePurchase purchase, string taxonomyRevision,
		IReadOnlyList<SuggestionCategory> taxonomy, string model)
	{
		ArgumentNullException.ThrowIfNull(taxonomy);
		if (model != CandidateWire.SupportedEvaluationModel)
		{
			throw new ArgumentException("Supply the supported pinned evaluation model explicitly.", nameof(model));
		}
		if (taxonomy.Count is < 1 or > SupportedCategoryCandidate.MaximumTaxonomySize || taxonomy.Any(x => x is null))
		{
			throw new ArgumentException("Invalid taxonomy.", nameof(taxonomy));
		}
		SuggestionCategory[] snapshot = taxonomy.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
		// Reuse the candidate's complete input/graph validation. Every node is a possible terminal,
		// including broad categories; asking only taxonomy leaves would miss valid broad answers.
		CandidateVerificationPath[] paths = snapshot.Select(category => SupportedCategoryCandidate
			.Plan(purchase, taxonomyRevision, snapshot, [category.Id]).Paths.Single(path => path.CategoryId == category.Id)).ToArray();
		string binding = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
		{
			version = Version,
			verifierVersion = SupportedCategoryCandidate.Version,
			model,
			purchase,
			taxonomyRevision,
			taxonomy = snapshot,
			evidenceRule = SupportedCategoryCandidate.EvidenceRule,
			question = PlausibilityQuestion,
			rejectionThreshold = SupportedCategoryCandidate.RejectedThreshold,
			MaximumQuestionsPerBatch,
			MaximumBatchBytes,
			SupportedCategoryCandidate.MaximumShortlistSize,
			SupportedCategoryCandidate.MaximumVerificationPaths
		}, CandidateParity.JsonOptions)));
		List<ShortlistBatch> batches = [];
		Dictionary<string, string> mapping = new(StringComparer.Ordinal);
		Dictionary<string, object> questions = new(StringComparer.Ordinal);
		for (int i = 0; i < paths.Length; i++)
		{
			CandidateVerificationPath path = paths[i];
			string id = $"{binding}_plausible_{i}";
			object question = new
			{
				type = "noul",
				instructions = new { question = PlausibilityQuestion.Question, evidence = "Follow state.evidenceRule.", path },
				criteria = new Dictionary<string, string> { ["true"] = PlausibilityQuestion.TrueCriterion, ["false"] = PlausibilityQuestion.FalseCriterion }
			};
			questions.Add(id, question);
			if (questions.Count > MaximumQuestionsPerBatch || Bytes(questions).Length > MaximumBatchBytes)
			{
				questions.Remove(id);
				if (questions.Count != 0)
				{
					Flush();
				}
				questions.Add(id, question);
				if (Bytes(questions).Length > MaximumBatchBytes)
				{
					throw new ArgumentException("A complete path exceeds the offline batch byte bound; no path was truncated.", nameof(taxonomy));
				}
			}
			mapping.Add(id, path.CategoryId);
		}
		Flush();
		return new(purchase, taxonomyRevision, snapshot, paths, model, binding, batches.ToArray(), mapping);

		byte[] Bytes(Dictionary<string, object> batchQuestions) => JsonSerializer.SerializeToUtf8Bytes(new
		{
			model,
			state = new { purchase, taxonomyRevision, evidenceRule = SupportedCategoryCandidate.EvidenceRule },
			questions = batchQuestions
		}, CandidateParity.JsonOptions);

		void Flush()
		{
			batches.Add(new(batches.Count, Encoding.UTF8.GetString(Bytes(questions)), Array.AsReadOnly(questions.Keys.ToArray())));
			questions.Clear();
		}
	}
}
