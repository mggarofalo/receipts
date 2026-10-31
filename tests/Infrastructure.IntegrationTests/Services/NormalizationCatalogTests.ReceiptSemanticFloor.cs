using Application.Interfaces.Services;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Pgvector;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	// RECEIPTS-1095: receipt entry offers a nearest semantic neighbour on its own once its similarity
	// reaches ReceiptSuggestionEvidence.SemanticFloor. Vectors are synthetic unit vectors.
	[Fact]
	public async Task ReceiptSuggestions_OfferASemanticNeighbourAboveTheFloorWithoutOtherEvidence()
	{
		await ResetAsync();
		float[] query = UnitVector(1, 0);
		var target = await EmbeddedCanonicalAsync("Chicken breast", query);
		// Cosine 0.5 with the query: a neighbour, but below the floor.
		var distractor = await EmbeddedCanonicalAsync("Vegetable broth", UnitVector(0.5f, MathF.Sqrt(0.75f)));
		var service = new ReceiptNormalizationCandidatesService(new Factory(fixture), TimeProvider.System, EmbeddingReturning("KS ORG CHKN BRST", query));

		var candidates = await service.GetCandidatesAsync(Actor, new("Synthetic market", "KS ORG CHKN BRST", null), CancellationToken.None);

		candidates.Should().ContainSingle(candidate => candidate.Id == target.Id)
			.Which.Channels.Should().Equal(["semantic"]);
		candidates.Should().NotContain(candidate => candidate.Id == distractor.Id);
		await using var verify = fixture.CreateDbContext();
		(await verify.NormalizationSourceMappings.CountAsync()).Should().Be(0);
		(await verify.ReceiptItems.CountAsync()).Should().Be(0);
	}

	[Fact]
	public async Task ReceiptSuggestions_RankSemanticOnlyRowsAfterWholeWordMatches()
	{
		await ResetAsync();
		float[] query = UnitVector(1, 0);
		var semanticOnly = await EmbeddedCanonicalAsync("Poultry cutlets", query);
		var wholeWord = await EmbeddedCanonicalAsync("Chicken breast", UnitVector(0, 1));
		var service = new ReceiptNormalizationCandidatesService(new Factory(fixture), TimeProvider.System, EmbeddingReturning("Chicken breast fillet", query));

		var candidates = await service.GetCandidatesAsync(Actor, new("Synthetic market", "Chicken breast fillet", null), CancellationToken.None);

		candidates.Select(candidate => candidate.Id).Should().Equal([wholeWord.Id, semanticOnly.Id],
			"a row admitted only by similarity follows every row with specific evidence, however close it is");
	}

	[Fact]
	public async Task ReceiptSuggestions_DegradeToLexicalEvidenceWhenEmbeddingFails()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Chicken breast");
		var embedding = new Mock<IEmbeddingService>(MockBehavior.Strict);
		embedding.SetupGet(service => service.IsConfigured).Returns(true);
		embedding.Setup(service => service.GenerateEmbeddingAsync("Chicken breast fillet", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("inference queue full"));
		var service = new ReceiptNormalizationCandidatesService(new Factory(fixture), TimeProvider.System, embedding.Object);

		var candidates = await service.GetCandidatesAsync(Actor, new("Synthetic market", "Chicken breast fillet", null), CancellationToken.None);

		candidates.Should().ContainSingle(candidate => candidate.Id == target.Id)
			.Which.Channels.Should().Equal(["lexical"]);
	}

	private static float[] UnitVector(float x, float y)
	{
		float[] vector = new float[OnnxEmbeddingService.EmbeddingDimension];
		vector[0] = x;
		vector[1] = y;
		return vector;
	}

	private static IEmbeddingService EmbeddingReturning(string text, float[] vector)
	{
		var embedding = new Mock<IEmbeddingService>(MockBehavior.Strict);
		embedding.SetupGet(service => service.IsConfigured).Returns(true);
		embedding.Setup(service => service.GenerateEmbeddingAsync(text, It.IsAny<CancellationToken>())).ReturnsAsync(vector);
		return embedding.Object;
	}

	private async Task<NormalizedDescriptionEntity> EmbeddedCanonicalAsync(string name, float[] vector)
	{
		await using var db = fixture.CreateDbContext();
		var entity = new NormalizedDescriptionEntity
		{
			Id = Guid.NewGuid(),
			CanonicalName = name,
			Status = NormalizedDescriptionStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow,
			Embedding = new Vector(vector),
			EmbeddingModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
		};
		db.NormalizedDescriptions.Add(entity);
		await db.SaveChangesAsync();
		return entity;
	}
}
