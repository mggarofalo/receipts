using System.Text.Json;
using FluentAssertions;
using Infrastructure.Migrations;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	// RECEIPTS-1094: the lexical channel ranks by how many source words a name contains as whole
	// words, not alphabetically, so names sharing one token cannot evict the name that matches all.
	[Fact]
	public async Task Candidates_RankTheNameMatchingMostWordsFirstAmongLexicalCandidates()
	{
		await ResetAsync();
		await SeedSuggestionNamesAsync(Enumerable.Range(0, 25).Select(i => $"Chicken a{i:D2}"));
		var target = await CanonicalAsync("Chicken breast");

		var candidates = await Service().GetCandidatesAsync(Actor, Source with { Description = "KS CHICKEN BREAST 6LB" }, CancellationToken.None);

		candidates.Where(candidate => candidate.Channels.Contains("lexical")).Select(candidate => candidate.Id)
			.Should().StartWith([target.Id], "a name matching chicken and breast outranks names matching chicken alone");
	}

	[Fact]
	public async Task Candidates_RankAWholeWordMatchAboveASubstringMatch()
	{
		await ResetAsync();
		var substring = await CanonicalAsync("A000 milkshake");
		var wholeWord = await CanonicalAsync("Milk");

		var candidates = await Service().GetCandidatesAsync(Actor, Source with { Description = "Whole milk" }, CancellationToken.None);

		candidates.Select(candidate => candidate.Id).Should().ContainInOrder(wholeWord.Id, substring.Id);
	}

	[Theory]
	[InlineData(AddNormalizedDescriptionTrigramIndexes.CanonicalNameIndex, "lower(\"CanonicalName\")")]
	[InlineData(AddNormalizedDescriptionTrigramIndexes.DisplayNameIndex, "lower(COALESCE(\"DisplayLabel\", \"CanonicalName\"))")]
	public async Task LexicalFilter_UsesTheTrigramIndex(string index, string expression)
	{
		await ResetAsync();
		await using var db = fixture.CreateDbContext();
		await using var transaction = await db.Database.BeginTransactionAsync();
		// Small tables always plan a sequential scan; forbid it to see which index the filter can use.
		await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off");

		await using var command = db.Database.GetDbConnection().CreateCommand();
		command.Transaction = transaction.GetDbTransaction();
		command.CommandText = $"""EXPLAIN (FORMAT JSON) SELECT "Id" FROM matching."NormalizedDescriptions" WHERE {expression} LIKE '%chicken%'""";
		string plan = (string)(await command.ExecuteScalarAsync())!;

		using JsonDocument document = JsonDocument.Parse(plan);
		IEnumerable<JsonElement> Nodes(JsonElement node) => node.TryGetProperty("Plans", out JsonElement children)
			? children.EnumerateArray().SelectMany(Nodes).Prepend(node) : [node];
		Nodes(document.RootElement[0].GetProperty("Plan")).Should().Contain(node =>
			node.GetProperty("Node Type").GetString() == "Bitmap Index Scan"
			&& node.GetProperty("Index Name").GetString() == index);
	}

	[Fact]
	public async Task LexicalQuery_FiltersThroughBothTrigramIndexes()
	{
		await ResetAsync();
		(string sql, var parameters) = NormalizationCandidateRetrieval.LexicalQuery(["ks", "chicken", "breast"], "KS CHICKEN BREAST");
		await using var db = fixture.CreateDbContext();
		await using var transaction = await db.Database.BeginTransactionAsync();
		await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off");
		await using var command = db.Database.GetDbConnection().CreateCommand();
		command.Transaction = transaction.GetDbTransaction();
		command.CommandText = $"EXPLAIN (FORMAT JSON) {sql}";
		command.Parameters.AddRange(parameters);

		using JsonDocument document = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
		IEnumerable<JsonElement> Nodes(JsonElement node) => node.TryGetProperty("Plans", out JsonElement children)
			? children.EnumerateArray().SelectMany(Nodes).Prepend(node) : [node];
		string[] indexes = Nodes(document.RootElement[0].GetProperty("Plan"))
			.Where(node => node.GetProperty("Node Type").GetString() == "Bitmap Index Scan")
			.Select(node => node.GetProperty("Index Name").GetString()!).Distinct().ToArray();
		indexes.Should().BeEquivalentTo([AddNormalizedDescriptionTrigramIndexes.CanonicalNameIndex,
			AddNormalizedDescriptionTrigramIndexes.DisplayNameIndex], "the production filter, a two-letter token included, stays index-supported");
	}

	[Fact]
	public async Task Candidates_CountAWordBoundedByPunctuationAsWhole()
	{
		await ResetAsync();
		var plain = await CanonicalAsync("Ham");
		var sized = await CanonicalAsync("Ham (lg)");
		await using (var db = fixture.CreateDbContext())
		{
			// A display label far from the description, so trigram similarity alone would rank it second.
			(await db.NormalizedDescriptions.SingleAsync(row => row.Id == sized.Id)).DisplayLabel = "Picnic shoulder smoked (lg)";
			await db.SaveChangesAsync();
		}

		var candidates = await Service().GetCandidatesAsync(Actor, Source with { Description = "HAM (LG)" }, CancellationToken.None);

		candidates.Select(candidate => candidate.Id).Should().ContainInOrder(sized.Id, plain.Id);
	}

	[Fact]
	public async Task Candidates_AcceptASupplementaryCharacterInTheDescription()
	{
		await ResetAsync();
		var pizza = await CanonicalAsync("Pizza");

		var candidates = await Service().GetCandidatesAsync(Actor, Source with { Description = "\U0001F355 PIZZA" }, CancellationToken.None);

		candidates.Should().Contain(candidate => candidate.Id == pizza.Id);
	}

	[Fact]
	public async Task ReceiptSuggestions_ReturnAtMostTwentyActiveIdentities()
	{
		await ResetAsync();
		var exact = await CanonicalAsync("Franks");
		await SeedSuggestionNamesAsync(Enumerable.Range(0, 30).Select(i => $"Franks {i:D2}"));
		await using (var db = fixture.CreateDbContext())
		{
			(await db.NormalizedDescriptions.SingleAsync(row => row.CanonicalName == "Franks 00")).Status = Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Rejected;
			await db.SaveChangesAsync();
		}
		var service = new ReceiptNormalizationCandidatesService(new Factory(fixture), TimeProvider.System);

		var candidates = await service.GetCandidatesAsync(Actor, new("Synthetic market", "Franks", null), CancellationToken.None);

		candidates.Should().HaveCount(20);
		candidates[0].Id.Should().Be(exact.Id);
		candidates.Should().NotContain(candidate => candidate.CanonicalName == "Franks 00");
	}
}
