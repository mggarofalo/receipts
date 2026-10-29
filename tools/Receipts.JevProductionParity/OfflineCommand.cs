using System.Text;
using System.Text.Json;
using Infrastructure.Jev;

namespace Receipts.JevProductionParity;

public static class OfflineCommand
{
	public static async Task<int> RunAsync(string[] args)
	{
		try
		{
			object result;
			string output;
			if (args is ["offline-plan", var packet, var repository, var planOutput])
			{
				ValidateFreeze(packet, repository);
				OfflineCase[] cases = Load<OfflineCase[]>(Path.Combine(packet, "cases.private.json"));
				OfflinePairedEvaluation.ValidateCases(cases, requireFullPacket: true);
				result = new
				{
					mode = "offline-only-no-dispatch",
					totalCases = cases.Length,
					maximumAttempts = OfflinePairedEvaluation.MaximumAttempts,
					baselineGate = 0.7,
					selectedPathGate = 0.7,
					maximumReservationNanodollars = OfflinePairedEvaluation.MaximumAttempts * OfflinePairedEvaluation.PerAttemptNanodollars,
					priorCarryNanodollars = OfflinePairedEvaluation.PriorCarryNanodollars,
					combinedMaximumReservationNanodollars = OfflinePairedEvaluation.PriorCarryNanodollars + OfflinePairedEvaluation.MaximumAttempts * OfflinePairedEvaluation.PerAttemptNanodollars,
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
			else if (args is ["offline-replay", var casesPath, var exchangesPath, var replayOutput])
			{
				ReplayExchange[] exchanges = Load<ReplayExchange[]>(exchangesPath);
				if (exchanges.Any(e => e is null || e.Body is null || e.StatusCode is < 100 or > 599 ||
					e.RequestSha256 is null || e.RequestSha256.Length != 64 || !e.RequestSha256.All(Uri.IsHexDigit)))
				{
					throw new ArgumentException("Replay exchanges must bind exact requests.");
				}

				result = await OfflinePairedEvaluation.ReplayAsync(Load<OfflineCase[]>(casesPath), exchanges);
				output = replayOutput;
			}
			else
			{
				Console.Error.WriteLine("Usage: offline-plan <packet-directory> <repository-root> <new-private-output.json> | offline-replay <cases.json> <exchanges.json> <new-private-output.json>");
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

	private static void ValidateFreeze(string packet, string repository)
	{
		using JsonDocument freeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(packet, "freeze.private.json")));
		if (freeze.RootElement.GetProperty("sourceCommit").GetString() != "20b13bf44108693e063ff8c61de69b26b4e5a4d0")
		{
			throw new ArgumentException("Unrecognized production pin.");
		}

		foreach (JsonProperty file in freeze.RootElement.GetProperty("filesSha256").EnumerateObject())
		{
			string root = Path.GetFullPath(packet) + Path.DirectorySeparatorChar;
			string path = Path.GetFullPath(Path.Combine(root, file.Name));
			if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
				OfflinePairedEvaluation.Hash(File.ReadAllBytes(path)) != file.Value.GetString())
			{
				throw new ArgumentException("Frozen artifact drift.");
			}
		}
		string[] sourcePaths = ["src/Application/Classification/CategoryAssistanceDispatcher.cs", "src/Application/Classification/CategorySuggestion.cs",
			"src/Infrastructure/Jev/JevCategoryAssistanceSelectedProvider.cs", "src/Infrastructure/Jev/JevSelectedPathQuestions.cs",
			"src/Infrastructure/Jev/JevSuggestionProvider.cs", "src/Infrastructure/Jev/JevSuggestionQuestions.cs"];
		foreach (string source in sourcePaths)
		{
			if (OfflinePairedEvaluation.Hash(File.ReadAllBytes(Path.Combine(repository, source))) !=
				OfflinePairedEvaluation.Hash(File.ReadAllBytes(Path.Combine(packet, "production-source", Path.GetFileName(source)))))
			{
				throw new ArgumentException("Production source drift.");
			}
		}

		using JsonDocument policy = JsonDocument.Parse(File.ReadAllText(Path.Combine(packet, "policy.private.json")));
		if (policy.RootElement.GetProperty("cases").GetInt32() != 24 || policy.RootElement.GetProperty("maximumAttempts").GetInt32() != 48 ||
			policy.RootElement.GetProperty("reservationNanodollarsPerAttempt").GetInt64() != OfflinePairedEvaluation.PerAttemptNanodollars ||
			policy.RootElement.GetProperty("conservativePriorCarryNanodollars").GetInt64() != OfflinePairedEvaluation.PriorCarryNanodollars)
		{
			throw new ArgumentException("Budget policy drift.");
		}
	}
}
