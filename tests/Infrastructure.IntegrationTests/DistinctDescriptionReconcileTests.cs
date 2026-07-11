using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// SaveChangesAsync reconciles the matching.DistinctDescriptions table so a row exists iff at
/// least one active ReceiptItem carries that description. These tests pin the reconcile semantics
/// against a real Postgres instance: the batched set-based reconcile must leave the table in the
/// same state as the old per-description loop, and it must NOT reconcile a description whose
/// active-item membership did not change (a metadata-only edit).
/// Descriptions are made unique per test so the shared fixture container does not cross-contaminate.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DistinctDescriptionReconcileTests(PostgresFixture fixture)
{
	[Fact]
	public async Task Reconcile_AddingActiveReceiptItem_InsertsDistinctDescription()
	{
		string description = $"Widget_{Guid.NewGuid():N}";

		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();

		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.Description = description;

		// Act
		context.ReceiptItems.Add(item);
		await context.SaveChangesAsync();

		// Assert
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		bool exists = await readContext.DistinctDescriptions.AnyAsync(d => d.Description == description);
		exists.Should().BeTrue("a DistinctDescriptions row is created when an active ReceiptItem uses the description");
	}

	[Fact]
	public async Task Reconcile_ChangingDescription_MovesDistinctDescriptionRow()
	{
		string oldDescription = $"Widget_{Guid.NewGuid():N}";
		string newDescription = $"Gadget_{Guid.NewGuid():N}";

		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();

		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.Description = oldDescription;
		context.ReceiptItems.Add(item);
		await context.SaveChangesAsync();

		// Act — rename the only item carrying oldDescription
		item.Description = newDescription;
		await context.SaveChangesAsync();

		// Assert — old description has no active items left, new one gained one
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		bool oldExists = await readContext.DistinctDescriptions.AnyAsync(d => d.Description == oldDescription);
		bool newExists = await readContext.DistinctDescriptions.AnyAsync(d => d.Description == newDescription);

		oldExists.Should().BeFalse("the old description no longer has any active ReceiptItem");
		newExists.Should().BeTrue("the new description now has an active ReceiptItem");
	}

	[Fact]
	public async Task Reconcile_SoftDeletingItem_RemovesDistinctDescription()
	{
		// Guards the DeletedAt-change branch: soft-deleting the last active item for a description
		// flips its active state, so the reconcile must still run and drop the row.
		string description = $"Widget_{Guid.NewGuid():N}";

		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();

		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.Description = description;
		context.ReceiptItems.Add(item);
		await context.SaveChangesAsync();

		// Sanity — the row exists before the soft delete
		(await context.DistinctDescriptions.AnyAsync(d => d.Description == description)).Should().BeTrue();

		// Act — soft-delete the item (goes through HandleSoftDelete -> Modified with DeletedAt set)
		context.ReceiptItems.Remove(item);
		await context.SaveChangesAsync();

		// Assert
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		bool exists = await readContext.DistinctDescriptions.AnyAsync(d => d.Description == description);
		exists.Should().BeFalse("soft-deleting the last active item must drop its DistinctDescriptions row");
	}

	[Fact]
	public async Task Reconcile_MetadataOnlyChange_DoesNotReconcileUnchangedDescription()
	{
		// Proves the waste-elimination: a Modified item whose Description and active state are
		// unchanged (only Quantity edited) must NOT trigger a reconcile for that description.
		// We first delete the DistinctDescriptions row out from under an active item; if the edit
		// reconciled the unchanged description (old behaviour), the row would be re-inserted.
		string description = $"Widget_{Guid.NewGuid():N}";

		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		context.Receipts.Add(receipt);
		await context.SaveChangesAsync();

		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.Description = description;
		context.ReceiptItems.Add(item);
		await context.SaveChangesAsync();

		// The active item created the row; delete it directly to detect any spurious reconcile.
		await context.Database.ExecuteSqlRawAsync(
			"""DELETE FROM "matching"."DistinctDescriptions" WHERE "Description" = {0};""",
			description);
		(await context.DistinctDescriptions.AnyAsync(d => d.Description == description)).Should().BeFalse();

		// Act — change ONLY the quantity; description and active state are unchanged
		item.Quantity = 7.5m;
		await context.SaveChangesAsync();

		// Assert — the row stays absent, proving the unchanged description was not reconciled
		await using ApplicationDbContext readContext = fixture.CreateDbContext();
		bool exists = await readContext.DistinctDescriptions.AnyAsync(d => d.Description == description);
		exists.Should().BeFalse("a metadata-only edit must not reconcile a description whose active membership did not change");
	}
}
