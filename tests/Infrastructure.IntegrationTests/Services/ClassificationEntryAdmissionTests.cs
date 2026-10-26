using System.Text.Json;
using API.Generated.Dtos;
using API.Validators;
using Application.Taxonomy;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task EntryWirePayloadsPersistNoChoiceClearRootParentAndDeepChildWithStableReplay()
	{
		var seed = await SeedAsync();
		var revision = await Tree().ChangeAsync(Actor, new(seed.Revision, Guid.NewGuid(), "Deep entry fixture",
			[new TaxonomyEdit("create", Guid.NewGuid(), seed.Child, "Deep child")]));
		var deep = revision.Nodes.Single(x => x.Name == "Deep child");
		string rootName = revision.Nodes.Single(x => x.Id == seed.Root).Name;
		var factory = new ActorFactory(fixture);
		var receiptMapper = new Infrastructure.Mapping.ReceiptMapper();
		var itemMapper = new Infrastructure.Mapping.ReceiptItemMapper();
		var writer = new CompleteReceiptWriter(factory, receiptMapper,
			new Infrastructure.Mapping.TransactionMapper(), itemMapper,
			new Infrastructure.Mapping.AdjustmentMapper(),
			Options.Create(new ClassificationOptions { WritesEnabled = true }));
		var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
		foreach (string choice in new[] { "no-choice", "clear", "root", "parent", "deep" })
		{
			Guid? categoryId = choice switch { "root" => seed.Root, "parent" => seed.Child, "deep" => deep.Id, _ => null };
			var payload = new
			{
				description = "Uncertain entry " + choice,
				quantity = 2,
				unitPrice = 3.5,
				category = categoryId.HasValue ? rootName : "",
				subcategory = choice is "parent" or "deep" ? "Child" : null,
				classification = choice == "no-choice" ? null : new
				{
					categoryId,
					taxonomyRevisionId = revision.Id,
					expectedAssignmentId = (Guid?)null,
					operationId = Guid.NewGuid(),
					reason = "Entry selection"
				}
			};
			// Exercise the real request DTO, validator, API mapper, domain admission,
			// aggregate writer, PostgreSQL and classification service together.
			var wire = JsonSerializer.SerializeToNode(payload)!;
			if (choice == "no-choice")
			{
				wire.AsObject().Remove("classification");
			}

			var request = wire.Deserialize<CreateReceiptItemRequest>(jsonOptions)!;
			new CreateReceiptItemRequestValidator().Validate(request).IsValid.Should().BeTrue(choice);
			var item = new API.Mapping.Core.ReceiptItemMapper().ToDomain(request);
			var receipt = new Domain.Core.Receipt(Guid.NewGuid(), "Entry admission", new DateOnly(2020, 1, 1), new Domain.Money(0));
			Guid requestId = Guid.NewGuid();
			var saved = await writer.CreateAsync(receipt, [], [item], [], requestId, default);
			var replay = await writer.CreateAsync(receipt, [], [item], [], requestId, default);
			Guid itemId = saved.Items.Single().Id;
			replay.Items.Single().Id.Should().Be(itemId);
			await using var db = fixture.CreateDbContext();
			var stored = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == itemId);
			stored.Category.Should().Be(payload.category);
			stored.Subcategory.Should().Be(payload.subcategory);
			stored.TotalAmount.Should().Be(7);
			var history = await Service().HistoryAsync(itemId, null, 10);
			history.Items.Should().ContainSingle().Which.CategoryId.Should().Be(categoryId);
			if (choice != "no-choice")
			{
				history.Items.Single().Source.Should().Be("manual");
			}
			// A facts-only edit must preserve the exact opening assignment.
			var update = JsonSerializer.Deserialize<UpdateReceiptItemRequest>(JsonSerializer.Serialize(new
			{
				id = itemId,
				payload.category,
				payload.subcategory,
				description = "Edited " + choice,
				payload.quantity,
				payload.unitPrice
			}), jsonOptions)!;
			new UpdateReceiptItemRequestValidator().Validate(update).IsValid.Should().BeTrue();
			var edited = new API.Mapping.Core.ReceiptItemMapper().ToDomain(update);
			edited.ReceiptId = saved.Receipt.Id;
			await new ReceiptItemRepository(factory).UpdateAsync([itemMapper.ToEntity(edited)], default);
			(await Service().HistoryAsync(itemId, null, 10)).Items.Should().BeEquivalentTo(history.Items);
			(await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == itemId)).Description.Should().Be("Edited " + choice);
			update.Category = "";
			update.Subcategory = null;
			update.Classification = new ClassificationRequest
			{
				CategoryId = null,
				TaxonomyRevisionId = revision.Id,
				ExpectedAssignmentId = history.Items.Single().Id,
				OperationId = Guid.NewGuid(),
				Reason = "Explicit Unclassified edit"
			};
			new UpdateReceiptItemRequestValidator().Validate(update).IsValid.Should().BeTrue();
			var cleared = new API.Mapping.Core.ReceiptItemMapper().ToDomain(update);
			cleared.ReceiptId = saved.Receipt.Id;
			var clearRequest = Guid.NewGuid();
			var clearRepository = new ReceiptItemRepository(factory, Options.Create(new ClassificationOptions { WritesEnabled = true }));
			await clearRepository.UpdateAsync([itemMapper.ToEntity(cleared)], clearRequest, default);
			await clearRepository.UpdateAsync([itemMapper.ToEntity(cleared)], clearRequest, default);
			var clearedHistory = await Service().HistoryAsync(itemId, null, 10);
			clearedHistory.Items.Should().HaveCount(2);
			clearedHistory.Items.First().CategoryId.Should().BeNull();
			clearedHistory.Items.First().PreviousAssignmentId.Should().Be(history.Items.Single().Id);
			var clearedStored = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == itemId);
			clearedStored.Category.Should().BeEmpty();
			clearedStored.Subcategory.Should().BeNull();
		}
	}
}
