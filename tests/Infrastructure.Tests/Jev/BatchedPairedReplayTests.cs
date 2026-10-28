using System.Text.Json;
using Application.Classification;
using Receipts.JevCandidateParity;
using Receipts.JevPairedRunner;

namespace Infrastructure.Tests.Jev;

public class BatchedPairedReplayTests
{
	private static PairedInput Input(int nodes = 112, string purchase = "Synthetic goods") => new(
		new(purchase, null, null), "synthetic-v1", nodes == 112
			? Enumerable.Range(0, 42).Select(i => new SuggestionCategory($"root-{i:D3}", $"Root {i}", "Goods", null))
				.Concat(Enumerable.Range(0, 70).Select(i => new SuggestionCategory($"leaf-{i:D3}", $"Kind {i}", "Goods", $"root-{i % 42:D3}"))).ToArray()
			: Enumerable.Range(0, nodes).Select(i => new SuggestionCategory($"node-{i:D3}", $"Kind {i}", "Goods", null)).ToArray());

	private static string Reply(string payload, double probability = .95)
	{
		using JsonDocument document = JsonDocument.Parse(payload);
		return JsonSerializer.Serialize(new
		{
			model = CandidateWire.SupportedEvaluationModel,
			answers = document.RootElement.GetProperty("questions").EnumerateObject().ToDictionary(
				q => q.Name, q => new { type = "noul", noul = q.Name.EndsWith("_unlisted") ? .05 : probability }),
			usage = new { input_tokens = 2, output_tokens = 1 }
		});
	}

	private static string ProductionReply(string payload)
	{
		using JsonDocument document = JsonDocument.Parse(payload);
		Dictionary<string, object> answers = [];
		foreach (JsonProperty question in document.RootElement.GetProperty("questions").EnumerateObject())
		{
			if (question.Value.GetProperty("type").GetString() == "noul")
			{
				answers.Add(question.Name, new { type = "noul", noul = .95 });
			}
			else
			{
				string[] options = question.Value.GetProperty("criteria").EnumerateObject().Select(p => p.Name).ToArray();
				answers.Add(question.Name, new
				{
					type = "choice",
					choice = options[0],
					confidence = 1,
					probabilities = options.ToDictionary(o => o, o => o == options[0] ? 1d : 0d)
				});
			}
		}
		return JsonSerializer.Serialize(new
		{
			model = CandidateWire.SupportedEvaluationModel,
			answers,
			usage = new { input_tokens = 2, output_tokens = 1 }
		});
	}

	[Theory]
	[InlineData(433)]
	[InlineData(449)]
	[InlineData(465)]
	[InlineData(1024)]
	public void InitialPlanRejectsCallOverflowBeforeExposingAnyPayload(int nodes)
	{
		ArgumentException error = Assert.Throws<ArgumentException>(() => new BatchedPairedReplayPlan(CallBoundInput(nodes)));
		Assert.Contains("30-request replay bound", error.Message);
	}

	[Fact]
	public void InitialBoundaryReservesVerificationAndExactVectorStillMustFit()
	{
		BatchedPairedReplayPlan plan = new(CallBoundInput(432));
		Assert.Equal(BatchedPairedReplayPlan.MaximumReplayRequests,
				1 + plan.ShortlistBatches.Count + BatchedPairedReplayPlan.MinimumVerificationRequests);
		// A selected root fits in the reserved minimum; no alternatives are pruned.
		string[] replies = plan.ShortlistBatches.Select(batch => ShortlistReply(batch.Payload, "_plausible_0")).ToArray();
		BatchedVerificationPlan verification = plan.PrepareVerification(replies)!;
		Assert.Equal(2, verification.Batches.Count);
		string[] wide = plan.ShortlistBatches.Select(batch => ShortlistReply(batch.Payload, "", 128)).ToArray();
		ArgumentException overflow = Assert.Throws<ArgumentException>(() => plan.PrepareVerification(wide));
		Assert.Contains("Complete verification exceeds the 30-request replay bound", overflow.Message);
	}

