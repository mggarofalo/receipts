using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Classification.Experimental;

namespace Receipts.JevCandidateParity;

public sealed record VerificationBatch(int Index, bool IsGlobalCoverage, string Payload, IReadOnlyList<string> QuestionIds);

/// <summary>Offline bounded requests; no response can produce a decision before the complete set is validated.</summary>
public sealed class BatchedVerificationPlan
{
	private readonly CandidateWireRequest complete;
	private readonly IReadOnlyDictionary<string, string> originalIds;

	internal BatchedVerificationPlan(CandidateWireRequest complete, string binding, VerificationBatch[] batches,
		IReadOnlyDictionary<string, string> originalIds)
	{
		this.complete = complete;
		this.originalIds = originalIds;
		PlanSha256 = binding;
		Batches = Array.AsReadOnly(batches);
	}

	public string PlanSha256 { get; }
	public IReadOnlyList<VerificationBatch> Batches { get; }

	/// <summary>Use a pinned-model tokenizer before any separately authorized transport. Byte bounds do not certify token fit.</summary>
	public void ValidateInputTokenBudget(Func<string, int> countInputTokens, int maximumInputTokens)
	{
		ArgumentNullException.ThrowIfNull(countInputTokens);
		if (maximumInputTokens < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(maximumInputTokens));
		}

		foreach (VerificationBatch batch in Batches)
		{
			int tokens = countInputTokens(batch.Payload);
			if (tokens < 1 || tokens > maximumInputTokens)
			{
				throw new ArgumentException("A complete verification request exceeds the supplied model input token budget; no context was truncated.");
			}
		}
	}

	/// <summary>Exactly one response per request, in request order. Coverage is global, never inferred from per-batch answers.</summary>
	public CandidateWireResult Aggregate(IReadOnlyList<string> responses)
	{
		if (responses is null || responses.Count != Batches.Count)
		{
			return Invalid();
		}

		try
		{
			JsonObject answers = new();
			long input = 0, output = 0;
			for (int i = 0; i < Batches.Count; i++)
			{
				string raw = responses[i];
				if (raw is null || raw.Length > CandidateWire.MaximumResponseBytes ||
					Encoding.UTF8.GetByteCount(raw) > CandidateWire.MaximumResponseBytes)
				{
					return Invalid();
				}

				using JsonDocument document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
				JsonElement root = document.RootElement;
				RequireObject(root, ["model", "answers", "usage"]);
				if (root.GetProperty("model").GetString() != CandidateWire.SupportedEvaluationModel)
				{
					return Invalid();
				}

				JsonElement batchAnswers = root.GetProperty("answers");
				RequireObject(batchAnswers, Batches[i].QuestionIds);
				foreach (string id in Batches[i].QuestionIds)
				{
					JsonElement answer = batchAnswers.GetProperty(id);
					RequireObject(answer, ["type", "noul"]);
					if (answer.GetProperty("type").GetString() != "noul" ||
						!answer.GetProperty("noul").TryGetDouble(out double probability) ||
						!double.IsFinite(probability) || probability is < 0 or > 1)
					{
						return Invalid();
					}

					answers.Add(originalIds[id], JsonNode.Parse(answer.GetRawText()));
				}
				JsonElement usage = root.GetProperty("usage");
				RequireObject(usage, ["input_tokens", "output_tokens"]);
				long batchInput = usage.GetProperty("input_tokens").GetInt64();
				long batchOutput = usage.GetProperty("output_tokens").GetInt64();
				if (batchInput < 0 || batchOutput < 0)
				{
					return Invalid();
				}

				input = checked(input + batchInput);
				output = checked(output + batchOutput);
			}
			return complete.ParseResponse(new JsonObject
			{
				["model"] = CandidateWire.SupportedEvaluationModel,
				["answers"] = answers,
				["usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output }
			}.ToJsonString());
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException or
			FormatException or OverflowException or ArgumentException)
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

	private static CandidateWireResult Invalid() => new(null, new(null, [], null, "invalid-batched-response"), null, null);
}

public static class BatchedVerification
{
	public const string Version = "offline-batched-verification-v1";
	public const int MaximumPathsPerBatch = 32;
	public const int MaximumBatchBytes = 32 * 1024;

	public static BatchedVerificationPlan Prepare(CandidateVerificationPlan plan, string model)
	{
		ArgumentNullException.ThrowIfNull(plan);
		CandidateWireRequest complete = CandidateWire.Prepare(plan, model);
		string binding = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
		{
			Version,
			complete.PlanSha256,
			MaximumPathsPerBatch,
			MaximumBatchBytes
		})));
		JsonObject source = JsonNode.Parse(complete.Payload)!.AsObject();
		JsonObject sourceQuestions = source["questions"]!.AsObject();
		Dictionary<string, string> originalIds = new(StringComparer.Ordinal);
		List<VerificationBatch> batches = [];
		// Exactly one coverage request sees the complete taxonomy and complete union of paths.
		AddBatch(true, [], ["purchase", "unlisted"]);
		List<int> pending = [];
		for (int index = 0; index < plan.Paths.Count; index++)
		{
			pending.Add(index);
			if (pending.Count > MaximumPathsPerBatch || Bytes(false, pending).Length > MaximumBatchBytes)
			{
				pending.RemoveAt(pending.Count - 1);
				if (pending.Count != 0)
				{
					AddPaths();
				}

				pending.Add(index);
				if (Bytes(false, pending).Length > MaximumBatchBytes)
				{
					throw new ArgumentException("A complete path exceeds the offline verification byte bound; no context was truncated.", nameof(plan));
				}
			}
		}
		if (pending.Count != 0)
		{
			AddPaths();
		}

		return new(complete, binding, batches.ToArray(), originalIds);

		void AddPaths()
		{
			AddBatch(false, pending, pending.SelectMany(i => new[] { $"path_{i}_support", $"path_{i}_context" }).ToArray());
			pending.Clear();
		}

		void AddBatch(bool global, IReadOnlyList<int> indexes, string[] suffixes)
		{
			byte[] bytes = Bytes(global, indexes);
			if (bytes.Length > MaximumBatchBytes)
			{
				throw new ArgumentException("Complete global coverage exceeds the offline verification byte bound; no taxonomy or union was truncated.", nameof(plan));
			}

			string[] ids = suffixes.Select(suffix => $"{binding}_{suffix}").ToArray();
			foreach (string suffix in suffixes)
			{
				originalIds.Add($"{binding}_{suffix}", $"{complete.PlanSha256}_{suffix}");
			}

			batches.Add(new(batches.Count, global, Encoding.UTF8.GetString(bytes), Array.AsReadOnly(ids)));
		}

		byte[] Bytes(bool global, IReadOnlyList<int> indexes)
		{
			string[] suffixes = global ? ["purchase", "unlisted"] : indexes.SelectMany(i => new[] { $"path_{i}_support", $"path_{i}_context" }).ToArray();
			JsonObject questions = new();
			foreach (string suffix in suffixes)
			{
				questions.Add($"{binding}_{suffix}", sourceQuestions[$"{complete.PlanSha256}_{suffix}"]!.DeepClone());
			}

			JsonObject state = source["state"]!.DeepClone().AsObject();
			if (!global)
			{
				state.Remove("taxonomy");
				state["paths"] = new JsonArray(indexes.Select(i => source["state"]!["paths"]![i]!.DeepClone()).ToArray());
			}
			return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
			{
				["model"] = model,
				["state"] = state,
				["questions"] = questions
			}, CandidateParity.JsonOptions);
		}
	}
}
