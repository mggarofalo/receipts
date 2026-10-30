using System.Text.Json;
using Application.Classification;
using Infrastructure.Jev;
using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

/// <summary>Offline round-4 production parity over the synthetic packet: production bytes, decisions and auto-write counts.</summary>
public sealed class ProductionParityTests : IDisposable
{
	private readonly SyntheticPacket files = new();

	public void Dispose() => files.Dispose();

	/// <summary>One recorded case: the stage-1 winner and fit, and the stage-2 winner the evaluation recorded.</summary>
	private sealed record Recorded(double Fit, string Root, double RootProbability, string Leaf, double LeafProbability);

	// Case 0 is a strong root and leaf; 1 and 2 abstain; 3 is a weak root that stays a suggestion.
	private static readonly Recorded[] Standard =
	[
		new(0.95, "fruit", 0.94, "apples", 0.95),
		new(0.1, "fruit", 0.4, "apples", 0.5),
		new(0.5, "dairy", 0.4, "milk", 0.5),
		new(0.9, "dairy", 0.6, "milk", 0.8)
	];

	/// <summary>Exchanges as the evaluation recorded them: stage 2 always sent, over the evaluation's own top-3 roots.</summary>
	private static List<ReplayExchange> Exchanges(TwoStagePacket packet, IReadOnlyList<Recorded> recorded)
	{
		List<ReplayExchange> exchanges = [];
		for (int c = 0; c < recorded.Count; c++)
		{
			CategorySuggestionRequest request = TwoStageQuestions.Request(packet.Cases[c], packet.Taxonomy);
			IReadOnlyList<JevTwoStageQuestions.Option> roots = JevTwoStageQuestions.Stage1Options(request);
			Dictionary<string, double> probabilities = roots.ToDictionary(o => o.CategoryId,
				o => o.CategoryId == recorded[c].Root ? recorded[c].RootProbability : (1 - recorded[c].RootProbability) / (roots.Count - 1));
			IReadOnlyList<string> top = TwoStageQuestions.TopRootIds(packet.Taxonomy, probabilities);
			exchanges.Add(new(SyntheticAnswers.Choice("category", roots, recorded[c].Root, recorded[c].RootProbability, recorded[c].Fit),
				200, Hashing.Hash(JevTwoStageQuestions.Stage1(request))));
			exchanges.Add(new(SyntheticAnswers.Choice("subcategory", JevTwoStageQuestions.Stage2Options(request, [.. top]), recorded[c].Leaf,
				recorded[c].LeafProbability), 200, Hashing.Hash(JevTwoStageQuestions.Stage2(request, [.. top]))));
		}

		return exchanges;
	}

	[Fact]
	public void ReplayMatchesProductionBytesAndCountsWhatWouldBeWritten()
	{
		TwoStagePacket packet = SyntheticPacket.Create();

		ProductionReplay replay = ProductionParity.Replay(packet, Exchanges(packet, Standard), new());

		Assert.Equal(4, replay.Cases);
		Assert.Equal(4, replay.Stage1BytesMatched);
		Assert.Equal(4, replay.Stage2BytesMatched);
		Assert.Equal(4, replay.Stage2RootsMatched);
		Assert.Equal(2, replay.Suggested);
		Assert.Equal(1, replay.AbstainedNoFit);
		Assert.Equal(1, replay.AbstainedUncertainFit);
		Assert.Equal(0, replay.Failed);
		Assert.Equal(2, replay.Stage2Requests);
		// Default options: roots only. The with-leaves counterfactual writes the strong leaf instead.
		Assert.Equal(1, replay.AutoWriteRoot);
		Assert.Equal(0, replay.AutoWriteLeaf);
		Assert.Equal(0, replay.AutoWriteWithLeavesRoot);
		Assert.Equal(1, replay.AutoWriteWithLeavesLeaf);
		Assert.Equal(1, replay.SuggestionOnly);

		ProductionCase strong = replay.Outcomes[0];
		Assert.Equal("apples", strong.Suggestion!.CategoryId);
		Assert.Equal(new CategoryAutoWriteDecision("fruit", CategoryAutoWriteLevel.Root, 0.95, 0.94, 0.95), strong.AutoWrite);
		Assert.Equal("apples", strong.AutoWriteWithLeaves!.CategoryId);
		Assert.Equal("milk", replay.Outcomes[3].Suggestion!.CategoryId);
		Assert.Null(replay.Outcomes[3].AutoWrite);
	}

