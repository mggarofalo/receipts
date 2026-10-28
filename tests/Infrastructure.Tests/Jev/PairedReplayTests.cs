using System.Text;
using System.Text.Json;
using Application.Classification;
using Application.Classification.Experimental;
using Infrastructure.Jev;
using Receipts.JevPairedRunner;

namespace Infrastructure.Tests.Jev;

public class PairedReplayTests
{
	[Fact]
	public async Task OfflineExchangeRejectsRealJunctionInEveryPrivatePath()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}
		string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
		string target = Path.Combine(directory, "target");
		string junction = Path.Combine(directory, "junction");
		Directory.CreateDirectory(target);
		try
		{
			var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			foreach (string argument in new[] { "/d", "/c", "mklink", "/J", junction, target })
			{
				start.ArgumentList.Add(argument);
			}
			using var process = System.Diagnostics.Process.Start(start)!;
			await process.WaitForExitAsync();
			Assert.Equal(0, process.ExitCode);
			Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
			string input = Path.Combine(target, "input.json");
			string replies = Path.Combine(target, "replies.json");
			string output = Path.Combine(target, "output.json");
			await File.WriteAllTextAsync(input, JsonSerializer.Serialize(Input(), Receipts.JevCandidateParity.CandidateParity.JsonOptions));
			await File.WriteAllTextAsync(replies, "{\"production\":\"\",\"shortlist\":[],\"verification\":null}");

			Assert.Equal(2, await OfflineExchangeCommand.RunAsync(["prepare", Path.Combine(junction, "input.json"), replies, output]));
			Assert.Equal(2, await OfflineExchangeCommand.RunAsync(["prepare", input, Path.Combine(junction, "replies.json"), output]));
			Assert.Equal(2, await OfflineExchangeCommand.RunAsync(["prepare", input, replies, Path.Combine(junction, "output.json")]));
			Assert.False(File.Exists(output));
		}
		finally
		{
			if (Directory.Exists(junction))
			{
				Directory.Delete(junction);
			}
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task OfflineExchangeExportsActualBytesAndNeverOverwrites()
	{
		string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
		Directory.CreateDirectory(directory);
		try
		{
			string input = Path.Combine(directory, "input.json");
			string replies = Path.Combine(directory, "replies.json");
			string output = Path.Combine(directory, "output.json");
			await File.WriteAllTextAsync(input, JsonSerializer.Serialize(Input(), Receipts.JevCandidateParity.CandidateParity.JsonOptions));
			await File.WriteAllTextAsync(replies, "{\"production\":\"\",\"shortlist\":[],\"verification\":null}");

			Assert.Equal(0, await OfflineExchangeCommand.RunAsync(["prepare", input, replies, output]));
			using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(output));
			Assert.Equal(PairedReplay.Prepare(Input()).ProductionPayload,
				document.RootElement.GetProperty("productionPayload").GetString());
			Assert.Equal(0, document.RootElement.GetProperty("maximumNetworkCalls").GetInt32());
			Assert.Equal(2, await OfflineExchangeCommand.RunAsync(["prepare", input, replies, output]));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	private static PairedInput Input(string? store = null, string? code = null) => new(
		new CandidatePurchase("Purchased fresh apples", store, code), "synthetic-v1",
		[new("food", "Food", "Edible goods", null), new("apple", "Apples", "Fresh apples", "food"),
		 new("tools", "Tools", "Hand tools", null)]);

	private const string ProductionReply = """
        {"model":"jev-1.14.0","answers":{
          "category":{"type":"choice","choice":"food","confidence":0.95,"probabilities":{"food":0.95,"tools":0.05}},
          "branch_0":{"type":"choice","choice":"apple","confidence":0.95,"probabilities":{"food":0.05,"apple":0.95}},
          "fits":{"type":"noul","noul":0.95}},"usage":{"input_tokens":100,"output_tokens":10}}
        """;

	private static string NoulReply(string payload, Func<JsonProperty, double> value)
	{
		using JsonDocument document = JsonDocument.Parse(payload);
		return JsonSerializer.Serialize(new
		{
			model = document.RootElement.GetProperty("model").GetString(),
			answers = document.RootElement.GetProperty("questions").EnumerateObject()
				.ToDictionary(question => question.Name, question => new { type = "noul", noul = value(question) }),
			usage = new { input_tokens = 100, output_tokens = 10 }
		});
	}

	private static string[] Shortlist(PairedReplayPlan plan) => plan.ShortlistBatches.Select(batch =>
		NoulReply(batch.Payload, question => question.Value.GetProperty("instructions").GetProperty("path")
			.GetProperty("categoryId").GetString() == "tools" ? .05 : .95)).ToArray();

	[Fact]
	public async Task ExecutesRealProductionAndCandidateReducersWithSamePurchaseAndTaxonomy()
	{
		PairedInput input = Input("Synthetic shop", "opaque-123");
		PairedReplayPlan plan = PairedReplay.Prepare(input);
		string[] shortlist = Shortlist(plan);
		var verification = plan.PrepareVerification(shortlist)!;
		string reply = NoulReply(verification.Payload, question => question.Name.EndsWith("_unlisted") ? .05 : .95);

		PairedResult result = await plan.ReplayAsync(new(ProductionReply, shortlist, reply));

		Assert.Equal("apple", result.Production.Suggestion!.CategoryId);
		Assert.Equal("apple", result.Candidate.CategoryId);
		Assert.Equal(3, result.ReplayRequests);
		Assert.Equal(verification.Payload, result.VerificationPayload);
		Assert.Equal(verification.PlanSha256, result.VerificationSha256);
		Assert.Equal(Encoding.UTF8.GetString(JevSuggestionQuestions.Serialize(new(input.Purchase.Description,
			input.TaxonomyRevision, input.Taxonomy))), plan.ProductionPayload);
		Assert.DoesNotContain("Synthetic shop", plan.ProductionPayload);
		Assert.DoesNotContain("opaque-123", plan.ProductionPayload);
		Assert.Contains("Synthetic shop", verification.Payload);
		Assert.Contains("opaque-123", verification.Payload);
		Assert.Equal(0, plan.MaximumNetworkCalls);
		Assert.False(plan.LiveExecutionEnabled);
		Assert.Equal(0, plan.Retries);
		Assert.Null(plan.MaximumInputTokensPerRequest);
		Assert.Null(plan.MaximumOutputTokensPerRequest);
		Assert.Null(plan.MaximumTokensPerQuestion);
	}

	[Fact]
	public void PlanSnapshotsCallerTaxonomyAndBindsPurchaseContext()
	{
		SuggestionCategory[] taxonomy = Input().Taxonomy.ToArray();
		PairedInput input = Input() with { Taxonomy = taxonomy };
		PairedReplayPlan plan = PairedReplay.Prepare(input);
		taxonomy[0] = taxonomy[0] with { Name = "MUTATED" };

		Assert.DoesNotContain("MUTATED", plan.ProductionPayload);
		Assert.DoesNotContain("MUTATED", plan.ShortlistBatches[0].Payload);
		Assert.Equal(plan.PlanSha256, PairedReplay.Prepare(Input()).PlanSha256);
		Assert.NotEqual(plan.PlanSha256, PairedReplay.Prepare(Input("different context")).PlanSha256);
		Assert.Equal(3, plan.MaximumReplayRequests);
		Assert.Equal(3, plan.ProductionQuestionCount);
		Assert.Equal(3, plan.ShortlistQuestionCount);
	}

	[Fact]
	public async Task IncompleteShortlistFailsClosedWithoutVerification()
	{
		PairedReplayPlan plan = PairedReplay.Prepare(Input());
		Assert.Null(plan.PrepareVerification([]));
		await Assert.ThrowsAsync<ArgumentException>(() => plan.ReplayAsync(new(ProductionReply, [], null)));
		PairedResult result = await plan.ReplayAsync(new(ProductionReply, ["{}"], null));
		Assert.NotNull(result.Candidate.Failure);
		Assert.Null(result.Candidate.CategoryId);
		Assert.Null(result.VerificationPayload);
		await Assert.ThrowsAsync<ArgumentException>(() => plan.ReplayAsync(new(ProductionReply, [], "{}")));
	}

	[Fact]
	public async Task NoPlausiblePathIsValidAbstentionAndSkipsVerification()
	{
		PairedReplayPlan plan = PairedReplay.Prepare(Input());
		string[] replies = plan.ShortlistBatches.Select(batch => NoulReply(batch.Payload, _ => .05)).ToArray();
		PairedResult result = await plan.ReplayAsync(new(ProductionReply, replies, null));
		Assert.Equal("no-plausible-path", result.Candidate.AbstentionReason);
		Assert.Null(result.Candidate.Failure);
		Assert.Equal(2, result.ReplayRequests);
	}

	[Fact]
	public async Task MalformedOrWrongModelRepliesAreFailuresWithoutRetry()
	{
		PairedReplayPlan plan = PairedReplay.Prepare(Input());
		string[] shortlist = Shortlist(plan);
		string verification = NoulReply(plan.PrepareVerification(shortlist)!.Payload, _ => .95)
			.Replace("jev-1.14.0", "wrong-model");
		PairedResult result = await plan.ReplayAsync(new("{}", shortlist, verification));
		Assert.Equal(SuggestionFailure.InvalidResponse, result.Production.Failure);
		Assert.Equal("invalid-response", result.Candidate.Failure);
		Assert.Equal(plan.MaximumReplayRequests, result.ReplayRequests);
	}

	[Fact]
	public async Task CancellationPreventsReplay()
	{
		using CancellationTokenSource source = new();
		source.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PairedReplay.Prepare(Input())
			.ReplayAsync(new(ProductionReply, [], null), source.Token));
	}

	[Fact]
	public async Task ExhaustiveBatchesCoverEveryNodeAndOverflowSkipsVerification()
	{
		SuggestionCategory[] taxonomy = Enumerable.Range(0, 33)
			.Select(index => new SuggestionCategory($"node-{index}", $"Kind {index}", $"Synthetic kind {index}", null)).ToArray();
		PairedReplayPlan plan = PairedReplay.Prepare(Input() with { Taxonomy = taxonomy });
		Assert.Equal(33, plan.ShortlistQuestionCount);
		Assert.Equal(3, plan.ShortlistBatches.Count);
		Assert.All(plan.ShortlistBatches, batch => Assert.InRange(batch.QuestionIds.Count, 1, 16));
		Assert.Equal(33, plan.ShortlistBatches.SelectMany(batch => batch.QuestionIds).Distinct().Count());
		string[] replies = plan.ShortlistBatches.Select(batch => NoulReply(batch.Payload, _ => .95)).ToArray();
		Assert.Null(plan.PrepareVerification(replies));
		PairedResult result = await plan.ReplayAsync(new("{}", replies, null));
		Assert.Equal("shortlist-overflow", result.Candidate.AbstentionReason);
		Assert.Equal(4, result.ReplayRequests);
		Assert.Equal(5, plan.MaximumReplayRequests);
		Assert.Null(result.VerificationPayload);
	}
}
