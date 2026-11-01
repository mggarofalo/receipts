using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task ReceiptPreview_GlobalCandidatesAreAdviceWhileOnlyExactApprovedScopeIsRecognized()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Reviewed beef franks");
		await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		var preview = new ReceiptNormalizationPreviewService(new Factory(fixture));
		await using var before = fixture.CreateDbContext();
		var audits = await before.AuditLogs.CountAsync();

		var recognized = await preview.PreviewAsync(Source, "Reviewed", 0, 1, CancellationToken.None);
		var differentStore = await preview.PreviewAsync(Source with { Location = "Another store" }, "Reviewed", 0, 1, CancellationToken.None);
		var missingCode = await preview.PreviewAsync(Source with { ItemCode = null }, "Reviewed", 0, 1, CancellationToken.None);

		recognized.TargetId.Should().Be(target.Id);
		recognized.TargetRevision.Should().Be(target.NormalizationRevision);
		recognized.MappingRevision.Should().NotBeNull();
		recognized.SourceFingerprint.Should().Be(NormalizationCatalogPolicy.Hash(Source));
		foreach (var advisory in new[] { differentStore, missingCode })
		{
			advisory.MappingId.Should().BeNull();
			advisory.TargetId.Should().BeNull();
			advisory.Candidates.Should().ContainSingle().Which.Id.Should().Be(target.Id);
		}
		await using var after = fixture.CreateDbContext();
		(await after.AuditLogs.CountAsync()).Should().Be(audits);
		(await after.ReceiptItems.CountAsync()).Should().Be(1);
		(await after.NormalizationReviewWork.CountAsync()).Should().Be(0);
	}

	[Fact]
	public async Task ReceiptPreview_FiltersInactiveTargetsBeforeLiteralSearchCountAndPagination()
	{
		await ResetAsync();
		var first = await CanonicalAsync("A 50% product");
		var second = await CanonicalAsync("B 50% product");
		var pending = await CanonicalAsync("0 50% pending");
		var rejected = await CanonicalAsync("0 50% rejected");
		await CanonicalAsync("0 500 product");
		await using (var db = fixture.CreateDbContext())
		{
			(await db.NormalizedDescriptions.SingleAsync(x => x.Id == pending.Id)).Status = NormalizedDescriptionStatus.PendingReview;
			(await db.NormalizedDescriptions.SingleAsync(x => x.Id == rejected.Id)).Status = NormalizedDescriptionStatus.Rejected;
			await db.SaveChangesAsync();
		}
		var service = new ReceiptNormalizationPreviewService(new Factory(fixture));

		var page = await service.PreviewAsync(Source, "50%", 1, 1, CancellationToken.None);

		page.Total.Should().Be(2);
		page.Offset.Should().Be(1);
		page.Limit.Should().Be(1);
		page.Candidates.Should().ContainSingle().Which.Id.Should().Be(second.Id);
		page.Candidates.Should().NotContain(x => x.Id == first.Id || x.Id == pending.Id || x.Id == rejected.Id);
		page.MappingId.Should().BeNull();
		await using var after = fixture.CreateDbContext();
		(await after.Receipts.CountAsync()).Should().Be(0);
		(await after.NormalizationReviewWork.CountAsync()).Should().Be(0);
	}

	[Fact]
	public async Task ReceiptPreview_RanksWholeWordScoreThenSimilarityWithinTheLiteralPhraseFilter()
	{
		await ResetAsync();
		await CanonicalAsync("Chicken thighs");
		var rotisserie = await CanonicalAsync("Rotisserie chicken breast");
		var exact = await CanonicalAsync("Chicken breast");
		await CanonicalAsync("Chickenbreast wrap");
		var pending = await CanonicalAsync("Chicken breast pending");
		await using (var db = fixture.CreateDbContext())
		{
			(await db.NormalizedDescriptions.SingleAsync(x => x.Id == pending.Id)).Status = NormalizedDescriptionStatus.PendingReview;
			await db.SaveChangesAsync();
		}
		var service = new ReceiptNormalizationPreviewService(new Factory(fixture));

		var page = await service.PreviewAsync(Source, "chicken breast", 0, 50, CancellationToken.None);

		// Both names hold "chicken" and "breast" as whole words (score 2); the exact name is more similar.
		page.Candidates.Select(x => x.Id).Should().Equal(exact.Id, rotisserie.Id);
		page.Total.Should().Be(2);
	}

	[Fact]
	public async Task ReceiptPreview_WholeWordMatchOutranksSubstringMatch()
	{
		await ResetAsync();
		var milkshake = await CanonicalAsync("Milkshake");
		var oatMilk = await CanonicalAsync("Oat milk");
		var service = new ReceiptNormalizationPreviewService(new Factory(fixture));

		var page = await service.PreviewAsync(Source, "milk", 0, 50, CancellationToken.None);

		page.Candidates.Select(x => x.Id).Should().Equal(oatMilk.Id, milkshake.Id);
	}

	[Fact]
	public async Task ReceiptPreview_TiedRelevancePagesWithoutDuplicatesOrGaps()
	{
		await ResetAsync();
		var ids = new List<Guid>();
		foreach (var n in new[] { 4, 1, 7, 3, 6, 2, 5 })
		{
			ids.Add((await CanonicalAsync($"Ab test {n}")).Id);
		}
		var service = new ReceiptNormalizationPreviewService(new Factory(fixture));

		var all = await service.PreviewAsync(Source, "test", 0, 50, CancellationToken.None);
		var paged = new List<Guid>();
		foreach (var offset in new[] { 0, 2, 4, 6 })
		{
			var page = await service.PreviewAsync(Source, "test", offset, 2, CancellationToken.None);
			page.Total.Should().Be(7);
			paged.AddRange(page.Candidates.Select(x => x.Id));
		}

		all.Total.Should().Be(7);
		all.Candidates.Select(x => x.Name).Should().Equal(Enumerable.Range(1, 7).Select(n => $"Ab test {n}"));
		paged.Should().Equal(all.Candidates.Select(x => x.Id));
		paged.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(ids);
	}
}
