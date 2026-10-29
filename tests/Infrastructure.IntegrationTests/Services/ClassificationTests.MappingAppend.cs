using System.Text.Json;
using Application.Normalization;
using Application.Taxonomy;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task HistoricalMappingAppendValidatesEvidenceAndReplaysWithoutChangingManualSemantics()
	{
		var seed = await SeedAsync();
		await using var read = fixture.CreateDbContext();
		var item = await read.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
		item.Description = "Mapping append " + Guid.NewGuid();
		await read.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"Description\" = {item.Description} WHERE \"Id\" = {seed.Item}");
		string location = await read.Receipts.Where(x => x.Id == item.ReceiptId).Select(x => x.Location).SingleAsync();
		var source = NormalizationCatalogPolicy.Normalize(new(location, item.Description, item.ReceiptItemCode));
		var fingerprint = NormalizationCatalogPolicy.Hash(source);
		var command = new CategoryMappingCommand(source, fingerprint, null, seed.Revision, seed.Child, Guid.NewGuid(), "Approved");
		var approval = await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor, command);
		var evidence = new CategoryMappingReuseEvidence(approval.Id, approval.TaxonomyRevisionId, fingerprint);
		Guid operation = Guid.NewGuid();

		async Task<PurchaseClassification> Append(CategoryMappingReuseEvidence supplied, Guid operationId, Guid category, Guid? expected = null)
		{
			await using var db = fixture.CreateDbContext();
			await using var transaction = await db.Database.BeginTransactionAsync();
			var result = await ClassificationService.AssignApprovedMappingInTransactionAsync(db, Actor, seed.Item, category,
				seed.Revision, expected ?? seed.Assignment, operationId, supplied, true, TimeProvider.System, default);
			await transaction.CommitAsync();
			return result;
		}

		foreach (var invalid in new[] { evidence with { MappingRevisionId = Guid.NewGuid() },
			evidence with { ApprovalTaxonomyRevisionId = Guid.NewGuid() }, evidence with { SourceFingerprint = "wrong" } })
		{
			Func<Task> attempt = () => Append(invalid, Guid.NewGuid(), seed.Child);
			(await attempt.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		}
		Func<Task> wrongCategory = () => Append(evidence, Guid.NewGuid(), seed.Root);
		(await wrongCategory.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		Func<Task> malformed = () => Append(evidence with { MappingRevisionId = Guid.Empty }, Guid.NewGuid(), seed.Child);
		(await malformed.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(400);

		await using (var db = fixture.CreateDbContext())
		{
			Func<Task> withoutTransaction = () => ClassificationService.AssignApprovedMappingInTransactionAsync(db, Actor, seed.Item,
				seed.Child, seed.Revision, seed.Assignment, Guid.NewGuid(), evidence, true, TimeProvider.System, default);
			await withoutTransaction.Should().ThrowAsync<InvalidOperationException>();
		}

		// An append remains inside the caller's transaction, so a failed historical outcome rolls it back.
		await using (var db = fixture.CreateDbContext())
		{
			await using var transaction = await db.Database.BeginTransactionAsync();
			await ClassificationService.AssignApprovedMappingInTransactionAsync(db, Actor, seed.Item, seed.Child,
				seed.Revision, seed.Assignment, Guid.NewGuid(), evidence, true, TimeProvider.System, default);
			await transaction.RollbackAsync();
		}
		(await Service().HistoryAsync(seed.Item, null, 10)).Items.Should().ContainSingle();

		var assigned = await Append(evidence, operation, seed.Child);
		assigned.Source.Should().Be("approved_mapping");
		assigned.OperationId.Should().Be(operation);
		assigned.PreviousAssignmentId.Should().Be(seed.Assignment);
		JsonSerializer.Deserialize<CategoryMappingReuseEvidence>(assigned.Reason).Should().Be(evidence);
		await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor,
			command with { ExpectedMappingRevisionId = approval.Id, CategoryId = null, OperationId = Guid.NewGuid() });
		(await Append(evidence, operation, seed.Child)).Should().BeEquivalentTo(assigned);
		Func<Task> revoked = () => Append(evidence, Guid.NewGuid(), seed.Child, assigned.Id);
		(await revoked.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		Func<Task> changed = () => Append(evidence with { MappingRevisionId = Guid.NewGuid() }, operation, seed.Child);
		(await changed.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		Func<Task> manualCollision = () => Service().AssignAsync(Actor, seed.Item,
			new(seed.Child, seed.Revision, seed.Assignment, operation, assigned.Reason));
		(await manualCollision.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		var manual = await Service().AssignAsync(Actor, seed.Item, new(null, seed.Revision, assigned.Id, Guid.NewGuid(), "Manual clear"));
		manual.Source.Should().Be("manual");
		manual.Reason.Should().Be("Manual clear");
		(await Service().HistoryAsync(seed.Item, null, 10)).Items.Should().HaveCount(3);
	}
	[Fact]
	public async Task MappingAppendRejectsStaleTrackedSourceCurrentAndTaxonomy()
	{
		var seed = await SeedAsync();
		await using var setup = fixture.CreateDbContext();
		var item = await setup.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
		item.Description = "Mapping append " + Guid.NewGuid();
		await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"Description\" = {item.Description} WHERE \"Id\" = {seed.Item}");
		string location = await setup.Receipts.Where(x => x.Id == item.ReceiptId).Select(x => x.Location).SingleAsync();
		var source = NormalizationCatalogPolicy.Normalize(new(location, item.Description, item.ReceiptItemCode));
		var fingerprint = NormalizationCatalogPolicy.Hash(source);
		var command = new CategoryMappingCommand(source, fingerprint, null, seed.Revision, seed.Child, Guid.NewGuid(), "Approved");
		var approval = await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor, command);
		var evidence = new CategoryMappingReuseEvidence(approval.Id, approval.TaxonomyRevisionId, fingerprint);

		async Task Attempt(ApplicationDbContext db, Guid expected)
		{
			await using var transaction = await db.Database.BeginTransactionAsync();
			await ClassificationService.AssignApprovedMappingInTransactionAsync(db, Actor, seed.Item, seed.Child,
				seed.Revision, expected, Guid.NewGuid(), evidence, true, TimeProvider.System, default);
			await transaction.CommitAsync();
		}
		await using (var stale = fixture.CreateDbContext())
		{
			await stale.ReceiptItems.SingleAsync(x => x.Id == seed.Item);
			string changed = item.Description + " changed";
			await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"Description\" = {changed} WHERE \"Id\" = {seed.Item}");
			try
			{
				Func<Task> attempt = () => Attempt(stale, seed.Assignment);
				(await attempt.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
			}
			finally
			{
				await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"Description\" = {item.Description} WHERE \"Id\" = {seed.Item}");
			}
		}
		await using (var stale = fixture.CreateDbContext())
		{
			await stale.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == seed.Item);
			var manual = await Service().AssignAsync(Actor, seed.Item, new(seed.Root, seed.Revision, seed.Assignment, Guid.NewGuid(), "Manual"));
			Func<Task> attempt = () => Attempt(stale, seed.Assignment);
			(await attempt.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
			(await Service().HistoryAsync(seed.Item, null, 1)).Items.Single().Id.Should().Be(manual.Id);
		}
		await using (var stale = fixture.CreateDbContext())
		{
			await stale.TaxonomyHeads.SingleAsync();
			var current = (await Service().HistoryAsync(seed.Item, null, 1)).Items.Single();
			await Tree().ChangeAsync(Actor, new(seed.Revision, Guid.NewGuid(), "Rename", [new TaxonomyEdit("rename", seed.Child, Name: "Renamed")]));
			Func<Task> attempt = () => Attempt(stale, current.Id);
			(await attempt.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		}
	}

	[Theory]
	[InlineData("description")]
	[InlineData("code")]
	[InlineData("location")]
	[InlineData("current")]
	public async Task MappingAppendRejectsPendingTrackedChangesWithoutDiscardingThem(string changed)
	{
		var seed = await SeedAsync();
		await using var setup = fixture.CreateDbContext();
		var item = await setup.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
		item.Description = "Mapping pending " + Guid.NewGuid();
		await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"Description\" = {item.Description} WHERE \"Id\" = {seed.Item}");
		string location = await setup.Receipts.Where(x => x.Id == item.ReceiptId).Select(x => x.Location).SingleAsync();
		var source = NormalizationCatalogPolicy.Normalize(new(location, item.Description, item.ReceiptItemCode));
		var fingerprint = NormalizationCatalogPolicy.Hash(source);
		var command = new CategoryMappingCommand(source, fingerprint, null, seed.Revision, seed.Child, Guid.NewGuid(), "Approved");
		var approval = await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor, command);
		var evidence = new CategoryMappingReuseEvidence(approval.Id, approval.TaxonomyRevisionId, fingerprint);
		await using var db = fixture.CreateDbContext();
		var trackedItem = await db.ReceiptItems.SingleAsync(x => x.Id == seed.Item);
		var trackedReceipt = await db.Receipts.SingleAsync(x => x.Id == item.ReceiptId);
		var trackedCurrent = await db.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == seed.Item);
		Guid pendingAssignment = Guid.NewGuid();
		switch (changed)
		{
			case "description": trackedItem.Description += " pending"; break;
			case "code": trackedItem.ReceiptItemCode = "pending-code"; break;
			case "location": trackedReceipt.Location += " pending"; break;
			case "current": trackedCurrent.AssignmentId = pendingAssignment; break;
		}
		await using var transaction = await db.Database.BeginTransactionAsync();
		Func<Task> attempt = () => ClassificationService.AssignApprovedMappingInTransactionAsync(db, Actor, seed.Item, seed.Child,
			seed.Revision, seed.Assignment, Guid.NewGuid(), evidence, true, TimeProvider.System, default);
		(await attempt.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		switch (changed)
		{
			case "description": trackedItem.Description.Should().Be(item.Description + " pending"); break;
			case "code": trackedItem.ReceiptItemCode.Should().Be("pending-code"); break;
			case "location": trackedReceipt.Location.Should().Be(location + " pending"); break;
			case "current": trackedCurrent.AssignmentId.Should().Be(pendingAssignment); break;
		}
		await transaction.RollbackAsync();
		(await Service().HistoryAsync(seed.Item, null, 10)).Items.Should().ContainSingle().Which.Id.Should().Be(seed.Assignment);
		(await setup.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item)).Description.Should().Be(item.Description);
		(await setup.Receipts.AsNoTracking().SingleAsync(x => x.Id == item.ReceiptId)).Location.Should().Be(location);
	}

}
