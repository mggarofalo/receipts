using Application.Interfaces.Services;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class EmbeddingFingerprintEligibilityTests(PostgresFixture fixture)
{
	[Fact]
	public async Task GetOrCreateAsync_StaleSemanticMatch_IsIgnoredAndExactCanonicalIdentityStillWorks()
	{
		await ResetTablesAsync();
		Guid staleId = Guid.NewGuid();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = staleId,
				CanonicalName = "Different canonical concept",
				Status = NormalizedDescriptionStatus.Active,
				Embedding = UnitVector(),
				EmbeddingModelVersion = "obsolete-space",
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}
		NormalizedDescriptionService service = new(
			new FixtureDbContextFactory(fixture),
			new FixedEmbeddingService(),
			new NormalizedDescriptionMapper(),
			new NormalizedDescriptionSettingsMapper());

		Application.Models.NormalizedDescriptions.GetOrCreateResult created =
			await service.GetOrCreateAsync("Brand new concept", CancellationToken.None);
		Application.Models.NormalizedDescriptions.GetOrCreateResult exact =
			await service.GetOrCreateAsync("different CANONICAL concept", CancellationToken.None);

		created.Description.Id.Should().NotBe(staleId,
			"an obsolete vector must not participate in semantic canonical matching");
		exact.Description.Id.Should().Be(staleId,
			"fingerprint gating semantic search must not hide exact canonical identity");
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await verify.NormalizedDescriptions.CountAsync()).Should().Be(2);
	}

	[Theory]
	[InlineData(false, 0.0)]
	[InlineData(true, 1.0)]
	public async Task GetSimilarItemsAsync_HybridSearch_UsesOnlyCurrentFingerprint(
		bool currentFingerprint,
		double expectedSemanticSimilarity)
	{
		await ResetTablesAsync();
		string fingerprint = currentFingerprint
			? OnnxEmbeddingService.EmbeddingSpaceFingerprint
			: "obsolete-space";
		Guid templateId = Guid.NewGuid();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.ItemTemplates.Add(new ItemTemplateEntity { Id = templateId, Name = "Organic Milk" });
			seed.ItemEmbeddings.Add(new ItemEmbeddingEntity
			{
				Id = Guid.NewGuid(),
				EntityType = "ItemTemplate",
				EntityId = templateId,
				EntityText = "Organic Milk",
				Embedding = UnitVector(),
				ModelVersion = fingerprint,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}
		ItemTemplateSimilarityService service = new(
			new FixtureDbContextFactory(fixture),
			new FixedEmbeddingService(),
			NullLogger<ItemTemplateSimilarityService>.Instance);

		List<Application.Queries.Core.ItemTemplate.GetSimilarItems.SimilarItemResult> results =
			await service.GetSimilarItemsAsync("Organic Milk", 5, 0, true, CancellationToken.None);

		results.Should().ContainSingle();
		results[0].SemanticSimilarity.Should().BeApproximately(expectedSemanticSimilarity, 0.0001);
	}

	private static Vector UnitVector()
	{
		float[] values = new float[OnnxEmbeddingService.EmbeddingDimension];
		values[0] = 1;
		return new Vector(values);
	}

	private async Task ResetTablesAsync()
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		await context.Database.ExecuteSqlRawAsync(
			"""TRUNCATE library."ItemTemplates", matching."ItemEmbeddings", matching."NormalizedDescriptions" RESTART IDENTITY CASCADE;""");
	}

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}

	private sealed class FixedEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => true;

		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
			=> Task.FromResult(UnitVector().ToArray());

		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken)
			=> Task.FromResult(texts.Select(_ => UnitVector().ToArray()).ToList());
	}
}
