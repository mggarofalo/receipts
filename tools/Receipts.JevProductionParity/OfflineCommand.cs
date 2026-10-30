using System.Text;
using System.Text.Json;
using Infrastructure.Jev;

namespace Receipts.JevProductionParity;

public static class OfflineCommand
{
	public const string PacketKind = "owner-labels-v1";

	public static readonly IReadOnlyList<string> ProductionSources = ["src/Application/Classification/CategoryAssistanceDispatcher.cs",
		"src/Application/Classification/CategorySuggestion.cs", "src/Infrastructure/Jev/JevCategoryAssistanceSelectedProvider.cs",
		"src/Infrastructure/Jev/JevSelectedPathQuestions.cs", "src/Infrastructure/Jev/JevSuggestionProvider.cs",
		"src/Infrastructure/Jev/JevSuggestionQuestions.cs"];

	public static async Task<int> RunAsync(string[] args)
	{
		try
		{
			object result;
			string output;
			if (args is ["offline-plan", var v4Packet, _, var v4Output] && PacketKindOf(v4Packet) == TwoStagePolicy.PacketKind)
			{
				// Round 4 builds its own requests; there is no production source to pin.
				result = TwoStageCommand.Plan(TwoStagePacket.Load(v4Packet));
				output = v4Output;
			}
			else if (args is ["offline-replay-v4", var replayPacket, var v4Exchanges, var v4ReplayOutput])
			{
				ReplayExchange[] exchanges = Load<ReplayExchange[]>(v4Exchanges);
				if (exchanges.Any(e => e is null || e.Body is null || e.StatusCode is < 100 or > 599 ||
					e.RequestSha256 is null || e.RequestSha256.Length != 64 || !e.RequestSha256.All(Uri.IsHexDigit)))
				{
					throw new ArgumentException("Replay exchanges must bind exact requests.");
				}

				result = TwoStageEvaluation.Replay(TwoStagePacket.Load(replayPacket), exchanges);
				output = v4ReplayOutput;
			}
			else if (args is ["offline-plan", var packet, var repository, var planOutput])
			{
				EvaluationPolicy policy = ValidateFreeze(packet, repository);
				OfflineCase[] cases = Load<OfflineCase[]>(Path.Combine(packet, "cases.private.json"));
				OfflinePairedEvaluation.ValidateCases(cases, policy, requireFullPacket: true);
				result = new
				{
					mode = "offline-only-no-dispatch",
					packetKind = PacketKind,
					totalCases = cases.Length,
					maximumAttempts = policy.MaximumAttempts,
					baselineGate = EvaluationPolicy.BaselineGate,
					autoWriteGlobalGate = policy.AutoWriteGlobalGate,
					autoWriteSelectedGate = policy.AutoWriteSelectedGate,
					perAttemptNanodollars = EvaluationPolicy.PerAttemptNanodollars,
					maximumReservationNanodollars = policy.MaximumReservationNanodollars,
					priorCarryNanodollars = policy.PriorCarryNanodollars,
					combinedMaximumReservationNanodollars = policy.CombinedMaximumReservationNanodollars,
					ceilingNanodollars = policy.CeilingNanodollars,
					pricingVerified = false,
					liveAuthorized = false,
					cases = cases.Select(c =>
					{
						byte[] payload = JevSuggestionQuestions.Serialize(c.Request);
						return new { c.Id, requestSha256 = OfflinePairedEvaluation.Hash(payload), requestJson = Encoding.UTF8.GetString(payload) };
					}).ToArray()
				};
				output = planOutput;
			}
			else if (args is ["offline-replay", var policyPath, var casesPath, var exchangesPath, var replayOutput])
			{
				EvaluationPolicy policy = LoadPolicy(policyPath);
				ReplayExchange[] exchanges = Load<ReplayExchange[]>(exchangesPath);
				if (exchanges.Any(e => e is null || e.Body is null || e.StatusCode is < 100 or > 599 ||
					e.RequestSha256 is null || e.RequestSha256.Length != 64 || !e.RequestSha256.All(Uri.IsHexDigit)))
				{
					throw new ArgumentException("Replay exchanges must bind exact requests.");
				}

				result = await OfflinePairedEvaluation.ReplayAsync(Load<OfflineCase[]>(casesPath), exchanges, policy);
				output = replayOutput;
			}
			else
			{
				Console.Error.WriteLine("Usage: offline-plan <packet-directory> <repository-root> <new-private-output.json> | offline-replay <policy.private.json> <cases.json> <exchanges.json> <new-private-output.json> | offline-replay-v4 <packet-directory> <exchanges.json> <new-private-output.json>");
				return 2;
			}
			// Never write request bodies or identifiers to the console, or overwrite evidence.
			using FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write);
			await JsonSerializer.SerializeAsync(stream, result, ProductionParity.JsonOptions);
			Console.WriteLine("Offline artifact created. No network transport was used.");
			return 0;
		}
		catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
		{
			Console.Error.WriteLine("Offline evaluation rejected input or could not create the artifact. No provider request was sent.");
			return 2;
		}
	}

	private static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), ProductionParity.JsonOptions)
		?? throw new JsonException("Missing input.");

	/// <summary>Parses a packet policy. Every field is required; live authorization never comes from the packet.</summary>
	public static EvaluationPolicy LoadPolicy(string path)
	{
		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
		JsonElement policy = document.RootElement;
		try
		{
			if (policy.GetProperty("packetKind").GetString() != PacketKind ||
				policy.GetProperty("reservationNanodollarsPerAttempt").GetInt64() != EvaluationPolicy.PerAttemptNanodollars ||
				policy.GetProperty("baselineGate").GetDouble() != EvaluationPolicy.BaselineGate ||
				policy.GetProperty("liveAuthorized").GetBoolean())
			{
				throw new ArgumentException("Policy drift.");
			}

			return EvaluationPolicy.Create(policy.GetProperty("cases").GetInt32(), policy.GetProperty("maximumAttempts").GetInt32(),
				policy.GetProperty("conservativePriorCarryNanodollars").GetInt64(), policy.GetProperty("cumulativeCeilingNanodollars").GetInt64(),
				policy.GetProperty("autoWriteGlobalGate").GetDouble(), policy.GetProperty("autoWriteSelectedGate").GetDouble());
		}
		catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
		{
			throw new ArgumentException("Invalid policy.");
		}
	}

	/// <summary>
	/// Verifies packet integrity and the production-source pin, and returns the frozen policy.
	/// The source snapshots, not the recorded commit, are the authoritative pin.
	/// </summary>
	public static EvaluationPolicy ValidateFreeze(string packet, string repository)
	{
		VerifyFrozenFiles(packet, "policy.private.json", "cases.private.json");
		foreach (string source in ProductionSources)
		{
			if (OfflinePairedEvaluation.Hash(File.ReadAllBytes(Path.Combine(repository, source))) !=
				OfflinePairedEvaluation.Hash(File.ReadAllBytes(Path.Combine(packet, "production-source", Path.GetFileName(source)))))
			{
				throw new ArgumentException("Production source drift.");
			}
		}

		return LoadPolicy(Path.Combine(packet, "policy.private.json"));
	}

	/// <summary>Verifies the freeze's commit format and every frozen file hash, and that the required files are frozen.</summary>
	public static void VerifyFrozenFiles(string packet, params string[] required)
	{
		using JsonDocument freeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(packet, "freeze.private.json")));
		string? commit = freeze.RootElement.GetProperty("sourceCommit").GetString();
		if (commit is not { Length: 40 } || !commit.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
		{
			throw new ArgumentException("Invalid source commit.");
		}

		string root = Path.GetFullPath(packet).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		HashSet<string> frozen = new(StringComparer.Ordinal);
		foreach (JsonProperty file in freeze.RootElement.GetProperty("filesSha256").EnumerateObject())
		{
			string path = Path.GetFullPath(Path.Combine(root, file.Name));
			if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
				OfflinePairedEvaluation.Hash(File.ReadAllBytes(path)) != file.Value.GetString())
			{
				throw new ArgumentException("Frozen artifact drift.");
			}

			frozen.Add(file.Name);
		}
		if (!required.All(frozen.Contains))
		{
			throw new ArgumentException("Policy and membership must be frozen.");
		}
	}

	/// <summary>Reads the packet kind without validating the rest of the policy.</summary>
	public static string? PacketKindOf(string packet)
	{
		using JsonDocument policy = JsonDocument.Parse(File.ReadAllText(Path.Combine(packet, "policy.private.json")));
		return policy.RootElement.ValueKind == JsonValueKind.Object &&
			policy.RootElement.TryGetProperty("packetKind", out JsonElement kind) && kind.ValueKind == JsonValueKind.String
			? kind.GetString() : null;
	}
}
