using System.Data.Common;
using Application.Taxonomy;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task CandidateRestore_RoundTripsUndoneAutomaticHistoryWhilePublicImportRejectsV14()
	{
		await using var source = await fixture.CreateIsolatedAsync();
		await using var destination = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(source).CandidateRestoreUndoneCore(destination);
	}

	private async Task CandidateRestoreUndoneCore(PostgresFixture destination)
	{
		var captured = await CaptureExecutableAsync();
		var executor = ApprovedExecutor();
		await executor.ApplyChunkAsync(Actor, captured.Request.OperationId, 1);
		var undo = new HistoricalUndoCommand(captured.Request.TaxonomyRevisionId, "Restore prior choice");
		(await executor.UndoChunkAsync(CurrentRestoreGeneration(), Actor, captured.Request.OperationId, undo, 1)).Undone.Should().Be(1);

		var sourceHistory = await Service().HistoryAsync(captured.Item, null, 10);
		var exporter = new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance);
		string path = await exporter.ExportCandidateToSqliteAsync();
		try
		{
			var importer = new BackupImportService(new Factory(destination), NullLogger<BackupImportService>.Instance);
			await using (var publicInput = File.OpenRead(path))
			{
				Func<Task> publicImport = () => importer.ImportFromSqliteAsync(publicInput, default);
				await publicImport.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Unsupported backup export_version '14'*");
			}
			var failingImporter = new BackupImportService(new FailCandidateRestoreFactory(destination), NullLogger<BackupImportService>.Instance);
			await using (var failingInput = File.OpenRead(path))
			{
				Func<Task> fail = () => failingImporter.ImportCandidateFromSqliteAsync(failingInput, default);
				await fail.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected late restore failure");
			}
			await using (var afterFailure = destination.CreateDbContext())
			{
				(await afterFailure.ReceiptItems.AnyAsync(x => x.Id == captured.Item)).Should().BeFalse();
				(await afterFailure.HistoricalOperations.AnyAsync(x => x.Id == captured.Request.OperationId)).Should().BeFalse();
				(await afterFailure.Classifications.AnyAsync(x => x.ReceiptItemId == captured.Item)).Should().BeFalse();
			}
			await using (var input = File.OpenRead(path))
			{
				await importer.ImportCandidateFromSqliteAsync(input, default);
			}
			var restored = new ClassificationTests(destination);
			var history = await restored.Service().HistoryAsync(captured.Item, null, 10);
			history.Items.Select(x => x.Id).Should().Equal(sourceHistory.Items.Select(x => x.Id));
			history.Items.First().Source.Should().Be("manual");
			history.Items.First().CategoryId.Should().BeNull();
			await using var db = destination.CreateDbContext();
			var item = await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == captured.Request.OperationId);
			item.UndoState.Should().Be(HistoricalItemState.Applied);
			item.UndoAssignmentId.Should().Be(history.Items.First().Id);
			(await restored.ApprovedExecutor().UndoChunkAsync(restored.CurrentRestoreGeneration(), Actor, captured.Request.OperationId, undo, 1)).Undone.Should().Be(1);
			var later = await restored.Service().AssignAsync(Actor, captured.Item,
					new ClassificationCommand(captured.Category, captured.Request.TaxonomyRevisionId, history.Items.First().Id, Guid.NewGuid(), "Later choice"));
			await using (var staleInput = File.OpenRead(path))
			{
				Func<Task> stale = () => importer.ImportCandidateFromSqliteAsync(staleInput, default);
				await stale.Should().ThrowAsync<InvalidOperationException>();
			}
			(await restored.Service().HistoryAsync(captured.Item, null, 1)).Items.Single().Id.Should().Be(later.Id);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task CandidateRestore_AbandonsPartialApplyPreservesEffectsAndReplays()
	{
		await using var source = await fixture.CreateIsolatedAsync();
		await using var destination = await fixture.CreateIsolatedAsync();
		var original = new ClassificationTests(source);
		var captured = await original.CaptureExecutableAsync(extraItems: 1);
		var progress = await original.ApprovedExecutor().ApplyChunkAsync(Actor, captured.Request.OperationId, 1);
		progress.State.Should().Be(HistoricalOperationState.Applying);
		progress.Applied.Should().Be(1); progress.Pending.Should().Be(1);
		string path = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportCandidateToSqliteAsync();
		try
		{
			var importer = new BackupImportService(new Factory(destination), NullLogger<BackupImportService>.Instance);
			for (int attempt = 0; attempt < 2; attempt++)
			{
				await using var input = File.OpenRead(path);
				await importer.ImportCandidateFromSqliteAsync(input, default);
			}
			var restored = new ClassificationTests(destination);
			var stopped = await restored.ApprovedExecutor().ApplyChunkAsync(Actor, captured.Request.OperationId, 1);
			stopped.State.Should().Be(HistoricalOperationState.Cancelled);
			stopped.Applied.Should().Be(1); stopped.Pending.Should().Be(1);
			await using var db = destination.CreateDbContext();
			var rows = await db.HistoricalOperationItems.AsNoTracking().Where(x => x.OperationId == captured.Request.OperationId).ToListAsync();
			var applied = rows.Single(x => x.State == HistoricalItemState.Applied);
			(await restored.Service().HistoryAsync(applied.ItemId, null, 1)).Items.Single().Id.Should().Be(applied.AppliedAssignmentId!.Value);
			var undo = new HistoricalUndoCommand(captured.Request.TaxonomyRevisionId, "Restore prior choice");
			(await restored.ApprovedExecutor().UndoChunkAsync(restored.CurrentRestoreGeneration(), Actor, captured.Request.OperationId, undo, 1)).Undone.Should().Be(1);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task CandidateRestore_RollsBackDestinationAbandonmentAndFencesOldWorker()
	{
		await using var destination = await fixture.CreateIsolatedAsync();
		var subject = new ClassificationTests(destination);
		var captured = await subject.CaptureExecutableAsync();
		string path = await new BackupService(new Factory(destination), NullLogger<BackupService>.Instance).ExportCandidateToSqliteAsync();
		try
		{
			var failing = new BackupImportService(new FailAfterAbandonFactory(destination), NullLogger<BackupImportService>.Instance);
			await using (var input = File.OpenRead(path))
			{
				Func<Task> import = () => failing.ImportCandidateFromSqliteAsync(input, default);
				await import.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected after abandonment");
			}
			await using (var db = destination.CreateDbContext())
			{
				(await db.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == captured.Request.OperationId)).State.Should().Be(HistoricalOperationState.Preview);
			}
			var importer = new BackupImportService(new Factory(destination), NullLogger<BackupImportService>.Instance);
			await using (var input = File.OpenRead(path)) { await importer.ImportCandidateFromSqliteAsync(input, default); }
			var stopped = await subject.ApprovedExecutor().ApplyChunkAsync(Actor, captured.Request.OperationId, 1);
			stopped.State.Should().Be(HistoricalOperationState.Cancelled);
			stopped.Applied.Should().Be(0); stopped.Pending.Should().Be(1);
		}
		finally { File.Delete(path); }
	}

	private sealed class FailAfterAbandonDb(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
	{
		private bool abandoned;
		public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
		{
			if (abandoned) { throw new InvalidOperationException("Injected after abandonment"); }
			bool cancelling = ChangeTracker.Entries<HistoricalOperationEntity>().Any(x => x.State == EntityState.Modified && x.Entity.State == HistoricalOperationState.Cancelled);
			int result = await base.SaveChangesAsync(cancellationToken);
			abandoned = cancelling;
			return result;
		}
	}

	private sealed class FailAfterAbandonFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new FailAfterAbandonDb(fixture.CreateOptions());
	}

	[Fact]
	public async Task CandidateRestore_PreservesPartialUndoAndFencesPriorRequest()
	{
		await using var source = await fixture.CreateIsolatedAsync();
		await using var destination = await fixture.CreateIsolatedAsync();
		var original = new ClassificationTests(source);
		var restored = new ClassificationTests(destination);
		var captured = await original.CaptureExecutableAsync(extraItems: 2);
		await original.ApprovedExecutor().ApplyChunkAsync(Actor, captured.Request.OperationId, 3);
		var undo = new HistoricalUndoCommand(captured.Request.TaxonomyRevisionId, "Restore prior choice");
		var progress = await original.ApprovedExecutor().UndoChunkAsync(original.CurrentRestoreGeneration(), Actor, captured.Request.OperationId, undo, 1);
		progress.State.Should().Be(HistoricalOperationState.Undoing);
		Guid oldGeneration = restored.CurrentRestoreGeneration();
		string path = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportCandidateToSqliteAsync();
		try
		{
			var failing = new BackupImportService(new FailCandidateRestoreFactory(destination), NullLogger<BackupImportService>.Instance);
			await using (var input = File.OpenRead(path))
			{ await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ImportCandidateFromSqliteAsync(input, default)); }
			restored.CurrentRestoreGeneration().Should().Be(oldGeneration);
			await using (var db = destination.CreateDbContext())
			{ (await db.HistoricalOperations.AnyAsync(x => x.Id == captured.Request.OperationId)).Should().BeFalse(); }
			var importer = new BackupImportService(new Factory(destination), NullLogger<BackupImportService>.Instance);
			for (int attempt = 0; attempt < 2; attempt++)
			{ await using var input = File.OpenRead(path); await importer.ImportCandidateFromSqliteAsync(input, default); }
			restored.CurrentRestoreGeneration().Should().NotBe(oldGeneration);
			(await Assert.ThrowsAsync<TaxonomyException>(() => restored.ApprovedExecutor().UndoChunkAsync(oldGeneration, Actor, captured.Request.OperationId, undo, 1))).Status.Should().Be(409);
			await using (var db = destination.CreateDbContext())
			{
				var operation = await db.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == captured.Request.OperationId);
				operation.UndoTaxonomyRevisionId.Should().Be(undo.TaxonomyRevisionId);
				operation.UndoReason.Should().Be(undo.Reason);
				var rows = await db.HistoricalOperationItems.AsNoTracking().Where(x => x.OperationId == operation.Id).ToListAsync();
				rows.Count(x => x.UndoState == HistoricalItemState.Applied).Should().Be(1);
				var pending = rows.First(x => x.UndoState == HistoricalItemState.Pending);
				await restored.Service().AssignAsync(Actor, pending.ItemId,
					new ClassificationCommand(captured.Category, undo.TaxonomyRevisionId, pending.AppliedAssignmentId, Guid.NewGuid(), "Later choice"));
			}
			Guid fresh = restored.CurrentRestoreGeneration();
			(await Assert.ThrowsAsync<TaxonomyException>(() => restored.ApprovedExecutor().UndoChunkAsync(
				fresh, Actor, captured.Request.OperationId, undo with { Reason = "Changed intent" }, 1))).Status.Should().Be(409);
			(await Assert.ThrowsAsync<TaxonomyException>(() => restored.ApprovedExecutor().UndoChunkAsync(
				fresh, Actor, captured.Request.OperationId, undo with { TaxonomyRevisionId = Guid.NewGuid() }, 1))).Status.Should().Be(409);
			var resumed = await restored.ApprovedExecutor().UndoChunkAsync(fresh, Actor, captured.Request.OperationId, undo, 1);
			resumed.State.Should().Be(HistoricalOperationState.Undoing);
			(resumed.Undone + resumed.UndoConflicted).Should().Be(2);
			var completed = await restored.ApprovedExecutor().UndoChunkAsync(fresh, Actor, captured.Request.OperationId, undo, 1);
			completed.State.Should().Be(HistoricalOperationState.Undone);
			completed.Undone.Should().Be(2);
			completed.UndoConflicted.Should().Be(1);
			(await restored.ApprovedExecutor().UndoChunkAsync(fresh, Actor, captured.Request.OperationId, undo, 1)).Should().Be(completed);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task CandidateRestore_BlocksStaleUndoAlreadyWaitingOnWriteGate()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		var subject = new ClassificationTests(isolated);
		var captured = await subject.CaptureExecutableAsync(extraItems: 1);
		await subject.ApprovedExecutor().ApplyChunkAsync(Actor, captured.Request.OperationId, 2);
		var undo = new HistoricalUndoCommand(captured.Request.TaxonomyRevisionId, "Frozen restore intent");
		Guid generation = subject.CurrentRestoreGeneration();
		(await subject.ApprovedExecutor().UndoChunkAsync(generation, Actor, captured.Request.OperationId, undo, 1)).Undone.Should().Be(1);
		string path = await new BackupService(new Factory(isolated), NullLogger<BackupService>.Instance).ExportCandidateToSqliteAsync();
		try
		{
			var restoreGate = new HistoricalRaceGate(true, writeGateOnly: true);
			var undoGate = new HistoricalRaceGate(false, writeGateOnly: true);
			var importer = new BackupImportService(new HistoricalRaceFactory(isolated, restoreGate), NullLogger<BackupImportService>.Instance);
			var executor = new HistoricalApprovedMappingExecutor(new HistoricalRaceFactory(isolated, undoGate), TimeProvider.System,
				Microsoft.Extensions.Options.Options.Create(new ClassificationOptions { WritesEnabled = true }));
			await using var input = File.OpenRead(path);
			Task restoring = importer.ImportCandidateFromSqliteAsync(input, default);
			Task<HistoricalProgress>? waiting = null;
			try
			{
				await restoreGate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
				waiting = executor.UndoChunkAsync(generation, Actor, captured.Request.OperationId, undo, 1);
				await subject.AssertHistoricalDatabaseWait(restoreGate, undoGate);
			}
			finally { restoreGate.Release.TrySetResult(); }
			await restoring;
			(await Assert.ThrowsAsync<TaxonomyException>(async () => await waiting!)).Status.Should().Be(409);
			await using (var db = isolated.CreateDbContext())
			{
				var rows = await db.HistoricalOperationItems.AsNoTracking().Where(x => x.OperationId == captured.Request.OperationId).ToListAsync();
				rows.Count(x => x.UndoState == HistoricalItemState.Applied).Should().Be(1);
				rows.Count(x => x.UndoState == HistoricalItemState.Pending).Should().Be(1);
			}
			Guid fresh = subject.CurrentRestoreGeneration();
			fresh.Should().NotBe(generation);
			(await executor.UndoChunkAsync(fresh, Actor, captured.Request.OperationId, undo, 1)).Undone.Should().Be(2);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task CandidateRestore_PartialUndoConflictsAfterTaxonomyChanges()
	{
		await using var source = await fixture.CreateIsolatedAsync();
		await using var destination = await fixture.CreateIsolatedAsync();
		var original = new ClassificationTests(source);
		var restored = new ClassificationTests(destination);
		var captured = await original.CaptureExecutableAsync(extraItems: 1);
		await original.ApprovedExecutor().ApplyChunkAsync(Actor, captured.Request.OperationId, 2);
		var undo = new HistoricalUndoCommand(captured.Request.TaxonomyRevisionId, "Frozen restore intent");
		Guid generation = original.CurrentRestoreGeneration();
		(await original.ApprovedExecutor().UndoChunkAsync(generation, Actor, captured.Request.OperationId, undo, 1)).Undone.Should().Be(1);
		string path = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportCandidateToSqliteAsync();
		try
		{
			var importer = new BackupImportService(new Factory(destination), NullLogger<BackupImportService>.Instance);
			await using (var input = File.OpenRead(path)) { await importer.ImportCandidateFromSqliteAsync(input, default); }
			await using (var adminDb = destination.CreateDbContext())
			{
				adminDb.Users.Add(new Infrastructure.Entities.ApplicationUser { Id = Actor, UserName = Actor });
				var role = await adminDb.Roles.FirstOrDefaultAsync(x => x.Name == Common.AppRoles.Admin);
				if (role == null) { role = new Microsoft.AspNetCore.Identity.IdentityRole { Id = Guid.NewGuid().ToString(), Name = Common.AppRoles.Admin }; adminDb.Roles.Add(role); }
				adminDb.UserRoles.Add(new() { UserId = Actor, RoleId = role.Id });
				await adminDb.SaveChangesAsync();
			}
			await restored.Tree().ChangeAsync(Actor, new(undo.TaxonomyRevisionId, Guid.NewGuid(), "Independent taxonomy revision",
				[new TaxonomyEdit("create", Guid.NewGuid(), Name: Guid.NewGuid().ToString())]));
			Guid fresh = restored.CurrentRestoreGeneration();
			var result = await restored.ApprovedExecutor().UndoChunkAsync(fresh, Actor, captured.Request.OperationId, undo, 1);
			result.State.Should().Be(HistoricalOperationState.Undone);
			result.Undone.Should().Be(1);
			result.UndoConflicted.Should().Be(1);
			await using var db = destination.CreateDbContext();
			var conflict = await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == captured.Request.OperationId && x.UndoState == HistoricalItemState.Conflicted);
			conflict.UndoReason.Should().Be("taxonomy-changed");
			(await restored.Service().HistoryAsync(conflict.ItemId, null, 1)).Items.Single().Id.Should().Be(conflict.AppliedAssignmentId!.Value);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task CandidateRestore_AppliedOperationCanBeUndoneAndCannotImmediatelyReapply()
	{
		await using var source = await fixture.CreateIsolatedAsync();
		await using var destination = await fixture.CreateIsolatedAsync();
		var original = new ClassificationTests(source);
		var captured = await original.CaptureExecutableAsync();
		(await original.ApprovedExecutor().ApplyChunkAsync(Actor, captured.Request.OperationId, 1)).Applied.Should().Be(1);
		string path = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportCandidateToSqliteAsync();
		try
		{
			var importer = new BackupImportService(new Factory(destination), NullLogger<BackupImportService>.Instance);
			await using (var input = File.OpenRead(path)) { await importer.ImportCandidateFromSqliteAsync(input, default); }
			var restored = new ClassificationTests(destination);
			var applied = (await restored.Service().HistoryAsync(captured.Item, null, 1)).Items.Single();
			applied.Source.Should().Be("approved_mapping");
			var undo = new HistoricalUndoCommand(captured.Request.TaxonomyRevisionId, "Restore prior choice");
			(await restored.ApprovedExecutor().UndoChunkAsync(restored.CurrentRestoreGeneration(), Actor, captured.Request.OperationId, undo, 1)).Undone.Should().Be(1);
			var latest = (await restored.Service().HistoryAsync(captured.Item, null, 1)).Items.Single();
			latest.Source.Should().Be("manual");
			latest.CategoryId.Should().BeNull();
			latest.PreviousAssignmentId.Should().Be(applied.Id);
			var retry = captured.Request with { OperationId = Guid.NewGuid() };
			(await new HistoricalApprovedMappingCapture(new Factory(destination), TimeProvider.System).CaptureAsync(retry)).Should().Be(0);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task CandidateRestore_RejectsDivergentDestinationBeforeMutation()
	{
		await using var source = await fixture.CreateIsolatedAsync();
		await using var destination = await fixture.CreateIsolatedAsync();
		var original = new ClassificationTests(source);
		var completed = await original.CaptureExecutableAsync();
		await original.ApprovedExecutor().ApplyChunkAsync(Actor, completed.Request.OperationId, 1);
		var pending = await new ClassificationTests(destination).CaptureExecutableAsync();
		string path = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportCandidateToSqliteAsync();
		try
		{
			var importer = new BackupImportService(new Factory(destination), NullLogger<BackupImportService>.Instance);
			await using var input = File.OpenRead(path);
			Func<Task> import = () => importer.ImportCandidateFromSqliteAsync(input, default);
			await import.Should().ThrowAsync<InvalidOperationException>();
			await using var db = destination.CreateDbContext();
			(await db.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == pending.Request.OperationId)).State.Should().Be(HistoricalOperationState.Preview);
			(await db.HistoricalOperations.AnyAsync(x => x.Id == completed.Request.OperationId)).Should().BeFalse();
			(await db.ReceiptItems.AnyAsync(x => x.Id == completed.Item)).Should().BeFalse();
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task CandidateExport_UsesOneSnapshotWhenAutomaticHistoryChangesDuringCapture()
	{
		await using var source = await fixture.CreateIsolatedAsync();
		var original = new ClassificationTests(source);
		var captured = await original.CaptureExecutableAsync();
		var gate = new CandidateSnapshotGate();
		var exporter = new BackupService(new CandidateSnapshotFactory(source, gate), NullLogger<BackupService>.Instance);
		Task<string> exporting = exporter.ExportCandidateToSqliteAsync();
		string? path = null;
		try
		{
			await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
			(await original.ApprovedExecutor().ApplyChunkAsync(Actor, captured.Request.OperationId, 1)).Applied.Should().Be(1);
			gate.Release.TrySetResult();
			path = await exporting;
			await using var sqlite = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
			await sqlite.OpenAsync();
			var archive = await PortableTaxonomyArchive.ReadCandidateAsync(sqlite, default);
			archive.HistoricalOperations.Single(x => x.Id == captured.Request.OperationId).State.Should().Be(HistoricalOperationState.Preview);
			archive.HistoricalItems.Single(x => x.OperationId == captured.Request.OperationId).State.Should().Be(HistoricalItemState.Pending);
			archive.Assignments.Should().NotContain(x => x.Source == "approved_mapping" && x.ReceiptItemId == captured.Item);
		}
		finally
		{
			gate.Release.TrySetResult();
			if (path == null) { path = await exporting; }
			File.Delete(path);
		}
	}

	private sealed class CandidateSnapshotGate : DbCommandInterceptor
	{
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
				CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("\"HistoricalOperations\"", StringComparison.Ordinal))
			{
				Entered.TrySetResult();
				await Release.Task.WaitAsync(cancellationToken);
			}
			return result;
		}
	}

	private sealed class CandidateSnapshotFactory(PostgresFixture fixture, CandidateSnapshotGate gate) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(gate).Options);
	}

	private sealed class FailCandidateRestoreDb(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
	{
		public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
		{
			if (ChangeTracker.Entries<HistoricalOperationItemEntity>().Any(x => x.State == EntityState.Added))
			{ throw new InvalidOperationException("Injected late restore failure"); }
			return base.SaveChangesAsync(cancellationToken);
		}
	}

	private sealed class FailCandidateRestoreFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new FailCandidateRestoreDb(fixture.CreateOptions());
	}
}
