using System.Text;
using System.Text.Json;

namespace Receipts.JevProductionParity;

public sealed record TwoStageAttempt(string CaseId, string Stage, string RequestSha256, string RequestJson, string Status,
	long ReservedInputTokens, long ReservationNanodollars);

public sealed record TwoStageOutcome(string Id, string Status, TwoStageChoice? Stage1, IReadOnlyList<string>? TopRootIds,
	TwoStageChoice? Stage2);

public sealed record TwoStageReplay(IReadOnlyList<TwoStageOutcome> Outcomes, IReadOnlyList<TwoStageAttempt> Attempts,
	int TotalCases, int CompletedCases, int UnattemptedCases, bool Stopped, long ReservationNanodollars,
	long PriorCarryNanodollars, long CombinedReservationNanodollars);

/// <summary>
/// Frozen round-4 limits. Every attempt reserves one input token per UTF-8 request byte (see the tool README for
/// why that bounds usage), priced at USD 0.042/million = 42 nanodollars/token. One extra 65,536-token attempt is
/// held back as headroom: a reported usage above an attempt's reservation stops the run before the next send,
/// so at most one attempt can exceed its reservation, and the provider rejects requests over its 64k context.
/// </summary>
public sealed record TwoStagePolicy(int Cases, int MaximumAttempts, long PriorCarryNanodollars, long CeilingNanodollars,
	int ApprovedMaximumAttempts, long ApprovedReservationCapNanodollars)
{
	public const string PacketKind = "owner-labels-v4";
	public const string ReservationBasis = "utf8-request-bytes";
	public const long NanodollarsPerInputToken = 42;
	public const long HeadroomInputTokens = 65_536;
	public const int MaximumPayloadBytes = 65_536;
	public const long HeadroomNanodollars = HeadroomInputTokens * NanodollarsPerInputToken;

	/// <summary>Every field is required; live authorization never comes from the packet.</summary>
	public static TwoStagePolicy Load(string path)
	{
		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
		JsonElement p = document.RootElement;
		try
		{
			TwoStagePolicy policy = new(p.GetProperty("cases").GetInt32(), p.GetProperty("maximumAttempts").GetInt32(),
				p.GetProperty("conservativePriorCarryNanodollars").GetInt64(), p.GetProperty("cumulativeCeilingNanodollars").GetInt64(),
				p.GetProperty("approvedMaximumAttempts").GetInt32(), p.GetProperty("approvedReservationCapNanodollars").GetInt64());
			if (p.GetProperty("packetKind").GetString() != PacketKind || p.GetProperty("model").GetString() != TwoStageQuestions.Model ||
				p.GetProperty("questionPolicy").GetString() != TwoStageQuestions.QuestionPolicy ||
				p.GetProperty("reservationBasis").GetString() != ReservationBasis ||
				p.GetProperty("inputNanodollarsPerToken").GetInt64() != NanodollarsPerInputToken ||
				p.GetProperty("headroomInputTokens").GetInt64() != HeadroomInputTokens ||
				p.GetProperty("liveAuthorized").GetBoolean() ||
				policy.Cases < 1 || policy.MaximumAttempts != 2 * policy.Cases || policy.MaximumAttempts > policy.ApprovedMaximumAttempts ||
				policy.PriorCarryNanodollars < 0 || policy.CeilingNanodollars <= policy.PriorCarryNanodollars ||
				policy.ApprovedReservationCapNanodollars <= 0)
			{
				throw new ArgumentException("Policy drift.");
			}

			return policy;
		}
		catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
		{
			throw new ArgumentException("Invalid policy.");
		}
	}
}

/// <summary>A verified round-4 packet: frozen policy, cases and item taxonomy, with exact reservation bounds.</summary>
public sealed class TwoStagePacket
{
	private TwoStagePacket(TwoStagePolicy policy, TwoStageCase[] cases, TwoStageTaxonomy taxonomy)
	{
		Policy = policy;
		Cases = cases;
		Taxonomy = taxonomy;
		Stage1Payloads = [.. cases.Select(c => TwoStageQuestions.Stage1(c, taxonomy))];
		LargestRoots = [.. taxonomy.Roots.Select((r, i) => (r.Id, i, Size: TwoStageQuestions.Stage2(cases[0], taxonomy, [r.Id]).Length))
			.OrderByDescending(x => x.Size).ThenBy(x => x.i).Take(TwoStageQuestions.TopRoots).Select(x => x.Id)];
		MaximumStage2Bytes = [.. cases.Select(c => TwoStageQuestions.Stage2(c, taxonomy, LargestRoots).Length)];
		if (Stage1Payloads.Any(p => p.Length > TwoStagePolicy.MaximumPayloadBytes) ||
			MaximumStage2Bytes.Any(b => b > TwoStagePolicy.MaximumPayloadBytes))
		{
			throw new ArgumentException("Request exceeds the reservation bound.");
		}

		MaximumReservationTokens = Stage1Payloads.Sum(p => (long)p.Length) + MaximumStage2Bytes.Sum(b => (long)b) +
			TwoStagePolicy.HeadroomInputTokens;
		if (MaximumReservationNanodollars > policy.ApprovedReservationCapNanodollars ||
			checked(policy.PriorCarryNanodollars + MaximumReservationNanodollars) > policy.CeilingNanodollars)
		{
			throw new ArgumentException("Packet reservation exceeds the approved cap or ceiling.");
		}
	}

