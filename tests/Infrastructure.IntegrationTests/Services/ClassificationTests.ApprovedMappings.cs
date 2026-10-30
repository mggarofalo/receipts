using System.Text.Json;
using Application.Normalization;
using Application.Taxonomy;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	private async Task<(ReceiptEntity Receipt, ReceiptItemEntity Item, CategoryMappingCommand Command, CategoryMappingRevision Approval)> MappingEntryAsync()
	{
		var seed = await SeedAsync();
		var receipt = ReceiptEntityGenerator.Generate();
		var item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.Description = "Approved source " + Guid.NewGuid(); item.Category = ""; item.Subcategory = null;
		var source = NormalizationCatalogPolicy.Normalize(new(receipt.Location, item.Description, item.ReceiptItemCode));
		CategoryMappingCommand command = new(source, NormalizationCatalogPolicy.Hash(source), null, seed.Revision,
			seed.Child, Guid.NewGuid(), "Reviewed exact source");
		var approval = await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor, command);
		return (receipt, item, command, approval);
	}
	private CompleteReceiptWriter MappingWriter(IDbContextFactory<ApplicationDbContext>? factory = null, bool enabled = true)
		=> new(factory ?? new ActorFactory(fixture), new Infrastructure.Mapping.ReceiptMapper(),
			new Infrastructure.Mapping.TransactionMapper(), new Infrastructure.Mapping.ReceiptItemMapper(),
			new Infrastructure.Mapping.AdjustmentMapper(), Options.Create(new ClassificationOptions { WritesEnabled = enabled }));

	[Fact]
	public async Task ApprovedMappingUsesCurrentRevisionRetainsApprovalAndReplaysAfterRevocation()
	{
		var entry = await MappingEntryAsync();
		var renamed = await Tree().ChangeAsync(Actor, new(entry.Command.ExpectedTaxonomyRevisionId, Guid.NewGuid(), "Rename",
			[new TaxonomyEdit("rename", entry.Command.CategoryId!.Value, Name: "Renamed approved category")]));
		var receipt = new Infrastructure.Mapping.ReceiptMapper().ToDomain(entry.Receipt);
		var item = new Infrastructure.Mapping.ReceiptItemMapper().ToDomain(entry.Item);
		Guid request = Guid.NewGuid();
		var saved = await MappingWriter().CreateAsync(receipt, [], [item], [], request, default);
		var assigned = (await Service().HistoryAsync(saved.Items.Single().Id, null, 10)).Items.Single();
		assigned.Source.Should().Be("approved_mapping"); assigned.CategoryId.Should().Be(entry.Command.CategoryId);
		assigned.TaxonomyRevisionId.Should().Be(renamed.Id);
		JsonSerializer.Deserialize<CategoryMappingReuseEvidence>(assigned.Reason).Should().Be(
			new CategoryMappingReuseEvidence(entry.Approval.Id, entry.Approval.TaxonomyRevisionId, entry.Command.SourceFingerprint));
		await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor,
			entry.Command with { ExpectedMappingRevisionId = entry.Approval.Id, ExpectedTaxonomyRevisionId = renamed.Id, CategoryId = null, OperationId = Guid.NewGuid() });
		var replay = await MappingWriter().CreateAsync(receipt, [], [item], [], request, default);
		replay.Items.Single().Id.Should().Be(saved.Items.Single().Id);
		(await Service().HistoryAsync(saved.Items.Single().Id, null, 10)).Items.Should().ContainSingle().Which.Should().BeEquivalentTo(assigned);
		await using var db = fixture.CreateDbContext();
		(await db.ClassificationOperations.SingleAsync(row => row.AssignmentId == assigned.Id)).OperationId.Should().Be(assigned.OperationId);
		var stored = await db.ReceiptItems.AsNoTracking().SingleAsync(row => row.Id == saved.Items.Single().Id);
		stored.Description = "Incidental source edit"; stored.UnitPrice += 1;
		await new ReceiptItemRepository(new ActorFactory(fixture)).UpdateAsync([stored], default);
		(await Service().HistoryAsync(stored.Id, null, 10)).Items.Should().ContainSingle().Which.Id.Should().Be(assigned.Id);
	}

	[Fact]
	public async Task MappingAssignmentAndOriginalApprovalRoundTripThroughV12()
	{
		var entry = await MappingEntryAsync();
		var saved = await MappingWriter().CreateAsync(new Infrastructure.Mapping.ReceiptMapper().ToDomain(entry.Receipt), [],
			[new Infrastructure.Mapping.ReceiptItemMapper().ToDomain(entry.Item)], [], Guid.NewGuid(), default);
		var original = (await Service().HistoryAsync(saved.Items.Single().Id, null, 10)).Items.Single();
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		var target = new PostgresFixture();
		try
		{
			(await new OfflinePortableBackupInspection().InspectAsync(path)).FormatVersion.Should().Be(15);
			await target.InitializeAsync();
			await using var input = File.OpenRead(path);
			await new BackupImportService(new Factory(target), NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(input, default);
			var restored = new ClassificationService(new Factory(target), TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = true }));
			(await restored.HistoryAsync(original.ReceiptItemId, null, 10)).Items.Should().ContainSingle().Which.Should().BeEquivalentTo(original);
			await using var db = target.CreateDbContext();
			(await db.CategoryMappingRevisions.SingleAsync(row => row.Id == entry.Approval.Id)).TaxonomyRevisionId.Should().Be(entry.Approval.TaxonomyRevisionId);
			(await db.ClassificationOperations.SingleAsync(row => row.AssignmentId == original.Id)).OperationId.Should().Be(original.OperationId);
		}
		finally { await target.DisposeAsync(); File.Delete(path); }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task IncompleteExistingBackfillFallsBackWithoutBlockingReceiptSave(bool inTrash)
	{
		var entry = await MappingEntryAsync();
		await using var db = fixture.CreateDbContext();
		var parent = ReceiptEntityGenerator.Generate();
		var orphan = ReceiptItemEntityGenerator.Generate(parent.Id);
		orphan.Category = ""; orphan.Subcategory = null;
		db.Receipts.Add(parent); db.ReceiptItems.Add(orphan); await db.SaveChangesAsync();
		if (inTrash)
		{
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"DeletedAt\" = {DateTimeOffset.UtcNow} WHERE \"Id\" = {orphan.Id}");
		}

		try
		{
			(await db.LegacyMigrations.AnyAsync()).Should().BeTrue();
			(await db.CurrentClassifications.AnyAsync(row => row.ReceiptItemId == orphan.Id)).Should().BeFalse();
			var saved = await MappingWriter().CreateAsync(new Infrastructure.Mapping.ReceiptMapper().ToDomain(entry.Receipt), [],
				[new Infrastructure.Mapping.ReceiptItemMapper().ToDomain(entry.Item)], [], Guid.NewGuid(), default);
			var assignment = (await Service().HistoryAsync(saved.Items.Single().Id, null, 10)).Items.Single();
			assignment.Source.Should().Be("legacy_write"); assignment.CategoryId.Should().BeNull();
			(await db.ReceiptItems.AnyAsync(row => row.Id == saved.Items.Single().Id)).Should().BeTrue();
			(await db.ClassificationOperations.AnyAsync(row => row.AssignmentId == assignment.Id)).Should().BeFalse();
		}
		finally
		{
			// Restore fixture readiness through the compatibility writer for subsequent cases.
			if (inTrash)
			{
				await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"DeletedAt\" = NULL WHERE \"Id\" = {orphan.Id}");
			}

			orphan.Category = await db.TaxonomyNodes.Where(row => row.RevisionId == entry.Approval.TaxonomyRevisionId
				&& row.ParentId == null && !row.Archived).Select(row => row.Name).FirstAsync();
			await new ReceiptItemRepository(new ActorFactory(fixture)).UpdateAsync([orphan], default);
		}
	}

	[Theory]
	[InlineData("revoked")]
	[InlineData("stale")]
	[InlineData("source")]
	[InlineData("disabled")]
	[InlineData("legacy")]
	[InlineData("clear")]
	public async Task UnavailableMappingAndExplicitEvidenceNeverGuess(string scenario)
	{
		var entry = await MappingEntryAsync();
		Guid revision = entry.Command.ExpectedTaxonomyRevisionId;
		if (scenario == "revoked")
		{
			await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor,
				entry.Command with { ExpectedMappingRevisionId = entry.Approval.Id, CategoryId = null, OperationId = Guid.NewGuid() });
		}

		if (scenario == "stale")
		{
			revision = (await Tree().ChangeAsync(Actor, new(revision, Guid.NewGuid(), "Meaning changed",
				[new TaxonomyEdit("define", entry.Command.CategoryId!.Value, Definition: "New meaning")]))).Id;
		}

		if (scenario == "source") { entry.Item.Description += " different"; }
		if (scenario == "legacy") { entry.Item.Category = "Ambiguous legacy evidence"; }
		var item = new Infrastructure.Mapping.ReceiptItemMapper().ToDomain(entry.Item);
		if (scenario == "clear") { item.Classification = new(null, revision, null, Guid.NewGuid(), "Deliberate clear"); }
		var saved = await MappingWriter(enabled: scenario != "disabled").CreateAsync(
			new Infrastructure.Mapping.ReceiptMapper().ToDomain(entry.Receipt), [], [item], [], Guid.NewGuid(), default);
		var assignment = (await Service().HistoryAsync(saved.Items.Single().Id, null, 10)).Items.Single();
		assignment.CategoryId.Should().BeNull(); assignment.Source.Should().Be(scenario == "clear" ? "manual" : "legacy_write");
		if (scenario == "clear")
		{
			await using var db = fixture.CreateDbContext();
			var stored = await db.ReceiptItems.AsNoTracking().SingleAsync(row => row.Id == saved.Items.Single().Id);
			stored.Description += " edited";
			await new ReceiptItemRepository(new ActorFactory(fixture), Options.Create(new ClassificationOptions { WritesEnabled = true })).UpdateAsync([stored], default);
			(await Service().HistoryAsync(stored.Id, null, 10)).Items.Should().ContainSingle().Which.Id.Should().Be(assignment.Id);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MappingAndReceiptAreAtomicAndSerializeWithRevocation(bool failCommit)
	{
		var entry = await MappingEntryAsync();
		var receipt = new Infrastructure.Mapping.ReceiptMapper().ToDomain(entry.Receipt);
		var item = new Infrastructure.Mapping.ReceiptItemMapper().ToDomain(entry.Item);
		await using var db = fixture.CreateDbContext();
		int count = await db.Classifications.CountAsync();
		if (failCommit)
		{
			Func<Task> save = async () => { await MappingWriter(new FailureFactory(fixture)).CreateAsync(receipt, [], [item], [], Guid.NewGuid(), default); };
			await save.Should().ThrowAsync<InvalidOperationException>();
			(await db.Classifications.CountAsync()).Should().Be(count);
			(await db.ReceiptItems.AnyAsync(row => row.Description == item.Description)).Should().BeFalse();
			return;
		}
		var barrier = new CommitBarrier();
		var revocation = new CategoryMappingService(new BarrierFactory(fixture, barrier), TimeProvider.System).ChangeAsync(Actor,
			entry.Command with { ExpectedMappingRevisionId = entry.Approval.Id, CategoryId = null, OperationId = Guid.NewGuid() });
		int blocker = await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
		var pending = MappingWriter().CreateAsync(receipt, [], [item], [], Guid.NewGuid(), default);
		try { await WaitForDatabaseBlockAsync(blocker); pending.IsCompleted.Should().BeFalse(); }
		finally { barrier.Release.TrySetResult(); }
		await revocation;
		var saved = await pending;
		var assignment = (await Service().HistoryAsync(saved.Items.Single().Id, null, 10)).Items.Single();
		assignment.Source.Should().Be("legacy_write"); assignment.CategoryId.Should().BeNull();
	}
}