	[Fact]
	public async Task EveryCoveragePartitionRejectsCrossPlanRepliesThroughActualPairedParser()
	{
		PairedInput input = new(new("Synthetic goods", null, null), "synthetic-v1",
				Enumerable.Range(0, 309).Select(i => new SuggestionCategory($"node-{i:D3}", $"Kind {i}", new string('x', 80), i < 4 ? null : $"node-{i % 4:D3}")).ToArray());
		BatchedPairedReplayPlan plan = new(input);
		BatchedPairedReplayPlan other = new(input with { Purchase = new("Other synthetic goods", null, null) });
		string[] shortlist = plan.ShortlistBatches.Select(b => ShortlistReply(b.Payload, "_plausible_0")).ToArray();
		string[] otherShortlist = other.ShortlistBatches.Select(b => ShortlistReply(b.Payload, "_plausible_0")).ToArray();
		BatchedVerificationPlan verification = plan.PrepareVerification(shortlist)!;
		BatchedVerificationPlan crossPlan = other.PrepareVerification(otherShortlist)!;
		Assert.True(verification.Batches.Count(b => b.IsGlobalCoverage) > 1);
		string[] responses = verification.Batches.Select(b => Reply(b.Payload)).ToArray();
		var valid = await plan.ReplayAsync(new("{}", shortlist, responses));
		Assert.Null(valid.Candidate.Failure);
		Assert.NotNull(valid.Candidate.CategoryId);
		foreach (VerificationBatch partition in verification.Batches.Where(b => b.IsGlobalCoverage))
		{
			string[] swapped = responses.ToArray();
			swapped[partition.Index] = Reply(crossPlan.Batches[partition.Index].Payload);
			Assert.False(verification.ValidatePrefix(swapped));
			var invalid = await plan.ReplayAsync(new("{}", shortlist, swapped));
			Assert.NotNull(invalid.Candidate.Failure);
			Assert.Null(invalid.Candidate.CategoryId);
		}
	}

	private static PairedInput CallBoundInput(int nodes) => new(new("Goods", null, null), "v1",
			Enumerable.Range(0, nodes).Select(i => new SuggestionCategory($"c{i:D4}", "C", "G",
					i < 4 ? null : $"c{i % 4:D4}")).ToArray());

	private static string ShortlistReply(string payload, string selectedSuffix, int? plausibleLimit = null)
	{
		using JsonDocument document = JsonDocument.Parse(payload);
		return JsonSerializer.Serialize(new
		{
			model = CandidateWire.SupportedEvaluationModel,
			answers = document.RootElement.GetProperty("questions").EnumerateObject().ToDictionary(
						q => q.Name, q => new { type = "noul", noul = (plausibleLimit is int limit ? int.Parse(q.Name.Split('_')[^1]) < limit : q.Name.EndsWith(selectedSuffix)) ? .95 : .05 }),
			usage = new { input_tokens = 2, output_tokens = 1 }
		});
	}

	[Fact]
	public async Task SeventyTerminalsRemainCompetitorsUntilCoverageLastAndCompleteAggregation()
	{
		BatchedPairedReplayPlan plan = new(Input());
		string[] shortlist = plan.ShortlistBatches.Select(b => Reply(b.Payload)).ToArray();
		var verification = plan.PrepareVerification(shortlist)!;
		Assert.True(verification.Batches.Count > 2);
		Assert.True(verification.Batches[^1].IsGlobalCoverage);
		Assert.All(verification.Batches.SkipLast(1), b => Assert.False(b.IsGlobalCoverage));
		Assert.Equal(226, verification.Batches.Sum(b => b.QuestionIds.Count));
		string[] responses = verification.Batches.Select(b => Reply(b.Payload)).ToArray();
		for (int count = 0; count < responses.Length; count++)
		{
			Assert.True(verification.ValidatePrefix(responses.Take(count).ToArray()));
			var partial = await plan.ReplayAsync(new("{}", shortlist, responses.Take(count).ToArray()));
			Assert.Null(partial.Candidate.CategoryId);
			Assert.NotNull(partial.Candidate.Failure);
		}
		var complete = await plan.ReplayAsync(new("{}", shortlist, responses));
		Assert.Null(complete.Candidate.Failure);
		Assert.Equal("conflicting-paths", complete.Candidate.AbstentionReason);
		Assert.Equal(1 + shortlist.Length + responses.Length, complete.ReplayRequests);
		Assert.Equal(0, plan.MaximumNetworkCalls);
		Assert.Equal(0, plan.Retries);
	}

