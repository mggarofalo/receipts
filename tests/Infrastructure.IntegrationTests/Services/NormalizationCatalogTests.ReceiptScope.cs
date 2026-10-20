using Application.Normalization;
using Domain;
using Domain.Core;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task ReceiptScope_ChangedIdentityCannotInheritReviewOfAnotherTarget()
	{
		await ResetAsync();
		var first = await CanonicalAsync("First identity");
		var second = await CanonicalAsync("Different identity");
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), first.Id, first.NormalizationRevision))], [], CancellationToken.None);
		await using (var change = new EntryActorFactory(fixture.CreateOptions(), Actor).CreateDbContext())
		{
			var row = await change.ReceiptItems.SingleAsync();
			row.NormalizedDescriptionId = second.Id;
			await change.SaveChangesAsync();
		}

		var changed = (await ReadAsync([saved.Items.Single().Id])).Single();
		changed.NormalizedDescriptionId.Should().Be(second.Id);
		changed.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		changed.NormalizationReviewDecisionId.Should().BeNull();
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationReviewWork.SingleAsync()).ItemId.Should().Be(changed.Id);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReceiptScope_AmbiguousUnsavedSourceCannotPublishOneRowsIdentityAsReusable(bool rememberFirst)
	{
		await ResetAsync();
		var first = await CanonicalAsync("First identity");
		var second = await CanonicalAsync("Different identity");
		var rows = new[] { first, second }.Select((target, index) => EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision, RememberSource: index == (rememberFirst ? 0 : 1)))).ToList();

		Func<Task> save = () => EntryWriter().CreateAsync(EntryReceipt(), [], rows, [], CancellationToken.None);

		(await save.Should().ThrowAsync<NormalizationCatalogException>()).Which.Code.Should().Be("normalization_source_ambiguous");
		await using var db = fixture.CreateDbContext();
		(await db.Receipts.CountAsync()).Should().Be(0);
		(await db.ReceiptItems.CountAsync()).Should().Be(0);
		(await db.NormalizationSourceMappings.CountAsync()).Should().Be(0);
		(await db.NormalizationReviewWork.CountAsync()).Should().Be(0);
	}

	[Fact]
	public async Task ReceiptScope_StandaloneBatchOnlyReusesExactApprovedStoreDescriptionAndCode()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Reviewed beef franks");
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting,
			Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		var parent = (await EntryWriter().CreateAsync(EntryReceipt(), [], [], [], CancellationToken.None)).Receipt;
		var otherStore = (await EntryWriter().CreateAsync(new(Guid.NewGuid(), "Store B", parent.Date, new Money(0)), [], [], [], CancellationToken.None)).Receipt;
		var service = new ReceiptItemService(new ReceiptItemRepository(new EntryActorFactory(fixture.CreateOptions(), Actor)), new ReceiptItemMapper());
		var exact = EntryItem(null);
		var missingCode = EntryItem(null);
		missingCode.ReceiptItemCode = null;
		var otherCode = EntryItem(null);
		otherCode.ReceiptItemCode = "other-code";
		var otherDescription = EntryItem(null);
		otherDescription.Description = "BF FRANKS 16CT";

		var rows = await service.CreateAsync([exact, missingCode, otherCode, otherDescription], parent.Id, CancellationToken.None);
		var other = await service.CreateAsync([EntryItem(null)], otherStore.Id, CancellationToken.None);

		var saved = await ReadAsync(rows.Concat(other).Select(x => x.Id).ToArray());
		var recognized = saved.Single(x => x.ReceiptId == parent.Id && x.ReceiptItemCode == Source.ItemCode && x.Description == Source.Description);
		recognized.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		recognized.NormalizedDescriptionId.Should().Be(target.Id);
		saved.Where(x => x.Id != recognized.Id).Should().OnlyContain(x => x.NormalizationReviewState == NormalizationReviewState.NeedsReview && x.NormalizedDescriptionId == null);
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationReviewWork.CountAsync()).Should().Be(4);
	}

	[Fact]
	public async Task ReceiptScope_RememberingConflictingIdentityCannotRewriteApprovedMappingOrHistory()
	{
		await ResetAsync();
		var first = await CanonicalAsync("Beef franks");
		var second = await CanonicalAsync("Chicken franks");
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting,
			Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), first.Id, first.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		await using var before = fixture.CreateDbContext();
		var historical = await before.ReceiptItems.AsNoTracking().SingleAsync();
		var mapping = await before.NormalizationSourceMappings.AsNoTracking().SingleAsync();
		var row = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), second.Id, second.NormalizationRevision, RememberSource: true));

		Func<Task> act = () => EntryWriter().CreateAsync(EntryReceipt(), [], [row], [], CancellationToken.None);
		(await act.Should().ThrowAsync<NormalizationCatalogException>()).Which.Code.Should().Be("normalization_mapping_conflict");

		await using var db = fixture.CreateDbContext();
		(await db.Receipts.CountAsync()).Should().Be(1);
		(await db.ReceiptItems.SingleAsync()).NormalizedDescriptionId.Should().Be(first.Id);
		(await db.ReceiptItems.SingleAsync()).NormalizationReviewDecisionId.Should().Be(historical.NormalizationReviewDecisionId);
		(await db.NormalizationSourceMappings.SingleAsync()).Revision.Should().Be(mapping.Revision);
		(await db.NormalizationSourceMappings.SingleAsync()).TargetId.Should().Be(first.Id);
	}

	[Fact]
	public async Task ReceiptScope_RevokeDowngradesDependentMappingButPreservesIndependentPurchaseReview()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks");
		var independent = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting,
			Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		var mapped = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null)], [], CancellationToken.None);
		var mapping = (await Service().ListMappingsAsync(Actor, Source, CancellationToken.None)).Single();
		var ids = independent.Items.Concat(mapped.Items).Select(x => x.Id).ToArray();
		var facts = await FactsAsync(ids);

		await Service().RevokeAsync(Actor, mapping.Id, mapping.Revision, CancellationToken.None);

		var rows = await ReadAsync(ids);
		rows.Single(x => x.Id == independent.Items.Single().Id).NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		rows.Single(x => x.Id == mapped.Items.Single().Id).NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		rows.Should().OnlyContain(x => x.NormalizedDescriptionId == target.Id, "revoking reuse must not silently unlink the historical association");
		(await FactsAsync(ids)).Should().BeEquivalentTo(facts);
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationReviewWork.SingleAsync()).ItemId.Should().Be(mapped.Items.Single().Id);
	}

	[Fact]
	public async Task ReceiptScope_ParentDatePreservesReviewButStoreChangeReevaluatesEveryLiveLine()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks");
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting,
			Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision))], [], CancellationToken.None);
		var original = (await ReadAsync([saved.Items.Single().Id])).Single();
		var receipts = new ReceiptService(new ReceiptRepository(new EntryActorFactory(fixture.CreateOptions(), Actor)), new ReceiptMapper());
		var edit = new Receipt(saved.Receipt.Id, Source.Location, new(2026, 10, 19), new Money(0));
		await receipts.UpdateAsync([edit], CancellationToken.None);
		var dated = (await ReadAsync([original.Id])).Single();
		dated.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		dated.NormalizationReviewDecisionId.Should().Be(original.NormalizationReviewDecisionId);

		edit.Location = "Store B";
		await receipts.UpdateAsync([edit], CancellationToken.None);
		(await ReadAsync([original.Id])).Single().NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationReviewWork.SingleAsync()).SourceHash.Should().Be(NormalizationCatalogPolicy.Hash(Source with { Location = "Store B" }));
	}
}
