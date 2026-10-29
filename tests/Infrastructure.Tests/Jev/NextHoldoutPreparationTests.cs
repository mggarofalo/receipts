using System.Text.Json;
using Application.Classification;
using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

public sealed class NextHoldoutPreparationTests : IDisposable
{
	private readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
	private string Packet => Path.Combine(root, "packet");
	private string Repository => Path.Combine(root, "repository");

	private void Prepare(double global = 0.9, double selected = 0.8, bool authorized = false, int count = 12)
	{
		Directory.CreateDirectory(Packet);
		string[] sources = ["src/Application/Classification/CategoryAssistanceDispatcher.cs", "src/Application/Classification/CategorySuggestion.cs",
			"src/Infrastructure/Jev/JevCategoryAssistanceSelectedProvider.cs", "src/Infrastructure/Jev/JevSelectedPathQuestions.cs",
			"src/Infrastructure/Jev/JevSuggestionProvider.cs", "src/Infrastructure/Jev/JevSuggestionQuestions.cs"];
		Dictionary<string, string> hashes = [];
		foreach (string source in sources)
		{
			string target = Path.Combine(Repository, source);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.WriteAllText(target, "synthetic pinned source");
			string snapshot = Path.Combine(Packet, "production-source", Path.GetFileName(source));
			Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
			File.Copy(target, snapshot);
			hashes["production-source/" + Path.GetFileName(source)] = OfflinePairedEvaluation.Hash(File.ReadAllBytes(snapshot));
		}
		string policy = Path.Combine(Packet, "policy.private.json");
		File.WriteAllText(policy, JsonSerializer.Serialize(new
		{
			packetKind = "next-holdout-preparation-only-v1",
			cases = count,
			maximumAttempts = 24,
			autoWriteGlobalGate = global,
			autoWriteSelectedGate = selected,
			liveAuthorized = authorized,
			reservationNanodollarsPerAttempt = 2752512,
			conservativePriorCarryNanodollars = 4873863040L
		}));
		hashes["policy.private.json"] = OfflinePairedEvaluation.Hash(File.ReadAllBytes(policy));
		OfflineCase[] cases = Enumerable.Range(0, count).Select(i => new OfflineCase("synthetic-" + i,
			new("synthetic apple", "synthetic-taxonomy", [new("fruit", "Fruit", "Fresh fruit", null)]))).ToArray();
		string casesPath = Path.Combine(Packet, "cases.private.json");
		File.WriteAllText(casesPath, JsonSerializer.Serialize(cases, ProductionParity.JsonOptions));
		hashes["cases.private.json"] = OfflinePairedEvaluation.Hash(File.ReadAllBytes(casesPath));
		File.WriteAllText(Path.Combine(Packet, "freeze.private.json"), JsonSerializer.Serialize(new
		{
			sourceCommit = "20b13bf44108693e063ff8c61de69b26b4e5a4d0",
			filesSha256 = hashes
		}));
	}

	[Fact]
	public async Task NextPlanUsesTwelveCasesAndDoesNotEnableLiveOrLegacyPlan()
	{
		Prepare();
		string output = Path.Combine(root, "next.json");
		Assert.Equal(0, await OfflineCommand.RunAsync(["offline-plan-next", Packet, Repository, output]));
		using JsonDocument plan = JsonDocument.Parse(File.ReadAllText(output));
		Assert.Equal(12, plan.RootElement.GetProperty("totalCases").GetInt32());
		Assert.Equal(24, plan.RootElement.GetProperty("maximumAttempts").GetInt32());
		Assert.Equal(0.9, plan.RootElement.GetProperty("autoWriteGlobalGate").GetDouble());
		Assert.Equal(0.8, plan.RootElement.GetProperty("selectedPathGate").GetDouble());
		Assert.False(plan.RootElement.GetProperty("liveAuthorized").GetBoolean());
		Assert.Equal(4939923328L, plan.RootElement.GetProperty("combinedMaximumReservationNanodollars").GetInt64());
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-plan", Packet, Repository, Path.Combine(root, "old.json")]));
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-plan-next", Packet, Repository, output]));
	}

	[Theory]
	[InlineData(0.89, 0.8, false)]
	[InlineData(0.9, 0.79, false)]
	[InlineData(0.9, 0.8, true)]
	public void NextPolicyCannotChangeFrozenThresholdsOrAuthorizeLive(double global, double selected, bool authorized)
	{
		Prepare(global, selected, authorized);
		Assert.Throws<ArgumentException>(() => OfflineCommand.ValidateFreeze(Packet, Repository, next: true));
	}

	[Fact]
	public async Task NextPlanRejectsWrongMembershipAndFrozenByteDrift()
	{
		Prepare(count: 11);
		string output = Path.Combine(root, "out.json");
		Assert.Equal(2, await OfflineCommand.RunAsync(["offline-plan-next", Packet, Repository, output]));
		Assert.False(File.Exists(output));
		File.AppendAllText(Path.Combine(Packet, "policy.private.json"), " ");
		Assert.Throws<ArgumentException>(() => OfflineCommand.ValidateFreeze(Packet, Repository, next: true));
	}

	public void Dispose() => Directory.Delete(root, recursive: true);
}
