using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.NormalizationAi;

namespace Receipts.JevProductionParity;

/// <summary>
/// RECEIPTS-1091 offline commands: build and freeze the smoke and full packets, plan, check replay parity and score.
/// Never writes request bodies, receipt text or identifiers to the console; private files go outside the checkout.
/// </summary>
public static class CatalogCommand
{
	// The owner's approval of 2026-11-01: at most 500 requests and USD 1.50 across both packets.
	public const long CeilingNanodollars = 1_500_000_000;
	public const int ApprovedRequests = 500;
	public const string SampleSeed = "receipts-1091-cohort-v1";

	/// <summary>The preregistered smoke cases, resent in the full run.</summary>
	public static readonly string[] SmokeCases = ["proxy-001", "proxy-002", "proxy-003", "not-in-shortlist-001", "not-in-shortlist-002",
		"hn-yogurt-flavor", "ho-franks-order-a", "inj-bananas", "inj-gift-card", "inj-example-pizza"];

	public static async Task<int> RunAsync(string[] args)
	{
		try
		{
			switch (args)
			{
				case ["catalog-build", var export, var synthetic, var catalogV2, var repository, var root, var commit]:
					PrivateEvidence.RequireOutside(repository, export, root);
					Build(export, synthetic, catalogV2, root, commit);
					Console.WriteLine("Smoke and full packets frozen. No provider request was sent.");
					return 0;
				case ["catalog-plan", var packet, var repository, var output]:
					PrivateEvidence.RequireOutside(repository, packet, output);
					await WriteAsync(output, Plan(CatalogPacket.Load(packet)));
					break;
				case ["catalog-parity", var packet, var repository, var liveOutput, var output]:
					PrivateEvidence.RequireOutside(repository, packet, liveOutput, output);
					object parity = Parity(CatalogPacket.Load(packet), Path.Combine(packet, "live.journal.jsonl"), liveOutput);
					await WriteAsync(output, parity);
					break;
				case ["catalog-score", var packet, var repository, var output]:
					PrivateEvidence.RequireOutside(repository, packet, output);
					CatalogPacket loaded = CatalogPacket.Load(packet);
					CatalogReplay replay = CatalogEvaluation.Replay(loaded, ReadJournal(Path.Combine(packet, "live.journal.jsonl")));
					await WriteAsync(output, CatalogScore.Score(loaded, replay, ReadLatencies(Path.Combine(packet, "latency.private.jsonl"))));
					break;
				default:
					Console.Error.WriteLine("Usage: catalog-build <export> <synthetic.json> <catalog.v2.json> <repository> <new-root> <source-commit> | " +
						"catalog-plan <packet> <repository> <output> | catalog-parity <packet> <repository> <live-output> <output> | " +
						"catalog-score <packet> <repository> <output>");
					return 2;
			}

			Console.WriteLine("Offline artifact created. No network transport was used.");
			return 0;
		}
		catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException or
			InvalidOperationException or KeyNotFoundException)
		{
			Console.Error.WriteLine("Catalog evaluation rejected input or could not create the artifact. No provider request was sent.");
			return 2;
		}
	}

	/// <summary>Budget and size numbers. No request content.</summary>
	public static object Numbers(CatalogPacket packet)
	{
		ArgumentNullException.ThrowIfNull(packet);
		int[] stage1 = [.. packet.Stage1Payloads.Where(p => p is not null).Select(p => p!.Length)];
		return new
		{
			packetKind = CatalogPolicy.PacketKind,
			questionPolicy = CatalogMatchingPolicy.Version,
			model = CatalogMatchingPolicy.Model,
			totalCases = packet.Cases.Count,
			strata = CatalogCase.Strata.ToDictionary(s => s, s => packet.Cases.Count(c => c.Stratum == s)),
			casesWithoutCandidates = packet.Cases.Count(c => c.Candidates.Count == 0),
			approvedExamplesSent = packet.Cases.Sum(c => c.Request.Candidates.Sum(candidate => candidate.ApprovedExamples.Count)),
			maximumAttempts = packet.Policy.MaximumAttempts,
			approvedMaximumAttempts = packet.Policy.ApprovedMaximumAttempts,
			stage1Bytes = new { minimum = stage1.Min(), maximum = stage1.Max(), total = stage1.Sum(b => (long)b) },
			maximumStage2BytesTotal = packet.MaximumStage2Bytes.Sum(b => (long)b),
			maximumReservationTokens = packet.MaximumReservationTokens,
			maximumReservationNanodollars = packet.MaximumReservationNanodollars,
			priorCarryNanodollars = packet.Policy.PriorCarryNanodollars,
			combinedMaximumReservationNanodollars = packet.CombinedMaximumReservationNanodollars,
			ceilingNanodollars = packet.Policy.CeilingNanodollars,
			liveAuthorized = false
		};
	}

	/// <summary>Private plan: numbers plus each stage-1 request, for review before any send.</summary>
	public static object Plan(CatalogPacket packet) => new
	{
		mode = "offline-only-no-dispatch",
		numbers = Numbers(packet),
		cases = packet.Cases.Select((c, i) => new
		{
			c.Id,
			c.Stratum,
			requestSha256 = packet.Stage1Payloads[i] is { } p ? Hashing.Hash(p) : null,
			requestJson = packet.Stage1Payloads[i] is { } q ? Encoding.UTF8.GetString(q) : null,
			maximumStage2Bytes = packet.MaximumStage2Bytes[i]
		}).ToArray()
	};

	/// <summary>
	/// Offline parity: every journalled request is rebuilt byte for byte from the packet, and replaying the journal
	/// through the production parser and decision reproduces the live run's saved result exactly.
	/// </summary>
	public static object Parity(CatalogPacket packet, string journalPath, string liveOutputPath)
	{
		IReadOnlyList<ReplayExchange> exchanges = ReadJournal(journalPath);
		CatalogReplay replay = CatalogEvaluation.Replay(packet, exchanges);
		bool requestsRebuilt = exchanges.Select((e, i) => e.RequestSha256 == replay.Attempts[i].RequestSha256).All(x => x);
		byte[] replayed = JsonSerializer.SerializeToUtf8Bytes(replay, ProductionParity.JsonOptions);
		bool resultIdentical = replayed.AsSpan().SequenceEqual(File.ReadAllBytes(liveOutputPath));
		return new
		{
			exchanges = exchanges.Count,
			requestsRebuilt,
			resultIdentical,
			parity = requestsRebuilt && resultIdentical,
			completedCases = replay.CompletedCases,
			stopped = replay.Stopped
		};
	}

	/// <summary>Builds the smoke and full packets in a new private root and freezes each.</summary>
	public static void Build(string exportPath, string syntheticPath, string catalogV2Path, string root, string commit)
	{
		List<CatalogCase> cases = [.. Exported(exportPath), .. Synthetic(syntheticPath), .. CatalogV2(catalogV2Path, syntheticPath)];
		// A seeded order mixes the strata, so a run that stops early does not lose one stratum entirely.
		cases = [.. cases.OrderBy(c => Hashing.Hash(Encoding.UTF8.GetBytes(SampleSeed + "\0order\0" + c.Id)), StringComparer.Ordinal)];
		List<CatalogCase> smoke = [.. SmokeCases.Select(id => cases.Single(c => c.Id == id))];

		CatalogPolicy smokePolicy = new(smoke.Count, 2 * smoke.Count(c => c.Candidates.Count > 0), 0, CeilingNanodollars,
			2 * SmokeCases.Length, CeilingNanodollars);
		CatalogPacket smokePacket = CatalogPacket.Create(smokePolicy, smoke);
		long carry = smokePacket.MaximumReservationNanodollars;
		CatalogPolicy fullPolicy = new(cases.Count, 2 * cases.Count(c => c.Candidates.Count > 0), carry, CeilingNanodollars,
			ApprovedRequests - smokePolicy.ApprovedMaximumAttempts, CeilingNanodollars - carry);
		_ = CatalogPacket.Create(fullPolicy, cases);
		if (Directory.Exists(root)) { throw new IOException("Root exists."); }

		CreatePrivateDirectory(root);
		Freeze(Path.Combine(root, "smoke"), smokePolicy, smoke, commit);
		Freeze(Path.Combine(root, "full"), fullPolicy, cases, commit);
	}

	private static void Freeze(string directory, CatalogPolicy policy, IReadOnlyList<CatalogCase> cases, string commit)
	{
		CreatePrivateDirectory(directory);
		Dictionary<string, string> hashes = new(StringComparer.Ordinal)
		{
			["policy.private.json"] = WriteNew(Path.Combine(directory, "policy.private.json"), new
			{
				packetKind = CatalogPolicy.PacketKind,
				model = CatalogMatchingPolicy.Model,
				questionPolicy = CatalogMatchingPolicy.Version,
				reservationBasis = TwoStagePolicy.ReservationBasis,
				inputNanodollarsPerToken = TwoStagePolicy.NanodollarsPerInputToken,
				headroomInputTokens = TwoStagePolicy.HeadroomInputTokens,
				liveAuthorized = false,
				cases = policy.Cases,
				maximumAttempts = policy.MaximumAttempts,
				approvedMaximumAttempts = policy.ApprovedMaximumAttempts,
				conservativePriorCarryNanodollars = policy.PriorCarryNanodollars,
				cumulativeCeilingNanodollars = policy.CeilingNanodollars,
				approvedReservationCapNanodollars = policy.ApprovedReservationCapNanodollars
			}),
			["cases.private.json"] = WriteNew(Path.Combine(directory, "cases.private.json"), cases)
		};
		WriteNew(Path.Combine(directory, "freeze.private.json"), new { sourceCommit = commit, filesSha256 = hashes });
		_ = CatalogPacket.Load(directory);
	}

	// Owner-only (0700 on Unix), like the private files inside it.
	private static void CreatePrivateDirectory(string path)
	{
		if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); }
		else { Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
	}

	private static string WriteNew(string path, object value)
	{
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, ProductionParity.JsonOptions);
		using (FileStream file = PrivateEvidence.CreateNew(path))
		{
			file.Write(bytes);
			file.Flush(flushToDisk: true);
		}

		return Hashing.Hash(bytes);
	}

	private static IEnumerable<CatalogCase> Exported(string path)
	{
		using JsonDocument export = JsonDocument.Parse(File.ReadAllText(path));
		if (export.RootElement.GetProperty("kind").GetString() != "catalog-jev-cohort-v1" ||
			export.RootElement.GetProperty("seed").GetString() != SampleSeed)
		{
			throw new ArgumentException("Unexpected export.");
		}

		return [.. export.RootElement.GetProperty("cases").EnumerateArray().Select(c => new CatalogCase(
			c.GetProperty("id").GetString()!, c.GetProperty("stratum").GetString()!, c.GetProperty("description").GetString()!,
			c.GetProperty("store").GetString(),
			c.GetProperty("candidates").Deserialize<CatalogMatchCandidate[]>(ProductionParity.JsonOptions)!,
			c.GetProperty("expectedCandidateId").ValueKind == JsonValueKind.Null ? null : c.GetProperty("expectedCandidateId").GetGuid()))];
	}

	private static IEnumerable<CatalogCase> Synthetic(string path)
	{
		using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(path));
		JsonElement root = fixture.RootElement;
		string store = root.GetProperty("store").GetString()!;
		return [.. root.GetProperty("cases").EnumerateArray().Select(c =>
		{
			string id = c.GetProperty("id").GetString()!;
			Guid? Key(JsonElement key) => key.ValueKind == JsonValueKind.Null ? null : SyntheticId(id, key.GetString()!);
			CatalogInjection? injection = c.TryGetProperty("injection", out JsonElement i)
				? new(i.GetProperty("kind").GetString()!, i.TryGetProperty("key", out JsonElement k) ? Key(k) : null) : null;
			return new CatalogCase(id, c.GetProperty("stratum").GetString()!, c.GetProperty("description").GetString()!, store,
				Candidates(id, c.GetProperty("candidates"), "name"), Key(c.GetProperty("expectedKey")), injection);
		})];
	}

	// catalog.v2 sends no store, as its ChatGPT runs did; its harness answers are the indirect baseline.
	private static IEnumerable<CatalogCase> CatalogV2(string path, string syntheticPath)
	{
		using JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(path));
		using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(syntheticPath));
		Dictionary<string, string> disagreements = fixture.RootElement.GetProperty("catalogV2Baseline").GetProperty("agreesWithLabelExcept")
			.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
		return [.. corpus.RootElement.GetProperty("cases").EnumerateArray().Select(c =>
		{
			string id = c.GetProperty("id").GetString()!;
			JsonElement expected = c.GetProperty("expected");
			string decision = expected.GetProperty("decision").GetString()!;
			Guid? key = expected.GetProperty("candidateKey").ValueKind == JsonValueKind.Null ? null
				: SyntheticId(id, expected.GetProperty("candidateKey").GetString()!);
			string baseline = disagreements.GetValueOrDefault(id, decision);
			return new CatalogCase(id, "hard-negative", c.GetProperty("description").GetString()!, null,
				Candidates(id, c.GetProperty("candidates"), "name"), decision == "same" ? key : null, null,
				decision, baseline, baseline == "same" ? key : null);
		})];
	}

	private static CatalogMatchCandidate[] Candidates(string caseId, JsonElement candidates, string nameProperty) =>
		[.. candidates.EnumerateArray().Select(c => new CatalogMatchCandidate(SyntheticId(caseId, c.GetProperty("key").GetString()!),
			c.GetProperty(nameProperty).GetString()!,
			c.TryGetProperty("approvedExamples", out JsonElement examples)
				? [.. examples.EnumerateArray().Select(e => new NormalizationApprovedExample(e.GetProperty("description").GetString()!,
					e.GetProperty("sameScope").GetBoolean()))]
				: []))];

	/// <summary>A stable synthetic candidate ID: the first 16 bytes of SHA-256 over the case and key.</summary>
	public static Guid SyntheticId(string caseId, string key) =>
		new(SHA256.HashData(Encoding.UTF8.GetBytes("receipts-1091-synthetic\0" + caseId + "\0" + key)).AsSpan(0, 16));

	internal static IReadOnlyList<ReplayExchange> ReadJournal(string journalPath)
	{
		using FileStream journal = new(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
		LiveJournalState state = LiveJournalState.Read(journal);
		return state.OutstandingIntent ? throw new InvalidOperationException("Outstanding intent.") : state.Exchanges;
	}

	internal static IReadOnlyList<long> ReadLatencies(string path) => File.Exists(path)
		? [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("milliseconds").GetInt64())]
		: [];

	private static async Task WriteAsync(string output, object value)
	{
		using FileStream stream = PrivateEvidence.CreateNew(output);
		await JsonSerializer.SerializeAsync(stream, value, ProductionParity.JsonOptions);
		stream.Flush(flushToDisk: true);
	}
}
