using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Classification.Experimental;

namespace Receipts.JevCandidateParity;

public sealed record CandidateWireResult(CandidateJudgment? Judgment, CandidateDecision Decision,
	long? InputTokens, long? OutputTokens);

/// <summary>An offline request and its parser. It cannot send requests or authorize assignments.</summary>
public sealed class CandidateWireRequest
{
	private readonly CandidateVerificationPlan plan;
	private readonly string model;
	private readonly IReadOnlyDictionary<string, (string? CategoryId, bool Context)> questions;

	internal CandidateWireRequest(CandidateVerificationPlan plan, string model, string binding, string payload,
		IReadOnlyDictionary<string, (string? CategoryId, bool Context)> questions)
	{
		this.plan = plan;
		this.model = model;
		this.questions = questions;
		PlanSha256 = binding;
		Payload = payload;
	}

	public string PlanSha256 { get; }
	public string Payload { get; }

	public CandidateWireResult ParseResponse(string raw)
	{
		if (raw is null || raw.Length > CandidateWire.MaximumResponseBytes ||
			Encoding.UTF8.GetByteCount(raw) > CandidateWire.MaximumResponseBytes)
		{
			return Invalid();
		}
		try
		{
			using JsonDocument document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
			JsonElement root = document.RootElement;
			RequireObject(root, ["model", "answers", "usage"]);
			if (root.GetProperty("model").GetString() != model)
			{
				return Invalid();
			}
			JsonElement answers = root.GetProperty("answers");
			RequireObject(answers, questions.Keys);
			Dictionary<string, double> values = new(StringComparer.Ordinal);
			foreach (string id in questions.Keys)
			{
				JsonElement answer = answers.GetProperty(id);
				RequireObject(answer, ["type", "noul"]);
				if (answer.GetProperty("type").GetString() != "noul" ||
					!answer.GetProperty("noul").TryGetDouble(out double value) ||
					!double.IsFinite(value) || value is < 0 or > 1)
				{
					return Invalid();
				}
				values.Add(id, value);
			}
			JsonElement usage = root.GetProperty("usage");
			RequireObject(usage, ["input_tokens", "output_tokens"]);
			long input = usage.GetProperty("input_tokens").GetInt64();
			long output = usage.GetProperty("output_tokens").GetInt64();
			if (input < 0 || output < 0)
			{
				return Invalid();
			}
			Dictionary<string, CandidatePathJudgment> paths = new(StringComparer.Ordinal);
			foreach (CandidateVerificationPath path in plan.Paths)
			{
				string support = questions.Single(question => question.Value.CategoryId == path.CategoryId && !question.Value.Context).Key;
				string context = questions.Single(question => question.Value.CategoryId == path.CategoryId && question.Value.Context).Key;
				paths.Add(path.CategoryId, new(values[support], values[context]));
			}
			CandidateJudgment judgment = new(plan.TaxonomyRevision, values[$"{PlanSha256}_purchase"],
				values[$"{PlanSha256}_unlisted"], paths);
			return new(judgment, SupportedCategoryCandidate.Decide(plan, judgment), input, output);
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
		{
			return Invalid();
		}
	}

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

	private static CandidateWireResult Invalid() => new(null, new(null, [], null, "invalid-response"), null, null);
}

/// <summary>Deterministic TypeSafe-shaped bytes only; no transport, credential access, or runtime registration.</summary>
public static class CandidateWire
{
	public const string Format = "candidate-typesafe-wire-v1";
	public const string SupportedEvaluationModel = "jev-1.14.0";
	public const int MaximumRequestBytes = 8 * 1024 * 1024;
	public const int MaximumResponseBytes = 1024 * 1024;

	public static CandidateWireRequest Prepare(CandidateVerificationPlan plan, string model)
	{
		ArgumentNullException.ThrowIfNull(plan);
		if (model != SupportedEvaluationModel)
		{
			throw new ArgumentException("The supported pinned evaluation model must be supplied explicitly.", nameof(model));
		}
		CandidatePlanSnapshot snapshot = new(CandidateParity.SnapshotFormat, SupportedCategoryCandidate.Version,
			SupportedCategoryCandidate.EvidenceRule, SupportedCategoryCandidate.SupportedThreshold,
			SupportedCategoryCandidate.RejectedThreshold, SupportedCategoryCandidate.PurchaseEvidenceQuestion,
			SupportedCategoryCandidate.UnlistedAlternativeQuestion, SupportedCategoryCandidate.PathSupportQuestion,
			SupportedCategoryCandidate.PathContextQuestion, plan);
		string binding = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
			new { format = Format, model, snapshot }, CandidateParity.JsonOptions)));
		Dictionary<string, object> wireQuestions = new(StringComparer.Ordinal);
		Dictionary<string, (string? CategoryId, bool Context)> mapping = new(StringComparer.Ordinal);
		Add("purchase", SupportedCategoryCandidate.PurchaseEvidenceQuestion, null, false, null);
		Add("unlisted", SupportedCategoryCandidate.UnlistedAlternativeQuestion, null, false, null);
		int index = 0;
		foreach (CandidateVerificationPath path in plan.Paths)
		{
			Add($"path_{index}_support", SupportedCategoryCandidate.PathSupportQuestion, path.CategoryId, false, path);
			Add($"path_{index}_context", SupportedCategoryCandidate.PathContextQuestion, path.CategoryId, true, path);
			index++;
		}
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
		{
			model,
			state = new
			{
				purchase = plan.Purchase,
				taxonomyRevision = plan.TaxonomyRevision,
				taxonomy = plan.Taxonomy,
				paths = plan.Paths,
				evidenceRule = SupportedCategoryCandidate.EvidenceRule
			},
			questions = wireQuestions
		}, CandidateParity.JsonOptions);
		if (bytes.Length > MaximumRequestBytes)
		{
			throw new ArgumentException("Candidate request exceeds the offline payload bound.", nameof(plan));
		}
		return new(plan, model, binding, Encoding.UTF8.GetString(bytes), mapping);

		void Add(string suffix, CandidateNoulQuestion question, string? categoryId, bool context, CandidateVerificationPath? path)
		{
			string id = $"{binding}_{suffix}";
			wireQuestions.Add(id, new
			{
				type = "noul",
				instructions = new { question = question.Question, evidence = "Follow state.evidenceRule.", path },
				criteria = new Dictionary<string, string> { ["true"] = question.TrueCriterion, ["false"] = question.FalseCriterion }
			});
			mapping.Add(id, (categoryId, context));
		}
	}
}
