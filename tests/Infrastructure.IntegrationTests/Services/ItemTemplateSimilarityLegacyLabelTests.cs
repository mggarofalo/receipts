using Application.Interfaces.Services;
using Application.Queries.Core.ItemTemplate.GetSimilarItems;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

/// <summary>
/// History matches no longer carry a receipt item's legacy category labels (RECEIPTS-1085);
/// templates keep their own defaults. Both the hybrid and trigram-only SQL paths are pinned.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class ItemTemplateSimilarityLegacyLabelTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task GetSimilarItemsAsync_HistoryMatchesOmitLegacyLabels_TemplatesKeepDefaults(bool semantic)
	{
		await SeededTemplateCleanup.RemoveFromFreshDatabaseAsync(fixture);
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.ItemTemplates.Add(new ItemTemplateEntity
			{
				Id = Guid.NewGuid(),
				Name = "Organic Milk",
				DefaultCategory = "Template category",
				DefaultSubcategory = "Template subcategory",
			});
			ReceiptEntity receipt = new()
			{
				Id = Guid.NewGuid(),
				Location = "Synthetic market",
				Date = new(2025, 1, 15),
				TaxAmountCurrency = Currency.USD,
			};
			seed.Receipts.Add(receipt);
			seed.ReceiptItems.Add(new ReceiptItemEntity
			{
				Id = Guid.NewGuid(),
				ReceiptId = receipt.Id,
				Description = "Organic Milk Gallon",
				ReceiptItemCode = "MILK-1",
				Quantity = 1m,
				UnitPrice = 4.25m,
				UnitPriceCurrency = Currency.USD,
				TotalAmount = 4.25m,
				TotalAmountCurrency = Currency.USD,
				Category = "Legacy category",
				Subcategory = "Legacy subcategory",
			});
			await seed.SaveChangesAsync();
		}
		ItemTemplateSimilarityService service = new(new FixtureDbContextFactory(fixture),
			new FixedEmbeddingService(), NullLogger<ItemTemplateSimilarityService>.Instance);

		List<SimilarItemResult> results =
			await service.GetSimilarItemsAsync("Organic Milk", 5, 0.1, semantic, CancellationToken.None);

		SimilarItemResult history = results.Should().ContainSingle(result => result.Source == "history").Subject;
		history.Name.Should().Be("Organic Milk Gallon");
		history.DefaultCategory.Should().BeNull();
		history.DefaultSubcategory.Should().BeNull();
		history.DefaultUnitPrice.Should().Be(4.25m);
		history.DefaultItemCode.Should().Be("MILK-1");
		SimilarItemResult template = results.Should().ContainSingle(result => result.Source == "template").Subject;
		template.DefaultCategory.Should().Be("Template category");
		template.DefaultSubcategory.Should().Be("Template subcategory");
	}

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}

	private sealed class FixedEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => true;

		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
		{
			float[] values = new float[OnnxEmbeddingService.EmbeddingDimension];
			values[0] = 1;
			return Task.FromResult(values);
		}

		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken)
			=> throw new NotSupportedException();
	}
}