	public TwoStagePolicy Policy { get; }
	public IReadOnlyList<TwoStageCase> Cases { get; }
	public TwoStageTaxonomy Taxonomy { get; }
	public IReadOnlyList<byte[]> Stage1Payloads { get; }

	/// <summary>
	/// The three roots whose stage-2 options take the most bytes. Stage-2 size is a fixed part plus the sum of
	/// each chosen root's option bytes (plus separators), so these three give the largest possible request.
	/// </summary>
	public IReadOnlyList<string> LargestRoots { get; }
	public IReadOnlyList<int> MaximumStage2Bytes { get; }

	/// <summary>All stage-1 bytes, every case's worst-case stage-2 bytes, and the one-attempt headroom.</summary>
	public long MaximumReservationTokens { get; }
	public long MaximumReservationNanodollars => MaximumReservationTokens * TwoStagePolicy.NanodollarsPerInputToken;
	public long CombinedMaximumReservationNanodollars => Policy.PriorCarryNanodollars + MaximumReservationNanodollars;

	public static TwoStagePacket Load(string packet)
	{
		OfflineCommand.VerifyFrozenFiles(packet, "policy.private.json", "cases.private.json", "taxonomy.private.json");
		TwoStagePolicy policy = TwoStagePolicy.Load(Path.Combine(packet, "policy.private.json"));
		TwoStageCase[] cases = JsonSerializer.Deserialize<TwoStageCase[]>(File.ReadAllText(Path.Combine(packet, "cases.private.json")),
			ProductionParity.JsonOptions) ?? throw new ArgumentException("Missing cases.");
		TwoStageTaxonomy taxonomy = JsonSerializer.Deserialize<TwoStageTaxonomy>(File.ReadAllText(Path.Combine(packet, "taxonomy.private.json")),
			ProductionParity.JsonOptions) ?? throw new ArgumentException("Missing taxonomy.");
		return Create(policy, cases, taxonomy);
	}

	public static TwoStagePacket Create(TwoStagePolicy policy, IReadOnlyList<TwoStageCase> cases, TwoStageTaxonomy taxonomy)
	{
		ArgumentNullException.ThrowIfNull(policy);
		Validate(cases, taxonomy);
		if (cases.Count != policy.Cases)
		{
			throw new ArgumentException("Case count differs from the policy.");
		}

		return new(policy, [.. cases], taxonomy);
	}

	private static void Validate(IReadOnlyList<TwoStageCase> cases, TwoStageTaxonomy taxonomy)
	{
		static bool Text(string? value) => !string.IsNullOrWhiteSpace(value);
		if (cases is null || cases.Count == 0 || cases.Any(c => c is null || !Text(c.Id) || !Text(c.Description) ||
				(c.Store is not null && !Text(c.Store))) ||
			cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != cases.Count)
		{
			throw new ArgumentException("Invalid cases.");
		}

		if (taxonomy?.Roots is null || !Text(taxonomy.Revision) || taxonomy.Roots.Count < 2 ||
			taxonomy.Roots.Count > TwoStageQuestions.MaximumOptions ||
			taxonomy.Roots.Any(r => r is null || !Text(r.Id) || !Text(r.Name) || !Text(r.Definition) || r.Leaves is null ||
				r.Leaves.Any(l => l is null || !Text(l.Id) || !Text(l.Name) || !Text(l.Definition))))
		{
			throw new ArgumentException("Invalid taxonomy.");
		}

		// Option keys are names, so they must be unique; ids must be unique across the whole tree.
		List<string> ids = [.. taxonomy.Roots.Select(r => r.Id), .. taxonomy.Roots.SelectMany(r => r.Leaves.Select(l => l.Id))];
		List<string> rootKeys = [.. TwoStageQuestions.Stage1Options(taxonomy).Select(o => o.Key)];
		List<string> leafKeys = [.. TwoStageQuestions.Stage2Options(taxonomy, [.. taxonomy.Roots.Select(r => r.Id)]).Select(o => o.Key)];
		if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count ||
			rootKeys.Distinct(StringComparer.Ordinal).Count() != rootKeys.Count ||
			leafKeys.Distinct(StringComparer.Ordinal).Count() != leafKeys.Count ||
			taxonomy.Roots.Any(r => r.Name.Contains(TwoStageQuestions.Separator, StringComparison.Ordinal)))
		{
			throw new ArgumentException("Option keys or ids are not unique.");
		}

