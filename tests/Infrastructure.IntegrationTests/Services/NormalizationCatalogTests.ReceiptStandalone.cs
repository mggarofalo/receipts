using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData(ReceiptNormalizationIntentKind.Defer, NormalizationReviewState.NeedsReview, true)]
	[InlineData(ReceiptNormalizationIntentKind.Exclude, NormalizationReviewState.Excluded, false)]
	public async Task ReceiptStandalone_ExplicitDeferOrExcludeOverridesKnownMappingWithoutChangingIt(
		ReceiptNormalizationIntentKind kind, NormalizationReviewState expected, bool outstanding)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Known product");
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		var mapping = (await Service().ListMappingsAsync(Actor, Source, CancellationToken.None)).Single();

		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(kind, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), Reason: "Review later or exclude this particular purchase"))], [], CancellationToken.None);

		var item = (await ReadAsync([saved.Items.Single().Id])).Single();
		item.NormalizationReviewState.Should().Be(expected);
		if (kind == ReceiptNormalizationIntentKind.Defer)
		{
			using var provider = OfflineProvider();
			using var resolver = EntryResolver(provider);
			await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);
			var afterBackground = (await ReadAsync([item.Id])).Single();
			afterBackground.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
			afterBackground.NormalizationReviewReason.Should().Be("deferred");
			afterBackground.NormalizedDescriptionId.Should().BeNull("background reuse cannot override an explicit deferred decision");
		}
		item.Description.Should().Be(Source.Description);
		item.Quantity.Should().Be(2);
		item.UnitPrice.Should().Be(3.125m);
		item.TotalAmount.Should().Be(6.25m);
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationReviewWork.AnyAsync(x => x.ItemId == item.Id)).Should().Be(outstanding);
		(await db.NormalizationSourceMappings.SingleAsync()).Revision.Should().Be(mapping.Revision);
		(await db.NormalizationSourceMappings.SingleAsync()).TargetId.Should().Be(target.Id);
	}

	[Fact]
	public async Task ReceiptStandalone_BulkTrashPurgeCascadesOutstandingWorkAndPreservesLiveReview()
	{
		await ResetAsync();
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null), EntryItem(null)], [], CancellationToken.None);
		var removed = saved.Items[0].Id;
		var live = saved.Items[1].Id;
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationReviewWork.CountAsync()).Should().Be(2);
		// Bulk trash operations bypass SaveChanges; prove the persisted FK protects cleanup too.
		await db.ReceiptItems.Where(x => x.Id == removed).ExecuteUpdateAsync(update => update.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow));

		await new TrashService(db).PurgeAllDeletedAsync(CancellationToken.None);

		(await db.ReceiptItems.IgnoreQueryFilters().AnyAsync(x => x.Id == removed)).Should().BeFalse();
		(await db.NormalizationReviewWork.SingleAsync()).ItemId.Should().Be(live);
		(await db.ReceiptItems.SingleAsync()).NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
	}

	[Theory]
	[InlineData("manual")]
	[InlineData("template")]
	[InlineData("automatic")]
	[InlineData("reviewed")]
	public async Task ReceiptStandalone_LegacyOriginAndActiveForeignKeyAreNotVerificationEvidence(string origin)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Legacy product");
		var ids = await ItemsAsync(Source, 1, origin, target.Id);

		var item = (await ReadAsync(ids)).Single();

		item.NormalizedDescriptionId.Should().Be(target.Id, "existing associations remain useful history");
		item.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		item.NormalizationReviewDecisionId.Should().BeNull();
		item.ReviewedBy.Should().BeNull();
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationReviewWork.SingleAsync()).ItemId.Should().Be(item.Id);
	}

	[Fact]
	public async Task ReceiptStandalone_BatchReplayReturnsOriginalRowsWithoutDuplicateReviewOrAudit()
	{
		await ResetAsync();
		var parent = (await EntryWriter().CreateAsync(EntryReceipt(), [], [], [], CancellationToken.None)).Receipt;
		var service = new ReceiptItemService(new ReceiptItemRepository(new EntryActorFactory(fixture.CreateOptions(), Actor)), new ReceiptItemMapper());
		var requestId = Guid.NewGuid();
		var intent = new ReceiptNormalizationIntent(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), NewName: "Standalone reviewed product", RememberSource: true);
		var first = await service.CreateAsync([EntryItem(intent)], parent.Id, [], requestId, CancellationToken.None);
		var audits = await AuditCountAsync([first.Single().Id]);

		var replay = await service.CreateAsync([EntryItem(intent)], parent.Id, [], requestId, CancellationToken.None);

		replay.Single().Id.Should().Be(first.Single().Id);
		(await AuditCountAsync([first.Single().Id])).Should().Be(audits);
		await using var db = fixture.CreateDbContext();
		(await db.ReceiptItems.SingleAsync()).NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		(await db.NormalizedDescriptions.CountAsync()).Should().Be(1);
		(await db.NormalizationSourceMappings.CountAsync()).Should().Be(1);
		(await db.NormalizationReviewWork.CountAsync()).Should().Be(0);
	}

	[Fact]
	public async Task ReceiptStandalone_RestoringAfterMappingRevocationPreservesHistoryButRequiresReview()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Reviewed product");
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null)], [], CancellationToken.None);
		var itemId = saved.Items.Single().Id;
		var before = (await ReadAsync([itemId])).Single();
		before.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		var repository = new ReceiptItemRepository(new EntryActorFactory(fixture.CreateOptions(), Actor));
		await repository.DeleteAsync([itemId], CancellationToken.None);
		var mapping = (await Service().ListMappingsAsync(Actor, Source, CancellationToken.None)).Single();
		await Service().RevokeAsync(Actor, mapping.Id, mapping.Revision, CancellationToken.None);

		(await repository.RestoreAsync(itemId, CancellationToken.None)).Should().BeTrue();

		var restored = (await ReadAsync([itemId])).Single();
		restored.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		restored.NormalizedDescriptionId.Should().Be(target.Id);
		restored.Description.Should().Be(before.Description);
		restored.Quantity.Should().Be(before.Quantity);
		restored.UnitPrice.Should().Be(before.UnitPrice);
		restored.TotalAmount.Should().Be(before.TotalAmount);
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationReviewWork.SingleAsync(x => x.ItemId == itemId)).SourceHash.Should().Be(NormalizationCatalogPolicy.Hash(Source));
	}
}
