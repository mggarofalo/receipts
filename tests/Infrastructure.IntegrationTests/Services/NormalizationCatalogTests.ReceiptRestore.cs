using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData(false, "source-aba")]
	[InlineData(true, "source-aba")]
	[InlineData(false, "target-aba")]
	[InlineData(true, "target-aba")]
	[InlineData(false, "direct-sql")]
	[InlineData(true, "direct-sql")]
	public async Task ReceiptRestore_ChangedProofWhileTrashedIsNeverRevived(bool wholeReceipt, string change)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Historical independent product");
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(
			ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source),
			target.Id, target.NormalizationRevision))], [], CancellationToken.None);
		Guid itemId = saved.Items.Single().Id;
		var factory = new EntryActorFactory(fixture.CreateOptions(), Actor);
		var receipts = new ReceiptRepository(factory);
		var items = new ReceiptItemRepository(factory);
		if (wholeReceipt) { await receipts.DeleteAsync([saved.Receipt.Id], CancellationToken.None); }
		else { await items.DeleteAsync([itemId], CancellationToken.None); }
		await using (var db = factory.CreateDbContext())
		{
			if (change == "source-aba")
			{
				var row = await db.ReceiptItems.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId);
				row.Description = "Changed while trashed";
				await db.SaveChangesAsync();
				row.Description = Source.Description;
				await db.SaveChangesAsync();
			}
			else if (change == "target-aba")
			{
				var identity = await db.NormalizedDescriptions.SingleAsync(value => value.Id == target.Id);
				identity.Status = NormalizedDescriptionStatus.Rejected;
				await db.SaveChangesAsync();
				identity.Status = NormalizedDescriptionStatus.Active;
				await db.SaveChangesAsync();
			}
			else
			{
				await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"DeletedAt\" = \"DeletedAt\" + interval '1 second' WHERE \"Id\" = {itemId}");
			}
		}
		if (wholeReceipt) { (await receipts.RestoreAsync(saved.Receipt.Id, CancellationToken.None)).EntityChanged.Should().BeTrue(); }
		else { (await items.RestoreAsync(itemId, CancellationToken.None)).Should().BeTrue(); }
		var restored = (await ReadAsync([itemId])).Single();
		restored.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		restored.NormalizedDescriptionId.Should().Be(target.Id);
		await using var after = fixture.CreateDbContext();
		(await after.NormalizationReviewWork.AnyAsync(work => work.ItemId == itemId)).Should().BeTrue();
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public async Task ReceiptRestore_UnchangedIndependentReviewSurvivesTrashAndRevokedReuse(bool wholeReceipt, bool exclusion)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Independently reviewed product");
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(
			ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source),
			target.Id, target.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		Guid itemId = saved.Items.Single().Id;
		if (exclusion)
		{
			var excluded = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(
				ReceiptNormalizationIntentKind.Exclude, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source),
				Reason: "This purchase is not product spending"))], [], CancellationToken.None);
			saved = excluded;
			itemId = excluded.Items.Single().Id;
		}
		var before = (await ReadAsync([itemId])).Single();
		var mapping = (await Service().ListMappingsAsync(Actor, Source, CancellationToken.None)).Single();
		await Service().RevokeAsync(Actor, mapping.Id, mapping.Revision, CancellationToken.None);
		var factory = new EntryActorFactory(fixture.CreateOptions(), Actor);
		var receipts = new ReceiptRepository(factory);
		var items = new ReceiptItemRepository(factory);
		if (wholeReceipt) { await receipts.DeleteAsync([saved.Receipt.Id], CancellationToken.None); }
		else { await items.DeleteAsync([itemId], CancellationToken.None); }
		await using (var db = fixture.CreateDbContext())
		{
			(await db.ReceiptItems.AnyAsync(item => item.Id == itemId)).Should().BeFalse();
			(await db.NormalizationReviewWork.AnyAsync(work => work.ItemId == itemId)).Should().BeFalse();
		}
		if (wholeReceipt) { (await receipts.RestoreAsync(saved.Receipt.Id, CancellationToken.None)).EntityChanged.Should().BeTrue(); }
		else { (await items.RestoreAsync(itemId, CancellationToken.None)).Should().BeTrue(); }
		var restored = (await ReadAsync([itemId])).Single();
		restored.NormalizationReviewState.Should().Be(before.NormalizationReviewState,
			"trash changes availability, not an unchanged independent review decision");
		restored.NormalizationReviewReason.Should().Be(before.NormalizationReviewReason);
		restored.NormalizationReviewDecisionId.Should().Be(before.NormalizationReviewDecisionId);
		restored.NormalizationAssignmentId.Should().Be(before.NormalizationAssignmentId);
		restored.ReviewedSourceHash.Should().Be(before.ReviewedSourceHash);
		restored.ReviewedTargetId.Should().Be(before.ReviewedTargetId);
		restored.ReviewedBy.Should().Be(before.ReviewedBy);
		restored.ReviewedAt.Should().Be(before.ReviewedAt);
		restored.NormalizedDescriptionId.Should().Be(before.NormalizedDescriptionId);
		restored.Description.Should().Be(before.Description);
		restored.Quantity.Should().Be(before.Quantity);
		restored.UnitPrice.Should().Be(before.UnitPrice);
		restored.TotalAmount.Should().Be(before.TotalAmount);
		await using var after = fixture.CreateDbContext();
		(await after.NormalizationReviewWork.AnyAsync(work => work.ItemId == itemId)).Should().BeFalse();
	}
}
