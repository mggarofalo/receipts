using System.Text.Json;
using Application.NormalizationAi;
using Infrastructure.Jev;
using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

/// <summary>Synthetic RECEIPTS-1091 catalog cases, packets and Jev-shaped answers. No real data or credentials.</summary>
internal static class SyntheticCatalog
{
	public const long PriorCarry = 100_000_000;
	public const long Ceiling = 1_500_000_000;
	public const int ApprovedAttempts = 100;
	public const long Cap = 1_000_000_000;

	public static Guid Id(int i) => new($"00000000-0000-0000-0000-{i:000000000000}");

	public static CatalogMatchCandidate Candidate(int id, string name, params NormalizationApprovedExample[] examples) =>
		new(Id(id), name, examples);

	public static CatalogCase Case(string id, string stratum, string description, Guid? expected, params CatalogMatchCandidate[] candidates) =>
		new(id, stratum, description, "Synthetic Store", candidates, expected);

	/// <summary>A policy matching the cases: one stage 1 and one stage 2 per case with candidates.</summary>
	public static CatalogPolicy Policy(IReadOnlyList<CatalogCase> cases, long carry = PriorCarry, long ceiling = Ceiling,
		int approved = ApprovedAttempts, long cap = Cap) =>
		new(cases.Count, 2 * cases.Count(c => c.Candidates.Count > 0), carry, ceiling, approved, cap);

	public static CatalogPacket Packet(params CatalogCase[] cases) => CatalogPacket.Create(Policy(cases), cases);

	public static Dictionary<string, object> PolicyJson(CatalogPolicy policy) => new()
	{
		["packetKind"] = "catalog-matching-v1",
		["model"] = "jev-1.14.0",
		["questionPolicy"] = "catalog-matching-questions-v1",
		["reservationBasis"] = "utf8-request-bytes",
		["inputNanodollarsPerToken"] = 42,
		["headroomInputTokens"] = 65_536,
		["liveAuthorized"] = false,
		["cases"] = policy.Cases,
		["maximumAttempts"] = policy.MaximumAttempts,
		["approvedMaximumAttempts"] = policy.ApprovedMaximumAttempts,
		["conservativePriorCarryNanodollars"] = policy.PriorCarryNanodollars,
		["cumulativeCeilingNanodollars"] = policy.CeilingNanodollars,
		["approvedReservationCapNanodollars"] = policy.ApprovedReservationCapNanodollars
	};

	/// <summary>Writes and freezes a catalog packet the way <see cref="CatalogCommand.Build"/> does.</summary>
	public static void WritePacket(string directory, IReadOnlyList<CatalogCase> cases, Action<Dictionary<string, object>>? editPolicy = null)
	{
		Directory.CreateDirectory(directory);
		Dictionary<string, object> policy = PolicyJson(Policy(cases));
		editPolicy?.Invoke(policy);
		Dictionary<string, string> hashes = [];
		void Write(string name, object value)
		{
			string path = Path.Combine(directory, name);
			File.WriteAllText(path, JsonSerializer.Serialize(value, ProductionParity.JsonOptions));
			hashes[name] = Hashing.Hash(File.ReadAllBytes(path));
		}

		Write("policy.private.json", policy);
		Write("cases.private.json", cases);
		File.WriteAllText(Path.Combine(directory, "freeze.private.json"),
			JsonSerializer.Serialize(new { sourceCommit = SyntheticPacket.Commit, filesSha256 = hashes }));
	}

	/// <summary>A stage-1 body: <paramref name="top"/> gets <paramref name="pTop"/> and the rest share the remainder.</summary>
	public static string Stage1(CatalogMatchRequest request, Guid top, double pTop, double fit, Guid? chosen = null)
	{
		double rest = request.Candidates.Count == 1 ? 0 : (1 - pTop) / (request.Candidates.Count - 1);
		return Stage1(request, request.Candidates.ToDictionary(c => c.Id, c => c.Id == top ? pTop : rest), fit, chosen ?? top);
	}

	public static string Stage1(CatalogMatchRequest request, IReadOnlyDictionary<Guid, double> probabilities, double fit, Guid chosen,
		string model = "jev-1.14.0")
	{
		string Name(Guid id) => request.Candidates.Single(c => c.Id == id).DisplayName;
		return JsonSerializer.Serialize(new
		{
			model,
			answers = new
			{
				candidate = new
				{
					type = "choice",
					choice = Name(chosen),
					confidence = probabilities[chosen],
					probabilities = probabilities.ToDictionary(p => Name(p.Key), p => p.Value, StringComparer.Ordinal)
				},
				fits = new { type = "noul", noul = fit }
			},
			usage = new { input_tokens = 10, output_tokens = 5 }
		});
	}

	public static string Stage2(double same) => JsonSerializer.Serialize(new
	{
		model = "jev-1.14.0",
		answers = new { same = new { type = "noul", noul = same } },
		usage = new { input_tokens = 10, output_tokens = 5 }
	});

	public static ReplayExchange Bound(byte[] payload, string body, int statusCode = 200) => new(body, statusCode, Hashing.Hash(payload));

	/// <summary>Answers whichever stage the payload asks: the ordinal-first name wins at 0.8, fit and same are 0.9.</summary>
	public static ReplayExchange For(byte[] payload)
	{
		using JsonDocument request = JsonDocument.Parse(payload);
		JsonElement questions = request.RootElement.GetProperty("questions");
		if (!questions.TryGetProperty("candidate", out JsonElement candidate))
		{
			return new(Stage2(0.9));
		}

		string[] names = [.. candidate.GetProperty("criteria").EnumerateObject().Select(p => p.Name)];
		double rest = names.Length == 1 ? 0 : 0.2 / (names.Length - 1);
		return new(JsonSerializer.Serialize(new
		{
			model = "jev-1.14.0",
			answers = new
			{
				candidate = new
				{
					type = "choice",
					choice = names[0],
					confidence = names.Length == 1 ? 1.0 : 0.8,
					probabilities = names.ToDictionary(n => n, n => n == names[0] ? (names.Length == 1 ? 1.0 : 0.8) : rest, StringComparer.Ordinal)
				},
				fits = new { type = "noul", noul = 0.9 }
			},
			usage = new { input_tokens = 10, output_tokens = 5 }
		}));
	}

	/// <summary>Valid exchanges for every asked case: the given top per case, fit 0.9 and same 0.9.</summary>
	public static List<ReplayExchange> Complete(CatalogPacket packet, Func<CatalogCase, Guid> top)
	{
		List<ReplayExchange> exchanges = [];
		for (int i = 0; i < packet.Cases.Count; i++)
		{
			CatalogCase item = packet.Cases[i];
			if (item.Candidates.Count == 0)
			{
				continue;
			}

			Guid chosen = top(item);
			exchanges.Add(Bound(packet.Stage1Payloads[i]!, Stage1(item.Request, chosen, 0.8, 0.9)));
			exchanges.Add(Bound(JevCatalogMatchingQuestions.Stage2(item.Request, chosen), Stage2(0.9)));
		}

		return exchanges;
	}
}
