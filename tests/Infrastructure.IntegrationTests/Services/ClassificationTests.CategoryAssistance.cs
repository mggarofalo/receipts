using Application.Classification;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task StandaloneCreateAndUpdateEnrollExactCurrentSourceButExplicitClearDoesNot()
	{
		var seed = await SeedAsync();
		var store = new CategoryAssistanceSettingsStore(new Factory(fixture), TimeProvider.System);
		var settings = await store.GetAsync();
		await store.UpdateAsync(Actor, settings.Revision, settings with { Enabled = true, Provider = "jev", Model = "jev-1.14.0" });
		try
		{
			await using var db = fixture.CreateDbContext();
			Guid receiptId = await db.ReceiptItems.Where(x => x.Id == seed.Item).Select(x => x.ReceiptId).SingleAsync();
			var item = SampleData.Entities.ReceiptItemEntityGenerator.Generate(receiptId);
			item.Category = ""; item.Subcategory = null; item.Description = "Original source"; item.ReceiptItemCode = "A-1";
			var repository = new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture),
				Options.Create(new ClassificationOptions { WritesEnabled = true }));
			Guid create = Guid.NewGuid();
			await repository.CreateAsync([item], [], create, default);
			var first = await db.CategoryAssistanceObservations.AsNoTracking().SingleAsync(x => x.SaveId == create && x.ReceiptItemId == item.Id);
			first.AssignmentId.Should().Be((await db.CurrentClassifications.AsNoTracking().SingleAsync(x => x.ReceiptItemId == item.Id)).AssignmentId);
			first.ItemCode.Should().Be("A-1");
			item.Description = "Changed source"; item.Classification = null;
			Guid update = Guid.NewGuid();
			await repository.UpdateAsync([item], update, default);
			await repository.UpdateAsync([item], update, default);
			var second = await db.CategoryAssistanceObservations.AsNoTracking().SingleAsync(x => x.SaveId == update && x.ReceiptItemId == item.Id);
			second.Description.Should().Be("Changed source"); second.ItemRevision.Should().NotBe(first.ItemRevision);
			second.AssignmentId.Should().Be(first.AssignmentId);
			item.Classification = new(null, seed.Revision, first.AssignmentId, Guid.NewGuid(), "Explicit clear");
			await repository.UpdateAsync([item], Guid.NewGuid(), default);
			item.Classification = null; item.Description = "Still manually cleared";
			await repository.UpdateAsync([item], Guid.NewGuid(), default);
			(await db.CategoryAssistanceObservations.CountAsync(x => x.ReceiptItemId == item.Id)).Should().Be(2);
		}
		finally
		{
			var current = await store.GetAsync(); await store.UpdateAsync(Actor, current.Revision, current with { Enabled = false });
		}
	}

	[Fact]
	public async Task ConsentedNoChoiceEntryRecordsObservationFenceWhileExplicitClearDoesNot()
	{
		var seed = await SeedAsync();
		var store = new CategoryAssistanceSettingsStore(new Factory(fixture), TimeProvider.System);
		var settings = await store.GetAsync();
		settings = await store.UpdateAsync(Actor, settings.Revision, settings with { Enabled = true, Provider = "jev", Model = "jev-1.14.0" });
		try
		{
			var writer = new CompleteReceiptWriter(new ActorFactory(fixture), new ReceiptMapper(), new TransactionMapper(),
				new ReceiptItemMapper(), new AdjustmentMapper(), Options.Create(new ClassificationOptions { WritesEnabled = true }));
			var item = new Domain.Core.ReceiptItem(Guid.NewGuid(), null, "No choice entry", 1, new Domain.Money(2), new Domain.Money(2), "", null);
			var receipt = new Domain.Core.Receipt(Guid.NewGuid(), "Consent entry", new DateOnly(2020, 1, 1), new Domain.Money(0));
			Guid save = Guid.NewGuid();
			var result = await writer.CreateAsync(receipt, [], [item], [], save, default);
			await writer.CreateAsync(receipt, [], [item], [], save, default);
			Guid id = result.Items.Single().Id;
			await using var db = fixture.CreateDbContext();
			var observation = await db.CategoryAssistanceObservations.SingleAsync(x => x.ReceiptItemId == id);
			var assignment = await db.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == id);
			observation.AssignmentId.Should().Be(assignment.AssignmentId);
			(await db.Classifications.SingleAsync(x => x.Id == assignment.AssignmentId)).Source.Should().Be("legacy_write");
			(await db.CategoryAssistanceObservations.CountAsync(x => x.ReceiptItemId == id)).Should().Be(1);
			await Service().AssignAsync(Actor, id, new(null, seed.Revision, assignment.AssignmentId, Guid.NewGuid(), "Explicit clear"));
			await using var tx = await db.Database.BeginTransactionAsync();
			(await CategoryAssistanceObservationWriter.RecordNoChoiceSaveAsync(db, Actor, Guid.NewGuid(), id, false, false)).Should().BeFalse();
			await tx.CommitAsync();
		}
		finally
		{
			var current = await store.GetAsync(); await store.UpdateAsync(Actor, current.Revision, current with { Enabled = false });
		}
	}
}
