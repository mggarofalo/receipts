using System.Text.Json;
using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

public sealed class OfflinePacketTests : IDisposable
{
	private readonly SyntheticPacket packet = new();

	public void Dispose() => packet.Dispose();

	[Fact]
	public async Task RoundFourPacketPlansWithFrozenPolicy()
	{
		packet.Prepare();
		TwoStagePacket loaded = TwoStagePacket.Load(packet.Packet);
		Assert.Equal(4, loaded.Policy.Cases);
		Assert.Equal(8, loaded.Policy.MaximumAttempts);

		string output = Path.Combine(packet.Root, "plan.json");
		Assert.Equal(0, await OfflineCommand.RunAsync(["offline-plan", packet.Packet, packet.Repository, output]));
		using JsonDocument plan = JsonDocument.Parse(File.ReadAllText(output));
		JsonElement numbers = plan.RootElement.GetProperty("numbers");
		Assert.Equal("owner-labels-v4", numbers.GetProperty("packetKind").GetString());
		Assert.Equal(4, numbers.GetProperty("totalCases").GetInt32());
		Assert.Equal(8, numbers.GetProperty("maximumAttempts").GetInt32());
		Assert.Equal(loaded.MaximumReservationNanodollars, numbers.GetProperty("maximumReservationNanodollars").GetInt64());
		Assert.Equal(SyntheticPacket.Ceiling, numbers.GetProperty("ceilingNanodollars").GetInt64());
		Assert.False(numbers.GetProperty("liveAuthorized").GetBoolean());
		Assert.Equal(4, plan.RootElement.GetProperty("cases").GetArrayLength());
		// Never overwrite evidence; retired commands are not accepted.
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-plan", packet.Packet, packet.Repository, output]));
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-plan-next", packet.Packet, packet.Repository, Path.Combine(packet.Root, "next.json")]));
	}

	[Fact]
	public async Task RoundThreePacketsAndCommandsAreRetired()
	{
		packet.Prepare(policy => policy["packetKind"] = "owner-labels-v1");
		string policy = Path.Combine(packet.Packet, "policy.private.json");
		string cases = Path.Combine(packet.Packet, "cases.private.json");
		string exchanges = Path.Combine(packet.Root, "exchanges.json");
		File.WriteAllText(exchanges, "[]");

		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-plan", packet.Packet, packet.Repository, Path.Combine(packet.Root, "plan.json")]));
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-replay", policy, cases, exchanges, packet.Repository, Path.Combine(packet.Root, "replay.json")]));
		Assert.False(File.Exists(Path.Combine(packet.Root, "plan.json")));
		Assert.False(File.Exists(Path.Combine(packet.Root, "replay.json")));
	}

	[Theory]
	[InlineData("20b13bf44108693e063ff8c61de69b26b4e5a4d")]
	[InlineData("0123456789ABCDEF0123456789abcdef01234567")]
	[InlineData("not-a-commit")]
	public void SourceCommitMustBeFullLowercaseHex(string commit)
	{
		packet.Prepare(commit: commit);
		Assert.Throws<ArgumentException>(() => TwoStagePacket.Load(packet.Packet));
	}

	[Theory]
	[InlineData("cases")]
	[InlineData("cumulativeCeilingNanodollars")]
	[InlineData("questionPolicy")]
	[InlineData("liveAuthorized")]
	public void PolicyFieldsAreRequired(string field)
	{
		packet.Prepare(policy => policy.Remove(field));
		Assert.Throws<ArgumentException>(() => TwoStagePacket.Load(packet.Packet));
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
		Assert.Throws<ArgumentException>(() => TwoStagePacket.Load(packet.Packet));
		packet.Dispose();
		packet.Prepare();
		File.AppendAllText(Path.Combine(packet.Packet, "policy.private.json"), " ");
		Assert.Throws<ArgumentException>(() => TwoStagePacket.Load(packet.Packet));
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
}
