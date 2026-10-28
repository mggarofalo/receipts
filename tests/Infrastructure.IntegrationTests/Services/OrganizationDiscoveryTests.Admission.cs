using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class OrganizationDiscoveryTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DisableCommittedWhileAdmissionOrCaptureWaitsIsObservedUnderSettingsLock(bool capture)
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			await Seed(fixture, Item(Guid.NewGuid(), Guid.NewGuid()));
			await Enable(fixture);
			await using var db = fixture.CreateDbContext();
			Guid revision = await db.CategoryAssistanceSettings.Select(x => x.Revision).SingleAsync();
			Guid run = capture ? await Store(fixture).StartAsync(revision, default) : Guid.Empty;
			await using var owner = new NpgsqlConnection(fixture.ConnectionString);
			await owner.OpenAsync();
			await using var tx = await owner.BeginTransactionAsync();
			await using var command = new NpgsqlCommand("UPDATE category_assistance.\"Settings\" SET \"Enabled\" = FALSE", owner, tx);
			await command.ExecuteNonQueryAsync();
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			Task pending = capture ? Store(fixture).DiscoverAsync(run, 200, timeout.Token) : Store(fixture).StartAsync(revision, timeout.Token);
			while (!await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'").AnyAsync(x => x > 0, timeout.Token))
			{ await Task.Delay(20, timeout.Token); }
			await tx.CommitAsync();
			await pending;
			if (capture) { (await (Task<int>)pending).Should().Be(0); }
			else { (await (Task<Guid>)pending).Should().BeEmpty(); }
			(await db.OrganizationRuns.CountAsync()).Should().Be(capture ? 1 : 0);
			(await db.OrganizationItems.CountAsync()).Should().Be(0);
			(await db.OrganizationDirtyItems.CountAsync()).Should().Be(1);
		}
		finally { await fixture.DisposeAsync(); }
	}
}
