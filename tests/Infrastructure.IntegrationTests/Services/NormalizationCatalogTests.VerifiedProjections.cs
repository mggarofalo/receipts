using Application.Models.Search;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task CanonicalOnlyPhrase_NeverMatchesPendingOrExcludedForeignKeysWithoutReview()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Unique canonical needle");
		var pending = await ItemsAsync(Source, 1, "legacy_unknown", target.Id);
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(
			ReceiptNormalizationIntentKind.Exclude, Guid.NewGuid(),
			Application.Normalization.NormalizationCatalogPolicy.Hash(Source), Reason: "Coupon"))], [], CancellationToken.None);
		var reader = new PurchaseSearchReader(new Factory(fixture));
		(await reader.SearchAsync("Unique canonical needle", 0, 25, new(), CancellationToken.None)).Total.Should().Be(0);
		var included = await reader.SearchAsync("Unique canonical needle", 0, 25,
			new(IncludeUnreviewed: true), CancellationToken.None);
		included.Items.Should().ContainSingle().Which.Id.Should().Be(pending[0]);
		included.Items[0].NormalizationReviewState.Should().Be("needsReview");
		var raw = await reader.SearchAsync(Source.Description, 0, 25, new(), CancellationToken.None);
		raw.Total.Should().Be(2, "raw purchases remain searchable regardless of product review state");
	}

	[Fact]
	public async Task VerifiedHistory_FiltersBeforeCountingAndPagingAndNeverTrustsLegacyForeignKeys()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Reviewed beef franks");
		var verified = await ItemsAsync(Source, 2);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		var legacy = await ItemsAsync(Source with { Description = "RAW legacy beef" }, 1, "reviewed", target.Id);
		var deleted = await ItemsAsync(Source with { Description = "RAW deleted beef" }, 1, "legacy_unknown", target.Id);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.ReceiptItems.SingleAsync(x => x.Id == verified[0])).TotalAmount = -2.90m;
			(await db.ReceiptItems.SingleAsync(x => x.Id == deleted[0])).DeletedAt = DateTimeOffset.UtcNow;
			await db.SaveChangesAsync();
		}
		var reader = new PurchaseSearchReader(new Factory(fixture));
		var options = new PurchaseSearchOptions(NormalizedDescriptionId: target.Id);
		var first = await reader.SearchAsync("", 0, 1, options, CancellationToken.None);
		var second = await reader.SearchAsync("", 1, 1, options, CancellationToken.None);
		first.Total.Should().Be(2);
		second.Total.Should().Be(2);
		first.Items.Concat(second.Items).Select(x => x.Id).Should().BeEquivalentTo(verified);
		first.Items.Concat(second.Items).Should().OnlyContain(x => x.NormalizationReviewState == "verified");
		first.Items.Concat(second.Items).Sum(x => x.TotalAmount).Should().Be(1m);
		(await reader.SearchAsync("missing phrase", 0, 25, options, CancellationToken.None)).Total.Should().Be(0);
		(await reader.SearchAsync("", 0, 25, options with { StartDate = new(2025, 1, 16) }, CancellationToken.None)).Total.Should().Be(0);
		var raw = await reader.SearchAsync("RAW legacy", 0, 25, new(), CancellationToken.None);
		raw.Items.Should().ContainSingle().Which.Id.Should().Be(legacy[0]);
		raw.Items[0].NormalizedDescriptionId.Should().BeNull();
		raw.Items[0].NormalizedDescription.Should().BeNull();
		var provisional = await reader.SearchAsync("", 0, 25, options with { IncludeUnreviewed = true }, CancellationToken.None);
		provisional.Total.Should().Be(3);
		provisional.Items.Single(x => x.Id == legacy[0]).NormalizationReviewState.Should().Be("needsReview");
		provisional.Items.Single(x => x.Id == legacy[0]).NormalizedDescriptionId.Should().Be(target.Id);
	}

	[Fact]
	public async Task Spending_GroupsVerifiedGuidsAndReconcilesSignedPendingAndExcludedTotals()
	{
		await ResetAsync();
		var firstTarget = await CanonicalAsync("First canonical");
		var secondTarget = await CanonicalAsync("Second canonical");
		var first = await ItemsAsync(Source, 1);
		var secondSource = Source with { Description = "Second raw source" };
		var second = await ItemsAsync(secondSource, 1);
		var service = Service();
		foreach (var pair in new[] { (Target: firstTarget, Source), (Target: secondTarget, Source: secondSource) })
		{
			var preview = await service.PreviewAsync(Actor, Request(pair.Target) with { Source = pair.Source }, CancellationToken.None);
			await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		}
		var legacy = await ItemsAsync(Source with { Description = "Legacy raw source" }, 1, "manual", firstTarget.Id);
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(
			ReceiptNormalizationIntentKind.Exclude, Guid.NewGuid(),
			Application.Normalization.NormalizationCatalogPolicy.Hash(Source), Reason: "Coupon"))], [], CancellationToken.None);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.NormalizedDescriptions.SingleAsync(x => x.Id == firstTarget.Id)).DisplayLabel = "First display label";
			(await db.NormalizedDescriptions.SingleAsync(x => x.Id == secondTarget.Id)).DisplayLabel = "Second display label";
			(await db.ReceiptItems.SingleAsync(x => x.Id == first[0])).TotalAmount = -2.90m;
			await db.SaveChangesAsync();
		}
		var report = await new ReportService(new Factory(fixture)).GetSpendingByNormalizedDescriptionAsync(
			null, null, "totalAmount", "desc", 1, 50, CancellationToken.None);
		report.TotalCount.Should().Be(4);
		var reviewed = report.Items.Where(x => x.NormalizationReviewState == "verified").ToArray();
		reviewed.Should().HaveCount(2);
		reviewed.Select(x => x.NormalizedDescriptionId).Should().BeEquivalentTo(new Guid?[] { firstTarget.Id, secondTarget.Id });
		reviewed.Should().OnlyContain(x => x.ItemCount == 1);
		reviewed.Select(x => x.CanonicalName).Should().BeEquivalentTo("First display label", "Second display label");
		report.Items.Single(x => x.NormalizationReviewState == "needsReview").TotalAmount.Should().Be(2.90m);
		report.Items.Single(x => x.NormalizationReviewState == "excluded").NormalizedDescriptionId.Should().BeNull();
		await using var read = fixture.CreateDbContext();
		var persisted = await read.ReceiptItems.SumAsync(x => x.TotalAmount);
		report.GrandTotal.Should().Be(persisted);
		report.Items.Sum(x => x.TotalAmount).Should().Be(persisted);
	}

	[Theory]
	[InlineData(NormalizedDescriptionStatus.PendingReview, true)]
	[InlineData(NormalizedDescriptionStatus.Rejected, false)]
	public async Task IdentityHistory_NonactiveTargetsRequireExplicitProvisionalConsent(
		NormalizedDescriptionStatus status, bool visibleWithConsent)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Hidden canonical needle");
		var ids = await ItemsAsync(Source, 1, "legacy_unknown", target.Id);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.NormalizedDescriptions.SingleAsync(x => x.Id == target.Id)).Status = status;
			await db.SaveChangesAsync();
		}
		var reader = new PurchaseSearchReader(new Factory(fixture));
		var options = new PurchaseSearchOptions(NormalizedDescriptionId: target.Id);
		(await reader.SearchAsync("", 0, 25, options, CancellationToken.None)).Total.Should().Be(0);
		var provisional = await reader.SearchAsync("", 0, 25,
			options with { IncludeUnreviewed = true }, CancellationToken.None);
		provisional.Total.Should().Be(visibleWithConsent ? 1 : 0);
		var phrase = await reader.SearchAsync("Hidden canonical needle", 0, 25,
			new(IncludeUnreviewed: true), CancellationToken.None);
		phrase.Total.Should().Be(visibleWithConsent ? 1 : 0);
		foreach (var include in new[] { false, true })
		{
			var raw = await reader.SearchAsync(Source.Description, 0, 25,
				new(IncludeUnreviewed: include), CancellationToken.None);
			raw.Items.Should().ContainSingle().Which.Id.Should().Be(ids[0]);
			raw.Items[0].NormalizationReviewState.Should().Be("needsReview");
			raw.Items[0].NormalizedDescriptionId.Should().Be(include && visibleWithConsent ? target.Id : null);
		}
	}

	[Fact]
	public async Task ReceiptListAndCoverage_CountOnlyLiveEvidenceAndOutstandingPendingWork()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Verified count target");
		var ids = await ItemsAsync(Source, 2);
		await ReviewExistingAsync([ids[0]], target);
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(
			ReceiptNormalizationIntentKind.Exclude, Guid.NewGuid(),
			Application.Normalization.NormalizationCatalogPolicy.Hash(Source), Reason: "Coupon"))], [], CancellationToken.None);
		var deleted = await ItemsAsync(Source with { Description = "Deleted pending" }, 1);
		await using (var db = fixture.CreateDbContext())
		{
			var receiptId = (await db.ReceiptItems.SingleAsync(x => x.Id == deleted[0])).ReceiptId;
			(await db.Receipts.SingleAsync(x => x.Id == receiptId)).DeletedAt = DateTimeOffset.UtcNow;
			await db.SaveChangesAsync();
		}
		var rows = await new Infrastructure.Repositories.ReceiptRepository(new Factory(fixture))
			.GetListAsync(0, 25, Application.Models.SortParams.Default, null, null, null, null, CancellationToken.None);
		rows.Should().HaveCount(2);
		rows.Sum(x => x.ItemCount).Should().Be(3);
		rows.Sum(x => x.VerifiedItemCount).Should().Be(1);
		rows.Sum(x => x.NeedsReviewItemCount).Should().Be(1);
		rows.Sum(x => x.ExcludedItemCount).Should().Be(1);
		var coverage = await Service().GetCoverageAsync(Actor, CancellationToken.None);
		coverage.TotalItems.Should().Be(3);
		coverage.VerifiedItems.Should().Be(1);
		coverage.PendingItems.Should().Be(1);
		coverage.PendingSources.Should().Be(1);
		coverage.ExcludedItems.Should().Be(1);
		await using var read = fixture.CreateDbContext();
		var expectedOldest = await read.NormalizationReviewWork.Where(x => x.ItemId == ids[1])
			.MinAsync(x => (DateTimeOffset?)x.CreatedAt);
		expectedOldest.Should().NotBeNull("live pending purchases must retain outstanding review work");
		coverage.OldestUnresolvedAt.Should().Be(expectedOldest);
	}

	[Theory]
	[InlineData(NormalizedDescriptionStatus.Active, NormalizationReviewState.NeedsReview, "Visible label")]
	[InlineData(NormalizedDescriptionStatus.PendingReview, NormalizationReviewState.NeedsReview, "Visible label")]
	[InlineData(NormalizedDescriptionStatus.Rejected, NormalizationReviewState.NeedsReview, null)]
	[InlineData(NormalizedDescriptionStatus.Active, NormalizationReviewState.Excluded, null)]
	public void ReceiptItemMapping_ConcealsRejectedAndExcludedLabelsButPreservesProvisionalState(
		NormalizedDescriptionStatus status, NormalizationReviewState state, string? expected)
	{
		var entity = new Infrastructure.Entities.Core.ReceiptItemEntity
		{
			Id = Guid.NewGuid(),
			Description = "Raw purchase",
			Quantity = 1,
			UnitPrice = 2,
			TotalAmount = 2,
			Category = "Food",
			UnitPriceCurrency = Common.Currency.USD,
			TotalAmountCurrency = Common.Currency.USD,
			NormalizationReviewState = state,
			NormalizedDescription = new() { Id = Guid.NewGuid(), CanonicalName = "Canonical", DisplayLabel = "Visible label", Status = status }
		};
		var mapped = new Infrastructure.Mapping.ReceiptItemMapper().ToDomain(entity);
		mapped.NormalizedDescriptionName.Should().Be(expected);
		mapped.NormalizationReviewState.Should().Be(state);
		mapped.Description.Should().Be("Raw purchase");
	}
}