		int largest = taxonomy.Roots.Select(r => Math.Max(1, r.Leaves.Count)).OrderDescending().Take(TwoStageQuestions.TopRoots).Sum();
		if (largest > TwoStageQuestions.MaximumOptions)
		{
			throw new ArgumentException("Stage 2 could exceed the option limit.");
		}
	}
}

/// <summary>Deterministic replay of saved exchanges: what each attempt sent, reserved and answered.</summary>
public static class TwoStageEvaluation
{
	public static TwoStageReplay Replay(TwoStagePacket packet, IReadOnlyList<ReplayExchange> exchanges)
	{
		ArgumentNullException.ThrowIfNull(packet);
		ArgumentNullException.ThrowIfNull(exchanges);
		List<TwoStageAttempt> attempts = [];
		List<TwoStageOutcome> outcomes = [];
		bool stopped = false;
		for (int c = 0; c < packet.Cases.Count; c++)
		{
			TwoStageCase item = packet.Cases[c];
			if (stopped) { outcomes.Add(new(item.Id, "unattempted", null, null, null)); continue; }

			(string status, ReplayExchange? exchange) = Take(item.Id, "stage-1", packet.Stage1Payloads[c]);
			TwoStageChoice? stage1 = exchange is null ? null : TwoStageQuestions.ParseStage1(exchange.Body, packet.Taxonomy);
			if (stage1 is null) { outcomes.Add(new(item.Id, status, null, null, null)); stopped = true; continue; }

			IReadOnlyList<string> top = TwoStageQuestions.TopRootIds(packet.Taxonomy, stage1.Probabilities);
			(status, exchange) = Take(item.Id, "stage-2", TwoStageQuestions.Stage2(item, packet.Taxonomy, [.. top]));
			TwoStageChoice? stage2 = exchange is null ? null : TwoStageQuestions.ParseStage2(exchange.Body, packet.Taxonomy, [.. top]);
			outcomes.Add(new(item.Id, stage2 is null ? status : "completed", stage1, top, stage2));
			stopped = stage2 is null;
		}

		if (exchanges.Count > attempts.Count)
		{
			throw new ArgumentException("Unused replay exchanges.");
		}

		long reservation = attempts.Sum(a => a.ReservationNanodollars);
		return new(outcomes, attempts, packet.Cases.Count, outcomes.Count(o => o.Status == "completed"),
			outcomes.Count(o => o.Status == "unattempted"), stopped, reservation, packet.Policy.PriorCarryNanodollars,
			packet.Policy.PriorCarryNanodollars + reservation);

		// Reserve before interpreting an exchange: a missing, mismatched, failed or invalid exchange keeps its attempt.
		(string Status, ReplayExchange? Exchange) Take(string caseId, string stage, byte[] payload)
		{
			if (attempts.Count >= packet.Policy.MaximumAttempts)
			{
				throw new InvalidOperationException("Offline attempt ceiling reached.");
			}

			string hash = OfflinePairedEvaluation.Hash(payload);
			ReplayExchange? exchange = attempts.Count < exchanges.Count ? exchanges[attempts.Count] : null;
			bool unknown = exchange is null || (exchange.RequestSha256 is not null && exchange.RequestSha256 != hash);
			attempts.Add(new(caseId, stage, hash, Encoding.UTF8.GetString(payload), unknown ? "unknown" : "replayed",
				payload.Length, payload.Length * TwoStagePolicy.NanodollarsPerInputToken));
			return unknown ? ("unknown", null)
				: exchange!.StatusCode is >= 200 and < 300 ? ("failed", exchange)
				: ("failed", null);
		}
	}
}

/// <summary>Drives the two-stage replay through the shared journalled loop.</summary>
public sealed class TwoStageProtocol(TwoStagePacket packet) : IJournalProtocol<TwoStageReplay>
{
	public int MaximumAttempts => packet.Policy.MaximumAttempts;
	public long PriorCarryNanodollars => packet.Policy.PriorCarryNanodollars;
	public long ReservationLimitNanodollars => packet.CombinedMaximumReservationNanodollars;
	public long HeadroomNanodollars => TwoStagePolicy.HeadroomNanodollars;

	public Task<ProtocolReplay<TwoStageReplay>> ReplayAsync(IReadOnlyList<ReplayExchange> exchanges, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		TwoStageReplay replay = TwoStageEvaluation.Replay(packet, exchanges);
		return Task.FromResult(new ProtocolReplay<TwoStageReplay>(replay,
			[.. replay.Attempts.Select(a => new JournalAttempt(a.RequestSha256, a.RequestJson, a.Status, a.ReservedInputTokens, a.ReservationNanodollars))],
			replay.Stopped));
	}
}
