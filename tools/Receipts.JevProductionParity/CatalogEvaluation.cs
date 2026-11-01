using System.Text;
using System.Text.Json;
using Application.NormalizationAi;
using Infrastructure.Jev;

namespace Receipts.JevProductionParity;

/// <summary>An instruction planted in a probe: it tries to force a candidate (CandidateId) or a no-fit (CandidateId null).</summary>
public sealed record CatalogInjection(string Kind, Guid? CandidateId);

/// <summary>
/// One evaluated source. Only Description, Store and Candidates are sent; the labels stay local. ExpectedCandidateId is
/// the correct match or null when no match is correct. ExpectedDecision and the Baseline fields exist only for the
/// catalog.v2 cases, whose 2026-10-20 harness answers are the indirect ChatGPT baseline. LeaveOneOut is set for real
/// sources, whose examples may include their own approved mapping; synthetic examples are constructed evidence and stay.
/// </summary>
public sealed record CatalogCase(string Id, string Stratum, string Description, string? Store,
	IReadOnlyList<CatalogMatchCandidate> Candidates, Guid? ExpectedCandidateId, CatalogInjection? Injection = null,
	string? ExpectedDecision = null, string? BaselineDecision = null, Guid? BaselineCandidateId = null, bool LeaveOneOut = false)
{
	public static readonly string[] Strata = ["proxy", "not-in-shortlist", "hard-negative", "injection"];

	/// <summary>The production request; a real source is removed from its own evidence (leave-one-out).</summary>
	public CatalogMatchRequest Request => LeaveOneOut
		? new CatalogMatchRequest(Description, Store, Candidates).WithoutExamplesOf(Description)
		: new CatalogMatchRequest(Description, Store, Candidates);
}

