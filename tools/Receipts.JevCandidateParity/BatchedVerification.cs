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

	/// <summary>Exactly one response per request, in request order. Every coverage partition is required; maxima are logical guards only.</summary>
	public CandidateWireResult Aggregate(IReadOnlyList<string> responses) => Parse(responses, false);

	/// <summary>Validates only an ordered prefix; never produces a decision.</summary>
	public bool ValidatePrefix(IReadOnlyList<string> responses) => Parse(responses, true).Decision.Failure is null;

	private CandidateWireResult Parse(IReadOnlyList<string> responses, bool prefix)
	{
		if (responses is null || responses.Count > Batches.Count || (!prefix && responses.Count != Batches.Count))
		{
			return Invalid();
		}

		try
		{
			JsonObject answers = new();
			double uncoveredGuard = 0;
			long input = 0, output = 0;
			for (int i = 0; i < responses.Count; i++)
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

					if (id.EndsWith("_unlisted", StringComparison.Ordinal))
					{
						// Logical all-partitions guard, NOT a calibrated probability of their union.
						uncoveredGuard = Math.Max(uncoveredGuard, probability);
					}
					else
					{
						answers.Add(originalIds[id], JsonNode.Parse(answer.GetRawText()));
					}
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
			if (prefix)
			{
				return new(null, new(null, [], null, null), input, output);
			}
			answers.Add($"{complete.PlanSha256}_unlisted", new JsonObject
			{
				["type"] = "noul",
				["noul"] = uncoveredGuard
			});
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
	public const string Version = "offline-batched-verification-v2";
	public const int MaximumPathsPerBatch = 32;
	public const int MaximumBatchBytes = 32 * 1024;
	public const string CoverageQuestion = "Does any category in state.taxonomy plausibly fit the purchase evidence while absent from state.verifiedCategoryIds?";

	public static BatchedVerificationPlan Prepare(CandidateVerificationPlan plan, string model, bool globalCoverageLast = false)
	{
		ArgumentNullException.ThrowIfNull(plan);
		CandidateWireRequest complete = CandidateWire.Prepare(plan, model);
		string binding = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
		{
			Version,
			complete.PlanSha256,
			MaximumPathsPerBatch,
			MaximumBatchBytes,
			CoverageQuestion,
			coveragePolicy = "complete-taxonomy-with-ancestor-context-max-logical-guard-v1"
		})));
		if (globalCoverageLast)
		{
			binding = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(binding + ":global-coverage-last-v1")));
		}
		JsonObject source = JsonNode.Parse(complete.Payload)!.AsObject();
		JsonObject sourceQuestions = source["questions"]!.AsObject();
		Dictionary<string, string> originalIds = new(StringComparer.Ordinal);
		List<VerificationBatch> batches = [];
		// Every canonical taxonomy node is checked once; ancestors supply context only.
		if (!globalCoverageLast)
		{
			AddCoverage();
		}
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

		if (globalCoverageLast)
		{
			AddCoverage();
		}
		return new(complete, binding, batches.ToArray(), originalIds);

		void AddPaths()
		{
			AddBatch(false, pending, pending.SelectMany(i => new[] { $"path_{i}_support", $"path_{i}_context" }).ToArray());
			pending.Clear();
		}

		void AddCoverage()
		{
			List<int> partition = [];
			int partitionIndex = 0;
			for (int index = 0; index < plan.Taxonomy.Count; index++)
			{
				partition.Add(index);
				if (Bytes(true, partition, partitionIndex).Length > MaximumBatchBytes)
				{
					partition.RemoveAt(partition.Count - 1);
					if (partition.Count != 0)
					{
						Flush();
					}
					partition.Add(index);
					if (Bytes(true, partition, partitionIndex).Length > MaximumBatchBytes)
					{
						throw new ArgumentException("A complete coverage alternative and its ancestor context exceeds the offline verification byte bound; no taxonomy or union was truncated.", nameof(plan));
					}
				}
			}
			if (partition.Count != 0)
			{
				Flush();
			}
			void Flush()
			{
				string[] suffixes = partitionIndex == 0
						? ["purchase", $"coverage_{partitionIndex}_unlisted"]
						: [$"coverage_{partitionIndex}_unlisted"];
				AddBatch(true, partition, suffixes, partitionIndex);
				partitionIndex++;
				partition.Clear();
			}
		}

		void AddBatch(bool global, IReadOnlyList<int> indexes, string[] suffixes, int partitionIndex = 0)
		{
			byte[] bytes = Bytes(global, indexes, partitionIndex);
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

		byte[] Bytes(bool global, IReadOnlyList<int> indexes, int partitionIndex = 0)
		{
			string[] suffixes = global ? (partitionIndex == 0 ? ["purchase", $"coverage_{partitionIndex}_unlisted"] : [$"coverage_{partitionIndex}_unlisted"]) : indexes.SelectMany(i => new[] { $"path_{i}_support", $"path_{i}_context" }).ToArray();
			JsonObject questions = new();
			foreach (string suffix in suffixes)
			{
				JsonNode question = sourceQuestions[$"{complete.PlanSha256}_{(suffix.EndsWith("_unlisted", StringComparison.Ordinal) ? "unlisted" : suffix)}"]!.DeepClone();
				if (suffix.EndsWith("_unlisted", StringComparison.Ordinal))
				{
					question["instructions"]!["question"] = CoverageQuestion;
					question["criteria"]!["true"] = "At least one category in state.taxonomy plausibly fits the purchase evidence and its id is absent from state.verifiedCategoryIds. state.taxonomyAncestors supplies complete ancestor context only.";
					question["criteria"]!["false"] = "Every plausible category in state.taxonomy has its id in state.verifiedCategoryIds, or none of these categories plausibly fits the purchase evidence.";
				}
				questions.Add($"{binding}_{suffix}", question);
			}

			JsonObject state = source["state"]!.DeepClone().AsObject();
			if (global)
			{
				JsonArray taxonomy = source["state"]!["taxonomy"]!.AsArray();
				state["taxonomy"] = new JsonArray(indexes.Select(i => taxonomy[i]!.DeepClone()).ToArray());
				HashSet<string> members = indexes.Select(i => plan.Taxonomy[i].Id).ToHashSet(StringComparer.Ordinal);
				HashSet<string> ancestors = new(StringComparer.Ordinal);
				Dictionary<string, int> positions = plan.Taxonomy.Select((node, index) => (node.Id, index)).ToDictionary(x => x.Id, x => x.index, StringComparer.Ordinal);
				foreach (int index in indexes)
				{
					string? parent = plan.Taxonomy[index].ParentId;
					while (parent is not null)
					{
						if (!members.Contains(parent))
						{
							ancestors.Add(parent);
						}
						parent = plan.Taxonomy[positions[parent]].ParentId;
					}
				}
				state["taxonomyAncestors"] = new JsonArray(plan.Taxonomy.Select((node, index) => (node, index))
						.Where(x => ancestors.Contains(x.node.Id)).Select(x => taxonomy[x.index]!.DeepClone()).ToArray());
				state.Remove("paths");
				state["verifiedCategoryIds"] = new JsonArray(plan.Paths.Select(path => (JsonNode?)JsonValue.Create(path.CategoryId)).ToArray());
			}
			else
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