	[Fact]
	public void AbstentionSkipsStageTwoButStillChecksItsRecordedBytes()
	{
		TwoStagePacket packet = SyntheticPacket.Create();

		ProductionReplay replay = ProductionParity.Replay(packet, Exchanges(packet, Standard), new());

		foreach (ProductionCase abstained in replay.Outcomes.Skip(1).Take(2))
		{
			Assert.Equal("abstained", abstained.Outcome);
			Assert.False(abstained.Stage2Sent);
			Assert.True(abstained.Stage2BytesMatch);
			Assert.Null(abstained.Suggestion!.CategoryId);
			Assert.Null(abstained.Suggestion.LeafCandidates);
			Assert.Null(abstained.AutoWrite);
		}

		Assert.Equal(["no-fit", "uncertain-fit"], replay.Outcomes.Skip(1).Take(2).Select(o => o.AbstentionReason));
	}

	[Fact]
	public void LeafWritesFollowTheSuppliedOptions()
	{
		TwoStagePacket packet = SyntheticPacket.Create();

		ProductionReplay replay = ProductionParity.Replay(packet, Exchanges(packet, Standard), new() { LeafAutoWriteEnabled = true });

		Assert.Equal(0, replay.AutoWriteRoot);
		Assert.Equal(1, replay.AutoWriteLeaf);
		Assert.True(replay.Options.LeafAutoWriteEnabled);
	}

	[Fact]
	public void LeafUnderAnotherRootBacksOffAndWritesOnlyTheRoot()
	{
		TwoStagePacket packet = SyntheticPacket.Create(1);

		ProductionReplay replay = ProductionParity.Replay(packet, Exchanges(packet, [new(0.95, "fruit", 0.94, "milk", 0.95)]),
			new() { LeafAutoWriteEnabled = true });

		ProductionCase outcome = Assert.Single(replay.Outcomes);
		Assert.Equal("fruit", outcome.Suggestion!.CategoryId);
		Assert.Equal("milk", outcome.Suggestion.LeafCandidates![0].CategoryId);
		Assert.Equal(CategoryAutoWriteLevel.Root, outcome.AutoWrite!.Level);
		Assert.Equal("fruit", outcome.AutoWrite.CategoryId);
	}

	[Fact]
	public void RecordedBytesThatProductionWouldNotSendAreCountedAsMismatches()
	{
		TwoStagePacket packet = SyntheticPacket.Create();
		List<ReplayExchange> exchanges = Exchanges(packet, Standard);
		exchanges[0] = exchanges[0] with { RequestSha256 = new string('a', 64) };
		exchanges[7] = exchanges[7] with { RequestSha256 = new string('b', 64) };

		ProductionReplay replay = ProductionParity.Replay(packet, exchanges, new());

		Assert.Equal(3, replay.Stage1BytesMatched);
		Assert.Equal(3, replay.Stage2BytesMatched);
		Assert.False(replay.Outcomes[0].Stage1BytesMatch);
		Assert.False(replay.Outcomes[3].Stage2BytesMatch);
		// Decisions are still replayed from the recorded answers.
		Assert.Equal(2, replay.Suggested);
	}

