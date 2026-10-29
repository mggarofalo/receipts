using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task CandidateRestore_PreservesPrivateDispositionsAndRetryHistory()
	{
		await using var source = await fixture.CreateIsolatedAsync();
		await using var destination = await fixture.CreateIsolatedAsync();
		var service = new HistoricalReclassificationService(new Factory(source), TimeProvider.System, Options.Create(new ClassificationOptions()));
		var group = new HistoricalGroupKey("Store", "Item", null);
		var first = new HistoricalDispositionRequest(group, HistoricalGroupDisposition.Later, 0, Guid.NewGuid());
		var second = new HistoricalDispositionRequest(group, HistoricalGroupDisposition.LeaveAsIs, 1, Guid.NewGuid());
		await service.SetDispositionAsync("unknown-actor", first);
		await service.SetDispositionAsync("unknown-actor", second);
		var otherActor = new HistoricalDispositionRequest(group, HistoricalGroupDisposition.Later, 0, Guid.NewGuid());
		var otherGroup = new HistoricalGroupKey("Store", new string('x', 128 * 1024 + 1), "");
		var otherFirst = new HistoricalDispositionRequest(otherGroup, HistoricalGroupDisposition.LeaveAsIs, 0, Guid.NewGuid());
		var otherReopened = new HistoricalDispositionRequest(otherGroup, null, 1, Guid.NewGuid());
		await service.SetDispositionAsync("other-actor", otherActor);
		await service.SetDispositionAsync("unknown-actor", otherFirst);
		await service.SetDispositionAsync("unknown-actor", otherReopened);
		string path = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportCandidateToSqliteAsync();
		try
		{
			var importer = new BackupImportService(new Factory(destination), NullLogger<BackupImportService>.Instance);
			var failing = new BackupImportService(new FailAfterDispositionFactory(destination), NullLogger<BackupImportService>.Instance);
			await using (var input = File.OpenRead(path))
			{
				Func<Task> fail = () => failing.ImportCandidateFromSqliteAsync(input, default);
				await fail.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected after disposition insert");
			}
			await using (var db = destination.CreateDbContext())
			{
				(await db.HistoricalGroupDispositions.CountAsync()).Should().Be(0);
				(await db.HistoricalDispositionOperations.CountAsync()).Should().Be(0);
			}
			for (int i = 0; i < 2; i++)
			{
				await using var input = File.OpenRead(path);
				await importer.ImportCandidateFromSqliteAsync(input, default);
			}
			var restored = new HistoricalReclassificationService(new Factory(destination), TimeProvider.System, Options.Create(new ClassificationOptions()));
			(await restored.SetDispositionAsync("unknown-actor", first)).Version.Should().Be(1);
			(await restored.SetDispositionAsync("unknown-actor", second)).Version.Should().Be(2);
			var reopened = new HistoricalDispositionRequest(group, null, 2, Guid.NewGuid());
			(await restored.SetDispositionAsync("unknown-actor", reopened)).Version.Should().Be(3);
			await using (var db = destination.CreateDbContext())
			{
				var rows = await db.HistoricalGroupDispositions.AsNoTracking().ToListAsync();
				rows.Should().HaveCount(3);
				rows.Single(x => x.Actor == "other-actor").Disposition.Should().Be(HistoricalGroupDisposition.Later);
				rows.Single(x => x.Actor == "unknown-actor" && x.RawDescription == otherGroup.RawDescription).Disposition.Should().BeNull();
				var row = rows.Single(x => x.Actor == "unknown-actor" && x.RawDescription == "Item");
				row.Disposition.Should().BeNull();
				(await db.HistoricalDispositionOperations.CountAsync()).Should().Be(6);
			}
			await using (var stale = File.OpenRead(path))
			{
				Func<Task> conflict = () => importer.ImportCandidateFromSqliteAsync(stale, default);
				await conflict.Should().ThrowAsync<InvalidOperationException>();
			}
			await using var after = destination.CreateDbContext();
			(await after.HistoricalGroupDispositions.SingleAsync(x => x.Actor == "unknown-actor" && x.RawDescription == "Item")).Version.Should().Be(3);
		}
		finally { File.Delete(path); }
	}
	private sealed class FailAfterDispositionDb(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
	{
		private bool inserted;
		public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
		{
			if (inserted) { throw new InvalidOperationException("Injected after disposition insert"); }
			bool adding = ChangeTracker.Entries<HistoricalGroupDispositionEntity>().Any(x => x.State == EntityState.Added);
			int result = await base.SaveChangesAsync(ct);
			inserted = adding;
			return result;
		}
	}
	private sealed class FailAfterDispositionFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new FailAfterDispositionDb(fixture.CreateOptions());
	}

	[Fact]
	public async Task CandidateRestore_SerializesDispositionWriterAndDetectsStaleVersion()
	{
		await using var source = await fixture.CreateIsolatedAsync();
		await using var destination = await fixture.CreateIsolatedAsync();
		var group = new HistoricalGroupKey("Store", "Item", null);
		var request = new HistoricalDispositionRequest(group, HistoricalGroupDisposition.Later, 0, Guid.NewGuid());
		var sourceService = new HistoricalReclassificationService(new Factory(source), TimeProvider.System, Options.Create(new ClassificationOptions()));
		await sourceService.SetDispositionAsync("actor", request);
		string path = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportCandidateToSqliteAsync();
		try
		{
			var restoreGate = new HistoricalRaceGate(true, writeGateOnly: true);
			var writerGate = new HistoricalRaceGate(false, writeGateOnly: true);
			var importer = new BackupImportService(new HistoricalRaceFactory(destination, restoreGate), NullLogger<BackupImportService>.Instance);
			var writer = new HistoricalReclassificationService(new HistoricalRaceFactory(destination, writerGate), TimeProvider.System, Options.Create(new ClassificationOptions()));
			await using var input = File.OpenRead(path);
			Task restoring = importer.ImportCandidateFromSqliteAsync(input, default);
			Task<HistoricalDispositionRecord>? writing = null;
			try
			{
				await restoreGate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
				writing = writer.SetDispositionAsync("actor", request with { OperationId = Guid.NewGuid(), Disposition = HistoricalGroupDisposition.LeaveAsIs });
				await new ClassificationTests(destination).AssertHistoricalDatabaseWait(restoreGate, writerGate);
			}
			finally { restoreGate.Release.TrySetResult(); }
			await restoring;
			(await Assert.ThrowsAsync<TaxonomyException>(async () => await writing!)).Status.Should().Be(409);
			await using var db = destination.CreateDbContext();
			(await db.HistoricalGroupDispositions.SingleAsync()).Disposition.Should().Be(HistoricalGroupDisposition.Later);
			(await db.HistoricalDispositionOperations.CountAsync()).Should().Be(1);
		}
		finally { File.Delete(path); }
	}

}
