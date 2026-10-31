using System.Text;
using System.Text.Json;
using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

/// <summary>Round-4 two-stage request, replay, reservation and dry run. Synthetic data only; nothing is sent.</summary>
public class TwoStageEvaluationTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "jev-two-stage-" + Guid.NewGuid().ToString("N"));
	private string Packet => Path.Combine(directory, "packet");
	private string Journal => Path.Combine(Packet, "live.journal.jsonl");

	public TwoStageEvaluationTests() => Directory.CreateDirectory(Packet);
	public void Dispose() => Directory.Delete(directory, recursive: true);

	private static TwoStageTaxonomy Taxonomy { get; } = new("synthetic-rev", [
		new("food", "Food", "Food", [new("fruit", "Fruit", "Fruit"), new("bread", "Bread", "Baked loaves and rolls")]),
		new("tools", "Tools", "Hand tools and fasteners", [new("bolts", "Bolts", "Bolts"), new("misc", "Misc", "Misc")]),
		new("toys", "Toys", "Toys", []),
		new("garden", "Garden", "Garden", [new("seeds", "Seeds", "Seeds")])]);

	private static TwoStageCase Case(int i, string? store = "Synthetic Store") => new("synthetic-" + i, "synthetic pear " + i, store);

	private static TwoStagePolicy Policy(int cases, long carry = 250_000_000, long ceiling = 5_000_000_000,
		int approvedAttempts = 120, long cap = 350_000_000) => new(cases, 2 * cases, carry, ceiling, approvedAttempts, cap);

	private static TwoStagePacket NewPacket(int cases = 2) =>
		TwoStagePacket.Create(Policy(cases), [.. Enumerable.Range(0, cases).Select(i => Case(i))], Taxonomy);

	private static Dictionary<string, object> PolicyJson(int cases) => new()
	{
		["packetKind"] = "owner-labels-v4",
		["model"] = "jev-1.14.0",
		["questionPolicy"] = "two-stage-v1",
		["cases"] = cases,
		["maximumAttempts"] = 2 * cases,
		["approvedMaximumAttempts"] = 120,
		["approvedReservationCapNanodollars"] = 350_000_000L,
		["reservationBasis"] = "utf8-request-bytes",
		["inputNanodollarsPerToken"] = 42,
		["headroomInputTokens"] = 65_536,
		["conservativePriorCarryNanodollars"] = 250_000_000L,
		["cumulativeCeilingNanodollars"] = 5_000_000_000L,
		["liveAuthorized"] = false
	};

	private void WritePacket(int cases = 2, Action<Dictionary<string, object>>? edit = null)
	{
		Dictionary<string, object> policy = PolicyJson(cases);
		edit?.Invoke(policy);
		Dictionary<string, string> hashes = [];
		void Write(string name, object value)
		{
			string path = Path.Combine(Packet, name);
			File.WriteAllText(path, JsonSerializer.Serialize(value, ProductionParity.JsonOptions));
			hashes[name] = Hashing.Hash(File.ReadAllBytes(path));
		}
		Write("policy.private.json", policy);
		Write("cases.private.json", Enumerable.Range(0, cases).Select(i => Case(i)).ToArray());
		Write("taxonomy.private.json", Taxonomy);
		File.WriteAllText(Path.Combine(Packet, "freeze.private.json"), JsonSerializer.Serialize(new
		{
			sourceCommit = SyntheticPacket.Commit,
			filesSha256 = hashes
		}));
	}

	private static JsonElement Request(byte[] payload) => JsonDocument.Parse(payload).RootElement.Clone();

	private static string[] OptionKeys(JsonElement request, string question) =>
		[.. request.GetProperty("questions").GetProperty(question).GetProperty("criteria").EnumerateObject().Select(p => p.Name)];

	/// <summary>A synthetic Jev-shaped answer: the first offered option gets the given weight.</summary>
	private static ReplayExchange Answer(byte[] payload, double top = 0.7, long inputTokens = -1, string? prefer = null)
	{
		JsonElement request = Request(payload);
		Dictionary<string, object> answers = [];
		foreach (JsonProperty question in request.GetProperty("questions").EnumerateObject())
		{
			if (question.Value.GetProperty("type").GetString() == "noul")
			{
				answers[question.Name] = new { type = "noul", noul = 0.9 };
				continue;
			}

			string[] keys = OptionKeys(request, question.Name);
			string winner = prefer is not null && keys.Contains(prefer) ? prefer : keys[0];
			double rest = (1 - top) / (keys.Length - 1);
			answers[question.Name] = new
			{
				type = "choice",
				choice = winner,
				confidence = top,
				probabilities = keys.ToDictionary(k => k, k => k == winner ? top : rest)
			};
		}
		return new(JsonSerializer.Serialize(new
		{
			model = "jev-1.14.0",
			answers,
			usage = new { input_tokens = inputTokens < 0 ? payload.Length / 2 : inputTokens, output_tokens = 5 }
		}));
	}

	[Fact]
	public void StageOneOffersItemRootsByNameWithStoreAndNoRepeatedDefinitions()
	{
		JsonElement request = Request(TwoStageQuestions.Stage1(Case(1), Taxonomy));
		Assert.Equal("jev-1.14.0", request.GetProperty("model").GetString());
		JsonElement purchase = request.GetProperty("state").GetProperty("purchase");
		Assert.Equal("synthetic pear 1", purchase.GetProperty("receiptText").GetString());
		Assert.Equal("Synthetic Store", purchase.GetProperty("store").GetString());
		Assert.Equal(["Food", "Tools", "Toys", "Garden"], OptionKeys(request, "category"));
		JsonElement criteria = request.GetProperty("questions").GetProperty("category").GetProperty("criteria");
		Assert.Equal(JsonValueKind.Null, criteria.GetProperty("Food").ValueKind);
		Assert.Equal("Hand tools and fasteners", criteria.GetProperty("Tools").GetString());
		JsonElement fits = request.GetProperty("questions").GetProperty("fits");
		Assert.Equal("noul", fits.GetProperty("type").GetString());
		Assert.Equal(4, fits.GetProperty("instructions").GetProperty("categories").GetArrayLength());
		// The receipt text stays in state; questions never embed it.
		Assert.DoesNotContain("synthetic pear", request.GetProperty("questions").GetRawText());
	}

	[Fact]
	public void UnknownStoreIsOmittedNotSentEmpty()
	{
		JsonElement purchase = Request(TwoStageQuestions.Stage1(Case(1, store: null), Taxonomy)).GetProperty("state").GetProperty("purchase");
		Assert.False(purchase.TryGetProperty("store", out _));
	}

	[Fact]
	public void StageTwoOffersLeavesOfChosenRootsInTaxonomyOrderWithRootOnlyFallback()
	{
		JsonElement request = Request(TwoStageQuestions.Stage2(Case(1), Taxonomy, ["toys", "food", "tools"]));
		Assert.Equal(["Food / Fruit", "Food / Bread", "Tools / Bolts", "Tools / Misc", "Toys"], OptionKeys(request, "subcategory"));
		Assert.Single(request.GetProperty("questions").EnumerateObject());
	}

	[Fact]
	public void TopRootsBreakTiesByTaxonomyOrder()
	{
		Dictionary<string, double> p = new() { ["food"] = 0.2, ["tools"] = 0.3, ["toys"] = 0.2, ["garden"] = 0.3 };
		Assert.Equal(["tools", "garden", "food"], TwoStageQuestions.TopRootIds(Taxonomy, p));
	}

	[Fact]
	public void ParsingRejectsAnythingButACompleteValidAnswer()
	{
		byte[] payload = TwoStageQuestions.Stage1(Case(1), Taxonomy);
		string good = Answer(payload, prefer: "Tools").Body;
		TwoStageChoice parsed = TwoStageQuestions.ParseStage1(good, Taxonomy)!;
		Assert.Equal(0.7, parsed.Probabilities["tools"], 6);
		Assert.Equal(0.9, parsed.Fit);
		Assert.Null(TwoStageQuestions.ParseStage1(good.Replace("jev-1.14.0", "jev-1.13.0"), Taxonomy));
		Assert.Null(TwoStageQuestions.ParseStage1(good.Replace("\"Garden\"", "\"Gardening\""), Taxonomy));
		Assert.Null(TwoStageQuestions.ParseStage1(good.Replace("\"noul\":0.9", "\"noul\":1.5"), Taxonomy));
		Assert.Null(TwoStageQuestions.ParseStage1(good.Replace("0.7", "0.9"), Taxonomy));
		Assert.Null(TwoStageQuestions.ParseStage1("{}", Taxonomy));
		Assert.Null(TwoStageQuestions.ParseStage2(good, Taxonomy, ["food", "tools", "toys"]));
	}

	[Fact]
	public void ReplayReservesOneTokenPerRequestByteAndFollowsStageOne()
	{
		TwoStagePacket packet = NewPacket();
		List<ReplayExchange> exchanges = [];
		TwoStageReplay replay;
		while ((replay = TwoStageEvaluation.Replay(packet, exchanges)).Stopped)
		{
			TwoStageAttempt next = replay.Attempts[^1];
			byte[] payload = Encoding.UTF8.GetBytes(next.RequestJson);
			exchanges.Add(Answer(payload, prefer: next.Stage == "stage-1" ? "Garden" : null) with { RequestSha256 = next.RequestSha256 });
		}

		Assert.Equal(2, replay.CompletedCases);
		Assert.Equal(["stage-1", "stage-2", "stage-1", "stage-2"], replay.Attempts.Select(a => a.Stage));
		Assert.All(replay.Attempts, a => Assert.Equal(Encoding.UTF8.GetByteCount(a.RequestJson), a.ReservedInputTokens));
		Assert.All(replay.Attempts, a => Assert.Equal(a.ReservedInputTokens * 42, a.ReservationNanodollars));
		Assert.Equal(replay.Attempts.Sum(a => a.ReservationNanodollars), replay.ReservationNanodollars);
		Assert.Equal(["garden", "food", "tools"], replay.Outcomes[0].TopRootIds);
		Assert.Equal(250_000_000 + replay.ReservationNanodollars, replay.CombinedReservationNanodollars);
	}

	[Fact]
	public void InvalidOrMissingExchangesStopAndKeepTheirReservation()
	{
		TwoStagePacket packet = NewPacket();
		TwoStageReplay missing = TwoStageEvaluation.Replay(packet, []);
		Assert.True(missing.Stopped);
		Assert.Equal(["unknown", "unattempted"], missing.Outcomes.Select(o => o.Status));
		Assert.Single(missing.Attempts);
		TwoStageReplay failed = TwoStageEvaluation.Replay(packet, [new ReplayExchange("{}", 402)]);
		Assert.Equal(["failed", "unattempted"], failed.Outcomes.Select(o => o.Status));
		Assert.Equal(failed.Attempts[0].ReservationNanodollars, failed.ReservationNanodollars);
		Assert.Throws<ArgumentException>(() => TwoStageEvaluation.Replay(packet,
			[new ReplayExchange("{}", 402), new ReplayExchange("{}", 402)]));
	}

	[Fact]
	public void WorstCaseStageTwoBoundIsTheLargestOfEveryRootTriple()
	{
		TwoStagePacket packet = NewPacket(1);
		string[] roots = [.. Taxonomy.Roots.Select(r => r.Id)];
		int largest = 0;
		for (int a = 0; a < roots.Length; a++)
		{
			for (int b = a + 1; b < roots.Length; b++)
			{
				for (int c = b + 1; c < roots.Length; c++)
				{
					largest = Math.Max(largest, TwoStageQuestions.Stage2(Case(0), Taxonomy, [roots[a], roots[b], roots[c]]).Length);
				}
			}
		}

		Assert.Equal(largest, packet.MaximumStage2Bytes[0]);
		Assert.Equal(packet.Stage1Payloads[0].Length + largest + 65_536, packet.MaximumReservationTokens);
		Assert.Equal(packet.MaximumReservationTokens * 42, packet.MaximumReservationNanodollars);
	}

	[Fact]
	public void PacketRefusesAmbiguousKeysAndBudgetOverruns()
	{
		TwoStageCase[] cases = [Case(0)];
		Assert.Throws<ArgumentException>(() => TwoStagePacket.Create(Policy(1), cases, Taxonomy with
		{
			Roots = [.. Taxonomy.Roots, new("dup", "Food", "Food", [])]
		}));
		Assert.Throws<ArgumentException>(() => TwoStagePacket.Create(Policy(1), cases, Taxonomy with
		{
			Roots = [.. Taxonomy.Roots, new("slash", "Food / Fruit", "x", [])]
		}));
		Assert.Throws<ArgumentException>(() => TwoStagePacket.Create(Policy(1, cap: 1_000), cases, Taxonomy));
		Assert.Throws<ArgumentException>(() => TwoStagePacket.Create(Policy(1, carry: 4_999_000_000), cases, Taxonomy));
		Assert.Throws<ArgumentException>(() => TwoStagePacket.Create(Policy(2), cases, Taxonomy));
		Assert.Throws<ArgumentException>(() => TwoStagePacket.Create(Policy(1), [Case(0), Case(0)], Taxonomy));
	}

	[Theory]
	[InlineData("maximumAttempts", 5)]
	[InlineData("approvedMaximumAttempts", 2)]
	[InlineData("reservationBasis", "fixed")]
	[InlineData("liveAuthorized", true)]
	[InlineData("questionPolicy", "classification-questions-v6")]
	public void PolicyDriftIsRejected(string field, object value)
	{
		WritePacket(cases: 2, edit: p => p[field] = value);
		Assert.Throws<ArgumentException>(() => TwoStagePacket.Load(Packet));
	}

	[Fact]
	public void FrozenPacketLoadsAndDetectsDrift()
	{
		WritePacket();
		Assert.Equal(2, TwoStagePacket.Load(Packet).Cases.Count);
		File.AppendAllText(Path.Combine(Packet, "taxonomy.private.json"), " ");
		Assert.Throws<ArgumentException>(() => TwoStagePacket.Load(Packet));
	}

	[Fact]
	public async Task JournalledRunSendsTwoRequestsPerCaseWithByteReservationsAndResumesWithoutResending()
	{
		TwoStagePacket packet = NewPacket();
		int calls = 0;
		Task<ReplayExchange> Send(byte[] payload, CancellationToken ct)
		{
			Assert.Throws<IOException>(() => File.OpenRead(Journal));
			calls++;
			return Task.FromResult(Answer(payload));
		}

		TwoStageReplay first = await JournalledRun.RunAsync(new TwoStageProtocol(packet), Journal, "pin", Send, () => { });
		TwoStageReplay second = await JournalledRun.RunAsync(new TwoStageProtocol(packet), Journal, "pin", Send, () => { });
		Assert.Equal(4, calls);
		Assert.False(second.Stopped);
		Assert.Equal(first.ReservationNanodollars, second.ReservationNanodollars);
		string[] intents = [.. File.ReadAllLines(Journal).Where(l => l.Contains("\"intent\"", StringComparison.Ordinal))];
		Assert.Equal(4, intents.Length);
		for (int i = 0; i < intents.Length; i++)
		{
			using JsonDocument intent = JsonDocument.Parse(intents[i]);
			Assert.Equal(first.Attempts[i].ReservationNanodollars, intent.RootElement.GetProperty("reservationNanodollars").GetInt64());
		}
	}

	[Fact]
	public async Task JournalIsCreatedOwnerOnly()
	{
		await JournalledRun.RunAsync(new TwoStageProtocol(NewPacket()), Journal, "pin", (payload, _) => Task.FromResult(Answer(payload)), () => { });
		if (!OperatingSystem.IsWindows())
		{
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Journal));
		}
	}

	[Fact]
	public async Task DriftOnTheFinalSavedExchangeFailsWithoutSending()
	{
		TwoStagePacket packet = NewPacket();
		int calls = 0;
		Task<ReplayExchange> Send(byte[] payload, CancellationToken ct)
		{
			calls++;
			return Task.FromResult(Answer(payload));
		}

		await JournalledRun.RunAsync(new TwoStageProtocol(packet), Journal, "pin", Send, () => { });
		Assert.Equal(4, calls);
		// Rebind the final intent and response to a request replay will not rebuild.
		string[] lines = File.ReadAllLines(Journal);
		string saved = JsonDocument.Parse(lines[^1]).RootElement.GetProperty("requestSha256").GetString()!;
		string other = new(saved[0] == 'a' ? 'b' : 'a', 64);
		lines[^2] = lines[^2].Replace(saved, other, StringComparison.Ordinal);
		lines[^1] = lines[^1].Replace(saved, other, StringComparison.Ordinal);
		File.WriteAllLines(Journal, lines);

		InvalidOperationException drift = await Assert.ThrowsAsync<InvalidOperationException>(
			() => JournalledRun.RunAsync(new TwoStageProtocol(packet), Journal, "pin", Send, () => { }));
		Assert.Equal("Request drift.", drift.Message);
		Assert.Equal(4, calls);
	}

	[Fact]
	public async Task UsageAboveTheByteReservationStopsBeforeTheNextSend()
	{
		TwoStagePacket packet = NewPacket();
		int calls = 0;
		Task<ReplayExchange> Send(byte[] payload, CancellationToken ct)
		{
			calls++;
			return Task.FromResult(Answer(payload, inputTokens: payload.Length + 1));
		}

		await Assert.ThrowsAsync<InvalidOperationException>(() => JournalledRun.RunAsync(new TwoStageProtocol(packet), Journal, "pin", Send, () => { }));
		await Assert.ThrowsAsync<InvalidOperationException>(() => JournalledRun.RunAsync(new TwoStageProtocol(packet), Journal, "pin", Send, () => { }));
		Assert.Equal(1, calls);
	}

	[Fact]
	public void ApprovalMustCoverTheByteBoundAndHeadroom()
	{
		TwoStagePacket packet = NewPacket();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		LiveApproval approval = new("freeze", now.AddDays(1), true, true, 250_000_000, 5_000_000_000);
		Assert.Null(TwoStageCommand.ApprovalProblem(approval, packet, "freeze", now));
		Assert.Equal("budget-mismatch", TwoStageCommand.ApprovalProblem(approval with { PriorCarryNanodollars = 0 }, packet, "freeze", now));
		Assert.Equal("budget-mismatch", TwoStageCommand.ApprovalProblem(
			approval with { AuthorizedBudgetNanodollars = 250_000_000 + packet.MaximumReservationNanodollars - 1 }, packet, "freeze", now));
	}

	[Fact]
	public async Task DryRunReportsNumbersWithoutContentOrJournal()
	{
		WritePacket();
		string repository = Path.Combine(directory, "repository");
		Directory.CreateDirectory(repository);
		string output = Path.Combine(directory, "dry-run.json");
		int code = await LiveCommand.RunAsync(["live-run", Packet, repository, Path.Combine(directory, "approval.json"), Journal, output, "--dry-run"],
			_ => null, isWindows: false);
		Assert.Equal(0, code);
		Assert.False(File.Exists(Journal));
		string report = File.ReadAllText(output);
		Assert.DoesNotContain("synthetic pear", report);
		Assert.DoesNotContain("Synthetic Store", report);
		using JsonDocument document = JsonDocument.Parse(report);
		JsonElement numbers = document.RootElement.GetProperty("numbers");
		Assert.Equal(4, numbers.GetProperty("maximumAttempts").GetInt32());
		Assert.Equal("missing", document.RootElement.GetProperty("approval").GetProperty("status").GetString());
		Assert.False(document.RootElement.GetProperty("credentialPresent").GetBoolean());
		Assert.True(numbers.GetProperty("maximumReservationNanodollars").GetInt64() <
			numbers.GetProperty("fixedReservationComparisonNanodollars").GetInt64());
	}

	[Fact]
	public async Task OfflinePlanAndReplayCommandsWriteOnlyToNewPrivateFiles()
	{
		WritePacket();
		string repository = Path.Combine(directory, "repository");
		Directory.CreateDirectory(repository);
		string plan = Path.Combine(directory, "plan.json");
		Assert.Equal(0, await OfflineCommand.RunAsync(["offline-plan", Packet, repository, plan]));
		using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(plan)))
		{
			Assert.Equal(2, document.RootElement.GetProperty("cases").GetArrayLength());
		}

		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-plan", Packet, repository, plan]));
		string exchanges = Path.Combine(directory, "exchanges.json");
		File.WriteAllText(exchanges, "[]");
		string replay = Path.Combine(directory, "replay.json");
		Assert.Equal(0, await OfflineCommand.RunAsync(["offline-replay-v4", Packet, exchanges, repository, replay]));
		using JsonDocument result = JsonDocument.Parse(File.ReadAllText(replay));
		Assert.True(result.RootElement.GetProperty("stopped").GetBoolean());
		if (!OperatingSystem.IsWindows())
		{
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(plan));
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(replay));
		}
	}

	[Fact]
	public async Task OfflineCommandsRejectOutputInsideTheRepository()
	{
		WritePacket();
		string exchanges = Path.Combine(directory, "exchanges.json");
		File.WriteAllText(exchanges, "[]");
		string plan = Path.Combine(directory, "plan.json");
		string replay = Path.Combine(directory, "replay.json");
		// The packet's parent stands in for the checkout: both outputs fall inside it.
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-plan", Packet, directory, plan]));
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-replay-v4", Packet, exchanges, directory, replay]));
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-replay-v4", Packet, exchanges, replay]));
		Assert.False(File.Exists(plan));
		Assert.False(File.Exists(replay));
	}
}