	[Fact]
	public void InvalidRecordedAnswersAreFailuresNotSuggestions()
	{
		TwoStagePacket packet = SyntheticPacket.Create();
		List<ReplayExchange> exchanges = Exchanges(packet, Standard);
		exchanges[0] = exchanges[0] with { Body = "{}" };
		exchanges[7] = exchanges[7] with { Body = exchanges[6].Body };

		ProductionReplay replay = ProductionParity.Replay(packet, exchanges, new());

		Assert.Equal(2, replay.Failed);
		Assert.Equal("failed", replay.Outcomes[0].Outcome);
		Assert.Null(replay.Outcomes[0].Suggestion);
		Assert.False(replay.Outcomes[0].Stage2Sent);
		// A stage-2 failure keeps the baseline for inspection but never writes.
		Assert.Equal("failed", replay.Outcomes[3].Outcome);
		Assert.Equal("dairy", replay.Outcomes[3].Suggestion!.CategoryId);
		Assert.Null(replay.Outcomes[3].AutoWrite);
	}

	[Fact]
	public void ReplayNeedsOneSuccessfulExchangePerStageAndCase()
	{
		TwoStagePacket packet = SyntheticPacket.Create();
		List<ReplayExchange> exchanges = Exchanges(packet, Standard);

		Assert.Throws<ArgumentException>(() => ProductionParity.Replay(packet, exchanges.Take(7).ToList(), new()));
		Assert.Throws<ArgumentException>(() => ProductionParity.Replay(packet,
			[.. exchanges.Take(7), exchanges[7] with { StatusCode = 402 }], new()));
	}

	[Fact]
	public void RequestListsRootsThenTheirLeavesInPacketOrder()
	{
		CategorySuggestionRequest request = TwoStageQuestions.Request(SyntheticPacket.Case(0), SyntheticPacket.Taxonomy);

		Assert.Equal("synthetic apple 0", request.Description);
		Assert.Equal("Synthetic Store", request.Store);
		Assert.Equal("synthetic-taxonomy", request.TaxonomyRevision);
		Assert.Equal(["fruit", "dairy", "bakery", "household", "apples", "citrus", "milk", "soap"], request.Categories.Select(c => c.Id));
		Assert.Equal([null, null, null, null, "fruit", "fruit", "dairy", "household"], request.Categories.Select(c => c.ParentId));
		Assert.True(JevTwoStageQuestions.Fits(request));
		Assert.Equal(JevTwoStageQuestions.Stage1(request), TwoStageQuestions.Stage1(SyntheticPacket.Case(0), SyntheticPacket.Taxonomy));
	}

	[Fact]
	public async Task OfflineProductionCommandWritesOnlyAPrivateArtifactOutsideTheCheckout()
	{
		files.Prepare();
		string exchanges = Path.Combine(files.Root, "exchanges.json");
		File.WriteAllText(exchanges, JsonSerializer.Serialize(Exchanges(SyntheticPacket.Create(), Standard), ProductionParity.JsonOptions));
		string output = Path.Combine(files.Root, "production.json");

		Assert.Equal(0, await OfflineCommand.RunAsync(["offline-production-v4", files.Packet, exchanges, files.Repository, output]));
		using (JsonDocument result = JsonDocument.Parse(File.ReadAllText(output)))
		{
			Assert.Equal(1, result.RootElement.GetProperty("autoWriteRoot").GetInt32());
			Assert.Equal(4, result.RootElement.GetProperty("stage1BytesMatched").GetInt32());
		}

		if (!OperatingSystem.IsWindows())
		{
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(output));
		}

		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-production-v4", files.Packet, exchanges, files.Repository, output]));
		string inside = Path.Combine(files.Repository, "production.json");
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-production-v4", files.Packet, exchanges, files.Repository, inside]));
		Assert.False(File.Exists(inside));
	}

	[Fact]
	public async Task OfflineProductionCommandRejectsUnboundExchanges()
	{
		files.Prepare();
		string exchanges = Path.Combine(files.Root, "exchanges.json");
		File.WriteAllText(exchanges, JsonSerializer.Serialize(
			Exchanges(SyntheticPacket.Create(), Standard).Select(e => e with { RequestSha256 = null }), ProductionParity.JsonOptions));
		string output = Path.Combine(files.Root, "production.json");

		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-production-v4", files.Packet, exchanges, files.Repository, output]));
		Assert.False(File.Exists(output));
	}
}
