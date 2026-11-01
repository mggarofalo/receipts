using Application.Models;
using Application.Taxonomy;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task ReceiptItemSearch_UsesRecordedAncestorsAndLeaves_WithStableAssignments()
	{
		var first = await SeedAsync();
		var second = await SeedAsync();
		string leafName = "Recorded-" + Guid.NewGuid().ToString("N");
		string staleLegacy = "Stale-" + Guid.NewGuid().ToString("N");
		Guid firstLeaf = Guid.NewGuid(), secondLeaf = Guid.NewGuid();
		TaxonomyRevision revision = await Tree().ChangeAsync(Actor, new(second.Revision,
			Guid.NewGuid(), "Create equal leaves in separate branches",
			[new TaxonomyEdit("create", firstLeaf, first.Child, leafName),
			 new TaxonomyEdit("create", secondLeaf, second.Child, leafName)]));

		PurchaseClassification firstAssignment = await Service().AssignAsync(Actor, first.Item,
			new(firstLeaf, revision.Id, first.Assignment, Guid.NewGuid(), "First leaf"));
		await Service().AssignAsync(Actor, second.Item,
			new(secondLeaf, revision.Id, second.Assignment, Guid.NewGuid(), "Second leaf"));
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			ReceiptItemEntity item = await db.ReceiptItems.SingleAsync(x => x.Id == first.Item);
			item.Category = staleLegacy;
			await db.SaveChangesAsync();
		}

		ReceiptItemRepository repository = new(new Factory(fixture));
		(await repository.GetCountAsync(leafName, default)).Should().Be(2);
		(await repository.GetAllAsync(0, 1, SortParams.Default, leafName, default))
			.Should().ContainSingle();
		(await repository.GetAllAsync(1, 1, SortParams.Default, leafName, default))
			.Should().ContainSingle();
		(await repository.GetAllAsync(0, 20, SortParams.Default, staleLegacy, default))
			.Should().BeEmpty();

		string firstRootName = revision.Nodes.Single(node => node.Id == first.Root).Name;
		(await repository.GetAllAsync(0, 20, SortParams.Default, firstRootName, default))
			.Select(x => x.Id).Should().Contain(first.Item).And.NotContain(second.Item);

		TaxonomyRevision renamed = await Tree().ChangeAsync(Actor, new(revision.Id,
			Guid.NewGuid(), "Rename recorded leaf",
			[new TaxonomyEdit("rename", firstLeaf, Name: "New-" + Guid.NewGuid().ToString("N"))]));
		TaxonomyRevision moved = await Tree().ChangeAsync(Actor, new(renamed.Id, Guid.NewGuid(), "Move recorded leaf",
			[new TaxonomyEdit("move", firstLeaf, second.Child)]));
		await Tree().ChangeAsync(Actor, new(moved.Id, Guid.NewGuid(), "Archive moved leaf",
			[new TaxonomyEdit("archive", firstLeaf)]));
		(await repository.GetCountAsync(leafName, default)).Should().Be(2);

		await Service().AssignAsync(Actor, first.Item,
			new(null, (await Tree().ReadAsync(null))!.Id, firstAssignment.Id,
				Guid.NewGuid(), "Manual clear"));
		(await repository.GetAllAsync(0, 20, SortParams.Default, leafName, default))
			.Select(x => x.Id).Should().Equal(second.Item);
		(await repository.GetAllAsync(0, 20, SortParams.Default, staleLegacy, default))
			.Should().BeEmpty();
	}

	[Fact]
	public async Task ReceiptItemSearch_IgnoresLegacyLabelsOfUnassignedItems_AndEscapesWildcards()
	{
		var seed = await SeedAsync();
		string label = "Legacy-" + Guid.NewGuid().ToString("N");
		string description = "Wild%_\\" + Guid.NewGuid().ToString("N");
		Guid legacyItemId = Guid.NewGuid();
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			Guid receiptId = await db.ReceiptItems.Where(x => x.Id == seed.Item)
				.Select(x => x.ReceiptId).SingleAsync();
			ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receiptId);
			item.Id = legacyItemId;
			item.Description = description;
			item.ReceiptItemCode = null;
			item.Category = label;
			item.Subcategory = label + "-sub";
			db.ReceiptItems.Add(item);
			await db.SaveChangesAsync();
		}

		try
		{
			ReceiptItemRepository repository = new(new Factory(fixture));
			// No current assignment: the legacy label is never searched.
			(await repository.GetAllAsync(0, 20, SortParams.Default, label, default))
					.Should().BeEmpty();
			(await repository.GetCountAsync(label, default)).Should().Be(0);
			(await repository.GetAllAsync(0, 20, SortParams.Default, label + "-sub", default))
					.Should().BeEmpty();
			(await repository.GetAllAsync(0, 20, SortParams.Default, description, default))
					.Select(x => x.Id).Should().Equal(legacyItemId);
			(await repository.GetAllAsync(0, 20, SortParams.Default, "Wild%X", default))
					.Should().BeEmpty();
		}
		finally
		{
			// This fixture is shared by ClassificationTests. Do not leave an
			// intentionally unassigned item for later readiness checks.
			await using ApplicationDbContext db = fixture.CreateDbContext();
			await db.ReceiptItems.Where(x => x.Id == legacyItemId).ExecuteDeleteAsync();
		}
	}
}
