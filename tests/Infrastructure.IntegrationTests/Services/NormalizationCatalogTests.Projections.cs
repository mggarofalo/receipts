using Application.Models.Search;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task ReviewedAliases_UnifyPurchaseSearchAndSpendingWithoutChangingStoredFinancialFacts()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Reviewed beef franks");
		var first = await ItemsAsync(Source, 1);
		var alternateSource = Source with { Description = "BEEF FRANKS 8 COUNT" };
		var second = await ItemsAsync(alternateSource, 1);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.ReceiptItems.SingleAsync(x => x.Id == second[0])).TotalAmount = 3.17m;
			await db.SaveChangesAsync();
		}
		var service = Service();
		foreach (var source in new[] { Source, alternateSource })
		{
			var preview = await service.PreviewAsync(Actor, Request(target) with { Source = source }, CancellationToken.None);
			await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		}
		var search = await new PurchaseSearchReader(new Factory(fixture)).SearchAsync(
			"Reviewed beef franks", 0, 25, new PurchaseSearchOptions(), CancellationToken.None);
		search.Total.Should().Be(2);
		search.Items.Select(x => x.Id).Should().BeEquivalentTo(first.Concat(second));
		search.Items.Should().OnlyContain(x => x.Quantity == 1.2345m && x.UnitPrice == 2.3456m);
		search.Items.Single(x => x.Id == first[0]).TotalAmount.Should().Be(2.90m);
		search.Items.Single(x => x.Id == second[0]).TotalAmount.Should().Be(3.17m);
		search.Items.Select(x => x.Description).Should().BeEquivalentTo(Source.Description, alternateSource.Description);
		search.Items.Should().OnlyContain(x => x.Location == Source.Location && x.ReceiptId != Guid.Empty);
		var report = await new ReportService(new Factory(fixture)).GetSpendingByNormalizedDescriptionAsync(
			null, null, "totalAmount", "desc", 1, 50, CancellationToken.None);
		var bucket = report.Items.Should().ContainSingle().Subject;
		bucket.CanonicalName.Should().Be(target.CanonicalName);
		bucket.ItemCount.Should().Be(2);
		bucket.TotalAmount.Should().Be(6.07m, "reporting must sum persisted totals, not recompute unit price times quantity");
		bucket.Currency.Should().Be("USD");
	}

	[Fact]
	public async Task Coverage_CountsCurrentEvidenceInsteadOfTreatingHistoricalOriginAsReview()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks");
		await ItemsAsync(Source, 2);
		await ItemsAsync(Source, 1, "legacy_unknown", target.Id);
		await ItemsAsync(Source, 1, "automatic", target.Id);
		await ItemsAsync(Source, 1, "manual", target.Id);
		await ItemsAsync(Source, 1, "reviewed", target.Id);
		await ItemsAsync(Source, 1, "mapped", target.Id);
		await ItemsAsync(Source, 1, "excluded");
		var deletedItem = await ItemsAsync(Source, 1);
		var deletedParent = await ItemsAsync(Source, 1);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.ReceiptItems.SingleAsync(x => x.Id == deletedItem[0])).DeletedAt = DateTimeOffset.UtcNow;
			var receiptId = (await db.ReceiptItems.SingleAsync(x => x.Id == deletedParent[0])).ReceiptId;
			(await db.Receipts.SingleAsync(x => x.Id == receiptId)).DeletedAt = DateTimeOffset.UtcNow;
			await db.SaveChangesAsync();
		}
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting,
			Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null)], [], CancellationToken.None);
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.Exclude,
			Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), Reason: "Non-product observation"))], [], CancellationToken.None);
		var service = Service();
		var coverage = await service.GetCoverageAsync(Actor, CancellationToken.None);
		coverage.TotalItems.Should().Be(11);
		coverage.Unassigned.Should().Be(3, "the historical excluded origin has no explicit exclusion evidence");
		coverage.LegacyUnknown.Should().Be(5, "all five historical links lack reviewed evidence regardless of provenance label");
		coverage.Automatic.Should().Be(0);
		coverage.Reviewed.Should().Be(1);
		coverage.Mapped.Should().Be(1);
		coverage.Protected.Should().Be(0);
		coverage.Excluded.Should().Be(1);
		var excludedSources = await service.ListSourcesAsync(Actor, null, "excluded", 0, 10, CancellationToken.None);
		excludedSources.Items.Should().ContainSingle().Which.Source.Should().Be(Source);
		excludedSources.Total.Should().Be(1);
		(coverage.Unassigned + coverage.LegacyUnknown + coverage.Automatic + coverage.Reviewed
			+ coverage.Mapped + coverage.Protected + coverage.Excluded).Should().Be(coverage.TotalItems);
	}

	[Fact]
	public async Task Candidates_OfflineLexicalRetrievalExcludesNonactiveRowsBeforeTakingTheLimit()
	{
		await ResetAsync();
		var active = await CanonicalAsync("Beef franks 8 count");
		await using (var db = fixture.CreateDbContext())
		{
			db.NormalizedDescriptions.AddRange(Enumerable.Range(0, 40).Select(i => new NormalizedDescriptionEntity
			{
				Id = Guid.NewGuid(),
				CanonicalName = $"Beef franks distraction {i}",
				Status = i % 2 == 0 ? NormalizedDescriptionStatus.PendingReview : NormalizedDescriptionStatus.Rejected,
				CreatedAt = DateTimeOffset.UtcNow,
			}));
			db.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = Guid.NewGuid(),
				CanonicalName = "beef franks",
				Status = NormalizedDescriptionStatus.PendingReview,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await db.SaveChangesAsync();
		}
		// The service has no configured model or vectors in this fixture. A pending exact
		// name must not short-circuit retrieval of an active, useful alternative.
		var candidates = await Service().GetCandidatesAsync(Actor,
			Source with { Description = "beef franks" }, CancellationToken.None);
		candidates.Should().ContainSingle(x => x.Id == active.Id);
		candidates.Should().OnlyContain(x => x.Id == active.Id);
		candidates.Single().Channels.Should().NotBeEmpty();
	}

	[Fact]
	public async Task ApprovedExamples_AreImmutableDecisionEvidenceRatherThanCurrentReceiptText()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks 8 count");
		var ids = await ItemsAsync(Source, 1);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		await using (var db = fixture.CreateDbContext())
		{
			var item = await db.ReceiptItems.SingleAsync(x => x.Id == ids[0]);
			item.Description = "CHICKEN FRANKS 16CT";
			await db.SaveChangesAsync();
		}
		var candidates = await service.GetCandidatesAsync(Actor, Source, CancellationToken.None);
		var candidate = candidates.Should().ContainSingle(x => x.Id == target.Id).Subject;
		candidate.ApprovedExamples.Should().Contain(Source.Description);
		candidate.ApprovedExamples.Should().NotContain("CHICKEN FRANKS 16CT",
			"later edits must not rewrite the evidence used to approve a source alias");
	}
}
