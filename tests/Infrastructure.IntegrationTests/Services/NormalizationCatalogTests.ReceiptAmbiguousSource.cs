using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReceiptAmbiguousSource_StagedOneOffDivergenceCannotVerifyUnselectedSibling(bool reversed)
	{
		await ResetAsync();
		var first = await CanonicalAsync("Beef franks");
		var second = await CanonicalAsync("Turkey franks");
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), first.Id, first.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		var chosen = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), second.Id, second.NormalizationRevision));
		var unselected = EntryItem(null);
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], reversed ? [unselected, chosen] : [chosen, unselected], [], CancellationToken.None);
		var rows = await ReadAsync(saved.Items.Select(x => x.Id).ToArray());
		rows.Should().HaveCount(2);
		rows.Should().ContainSingle(x => x.NormalizationReviewState == NormalizationReviewState.Verified && x.NormalizedDescriptionId == second.Id);
		rows.Should().ContainSingle(x => x.NormalizationReviewState == NormalizationReviewState.NeedsReview && x.NormalizedDescriptionId == null);
	}
	[Fact]
	public async Task ReceiptAmbiguousSource_ExistingRuleCannotSilentlyClassifyConflictingSource()
	{
		await ResetAsync();
		var first = await CanonicalAsync("Beef franks");
		var second = await CanonicalAsync("Turkey franks");
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), first.Id, first.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		var preview = new ReceiptNormalizationPreviewService(new Factory(fixture));
		var known = await preview.PreviewAsync(Source, null, 0, 10, CancellationToken.None);
		known.MappingRevision.Should().NotBeNull();
		// An explicitly reviewed one-off purchase may intentionally disagree with a rule.
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), second.Id, second.NormalizationRevision))], [], CancellationToken.None);

		var now = await preview.PreviewAsync(Source, null, 0, 10, CancellationToken.None);
		now.SourceAmbiguous.Should().BeTrue();
		now.MappingId.Should().BeNull();
		var pending = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null)], [], CancellationToken.None);
		var row = (await ReadAsync([pending.Items.Single().Id])).Single();
		row.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		row.NormalizedDescriptionId.Should().BeNull();

		Func<Task> stale = () => EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.KnownSource, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), ExpectedMappingRevision: known.MappingRevision))], [], CancellationToken.None);
		(await stale.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);
	}
	[Fact]
	public async Task ReceiptAmbiguousSource_PreviewCountsOnlyDistinctLiveIdentitiesInExactScope()
	{
		await ResetAsync();
		var first = await CanonicalAsync("Beef franks");
		var second = await CanonicalAsync("Turkey franks");
		await ItemsAsync(Source, 2, "legacy_unknown", first.Id);
		await ItemsAsync(Source with { Location = "Other store" }, 1, "legacy_unknown", second.Id);
		await ItemsAsync(Source with { ItemCode = "Other code" }, 1, "legacy_unknown", second.Id);
		await ItemsAsync(Source with { ItemCode = null }, 1, "legacy_unknown", second.Id);
		var trashed = (await ItemsAsync(Source, 1, "legacy_unknown", second.Id)).Single();
		await using (var db = fixture.CreateDbContext())
		{
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"DeletedAt\"=now() WHERE \"Id\"={trashed}");
		}
		var service = new ReceiptNormalizationPreviewService(new Factory(fixture));

		var isolated = await service.PreviewAsync(Source, null, 0, 10, CancellationToken.None);
		isolated.SourceAmbiguous.Should().BeFalse("repeated purchases and other stores/codes or trashed rows cannot create ambiguity for this exact source");

		await ItemsAsync(Source, 1, "legacy_unknown", second.Id);
		await using var before = fixture.CreateDbContext();
		var auditCount = await before.AuditLogs.CountAsync();
		var ambiguous = await service.PreviewAsync(Source, null, 0, 10, CancellationToken.None);

		ambiguous.SourceAmbiguous.Should().BeTrue();
		ambiguous.MappingId.Should().BeNull();
		await using var after = fixture.CreateDbContext();
		(await after.AuditLogs.CountAsync()).Should().Be(auditCount, "preview is advisory and must not approve, merge or modify purchases");
	}

	[Fact]
	public async Task ReceiptAmbiguousSource_CannotRememberUniversalRuleButCanReviewOnePurchase()
	{
		await ResetAsync();
		var first = await CanonicalAsync("Beef franks");
		var second = await CanonicalAsync("Turkey franks");
		await ItemsAsync(Source, 1, "legacy_unknown", first.Id);
		await ItemsAsync(Source, 1, "legacy_unknown", second.Id);
		var remembered = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), first.Id, first.NormalizationRevision, RememberSource: true));

		Func<Task> remember = () => EntryWriter().CreateAsync(EntryReceipt(), [], [remembered], [], CancellationToken.None);
		(await remember.Should().ThrowAsync<NormalizationCatalogException>()).Which.Code.Should().Be("normalization_source_ambiguous");
		await using (var rejected = fixture.CreateDbContext())
		{
			(await rejected.NormalizationSourceMappings.AnyAsync()).Should().BeFalse();
			(await rejected.ReceiptItems.CountAsync()).Should().Be(2);
		}

		var oneOff = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), first.Id, first.NormalizationRevision));
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [oneOff], [], CancellationToken.None);
		var row = (await ReadAsync([saved.Items.Single().Id])).Single();
		row.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		row.NormalizedDescriptionId.Should().Be(first.Id);
		row.NormalizationMappingId.Should().BeNull();
		await using var after = fixture.CreateDbContext();
		(await after.NormalizationSourceMappings.AnyAsync()).Should().BeFalse();
	}
}
