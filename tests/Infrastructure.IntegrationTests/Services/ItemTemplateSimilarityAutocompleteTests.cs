using System.Data.Common;
using Application.Interfaces.Services;
using Application.Queries.Core.ItemTemplate.GetSimilarItems;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pgvector;

namespace Infrastructure.IntegrationTests.Services;

/// <summary>Description autocomplete: index use, row selection and semantic recall (RECEIPTS-1098).</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class ItemTemplateSimilarityAutocompleteTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Query_FiltersThroughTheTrigramIndexes(bool semantic)
	{
		await SeededTemplateCleanup.RemoveFromFreshDatabaseAsync(fixture);
		await SeedHistoryAsync(("Organic Milk Gallon", new DateOnly(2025, 1, 15), 4.25m, "MILK-1", null));
		await using ApplicationDbContext context = fixture.CreateDbContext();
		await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync();
		// With sequential and plain index scans priced out, only an index that can evaluate the filter avoids a
		// disabled plan. On a tiny table a full scan of an unrelated btree, or reaching items from receipts by a
		// nested loop, is otherwise cheaper than the GIN index; on real data the planner picks the trigram index
		// unprompted. similarity() >= t could use no index at all.
		await context.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off");
		await context.Database.ExecuteSqlRawAsync("SET LOCAL enable_indexscan = off");
		await context.Database.ExecuteSqlRawAsync("SET LOCAL enable_nestloop = off");
		await ItemTemplateSimilarityService.SetThresholdAsync(context, 0.3, CancellationToken.None);
		(string sql, NpgsqlParameter[] parameters) = ItemTemplateSimilarityService.Query("organic milk",
			semantic ? new Vector(Axis(0)) : null, 5);

		string plan = await ExplainAsync(context, transaction, sql, parameters);

		plan.Should().Contain("Index Scan on \"IX_ReceiptItems_Description_trgm\"")
			.And.Contain("Index Cond: (\"Description\" % ");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task GetSimilarItemsAsync_HistoryDefaultsComeFromTheLatestPurchaseThenTheLatestId(bool semantic)
	{
		await SeededTemplateCleanup.RemoveFromFreshDatabaseAsync(fixture);
		Guid lowerId = Guid.Parse("00000000-0000-7000-8000-000000000001");
		Guid higherId = Guid.Parse("00000000-0000-7000-8000-000000000002");
		await SeedHistoryAsync(
			("Organic Milk Gallon", new DateOnly(2025, 3, 1), 4.75m, "LATEST-HIGH", higherId),
			("Organic Milk Gallon", new DateOnly(2025, 1, 10), 3.10m, "OLDEST", null),
			("Organic Milk Gallon", new DateOnly(2025, 3, 1), 4.50m, "LATEST-LOW", lowerId));

		List<SimilarItemResult> results = await Service(new AxisEmbeddings()).GetSimilarItemsAsync(
			"Organic Milk", 5, 0.1, semantic, CancellationToken.None);

		SimilarItemResult history = results.Should().ContainSingle().Subject;
		history.DefaultUnitPrice.Should().Be(4.75m);
		history.DefaultItemCode.Should().Be("LATEST-HIGH");
	}

	[Fact]
	public async Task Handle_OffersAnAbbreviatedNeighbourTheTrigramThresholdMisses()
	{
		await SeededTemplateCleanup.RemoveFromFreshDatabaseAsync(fixture);
		List<Guid> chicken = await SeedHistoryAsync([.. Enumerable.Range(0, 40).Select(day =>
			("KS CHKN BRST", new DateOnly(2025, 1, 1).AddDays(day), 9.99m, (string?)"1001", (Guid?)null))]);
		List<Guid> towels = await SeedHistoryAsync(("PAPER TWLS 6PK", new DateOnly(2025, 1, 1), 7.99m, null, null));
		await SeedVectorsAsync(chicken, "KS CHKN BRST", Axis(0));
		// Cosine 0.7 to the query: a neighbour, but below the semantic floor.
		await SeedVectorsAsync(towels, "PAPER TWLS 6PK", Blend(0.7));
		GetSimilarItemsQueryHandler handler = new(Service(new AxisEmbeddings()));

		List<SimilarItemResult> results = [.. await handler.Handle(new("chicken br", 5, 0.3), CancellationToken.None)];

		SimilarItemResult offered = results.Should().ContainSingle().Subject;
		offered.Name.Should().Be("KS CHKN BRST");
		offered.AdmittedBySemanticSearchOnly.Should().BeTrue("\"chicken br\" shares too few trigrams with it");
		offered.SemanticSimilarity.Should().BeApproximately(1, 1e-6);
		offered.DefaultUnitPrice.Should().Be(9.99m);
	}

	[Fact]
	public async Task Handle_FindsANeighbourBehindMoreRepeatsOfOneDescriptionThanItsOwnBudgetOfNames()
	{
		await SeededTemplateCleanup.RemoveFromFreshDatabaseAsync(fixture);
		// Purchases repeat a description, one vector each; 150 nearer repeats would fill a budget of 100 rows.
		List<Guid> repeats = await SeedHistoryAsync([.. Enumerable.Range(0, 150).Select(day =>
			("KS CHKN BRST", new DateOnly(2025, 1, 1).AddDays(day), 9.99m, (string?)null, (Guid?)null))]);
		List<Guid> other = await SeedHistoryAsync(("KS BNLS SKNLS BRST", new DateOnly(2025, 1, 1), 11.49m, null, null));
		await SeedVectorsAsync(repeats, "KS CHKN BRST", Axis(0));
		await SeedVectorsAsync(other, "KS BNLS SKNLS BRST", Blend(0.95));
		GetSimilarItemsQueryHandler handler = new(Service(new AxisEmbeddings()));

		List<SimilarItemResult> results = [.. await handler.Handle(new("chicken br", 5, 0.3), CancellationToken.None)];

		results.Select(result => result.Name).Should().Equal("KS CHKN BRST", "KS BNLS SKNLS BRST");
	}

	[Fact]
	public async Task Handle_KeepsACompletionWhenManyNeighboursBelowTheFloorOutscoreIt()
	{
		await SeededTemplateCleanup.RemoveFromFreshDatabaseAsync(fixture);
		// No vector, so it scores by its low trigram similarity alone.
		await SeedHistoryAsync(("CHICKEN BREAST BONELESS SKINLESS FAMILY PACK", new DateOnly(2025, 1, 1), 14.99m, null, null));
		// Each scores 0.6 * 0.79 by vector alone: above the completion, more than the reserve, and below the floor.
		foreach (int n in Enumerable.Range(0, 30))
		{
			string name = $"ZZ{n:D2} QX";
			await SeedVectorsAsync(await SeedHistoryAsync((name, new DateOnly(2025, 1, 1), 1m, null, null)), name, Blend(0.79));
		}
		GetSimilarItemsQueryHandler handler = new(Service(new AxisEmbeddings()));

		List<SimilarItemResult> results = [.. await handler.Handle(new("chicken br", 5, 0.1), CancellationToken.None)];

		results.Should().ContainSingle().Which.Name.Should().Be("CHICKEN BREAST BONELESS SKINLESS FAMILY PACK");
	}

	[Fact]
	public async Task Handle_OffersATemplateNeighbourOnceAndInPlaceOfItsHistory()
	{
		await SeededTemplateCleanup.RemoveFromFreshDatabaseAsync(fixture);
		Guid template = Guid.NewGuid();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.ItemTemplates.Add(new ItemTemplateEntity { Id = template, Name = "KS CHKN BRST", DefaultUnitPrice = 8.49m, DefaultUnitPriceCurrency = Currency.USD });
			seed.ItemEmbeddings.Add(new ItemEmbeddingEntity
			{
				Id = Guid.NewGuid(),
				EntityType = "ItemTemplate",
				EntityId = template,
				EntityText = "KS CHKN BRST",
				Embedding = new Vector(Axis(0)),
				ModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}
		await SeedVectorsAsync(await SeedHistoryAsync(("ks chkn brst", new DateOnly(2025, 1, 1), 9.99m, null, null)), "ks chkn brst", Axis(0));
		GetSimilarItemsQueryHandler handler = new(Service(new AxisEmbeddings()));

		List<SimilarItemResult> results = [.. await handler.Handle(new("chicken br", 5, 0.3), CancellationToken.None)];

		SimilarItemResult offered = results.Should().ContainSingle().Subject;
		offered.Source.Should().Be("template");
		offered.AdmittedBySemanticSearchOnly.Should().BeTrue();
		offered.DefaultUnitPrice.Should().Be(8.49m);
	}

	[Fact]
	public async Task GetSimilarItemsAsync_RowsWithoutACurrentVectorScoreByTrigramAlone()
	{
		await SeededTemplateCleanup.RemoveFromFreshDatabaseAsync(fixture);
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.ItemTemplates.Add(new ItemTemplateEntity { Id = Guid.NewGuid(), Name = "Organic Milk" });
			await seed.SaveChangesAsync();
		}
		List<Guid> history = await SeedHistoryAsync(("Organic Milk Gallon", new DateOnly(2025, 1, 15), 4.25m, null, null));
		await SeedVectorsAsync(history, "Organic Milk Gallon", Blend(0.5));

		List<SimilarItemResult> results = await Service(new AxisEmbeddings()).GetSimilarItemsAsync(
			"Organic Milk", 5, 0.1, true, CancellationToken.None);

		SimilarItemResult template = results.Single(result => result.Source == "template");
		template.SemanticSimilarity.Should().BeNull();
		template.CombinedScore.Should().Be(template.Similarity, "a missing vector is not a zero similarity");
		SimilarItemResult embedded = results.Single(result => result.Source == "history");
		embedded.SemanticSimilarity.Should().BeApproximately(0.5, 1e-6);
		embedded.CombinedScore.Should().BeApproximately(0.4 * embedded.Similarity + 0.6 * 0.5, 1e-6);
		results.Should().NotContain(result => result.AdmittedBySemanticSearchOnly);
	}

	private ItemTemplateSimilarityService Service(IEmbeddingService embeddings) =>
		new(new FixtureDbContextFactory(fixture), embeddings, NullLogger<ItemTemplateSimilarityService>.Instance);

	private async Task<List<Guid>> SeedHistoryAsync(params (string Description, DateOnly Date, decimal Price, string? Code, Guid? Id)[] rows)
	{
		await using ApplicationDbContext seed = fixture.CreateDbContext();
		List<Guid> ids = [];
		foreach (var row in rows)
		{
			ReceiptEntity receipt = new() { Id = Guid.NewGuid(), Location = "Synthetic market", Date = row.Date, TaxAmountCurrency = Currency.USD };
			seed.Receipts.Add(receipt);
			Guid id = row.Id ?? Guid.NewGuid();
			ids.Add(id);
			seed.ReceiptItems.Add(new ReceiptItemEntity
			{
				Id = id,
				ReceiptId = receipt.Id,
				Description = row.Description,
				ReceiptItemCode = row.Code,
				Quantity = 1m,
				UnitPrice = row.Price,
				UnitPriceCurrency = Currency.USD,
				TotalAmount = row.Price,
				TotalAmountCurrency = Currency.USD,
				Category = $"Category {ids.Count}",
			});
		}
		await seed.SaveChangesAsync();
		return ids;
	}

	private async Task SeedVectorsAsync(IEnumerable<Guid> itemIds, string text, float[] vector)
	{
		await using ApplicationDbContext seed = fixture.CreateDbContext();
		seed.ItemEmbeddings.AddRange(itemIds.Select(id => new ItemEmbeddingEntity
		{
			Id = Guid.NewGuid(),
			EntityType = "ReceiptItem",
			EntityId = id,
			EntityText = text,
			Embedding = new Vector(vector),
			ModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
			CreatedAt = DateTimeOffset.UtcNow,
		}));
		await seed.SaveChangesAsync();
	}

	private static async Task<string> ExplainAsync(ApplicationDbContext context, IDbContextTransaction transaction, string sql, NpgsqlParameter[] parameters)
	{
		DbConnection connection = context.Database.GetDbConnection();
		await using DbCommand command = connection.CreateCommand();
		command.Transaction = transaction.GetDbTransaction();
		command.CommandText = "EXPLAIN " + sql;
		command.Parameters.AddRange(parameters);
		List<string> lines = [];
		await using DbDataReader reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) { lines.Add(reader.GetString(0)); }
		return string.Join('\n', lines);
	}

	private static float[] Axis(int axis)
	{
		float[] values = new float[OnnxEmbeddingService.EmbeddingDimension];
		values[axis] = 1;
		return values;
	}

	/// <summary>A unit vector at the given cosine to <see cref="Axis"/>(0).</summary>
	private static float[] Blend(double cosine)
	{
		float[] values = new float[OnnxEmbeddingService.EmbeddingDimension];
		values[0] = (float)cosine;
		values[1] = (float)Math.Sqrt(1 - cosine * cosine);
		return values;
	}

	/// <summary>Every query embeds to axis 0.</summary>
	private sealed class AxisEmbeddings : IEmbeddingService
	{
		public bool IsConfigured => true;

		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken) => Task.FromResult(Axis(0));

		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken)
			=> throw new NotSupportedException();
	}

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
