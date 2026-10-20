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
	public async Task ReceiptKnownSource_ObservedMappingCannotSilentlyFollowRevocationOrReplacement(bool replace)
	{
		await ResetAsync();
		var original = await CanonicalAsync("Observed identity");
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting,
			Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), original.Id, original.NormalizationRevision,
			RememberSource: true))], [], CancellationToken.None);
		var observed = await new ReceiptNormalizationPreviewService(new Factory(fixture)).PreviewAsync(Source, null, 0, 10, CancellationToken.None);
		observed.MappingId.Should().NotBeNull();
		observed.MappingRevision.Should().NotBeNull();
		await Service().RevokeAsync(Actor, observed.MappingId!.Value, observed.MappingRevision!.Value, CancellationToken.None);
		if (replace)
		{
			// Even a newly approved mapping to the same identity needs explicit refreshed consent.
			var replacement = original;
			await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting,
				Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), replacement.Id, replacement.NormalizationRevision,
				RememberSource: true))], [], CancellationToken.None);
		}
		await using var before = fixture.CreateDbContext();
		var receipts = await before.Receipts.CountAsync();
		var items = await before.ReceiptItems.CountAsync();
		var audits = await before.AuditLogs.CountAsync();
		var work = await before.NormalizationReviewWork.CountAsync();
		var stale = EntryItem(new(ReceiptNormalizationIntentKind.KnownSource, Guid.NewGuid(),
			NormalizationCatalogPolicy.Hash(Source), ExpectedMappingRevision: observed.MappingRevision));

		Func<Task> save = () => EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null), stale], [], CancellationToken.None);
		var failure = (await save.Should().ThrowAsync<NormalizationCatalogException>()).Which;
		failure.StatusCode.Should().Be(409);
		failure.Code.Should().Be("normalization_mapping_changed");

		await using var after = fixture.CreateDbContext();
		(await after.Receipts.CountAsync()).Should().Be(receipts);
		(await after.ReceiptItems.CountAsync()).Should().Be(items);
		(await after.AuditLogs.CountAsync()).Should().Be(audits);
		(await after.NormalizationReviewWork.CountAsync()).Should().Be(work);
	}
}
