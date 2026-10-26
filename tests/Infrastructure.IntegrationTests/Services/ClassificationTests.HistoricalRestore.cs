using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task HistoricalPortableRestorePreservesPendingFencesAndUndoOwnership()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await using (var db = fixture.CreateDbContext())
		{
			var original = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
			var second = SampleData.Entities.ReceiptItemEntityGenerator.Generate(original.ReceiptId);
			second.Description = original.Description; second.ReceiptItemCode = original.ReceiptItemCode;
			await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync([second], default);
		}
		(await Historical().SavePreviewAsync(request)).Total.Should().Be(2);
		(await Historical().ApplyChunkAsync(Actor, request.OperationId, 1)).Applied.Should().Be(1);
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		var target = new PostgresFixture();
		await target.InitializeAsync();
		try
		{
			var factory = new Factory(target);
			var importer = new BackupImportService(factory, NullLogger<BackupImportService>.Instance);
			async Task Restore() { await using var stream = File.OpenRead(path); await importer.ImportFromSqliteAsync(stream, default); }
			var runner = new HistoricalReclassificationService(factory, TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = true }));
			await Restore();
			await Restore(); // Existing exact rows retain their tokens despite import's intermediate writes.
			(await runner.ApplyChunkAsync(Actor, request.OperationId, 100)).Applied.Should().Be(2);
			var classifications = new ClassificationService(factory, TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = true }));
			var applied = (await classifications.HistoryAsync(seed.Item, null, 1)).Items.Single();
			var manual = await classifications.AssignAsync(Actor, seed.Item, new(seed.Child, seed.Revision, applied.Id, Guid.NewGuid(), "Keep manual choice after restore"));
			(await runner.UndoChunkAsync(Actor, request.OperationId, new(seed.Revision, "Undo restored operation"), 100)).UndoConflicted.Should().Be(1);
			(await runner.ReadAsync(Actor, request.OperationId)).Undone.Should().Be(1);
			(await classifications.HistoryAsync(seed.Item, null, 1)).Items.Single().Id.Should().Be(manual.Id);
			Func<Task> stale = Restore;
			await stale.Should().ThrowAsync<InvalidOperationException>();
			(await classifications.HistoryAsync(seed.Item, null, 1)).Items.Single().Id.Should().Be(manual.Id);
		}
		finally { await target.DisposeAsync(); File.Delete(path); }
	}

	[Fact]
	public async Task HistoricalPortableRestoreStillDetectsSourceEditAwayAndBack()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		var target = new PostgresFixture(); await target.InitializeAsync();
		try
		{
			var factory = new Factory(target);
			await using (var stream = File.OpenRead(path)) { await new BackupImportService(factory, NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, default); }
			await using var db = target.CreateDbContext();
			string original = await db.ReceiptItems.Where(x => x.Id == seed.Item).Select(x => x.Description).SingleAsync();
			await db.ReceiptItems.Where(x => x.Id == seed.Item).ExecuteUpdateAsync(s => s.SetProperty(x => x.Description, "Changed then reverted"));
			await db.ReceiptItems.Where(x => x.Id == seed.Item).ExecuteUpdateAsync(s => s.SetProperty(x => x.Description, original));
			var runner = new HistoricalReclassificationService(factory, TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = true }));
			(await runner.ApplyChunkAsync(Actor, request.OperationId, 1)).Conflicted.Should().Be(1);
			(await runner.ReadItemsAsync(Actor, request.OperationId, 0, 1)).Items.Single().Reason.Should().Be("source-changed");
		}
		finally { await target.DisposeAsync(); File.Delete(path); }
	}
}
