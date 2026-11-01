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
		var plural = await CanonicalAsync("Chicken breasts");
		var pending = await CanonicalAsync("Chicken breast pending");
		await using (var db = fixture.CreateDbContext())
		{
			(await db.NormalizedDescriptions.SingleAsync(x => x.Id == pending.Id)).Status = NormalizedDescriptionStatus.PendingReview;
			await db.SaveChangesAsync();
		}
		var service = new ReceiptNormalizationPreviewService(new Factory(fixture));

		var page = await service.PreviewAsync(Source, "chicken breast", 0, 50, CancellationToken.None);

		// Both names hold "chicken" and "breast" as whole words (score 2); the exact name is more similar.
		// "Chicken breasts" holds "breast" only inside a longer word (score 1), so it comes last even
		// though its trigram similarity is high.
		page.Candidates.Select(x => x.Id).Should().Equal(exact.Id, rotisserie.Id, plural.Id);
		page.Total.Should().Be(3);
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

	[Theory]
	[InlineData("50_50", "50_50 blend")]
	[InlineData(@"back\slash", @"back\slash item")]
	public async Task ReceiptPreview_TreatsLikeWildcardsAndEscapeCharacterLiterally(string query, string expected)
	{
		await ResetAsync();
		await CanonicalAsync("50_50 blend");
		await CanonicalAsync("50x50 blend");
		await CanonicalAsync(@"back\slash item");
		await CanonicalAsync("backXslash item");
		var service = new ReceiptNormalizationPreviewService(new Factory(fixture));

		var page = await service.PreviewAsync(Source, query, 0, 50, CancellationToken.None);

		page.Total.Should().Be(1);
		page.Candidates.Should().ContainSingle().Which.Name.Should().Be(expected);
	}

	[Fact]
	public async Task ReceiptPreview_PhraseWithoutCatalogTokensStillFiltersAndRanks()
	{
		await ResetAsync();
		var banana = await CanonicalAsync("Banana");
		var apple = await CanonicalAsync("Apple");
		await CanonicalAsync("Kiwi");
		var service = new ReceiptNormalizationPreviewService(new Factory(fixture));

		var page = await service.PreviewAsync(Source, "a", 0, 50, CancellationToken.None);

		// "a" is below the Catalog token minimum, so every match scores 0. "Apple" shares the leading
		// "  a" trigram with the phrase and "Banana" shares none, so similarity fixes the order (and the
		// shown-name tie-break would agree).
		page.Total.Should().Be(2);
		page.Candidates.Select(x => x.Id).Should().Equal(apple.Id, banana.Id);
	}

	[Fact]
	public async Task ReceiptPreview_MatchesOnDisplayLabelAndReturnsItAsTheName()
	{
		await ResetAsync();
		var labelled = await CanonicalAsync("KS ORG CHKN BRST");
		await using (var db = fixture.CreateDbContext())
		{
			(await db.NormalizedDescriptions.SingleAsync(x => x.Id == labelled.Id)).DisplayLabel = "Chicken breast";
			await db.SaveChangesAsync();
		}
		var service = new ReceiptNormalizationPreviewService(new Factory(fixture));

		var page = await service.PreviewAsync(Source, "chicken", 0, 50, CancellationToken.None);

		page.Total.Should().Be(1);
		var candidate = page.Candidates.Should().ContainSingle().Subject;
		candidate.Id.Should().Be(labelled.Id);
		candidate.Name.Should().Be("Chicken breast");
	}

	[Fact]
	public async Task ReceiptPreview_TiedRelevanceOrdersByShownNameNotCanonicalName()
	{
		await ResetAsync();
		// Same-length shown names, so score (1) and trigram similarity (5/8) tie exactly. By canonical
		// name "Bb test" would sort before "Zz test"; by shown name "Aa test" comes first.
		var labelled = await CanonicalAsync("Zz test");
		var plain = await CanonicalAsync("Bb test");
		await using (var db = fixture.CreateDbContext())
		{
			(await db.NormalizedDescriptions.SingleAsync(x => x.Id == labelled.Id)).DisplayLabel = "Aa test";
			await db.SaveChangesAsync();
		}
		var service = new ReceiptNormalizationPreviewService(new Factory(fixture));

		var page = await service.PreviewAsync(Source, "test", 0, 50, CancellationToken.None);

		page.Total.Should().Be(2);
		page.Candidates.Select(x => x.Id).Should().Equal(labelled.Id, plain.Id);
		page.Candidates.Select(x => x.Name).Should().Equal("Aa test", "Bb test");
	}
}
