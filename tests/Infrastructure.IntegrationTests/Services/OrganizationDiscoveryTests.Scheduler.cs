using Application.Classification;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class OrganizationDiscoveryTests
{
	private static ServiceProvider DiscoveryServices(PostgresFixture fixture) => new ServiceCollection()
		.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture))
		.AddSingleton<TimeProvider>(TimeProvider.System)
		.AddScoped<ICategoryAssistanceSettingsStore, CategoryAssistanceSettingsStore>()
		.AddScoped<OrganizationDiscoveryStore>().BuildServiceProvider();

	private static OrganizationDiscoveryWorker Worker(ServiceProvider services) => new(
		services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OrganizationDiscoveryWorker>.Instance);

	private static async Task Eventually(Func<Task<bool>> assertion)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
		while (!await assertion()) { await Task.Delay(50, timeout.Token); }
	}

	[Fact]
	public async Task HostedStartupScansMoreThanOnePageAndWatchesFuturePurchasesWithoutProviderAndRestartReusesRun()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			Guid receipt = Guid.NewGuid();
			await Seed(fixture, Enumerable.Range(0, 405).Select(_ => Item(receipt, Guid.NewGuid())).ToArray());
			await Enable(fixture);
			await using var db = fixture.CreateDbContext();
			await db.OrganizationDirtyItems.ExecuteDeleteAsync(); // Require the historical sweep.
			long audits = await db.AuditLogs.LongCountAsync();
			using var services = DiscoveryServices(fixture); // No provider, dispatcher or AI service registered.
			using (var worker = Worker(services))
			{
				await worker.StartAsync(default);
				try
				{
					await Eventually(async () => await db.OrganizationItems.CountAsync() == 405 &&
						await db.OrganizationRuns.AnyAsync(x => x.State == "watching"));
					(await db.AuditLogs.LongCountAsync()).Should().Be(audits);
					db.ReceiptItems.Add(Item(receipt, Guid.NewGuid(), "Future synthetic purchase"));
					await db.SaveChangesAsync();
					audits = await db.AuditLogs.LongCountAsync();
					await Eventually(async () => await db.OrganizationItems.CountAsync() == 406);
				}
				finally { await worker.StopAsync(default); }
			}
			Guid run = await db.OrganizationRuns.Select(x => x.Id).SingleAsync();
			using (var restarted = Worker(services)) { await restarted.RunOnceAsync(); }
			(await db.OrganizationRuns.Select(x => x.Id).SingleAsync()).Should().Be(run);
			(await db.Classifications.CountAsync()).Should().Be(0);
			(await db.CategoryAssistanceAttempts.CountAsync()).Should().Be(0);
			(await db.AuditLogs.LongCountAsync()).Should().Be(audits);
		}
		finally { await fixture.DisposeAsync(); }
	}

	[Fact]
	public async Task DisabledAdmissionAndCapturePreserveInboxAndDoNotCreateWork()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			await Seed(fixture, Item(Guid.NewGuid(), Guid.NewGuid()));
			await using var db = fixture.CreateDbContext();
			Guid revision = await db.CategoryAssistanceSettings.Select(x => x.Revision).SingleAsync();
			(await Store(fixture).StartAsync(revision, default)).Should().BeEmpty();
			using var services = DiscoveryServices(fixture);
			using var worker = Worker(services);
			(await worker.RunOnceAsync()).Should().Be(0);
			(await db.OrganizationRuns.CountAsync()).Should().Be(0);
			Guid run = await Start(fixture);
			await db.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"Settings\" SET \"Enabled\" = FALSE");
			(await Store(fixture).DiscoverAsync(run, 200, default)).Should().Be(0);
			(await db.OrganizationItems.CountAsync()).Should().Be(0);
			(await db.OrganizationDirtyItems.CountAsync()).Should().Be(1);
			(await Store(fixture).GetProgressAsync()).Should().Be(new OrganizationDiscoveryProgress(false, "disabled", 0, 0, 0, 0, 0, null));
		}
		finally { await fixture.DisposeAsync(); }
	}

	[Fact]
	public async Task CancellationDuringCaptureRollsBackAndReleasesLocksForNextPass()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			await Seed(fixture, Item(Guid.NewGuid(), Guid.NewGuid()));
			Guid run = await Start(fixture);
			var pause = new PauseSave();
			using var cancel = new CancellationTokenSource();
			Task capture = Store(fixture, pause).DiscoverAsync(run, 200, cancel.Token);
			await pause.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
			cancel.Cancel();
			await FluentActions.Awaiting(() => capture).Should().ThrowAsync<OperationCanceledException>();
			await using var db = fixture.CreateDbContext();
			(await db.OrganizationItems.CountAsync()).Should().Be(0);
			(await db.OrganizationDirtyItems.CountAsync()).Should().Be(1);
			await Drain(fixture, run);
			(await db.OrganizationItems.CountAsync()).Should().Be(1);
		}
		finally { await fixture.DisposeAsync(); }
	}

	[Theory]
	[InlineData("Revision")]
	[InlineData("RestoreGeneration")]
	[InlineData("Taxonomy")]
	public async Task AggregateCountsPartitionManifestAndHidePreviousFence(string fence)
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			Guid receipt = Guid.NewGuid();
			var items = Enumerable.Range(0, 5).Select(_ => Item(receipt, Guid.NewGuid())).ToArray();
			items[2].Category = "Synthetic protected";
			await Seed(fixture, items);
			await using var db = fixture.CreateDbContext();
			// Legacy/imported invalid facts bypass the write-path validator.
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"Description\" = ' ' WHERE \"Id\" = {items[4].Id}");
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"DeletedAt\" = now() WHERE \"Id\" = {items[3].Id}");
			Guid run = await Start(fixture); await Drain(fixture, run);
			var progress = await Store(fixture).GetProgressAsync();
			progress.SourceGroups.Should().Be(1);
			progress.ObservedItems.Should().Be(2);
			progress.ProtectedItems.Should().Be(1);
			progress.UnavailableItems.Should().Be(1);
			progress.InvalidSourceItems.Should().Be(1);
			(progress.ObservedItems + progress.ProtectedItems + progress.UnavailableItems + progress.InvalidSourceItems).Should().Be(5);
			if (fence == "Taxonomy")
			{
				var head = await db.TaxonomyHeads.SingleAsync();
				var revision = new Infrastructure.Entities.Taxonomy.TaxonomyRevisionEntity { Id = Guid.NewGuid(), Sequence = 1, PreviousRevisionId = head.RevisionId, Actor = "synthetic", Reason = "Fence fixture", OperationId = Guid.NewGuid(), RecordedAt = DateTimeOffset.UtcNow };
				db.TaxonomyRevisions.Add(revision); head.RevisionId = revision.Id; await db.SaveChangesAsync();
			}
			else if (fence == "Revision") { await db.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"Settings\" SET \"Revision\" = gen_random_uuid()"); }
			else { await db.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"Settings\" SET \"RestoreGeneration\" = gen_random_uuid()"); }
			(await Store(fixture).GetProgressAsync()).Should().Be(new OrganizationDiscoveryProgress(true, "awaiting_run", 0, 0, 0, 0, 0, null));
		}
		finally { await fixture.DisposeAsync(); }
	}
}