	[Fact]
	public async Task MissingDuplicateCrossPlanAndClosureOverflowFailClosed()
	{
		BatchedPairedReplayPlan plan = new(Input());
		string[] shortlist = plan.ShortlistBatches.Select(b => Reply(b.Payload)).ToArray();
		Assert.Throws<ArgumentException>(() => plan.PrepareVerification(shortlist.SkipLast(1).ToArray()));
		var verification = plan.PrepareVerification(shortlist)!;
		string[] replies = verification.Batches.Select(b => Reply(b.Payload)).ToArray();
		replies[1] = replies[0];
		Assert.False(verification.ValidatePrefix(replies));
		Assert.NotNull((await plan.ReplayAsync(new("{}", shortlist, replies))).Candidate.Failure);
		BatchedPairedReplayPlan other = new(Input(purchase: "Different purchase"));
		string[] otherShortlist = other.ShortlistBatches.Select(b => Reply(b.Payload)).ToArray();
		string[] crossPlan = other.PrepareVerification(otherShortlist)!.Batches.Select(b => Reply(b.Payload)).ToArray();
		Assert.False(verification.ValidatePrefix(crossPlan));
		Assert.NotNull((await plan.ReplayAsync(new("{}", shortlist, crossPlan))).Candidate.Failure);
		BatchedPairedReplayPlan overflow = new(Input(129));
		string[] overflowShortlist = overflow.ShortlistBatches.Select(b => Reply(b.Payload)).ToArray();
		Assert.Null(overflow.PrepareVerification(overflowShortlist));
		Assert.Equal("shortlist-overflow", (await overflow.ReplayAsync(new("{}", overflowShortlist, []))).Candidate.AbstentionReason);
	}

	[Fact]
	public async Task ActualPrivateFileCommandsExportAndValidateOrderedBatchesWithoutOverwritingEvidence()
	{
		string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
		Directory.CreateDirectory(directory);
		try
		{
			string input = Path.Combine(directory, "input.json");
			string replies = Path.Combine(directory, "replies.json");
			string output = Path.Combine(directory, "output.json");
			BatchedPairedReplayPlan plan = new(Input());
			string[] shortlist = plan.ShortlistBatches.Select(b => Reply(b.Payload)).ToArray();
			await File.WriteAllTextAsync(input, JsonSerializer.Serialize(Input(), CandidateParity.JsonOptions));
			await File.WriteAllTextAsync(replies, JsonSerializer.Serialize(new BatchedReplayReplies("", shortlist, []), CandidateParity.JsonOptions));
			Assert.Equal(0, await OfflineExchangeCommand.RunAsync(["prepare-batched", input, replies, output]));
			string saved = await File.ReadAllTextAsync(output);
			using JsonDocument document = JsonDocument.Parse(saved);
			JsonElement batches = document.RootElement.GetProperty("verificationBatches");
			Assert.Equal(plan.PrepareVerification(shortlist)!.Batches.Select(b => b.Payload),
				batches.EnumerateArray().Select(b => b.GetProperty("payload").GetString()));
			Assert.True(batches[batches.GetArrayLength() - 1].GetProperty("isGlobalCoverage").GetBoolean());
			Assert.Equal(2, await OfflineExchangeCommand.RunAsync(["prepare-batched", input, replies, output]));
			Assert.Equal(saved, await File.ReadAllTextAsync(output));
			string production = ProductionReply(plan.ProductionPayload);
			string[] responses = plan.PrepareVerification(shortlist)!.Batches.Select(b => Reply(b.Payload)).ToArray();
			for (int count = 0; count <= responses.Length; count++)
			{
				await File.WriteAllTextAsync(replies, JsonSerializer.Serialize(new BatchedReplayReplies(
					production, shortlist, responses.Take(count).ToArray()), CandidateParity.JsonOptions));
				Assert.Equal(0, await OfflineExchangeCommand.RunAsync(["validate-batched", input, replies,
					Path.Combine(directory, $"validated-{count}.json")]));
			}
			await File.WriteAllTextAsync(replies, JsonSerializer.Serialize(new BatchedReplayReplies(
				production, shortlist, [responses[1]]), CandidateParity.JsonOptions));
			string rejected = Path.Combine(directory, "rejected.json");
			Assert.Equal(2, await OfflineExchangeCommand.RunAsync(["validate-batched", input, replies, rejected]));
			Assert.False(File.Exists(rejected));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}
}
