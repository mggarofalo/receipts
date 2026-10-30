using System.Text.Json;
using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

public sealed class OfflinePacketTests : IDisposable
{
	private readonly SyntheticPacket packet = new();

	public void Dispose() => packet.Dispose();

	[Fact]
	public async Task OwnerLabelsPacketPlansWithFrozenPolicyAndAnyPinnedCommit()
	{
		packet.Prepare();
		EvaluationPolicy actual = OfflineCommand.ValidateFreeze(packet.Packet, packet.Repository);
		Assert.Equal(4, actual.Cases);
		Assert.Equal(8, actual.MaximumAttempts);
		Assert.Equal(0.9, actual.AutoWriteGlobalGate);
		Assert.Equal(0.8, actual.AutoWriteSelectedGate);

		string output = Path.Combine(packet.Root, "plan.json");
		Assert.Equal(0, await OfflineCommand.RunAsync(["offline-plan", packet.Packet, packet.Repository, output]));
		using JsonDocument plan = JsonDocument.Parse(File.ReadAllText(output));
		JsonElement root = plan.RootElement;
		Assert.Equal("owner-labels-v1", root.GetProperty("packetKind").GetString());
		Assert.Equal(4, root.GetProperty("totalCases").GetInt32());
		Assert.Equal(8, root.GetProperty("maximumAttempts").GetInt32());
		Assert.Equal(0.7, root.GetProperty("baselineGate").GetDouble());
		Assert.Equal(0.9, root.GetProperty("autoWriteGlobalGate").GetDouble());
		Assert.Equal(0.8, root.GetProperty("autoWriteSelectedGate").GetDouble());
		Assert.Equal(8 * 2_752_512L, root.GetProperty("maximumReservationNanodollars").GetInt64());
		Assert.Equal(8 * 2_752_512L, root.GetProperty("combinedMaximumReservationNanodollars").GetInt64());
		Assert.Equal(5_000_000_000L, root.GetProperty("ceilingNanodollars").GetInt64());
		Assert.False(root.GetProperty("liveAuthorized").GetBoolean());
		Assert.Equal(4, root.GetProperty("cases").GetArrayLength());
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-plan", packet.Packet, packet.Repository, output]));
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-plan-next", packet.Packet, packet.Repository, Path.Combine(packet.Root, "next.json")]));
	}

	[Theory]
	[InlineData("20b13bf44108693e063ff8c61de69b26b4e5a4d")]
	[InlineData("0123456789ABCDEF0123456789abcdef01234567")]
	[InlineData("not-a-commit")]
	public void SourceCommitMustBeFullLowercaseHex(string commit)
	{
		packet.Prepare(commit: commit);
		Assert.Throws<ArgumentException>(() => OfflineCommand.ValidateFreeze(packet.Packet, packet.Repository));
	}

	public static TheoryData<string, object> PolicyDrift => new()
	{
		{ "packetKind", "next-holdout-preparation-only-v1" },
		{ "liveAuthorized", true },
		{ "baselineGate", 0.8 },
		{ "reservationNanodollarsPerAttempt", 2_752_513 },
		{ "maximumAttempts", 7 },
		{ "cumulativeCeilingNanodollars", 6_000_000_000L },
		{ "conservativePriorCarryNanodollars", 4_990_000_000L },
		{ "autoWriteGlobalGate", 0.69 },
		{ "autoWriteSelectedGate", "0.8" },
	};

	[Theory]
	[MemberData(nameof(PolicyDrift))]
	public void PolicyFieldsAreValidated(string field, object value)
	{
		packet.Prepare(policy => policy[field] = value);
		Assert.Throws<ArgumentException>(() => OfflineCommand.ValidateFreeze(packet.Packet, packet.Repository));
	}

	[Theory]
	[InlineData("cases")]
	[InlineData("cumulativeCeilingNanodollars")]
	[InlineData("autoWriteSelectedGate")]
	[InlineData("liveAuthorized")]
	public void PolicyFieldsAreRequired(string field)
	{
		packet.Prepare(policy => policy.Remove(field));
		Assert.Throws<ArgumentException>(() => OfflineCommand.ValidateFreeze(packet.Packet, packet.Repository));
	}

