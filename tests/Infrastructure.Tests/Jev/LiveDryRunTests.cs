using System.Text.Json;
using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

/// <summary>Dry runs use injected synthetic environments and never create transport or journal state.</summary>
public sealed class LiveDryRunTests : IDisposable
{
	private const string Key = "synthetic-credential";
	private readonly SyntheticPacket packet = new();
	private string Output => Path.Combine(packet.Root, "dry-run.json");

	public void Dispose() => packet.Dispose();

	private Task<int> Run(string approval, Dictionary<string, string> variables, params string[] flags) =>
		LiveCommand.RunAsync(["live-run", packet.Packet, packet.Repository, approval, packet.Journal, Output, .. flags],
			name => variables.GetValueOrDefault(name), isWindows: false);

	private JsonElement Report()
	{
		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Output));
		return document.RootElement.Clone();
	}

	[Fact]
	public async Task ReportsReadinessWithoutSendingOrCreatingJournal()
	{
		packet.Prepare();
		string approval = packet.WriteApproval();
		Assert.Equal(0, await Run(approval, new() { ["TYPESAFE_API_KEY"] = Key }, "--dry-run"));
		Assert.False(File.Exists(packet.Journal));
		JsonElement report = Report();
		Assert.Equal("dry-run-no-dispatch", report.GetProperty("mode").GetString());
		JsonElement numbers = report.GetProperty("numbers");
		Assert.Equal(4, numbers.GetProperty("totalCases").GetInt32());
		Assert.Equal(8, numbers.GetProperty("maximumAttempts").GetInt32());
		Assert.Equal(SyntheticPacket.Create().CombinedMaximumReservationNanodollars,
			numbers.GetProperty("combinedMaximumReservationNanodollars").GetInt64());
		Assert.True(report.GetProperty("credentialPresent").GetBoolean());
		Assert.True(report.GetProperty("approval").GetProperty("valid").GetBoolean());
		Assert.Equal("absent", report.GetProperty("journal").GetProperty("status").GetString());
		Assert.Equal(4, numbers.GetProperty("stage1Requests").GetInt32());
		Assert.Equal(4, report.GetProperty("stage1RequestSha256s").GetArrayLength());
		Assert.True(numbers.GetProperty("stage1Bytes").GetProperty("total").GetInt64() > 0);
		string text = File.ReadAllText(Output);
		Assert.DoesNotContain(Key, text);
		Assert.DoesNotContain("synthetic apple", text);
		Assert.DoesNotContain("synthetic-0", text);
		Assert.DoesNotContain("Synthetic Store", text);
	}

	[Fact]
	public async Task MissingApprovalAndCredentialAreReportedNotFatal()
	{
		packet.Prepare();
		Assert.Equal(0, await Run(Path.Combine(packet.Root, "absent-approval.json"), new() { ["HOME"] = packet.Root }, "--dry-run", "--api-user-secrets"));
		JsonElement report = Report();
		Assert.False(report.GetProperty("credentialPresent").GetBoolean());
		Assert.False(report.GetProperty("approval").GetProperty("present").GetBoolean());
		Assert.Equal("missing", report.GetProperty("approval").GetProperty("status").GetString());
		Assert.False(File.Exists(packet.Journal));
	}

	[Fact]
	public async Task ApprovalOutsidePolicyIsReportedByCategory()
	{
		packet.Prepare();
		string approval = packet.WriteApproval(priorCarry: 0);
		Assert.Equal(0, await Run(approval, new() { ["TYPESAFE_API_KEY"] = Key }, "--dry-run"));
		Assert.Equal("budget-mismatch", Report().GetProperty("approval").GetProperty("status").GetString());
	}

	[Fact]
	public async Task OutstandingIntentIsReportedAndJournalUnchanged()
	{
		packet.Prepare();
		string journal = """{"kind":"header","identity":"other"}""" + "\n" + """{"kind":"intent","index":0,"requestSha256":"aa"}""" + "\n";
		File.WriteAllText(packet.Journal, journal);
		Assert.Equal(0, await Run(packet.WriteApproval(), new() { ["TYPESAFE_API_KEY"] = Key }, "--dry-run"));
		JsonElement status = Report().GetProperty("journal");
		Assert.Equal("outstanding-intent", status.GetProperty("status").GetString());
		Assert.False(status.GetProperty("identityMatches").GetBoolean());
		Assert.Equal(journal, File.ReadAllText(packet.Journal));
	}

	[Fact]
	public async Task ValidationFailuresExitTwoWithoutReport()
	{
		packet.Prepare(caseCount: 3);
		string approval = packet.WriteApproval();
		Assert.Equal(2, await Run(approval, new() { ["TYPESAFE_API_KEY"] = Key }, "--dry-run"));
		Assert.False(File.Exists(Output));
		packet.Dispose();
		packet.Prepare();
		Assert.Equal(2, await Run(approval, new() { ["TYPESAFE_API_KEY"] = Key }, "--dry-run", "--send-authorized"));
		Assert.Equal(2, await Run(approval, new() { ["TYPESAFE_API_KEY"] = Key }, "--dry-run", "--dry-run"));
		Assert.Equal(2, await Run(approval, new() { ["TYPESAFE_API_KEY"] = Key }));
		File.WriteAllText(Output, "{}");
		Assert.Equal(2, await Run(approval, new() { ["TYPESAFE_API_KEY"] = Key }, "--dry-run"));
		Assert.False(File.Exists(packet.Journal));
	}

	[Fact]
	public async Task RoundThreePacketIsRejectedWithoutReport()
	{
		packet.Prepare(policy => policy["packetKind"] = "owner-labels-v1");
		Assert.Equal(2, await Run(packet.WriteApproval(), new() { ["TYPESAFE_API_KEY"] = Key }, "--dry-run"));
		Assert.False(File.Exists(Output));
		Assert.False(File.Exists(packet.Journal));
	}

	[Fact]
	public async Task SendModeWithoutCredentialStopsBeforeJournal()
	{
		packet.Prepare();
		Assert.Equal(2, await Run(packet.WriteApproval(), new() { ["TYPESAFE_API_KEY"] = "" }, "--send-authorized"));
		Assert.False(File.Exists(packet.Journal));
		Assert.False(File.Exists(Output));
	}
}
