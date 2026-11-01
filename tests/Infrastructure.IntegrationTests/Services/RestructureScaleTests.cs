using System.Diagnostics;
using Application.Taxonomy.Restructure;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;

namespace Infrastructure.IntegrationTests.Services;

[Collection(ScaleCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
[Trait("Scale", "Restructure")]
public sealed class RestructureScaleTests(PostgresFixture server) : RestructureTestBase(server)
{
	[Fact]
	public async Task TwentyThousandPurchasesAreCheckedAndCapturedInOneApplyWithinBudget()
	{
		Seed seed = await SeedAsync();
		await AddItemsAsync(seed, seed.Placeholder, 10_000, seed.Pantry);
		await AddItemsAsync(seed, seed.EventDairy, 10_000, seed.Party);
		RestructurePlan plan = Plan(seed, DateTimeOffset.UtcNow);
		// Heap size is process-wide: the scale collection runs alone, so it measures this restructure only.
		long memoryBefore = GC.GetTotalMemory(true);
		Stopwatch clock = Stopwatch.StartNew();

		RestructurePreview preview = await Service().PreviewAsync(plan);
		TimeSpan dryRun = clock.Elapsed;
		RestructureApplicationSummary begun = await Service().BeginAsync(Actor, Guid.NewGuid(), plan);
		TimeSpan capture = clock.Elapsed - dryRun;
		long memory = GC.GetTotalMemory(true) - memoryBefore;
		RestructureApplicationSummary chunk = begun;
		Stopwatch applying = Stopwatch.StartNew();
		for (int index = 0; index < 5; index++) { chunk = await Service().ApplyChunkAsync(Actor, begun.Id, 100); }
		TimeSpan perPurchase = applying.Elapsed / 500;
		await Service().CancelAsync(Actor, begun.Id);
		RestructureApplicationSummary undone = await UndoAllAsync(begun.Id);

		preview.Report.Items.Reassigned.Should().Be(20_000);
		begun.Progress.Pending.Should().Be(20_000);
		chunk.Progress.Applied.Should().Be(500);
		undone.Progress.Undone.Should().Be(500);
		var after = await SnapshotAsync();
		// The 500 put back carry a `restructure` undo assignment on their original category; nothing else changed.
		after.Values.Where(x => x.Source == "restructure").Should().HaveCount(500)
			.And.OnlyContain(x => x.Category == seed.Placeholder || x.Category == seed.EventDairy);
		// Generous bounds for a loaded host; the rehearsal measures a full apply on real data.
		dryRun.Should().BeLessThan(TimeSpan.FromMinutes(1));
		capture.Should().BeLessThan(TimeSpan.FromMinutes(2));
		perPurchase.Should().BeLessThan(TimeSpan.FromMilliseconds(250));
		memory.Should().BeLessThan(256L * 1024 * 1024);
		Console.WriteLine($"restructure scale: dry run {dryRun}, capture {capture}, {perPurchase.TotalMilliseconds:F1} ms per purchase, {memory / 1024 / 1024} MiB");
	}
}
