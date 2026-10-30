using System.Text.Json;
using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

internal static class Policies
{
	/// <summary>The original 24-case pilot limits, expressed as a policy.</summary>
	public static EvaluationPolicy Pilot { get; } = EvaluationPolicy.Create(24, 48, 4_750_000_000, 5_000_000_000, 0.7, 0.7);
	public static EvaluationPolicy Four { get; } = EvaluationPolicy.Create(4, 8, 0, 5_000_000_000, 0.9, 0.8);
}

/// <summary>A synthetic packet and checkout in a private temp directory. No real data or credentials.</summary>
internal sealed class SyntheticPacket : IDisposable
{
	public const string Commit = "0123456789abcdef0123456789abcdef01234567";

	public string Root { get; } = Path.Combine(Path.GetTempPath(), "jev-packet-" + Guid.NewGuid().ToString("N"));
	public string Packet => Path.Combine(Root, "packet");
	public string Repository => Path.Combine(Root, "repository");
	public string Journal => Path.Combine(Packet, "live.journal.jsonl");
	public string FreezeSha256 => OfflinePairedEvaluation.Hash(File.ReadAllBytes(Path.Combine(Packet, "freeze.private.json")));

	public static Dictionary<string, object> PolicyJson(int cases = 4) => new()
	{
		["packetKind"] = "owner-labels-v1",
		["model"] = "jev-1.14.0",
		["cases"] = cases,
		["maximumAttempts"] = 2 * cases,
		["reservationNanodollarsPerAttempt"] = 2_752_512,
		["conservativePriorCarryNanodollars"] = 0L,
		["cumulativeCeilingNanodollars"] = 5_000_000_000L,
		["baselineGate"] = 0.7,
		["autoWriteGlobalGate"] = 0.9,
		["autoWriteSelectedGate"] = 0.8,
		["liveAuthorized"] = false
	};

	public SyntheticPacket Prepare(Action<Dictionary<string, object>>? editPolicy = null, int caseCount = 4,
		string commit = Commit, bool freezePolicy = true)
	{
		Directory.CreateDirectory(Path.Combine(Packet, "production-source"));
		Dictionary<string, string> hashes = [];
		foreach (string source in OfflineCommand.ProductionSources)
		{
			string target = Path.Combine(Repository, source);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.WriteAllText(target, "synthetic pinned source " + source);
			string snapshot = Path.Combine(Packet, "production-source", Path.GetFileName(source));
			File.Copy(target, snapshot);
			hashes["production-source/" + Path.GetFileName(source)] = OfflinePairedEvaluation.Hash(File.ReadAllBytes(snapshot));
		}

		Dictionary<string, object> policy = PolicyJson();
		editPolicy?.Invoke(policy);
		string policyPath = Path.Combine(Packet, "policy.private.json");
		File.WriteAllText(policyPath, JsonSerializer.Serialize(policy));
		if (freezePolicy)
		{
			hashes["policy.private.json"] = OfflinePairedEvaluation.Hash(File.ReadAllBytes(policyPath));
		}

		OfflineCase[] cases = [.. Enumerable.Range(0, caseCount).Select(i => new OfflineCase("synthetic-" + i,
			new("synthetic apple " + i, "synthetic-taxonomy", [new("fruit", "Fruit", "Fresh fruit", null)])))];
		string casesPath = Path.Combine(Packet, "cases.private.json");
		File.WriteAllText(casesPath, JsonSerializer.Serialize(cases, ProductionParity.JsonOptions));
		hashes["cases.private.json"] = OfflinePairedEvaluation.Hash(File.ReadAllBytes(casesPath));
		File.WriteAllText(Path.Combine(Packet, "freeze.private.json"), JsonSerializer.Serialize(new
		{
			sourceCommit = commit,
			filesSha256 = hashes
		}));
		return this;
	}

	public string WriteApproval(long priorCarry = 0, long budget = 5_000_000_000, string? freeze = null)
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
