using System.Text.Json;
using Infrastructure.Jev;
using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

/// <summary>A synthetic round-4 packet and checkout in a private temp directory. No real data or credentials.</summary>
internal sealed class SyntheticPacket : IDisposable
{
	public const string Commit = "0123456789abcdef0123456789abcdef01234567";
	public const long PriorCarry = 250_000_000;
	public const long Ceiling = 5_000_000_000;

	/// <summary>Two-level item tree in a fixed order; Bakery has no leaves and is offered in stage 2 as itself.</summary>
	public static TwoStageTaxonomy Taxonomy { get; } = new("synthetic-taxonomy", [
		new("fruit", "Fruit", "Fresh or dried fruit", [new("apples", "Apples", "Apples"), new("citrus", "Citrus", "Oranges and lemons")]),
		new("dairy", "Dairy", "Dairy", [new("milk", "Milk", "Milk")]),
		new("bakery", "Bakery", "Bread and pastries", []),
		new("household", "Household", "Cleaning and paper goods", [new("soap", "Soap", "Soap")])]);

	public static TwoStageCase Case(int i) => new("synthetic-" + i, "synthetic apple " + i, "Synthetic Store");

	public static TwoStageCase[] Cases(int count) => [.. Enumerable.Range(0, count).Select(Case)];

	/// <summary>The in-memory packet the files written by <see cref="Prepare"/> describe.</summary>
	public static TwoStagePacket Create(int cases = 4) =>
		TwoStagePacket.Create(new(cases, 2 * cases, PriorCarry, Ceiling, 120, 350_000_000), Cases(cases), Taxonomy);

	public string Root { get; } = Path.Combine(Path.GetTempPath(), "jev-packet-" + Guid.NewGuid().ToString("N"));
	public string Packet => Path.Combine(Root, "packet");
	public string Repository => Path.Combine(Root, "repository");
	public string Journal => Path.Combine(Packet, "live.journal.jsonl");
	public string FreezeSha256 => Hashing.Hash(File.ReadAllBytes(Path.Combine(Packet, "freeze.private.json")));

	public static Dictionary<string, object> PolicyJson(int cases = 4) => new()
	{
		["packetKind"] = TwoStagePolicy.PacketKind,
		["model"] = "jev-1.14.0",
		["questionPolicy"] = "two-stage-v1",
		["cases"] = cases,
		["maximumAttempts"] = 2 * cases,
		["approvedMaximumAttempts"] = 120,
		["approvedReservationCapNanodollars"] = 350_000_000L,
		["reservationBasis"] = "utf8-request-bytes",
		["inputNanodollarsPerToken"] = 42,
		["headroomInputTokens"] = 65_536,
		["conservativePriorCarryNanodollars"] = PriorCarry,
		["cumulativeCeilingNanodollars"] = Ceiling,
		["liveAuthorized"] = false
	};

	/// <summary>Writes policy, cases and taxonomy and freezes their hashes. The policy always declares four cases.</summary>
	public SyntheticPacket Prepare(Action<Dictionary<string, object>>? editPolicy = null, int caseCount = 4,
		string commit = Commit, bool freezePolicy = true)
	{
		Directory.CreateDirectory(Packet);
		Directory.CreateDirectory(Repository);
		Dictionary<string, string> hashes = [];
		void Write(string name, object value, bool freeze = true)
		{
			string path = Path.Combine(Packet, name);
			File.WriteAllText(path, JsonSerializer.Serialize(value, ProductionParity.JsonOptions));
			if (freeze)
			{
				hashes[name] = Hashing.Hash(File.ReadAllBytes(path));
			}
		}

		Dictionary<string, object> policy = PolicyJson();
		editPolicy?.Invoke(policy);
		Write("policy.private.json", policy, freezePolicy);
		Write("cases.private.json", Cases(caseCount));
		Write("taxonomy.private.json", Taxonomy);
		File.WriteAllText(Path.Combine(Packet, "freeze.private.json"), JsonSerializer.Serialize(new
		{
			sourceCommit = commit,
			filesSha256 = hashes
		}));
		return this;
	}

	public string WriteApproval(long priorCarry = PriorCarry, long budget = Ceiling, string? freeze = null)
	{
		string path = Path.Combine(Root, "approval.private.json");
		File.WriteAllText(path, JsonSerializer.Serialize(new LiveApproval(freeze ?? FreezeSha256,
			new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero), true, true, priorCarry, budget), ProductionParity.JsonOptions));
		return path;
	}

	public void Dispose()
	{
		if (Directory.Exists(Root))
		{
			Directory.Delete(Root, recursive: true);
		}
	}
}

/// <summary>Synthetic Jev-shaped response bodies. Probabilities are keyed by option key exactly as the request offered them.</summary>
internal static class SyntheticAnswers
{
	/// <summary>One Choice answer where <paramref name="winnerId"/> gets <paramref name="top"/> and the rest share the remainder.</summary>
	public static string Choice(string question, IReadOnlyList<JevTwoStageQuestions.Option> options, string winnerId, double top,
		double? fit = null, long inputTokens = 100)
	{
		JevTwoStageQuestions.Option winner = options.Single(o => o.CategoryId == winnerId);
		double rest = options.Count == 1 ? 0 : (1 - top) / (options.Count - 1);
		Dictionary<string, object> answers = new(StringComparer.Ordinal)
		{
			[question] = new
			{
				type = "choice",
				choice = winner.Key,
				confidence = top,
				probabilities = options.ToDictionary(o => o.Key, o => o == winner ? top : rest, StringComparer.Ordinal)
			}
		};
		if (fit is double value)
		{
			answers["fits"] = new { type = "noul", noul = value };
		}

		return JsonSerializer.Serialize(new { model = "jev-1.14.0", answers, usage = new { input_tokens = inputTokens, output_tokens = 5 } });
	}

	/// <summary>Answers whichever stage the payload asks: the first offered option wins and fit is high.</summary>
	public static ReplayExchange For(byte[] payload, int statusCode = 200)
	{
		using JsonDocument request = JsonDocument.Parse(payload);
		JsonElement questions = request.RootElement.GetProperty("questions");
		bool stage1 = questions.TryGetProperty("category", out _);
		string question = stage1 ? "category" : "subcategory";
		List<JevTwoStageQuestions.Option> options = [.. questions.GetProperty(question).GetProperty("criteria").EnumerateObject()
			.Select(p => new JevTwoStageQuestions.Option(p.Name, p.Name, null))];
		return new(Choice(question, options, options[0].CategoryId, 0.7, stage1 ? 0.9 : null), statusCode);
	}
}
