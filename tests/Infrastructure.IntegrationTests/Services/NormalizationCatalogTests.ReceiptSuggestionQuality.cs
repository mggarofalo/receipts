using System.Text.Json;
using Application.Interfaces.Services;
using Application.Normalization;
using Application.Queries.Core.ItemTemplate.GetSimilarItems;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	// Synthetic counterexamples. No real receipt text or provider calls are used.
	// The entry suggestion contract is stricter than administrative candidate recall.
	[Theory]
	[InlineData("Baked beans 12 oz", "Rubber mallet 12 inch")]
	[InlineData("Baked beans 16 oz", "Claw hammer 16 oz")]
	[InlineData("Baked beans 400 g", "Rubber mallet 400 g")]
	[InlineData("Ham", "Hamburger buns")]
	[InlineData("Baked beans", "Baked potato")]
	[InlineData("Acme beans", "Acme mallet")]
	[InlineData("Organic whole milk", "Organic whole chicken")]
	public async Task ReceiptSuggestions_AbstainWhenOnlyPackageNumberOrUnitConnectsUnrelatedProducts(
		string description, string unrelatedName)
	{
		await ResetAsync();
		var unrelated = await CanonicalAsync(unrelatedName);
		var service = new ReceiptNormalizationCandidatesService(new Factory(fixture), TimeProvider.System);

		var candidates = await service.GetCandidatesAsync(Actor,
			new("Synthetic market", description, null), CancellationToken.None);

		candidates.Should().NotContain(candidate => candidate.Id == unrelated.Id,
			"package numbers, units, or a substring inside another word alone do not support a product suggestion");
		candidates.Should().BeEmpty("there is no supported product identity in this synthetic catalog");
		await using var verify = fixture.CreateDbContext();
		(await verify.NormalizationSourceMappings.CountAsync()).Should().Be(0);
		(await verify.ReceiptItems.CountAsync()).Should().Be(0);
	}

	[Theory]
	[InlineData("Whole milk", "Milk")]
	[InlineData("Organic whole milk", "Milk")]
	[InlineData("Baked beans 12 oz", "Baked beans")]
	public async Task ReceiptSuggestions_RetainUsefulWholeWordProductEvidence(string description, string name)
	{
		await ResetAsync();
		var target = await CanonicalAsync(name);
		var service = new ReceiptNormalizationCandidatesService(new Factory(fixture), TimeProvider.System);

		var candidates = await service.GetCandidatesAsync(Actor,
			new("Synthetic market", description, null), CancellationToken.None);

		candidates.Should().Contain(candidate => candidate.Id == target.Id && candidate.Channels.Contains("lexical"));
		await using var verify = fixture.CreateDbContext();
		(await verify.NormalizationSourceMappings.CountAsync()).Should().Be(0);
		(await verify.ReceiptItems.CountAsync()).Should().Be(0);
	}

	[Fact]
	public async Task ReceiptSuggestions_RetainAnExactProductNameWithoutRecordingAChoice()
	{
		await ResetAsync();
		var exact = await CanonicalAsync("Baked beans 12 oz");
		await CanonicalAsync("Rubber mallet 12 inch");
		var service = new ReceiptNormalizationCandidatesService(new Factory(fixture), TimeProvider.System);

		var candidates = await service.GetCandidatesAsync(Actor,
			new("Synthetic market", exact.CanonicalName, null), CancellationToken.None);

		candidates.Should().Contain(candidate => candidate.Id == exact.Id && candidate.Channels.Contains("exact"));
		candidates.Should().NotContain(candidate => candidate.CanonicalName == "Rubber mallet 12 inch");
		await using var verify = fixture.CreateDbContext();
		(await verify.NormalizationSourceMappings.CountAsync()).Should().Be(0);
		(await verify.ReceiptItems.CountAsync()).Should().Be(0);
	}

	[Fact]
	public async Task ReceiptSuggestions_FilterWeakRowsBeforeFillingTheCandidatePage()
	{
		await ResetAsync();
		// Every distractor matches the SQL substring search for milk and sorts
		// before Milk. None has the complete product word milk.
		await SeedSuggestionNamesAsync(Enumerable.Range(0, 30).Select(i => $"A{i:D3} milkshake"));
		var useful = await CanonicalAsync("Milk");
		var service = new ReceiptNormalizationCandidatesService(new Factory(fixture), TimeProvider.System);

		var candidates = await service.GetCandidatesAsync(Actor,
			new("Synthetic market", "Organic whole milk", null), CancellationToken.None);

		candidates.Should().ContainSingle().Which.Id.Should().Be(useful.Id,
			"more than twenty weak substring matches must not crowd out useful product evidence");
	}

	[Fact]
	public async Task ReceiptSuggestions_FindAWholeWordMatchBeyondSixHundredSubstringDistractors()
	{
		await ResetAsync();
		await SeedSuggestionNamesAsync(Enumerable.Range(0, 600).Select(i => $"A{i:D3} milkshake"));
		var useful = await CanonicalAsync("Milk");
		var service = new ReceiptNormalizationCandidatesService(new Factory(fixture), TimeProvider.System);

		var candidates = await service.GetCandidatesAsync(Actor,
			new("Synthetic market", "Whole milk", null), CancellationToken.None);

		candidates.Should().Contain(candidate => candidate.Id == useful.Id,
			"a whole-word match outranks any number of names that only contain the word, so no alphabetical scan limit hides it");
	}
	[Fact]
	public async Task ReceiptSuggestions_ReturnAtMostTwentyUsefulCandidates()
	{
		await ResetAsync();
		await SeedSuggestionNamesAsync(Enumerable.Range(0, 30).Select(i => $"Milk {i:D3}"));
		var service = new ReceiptNormalizationCandidatesService(new Factory(fixture), TimeProvider.System);

		var candidates = await service.GetCandidatesAsync(Actor,
			new("Synthetic market", "Whole milk", null), CancellationToken.None);

		candidates.Should().HaveCount(20);
		candidates.Should().OnlyContain(candidate => candidate.Channels.Contains("lexical"));
	}

	private async Task SeedSuggestionNamesAsync(IEnumerable<string> names)
	{
		await using var db = fixture.CreateDbContext();
		db.NormalizedDescriptions.AddRange(names.Select(name => new NormalizedDescriptionEntity
		{
			Id = Guid.NewGuid(),
			CanonicalName = name,
			Status = NormalizedDescriptionStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow,
		}));
		await db.SaveChangesAsync();
	}

	[Fact]
	public async Task ReceiptSuggestions_ActualTrigramHistoryAdmitsPackagingFalsePositiveButHandlerRejectsIt()
	{
		await ResetAsync();
		await ItemsAsync(new("Synthetic market", "Hammer 12 oz", null), 1);
		await ItemsAsync(new("Synthetic market", "Milk", null), 1);
		await ItemsAsync(new("Synthetic market", "Acme mallet", null), 1);
		var embedding = new Mock<IEmbeddingService>(MockBehavior.Strict);
		var retrieval = new ItemTemplateSimilarityService(new Factory(fixture), embedding.Object,
			NullLogger<ItemTemplateSimilarityService>.Instance);
		var handler = new GetSimilarItemsQueryHandler(retrieval);

		var raw = await retrieval.GetSimilarItemsAsync("Beans 12 oz", 5, .3, false, CancellationToken.None);
		var filtered = await handler.Handle(new("Beans 12 oz", 5, .3, false), CancellationToken.None);
		var useful = await handler.Handle(new("mil", 5, .3, false), CancellationToken.None);
		var sharedBrandRaw = await retrieval.GetSimilarItemsAsync("Acme beans", 5, .2, false, CancellationToken.None);
		var sharedBrandFiltered = await handler.Handle(new("Acme beans", 5, .2, false), CancellationToken.None);

		raw.Should().Contain(candidate => candidate.Name == "Hammer 12 oz" && candidate.Source == "history",
			"real PostgreSQL trigram similarity admits this packaging-only overlap above the default threshold");
		filtered.Should().BeEmpty();
		useful.Should().Contain(candidate => candidate.Name == "Milk" && candidate.Source == "history");
		sharedBrandRaw.Should().Contain(candidate => candidate.Name == "Acme mallet" && candidate.Source == "history");
		sharedBrandFiltered.Should().BeEmpty("a shared brand alone does not support a product completion");
		embedding.VerifyNoOtherCalls();
	}

	public static IEnumerable<object[]> SuggestionCorpusCases()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName,
			"tests", "Fixtures", "receipt-suggestion-quality.synthetic.json")))
		{
			directory = directory.Parent;
		}
		if (directory is null) { throw new FileNotFoundException("Synthetic receipt suggestion corpus not found."); }
		using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName,
			"tests", "Fixtures", "receipt-suggestion-quality.synthetic.json")));
		return corpus.RootElement.GetProperty("cases").EnumerateArray()
			.Select(row => new object[] { row.GetProperty("id").GetString()!, row.GetRawText() }).ToArray();
	}

	[Theory]
	[MemberData(nameof(SuggestionCorpusCases))]
	public async Task ReceiptSuggestions_SyntheticCorpus(string caseId, string json)
	{
		await ResetAsync();
		using var parsed = JsonDocument.Parse(json);
		var row = parsed.RootElement;
		var targets = new Dictionary<string, NormalizedDescriptionEntity>();
		foreach (var name in row.GetProperty("catalog").EnumerateArray())
		{
			targets[name.GetString()!] = await CanonicalAsync(name.GetString()!);
		}
		string? Optional(JsonElement element, string name) => element.TryGetProperty(name, out var value)
			? value.GetString() : null;
		NormalizationSourceKey Key(JsonElement element) => new(Optional(element, "location") ?? "Synthetic market",
			element.GetProperty("description").GetString()!, Optional(element, "itemCode"));
		var source = Key(row);
		var catalog = Service();
		foreach (var property in new[] { "approvedExample", "unrelatedPriorSource" })
		{
			if (!row.TryGetProperty(property, out var prior)) { continue; }
			var priorSource = Key(prior);
			var target = targets[Optional(prior, "target") ?? priorSource.Description];
			await ItemsAsync(priorSource, 1);
			var preview = await catalog.PreviewAsync(Actor, Request(target) with { Source = priorSource }, CancellationToken.None);
			await catalog.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		}
		await using var before = fixture.CreateDbContext();
		int mappings = await before.NormalizationSourceMappings.CountAsync();
		int items = await before.ReceiptItems.CountAsync();
		var service = new ReceiptNormalizationCandidatesService(new Factory(fixture), TimeProvider.System);

		var candidates = await service.GetCandidatesAsync(Actor, source, CancellationToken.None);

		string[] offered = candidates.Select(candidate => candidate.CanonicalName).ToArray();
		if (row.GetProperty("expect").GetString() == "abstain") { offered.Should().BeEmpty(caseId); }
		if (row.TryGetProperty("offer", out var expected))
		{
			offered.Should().BeEquivalentTo(expected.EnumerateArray().Select(value => value.GetString()), caseId);
		}
		if (row.TryGetProperty("reject", out var rejected))
		{
			offered.Should().NotIntersectWith(rejected.EnumerateArray().Select(value => value.GetString()), caseId);
		}
		await using var after = fixture.CreateDbContext();
		(await after.NormalizationSourceMappings.CountAsync()).Should().Be(mappings, "retrieval cannot create mappings");
		(await after.ReceiptItems.CountAsync()).Should().Be(items, "retrieval cannot create purchase records");
	}

	[Fact]
	public async Task ReceiptSuggestions_RetainApprovedDescriptionsAcrossStoreAndCodeChangesAsOptionalEvidence()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks 8 count");
		await ItemsAsync(Source, 1);
		var catalog = Service();
		var preview = await catalog.PreviewAsync(Actor, Request(target), CancellationToken.None);
		await catalog.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		var service = new ReceiptNormalizationCandidatesService(new Factory(fixture), TimeProvider.System);

		var candidates = await service.GetCandidatesAsync(Actor,
			Source with { Location = "Synthetic other market", ItemCode = "changed-code" }, CancellationToken.None);

		candidates.Should().Contain(candidate => candidate.Id == target.Id && candidate.Channels.Contains("approved_example"));
		// This is retrieval evidence, never permission to create a cross-store rule.
		(await catalog.ListMappingsAsync(Actor,
			Source with { Location = "Synthetic other market", ItemCode = "changed-code" }, CancellationToken.None))
			.Should().BeEmpty();
		await using var verify = fixture.CreateDbContext();
		(await verify.NormalizationSourceMappings.CountAsync()).Should().Be(1);
		(await verify.ReceiptItems.CountAsync()).Should().Be(1);
	}
}