/// <summary>
/// Frozen limits of one catalog packet, priced and reserved exactly as round 4 (<see cref="TwoStagePolicy"/>): one input
/// token per UTF-8 request byte at 42 nanodollars, plus one 65,536-token attempt of headroom.
/// </summary>
public sealed record CatalogPolicy(int Cases, int MaximumAttempts, long PriorCarryNanodollars, long CeilingNanodollars,
	int ApprovedMaximumAttempts, long ApprovedReservationCapNanodollars)
{
	public const string PacketKind = "catalog-matching-v1";

	public static CatalogPolicy Load(string path)
	{
		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
		JsonElement p = document.RootElement;
		try
		{
			CatalogPolicy policy = new(p.GetProperty("cases").GetInt32(), p.GetProperty("maximumAttempts").GetInt32(),
				p.GetProperty("conservativePriorCarryNanodollars").GetInt64(), p.GetProperty("cumulativeCeilingNanodollars").GetInt64(),
				p.GetProperty("approvedMaximumAttempts").GetInt32(), p.GetProperty("approvedReservationCapNanodollars").GetInt64());
			if (p.GetProperty("packetKind").GetString() != PacketKind || p.GetProperty("model").GetString() != CatalogMatchingPolicy.Model ||
				p.GetProperty("questionPolicy").GetString() != CatalogMatchingPolicy.Version ||
				p.GetProperty("reservationBasis").GetString() != TwoStagePolicy.ReservationBasis ||
				p.GetProperty("inputNanodollarsPerToken").GetInt64() != TwoStagePolicy.NanodollarsPerInputToken ||
				p.GetProperty("headroomInputTokens").GetInt64() != TwoStagePolicy.HeadroomInputTokens ||
				p.GetProperty("liveAuthorized").GetBoolean() ||
				policy.Cases < 1 || policy.MaximumAttempts < 1 || policy.MaximumAttempts > policy.ApprovedMaximumAttempts ||
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

/// <summary>A verified catalog packet: frozen policy and cases, every request fitting, with exact reservation bounds.</summary>
public sealed class CatalogPacket
{
	private CatalogPacket(CatalogPolicy policy, CatalogCase[] cases)
	{
		Policy = policy;
		Cases = cases;
		Stage1Payloads = [.. cases.Select(c => c.Candidates.Count == 0 ? null : JevCatalogMatchingQuestions.Stage1(c.Request))];
		MaximumStage2Bytes = [.. cases.Select(c => c.Candidates.Count == 0 ? 0
			: c.Candidates.Max(candidate => JevCatalogMatchingQuestions.Stage2(c.Request, candidate.Id).Length))];
		int asked = cases.Count(c => c.Candidates.Count > 0);
		if (policy.Cases != cases.Length || policy.MaximumAttempts != 2 * asked)
		{
			throw new ArgumentException("Case or attempt count differs from the policy.");
		}

		MaximumReservationTokens = Stage1Payloads.Sum(p => (long)(p?.Length ?? 0)) + MaximumStage2Bytes.Sum(b => (long)b) +
			TwoStagePolicy.HeadroomInputTokens;
		if (MaximumReservationNanodollars > policy.ApprovedReservationCapNanodollars ||
			checked(policy.PriorCarryNanodollars + MaximumReservationNanodollars) > policy.CeilingNanodollars)
		{
			throw new ArgumentException("Packet reservation exceeds the approved cap or ceiling.");
		}
	}

	public CatalogPolicy Policy { get; }
	public IReadOnlyList<CatalogCase> Cases { get; }

	/// <summary>Stage-1 bytes per case; null for an empty shortlist, which is decided without a request.</summary>
	public IReadOnlyList<byte[]?> Stage1Payloads { get; }

	/// <summary>The largest stage 2 any top choice could produce, per case.</summary>
	public IReadOnlyList<int> MaximumStage2Bytes { get; }
	public long MaximumReservationTokens { get; }
	public long MaximumReservationNanodollars => MaximumReservationTokens * TwoStagePolicy.NanodollarsPerInputToken;
	public long CombinedMaximumReservationNanodollars => Policy.PriorCarryNanodollars + MaximumReservationNanodollars;

	public static CatalogPacket Load(string packet)
	{
		OfflineCommand.VerifyFrozenFiles(packet, "policy.private.json", "cases.private.json");
		CatalogPolicy policy = CatalogPolicy.Load(Path.Combine(packet, "policy.private.json"));
		CatalogCase[] cases = JsonSerializer.Deserialize<CatalogCase[]>(File.ReadAllText(Path.Combine(packet, "cases.private.json")),
			ProductionParity.JsonOptions) ?? throw new ArgumentException("Missing cases.");
		return Create(policy, cases);
	}

	public static CatalogPacket Create(CatalogPolicy policy, IReadOnlyList<CatalogCase> cases)
	{
		ArgumentNullException.ThrowIfNull(policy);
		if (cases is null || cases.Count == 0 || cases.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) ||
				!CatalogCase.Strata.Contains(c.Stratum) || c.Candidates is null ||
				(c.Candidates.Count > 0 && !JevCatalogMatchingQuestions.Fits(c.Request)) ||
				(c.ExpectedCandidateId is Guid expected && c.Candidates.All(candidate => candidate.Id != expected))) ||
			cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != cases.Count)
		{
			throw new ArgumentException("Invalid cases.");
		}

		return new(policy, [.. cases]);
	}
}

public sealed record CatalogAttempt(string CaseId, string Stage, string RequestSha256, string RequestJson, string Status,
	long ReservedInputTokens, long ReservationNanodollars);

/// <summary>
/// One case's validated answers and the production decision at the default thresholds. Top is the stage-1 top choice
/// that stage 2 asked about. Status is completed, failed, unknown (not yet answered) or unattempted.
/// </summary>
public sealed record CatalogOutcome(string Id, string Status, CatalogStage1Judgment? Stage1, Guid? Top, CatalogStage2Judgment? Stage2,
	CatalogMatchDecision? Decision);

public sealed record CatalogReplay(IReadOnlyList<CatalogOutcome> Outcomes, IReadOnlyList<CatalogAttempt> Attempts,
	int TotalCases, int CompletedCases, int UnattemptedCases, bool Stopped, long ReservationNanodollars,
	long PriorCarryNanodollars, long CombinedReservationNanodollars);

/// <summary>
/// Deterministic replay of saved exchanges through the production builder, parser, validation and decision. Unlike
/// production, every case asks stage 2 about its top choice, so Q3 is scored everywhere.
/// </summary>
public static class CatalogEvaluation
{
	public static CatalogReplay Replay(CatalogPacket packet, IReadOnlyList<ReplayExchange> exchanges)
	{
		ArgumentNullException.ThrowIfNull(packet);
		ArgumentNullException.ThrowIfNull(exchanges);
		List<CatalogAttempt> attempts = [];
		List<CatalogOutcome> outcomes = [];
		bool stopped = false;
		for (int c = 0; c < packet.Cases.Count; c++)
		{
			CatalogCase item = packet.Cases[c];
			if (item.Candidates.Count == 0)
			{
				outcomes.Add(new(item.Id, "completed", null, null, null, CatalogMatchingPolicy.NoCandidates));
				continue;
			}

			if (stopped) { outcomes.Add(new(item.Id, "unattempted", null, null, null, null)); continue; }

			CatalogMatchRequest request = item.Request;
			(string status, ReplayExchange? exchange) = Take(item.Id, "stage-1", packet.Stage1Payloads[c]!);
			CatalogStage1Judgment? stage1 = exchange is null ? null : JevCatalogMatchingQuestions.ParseStage1(Bytes(exchange), request);
			if (!CatalogMatchingPolicy.IsValidJudgment(request, stage1))
			{
				outcomes.Add(new(item.Id, status, null, null, null, null));
				stopped = true;
				continue;
			}

			Guid top = CatalogMatchingPolicy.TopCandidate(request, stage1!);
			(status, exchange) = Take(item.Id, "stage-2", JevCatalogMatchingQuestions.Stage2(request, top));
			CatalogStage2Judgment? stage2 = exchange is null ? null : JevCatalogMatchingQuestions.ParseStage2(Bytes(exchange));
			bool valid = CatalogMatchingPolicy.IsValidJudgment(stage2);
			outcomes.Add(new(item.Id, valid ? "completed" : status, stage1, top, valid ? stage2 : null,
				valid ? CatalogMatchingPolicy.Decide(request, stage1!, stage2, new()) : null));
			stopped = !valid;
		}

		if (exchanges.Count > attempts.Count)
		{
			throw new ArgumentException("Unused replay exchanges.");
		}

		long reservation = attempts.Sum(a => a.ReservationNanodollars);
		return new(outcomes, attempts, packet.Cases.Count, outcomes.Count(o => o.Status == "completed"),
			outcomes.Count(o => o.Status == "unattempted"), stopped, reservation, packet.Policy.PriorCarryNanodollars,
			packet.Policy.PriorCarryNanodollars + reservation);

		static byte[] Bytes(ReplayExchange exchange) => Encoding.UTF8.GetBytes(exchange.Body);

		// Reserve before interpreting an exchange: a missing, mismatched, failed or invalid exchange keeps its attempt.
		(string Status, ReplayExchange? Exchange) Take(string caseId, string stage, byte[] payload)
		{
			if (attempts.Count >= packet.Policy.MaximumAttempts)
			{
				throw new InvalidOperationException("Offline attempt ceiling reached.");
			}

			string hash = Hashing.Hash(payload);
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

/// <summary>Drives the catalog replay through the shared journalled loop.</summary>
public sealed class CatalogProtocol(CatalogPacket packet) : IJournalProtocol<CatalogReplay>
{
	public int MaximumAttempts => packet.Policy.MaximumAttempts;
	public long PriorCarryNanodollars => packet.Policy.PriorCarryNanodollars;
	public long ReservationLimitNanodollars => packet.CombinedMaximumReservationNanodollars;
	public long HeadroomNanodollars => TwoStagePolicy.HeadroomNanodollars;

	public Task<ProtocolReplay<CatalogReplay>> ReplayAsync(IReadOnlyList<ReplayExchange> exchanges, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		CatalogReplay replay = CatalogEvaluation.Replay(packet, exchanges);
		return Task.FromResult(new ProtocolReplay<CatalogReplay>(replay,
			[.. replay.Attempts.Select(a => new JournalAttempt(a.RequestSha256, a.RequestJson, a.Status, a.ReservedInputTokens, a.ReservationNanodollars))],
			replay.Stopped));
	}
}