	[Fact]
	public async Task MembershipMustMatchPolicyCases()
	{
		packet.Prepare(caseCount: 3);
		string output = Path.Combine(packet.Root, "out.json");
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-plan", packet.Packet, packet.Repository, output]));
		Assert.False(File.Exists(output));
	}

	[Fact]
	public void PolicyMustBeFrozenAndUnchanged()
	{
		packet.Prepare(freezePolicy: false);
		Assert.Throws<ArgumentException>(() => OfflineCommand.ValidateFreeze(packet.Packet, packet.Repository));
		packet.Dispose();
		packet.Prepare();
		File.AppendAllText(Path.Combine(packet.Packet, "policy.private.json"), " ");
		Assert.Throws<ArgumentException>(() => OfflineCommand.ValidateFreeze(packet.Packet, packet.Repository));
	}

	[Fact]
	public void ProductionSourceDriftIsRejected()
	{
		packet.Prepare();
		File.AppendAllText(Path.Combine(packet.Repository, OfflineCommand.ProductionSources[4]), "// drift");
		Assert.Throws<ArgumentException>(() => OfflineCommand.ValidateFreeze(packet.Packet, packet.Repository));
	}

	[Fact]
	public async Task PlanAndReplayRejectOutputInsideTheRepository()
	{
		packet.Prepare();
		string policy = Path.Combine(packet.Packet, "policy.private.json");
		string cases = Path.Combine(packet.Packet, "cases.private.json");
		string exchanges = Path.Combine(packet.Root, "exchanges.json");
		File.WriteAllText(exchanges, "[]");
		string plan = Path.Combine(packet.Repository, "plan.json");
		string replay = Path.Combine(packet.Repository, "nested", "..", "replay.json");

		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-plan", packet.Packet, packet.Repository, plan]));
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-replay", policy, cases, exchanges, packet.Repository, replay]));
		Assert.False(File.Exists(plan));
		Assert.False(File.Exists(Path.Combine(packet.Repository, "replay.json")));
	}

	[Fact]
	public async Task PlanIsCreatedOwnerOnly()
	{
		packet.Prepare();
		string output = Path.Combine(packet.Root, "plan.json");
		Assert.Equal(0, await OfflineCommand.RunAsync(["offline-plan", packet.Packet, packet.Repository, output]));
		if (!OperatingSystem.IsWindows())
		{
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(output));
		}
	}

	[Fact]
	public async Task ReplayCommandTakesPolicyFirstAndEnforcesItsCap()
	{
		packet.Prepare();
		string policy = Path.Combine(packet.Packet, "policy.private.json");
		string cases = Path.Combine(packet.Packet, "cases.private.json");
		string exchanges = Path.Combine(packet.Root, "exchanges.json");
		File.WriteAllText(exchanges, "[]");
		string output = Path.Combine(packet.Root, "replay.json");

		Assert.Equal(0, await OfflineCommand.RunAsync(["offline-replay", policy, cases, exchanges, packet.Repository, output]));
		using (JsonDocument replay = JsonDocument.Parse(File.ReadAllText(output)))
		{
			Assert.Equal(0, replay.RootElement.GetProperty("priorCarryNanodollars").GetInt64());
			Assert.Equal(4, replay.RootElement.GetProperty("totalCases").GetInt32());
			Assert.Equal("unknown", replay.RootElement.GetProperty("outcomes")[0].GetProperty("status").GetString());
		}

		string tooMany = Path.Combine(packet.Root, "nine.json");
		File.WriteAllText(tooMany, JsonSerializer.Serialize(Enumerable.Range(0, 9).Select(_ => new { body = "{}", statusCode = 200, requestSha256 = new string('a', 64) })));
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-replay", policy, cases, tooMany, packet.Repository, Path.Combine(packet.Root, "r2.json")]));
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-replay", cases, exchanges, packet.Repository, Path.Combine(packet.Root, "r3.json")]));
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-replay", policy, cases, exchanges, Path.Combine(packet.Root, "r4.json")]));
	}
}
