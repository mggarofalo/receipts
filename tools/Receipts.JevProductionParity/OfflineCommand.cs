using System.Text;
using System.Text.Json;

namespace Receipts.JevProductionParity;

public static class OfflineCommand
{
	public static async Task<int> RunAsync(string[] args)
	{
		try
		{
			object result;
			string output, repository;
			if (args is ["offline-plan", var v4Packet, var v4Repository, var v4Output] && PacketKindOf(v4Packet) == TwoStagePolicy.PacketKind)
			{
				// Round 4 builds its own requests; there is no production source to pin.
				(output, repository) = (v4Output, v4Repository);
				result = TwoStageCommand.Plan(TwoStagePacket.Load(v4Packet));
			}
			else if (args is ["offline-replay-v4", var replayPacket, var v4Exchanges, var v4ReplayRepository, var v4ReplayOutput])
			{
				(output, repository) = (v4ReplayOutput, v4ReplayRepository);
				ReplayExchange[] exchanges = Load<ReplayExchange[]>(v4Exchanges);
				if (exchanges.Any(e => e is null || e.Body is null || e.StatusCode is < 100 or > 599 ||
					e.RequestSha256 is null || e.RequestSha256.Length != 64 || !e.RequestSha256.All(Uri.IsHexDigit)))
				{
					throw new ArgumentException("Replay exchanges must bind exact requests.");
				}

				result = TwoStageEvaluation.Replay(TwoStagePacket.Load(replayPacket), exchanges);
			}
			else if (args is ["offline-production-v4", var productionPacket, var productionExchanges, var productionRepository, var productionOutput])
			{
				(output, repository) = (productionOutput, productionRepository);
				ReplayExchange[] exchanges = Load<ReplayExchange[]>(productionExchanges);
				if (exchanges.Any(e => e is null || e.Body is null || e.RequestSha256 is null || e.RequestSha256.Length != 64 ||
					!e.RequestSha256.All(Uri.IsHexDigit)))
				{
					throw new ArgumentException("Replay exchanges must bind exact requests.");
				}

				// Default production thresholds; configuration is not read here.
				result = ProductionParity.Replay(TwoStagePacket.Load(productionPacket), exchanges, new());
			}
			else
			{
				Console.Error.WriteLine("Usage: offline-plan <packet-directory> <repository-root> <new-private-output.json> | offline-replay-v4 <packet-directory> <exchanges.json> <repository-root> <new-private-output.json> | offline-production-v4 <packet-directory> <exchanges.json> <repository-root> <new-private-output.json>");
				return 2;
			}
			// Never write request bodies or identifiers to the console or the checkout, or overwrite evidence.
			PrivateEvidence.RequireOutside(repository, output);
			using FileStream stream = PrivateEvidence.CreateNew(output);
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
				Hashing.Hash(File.ReadAllBytes(path)) != file.Value.GetString())
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
