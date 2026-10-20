using Application.Interfaces.Services;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Moq;
using Pgvector;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task Candidates_ReserveNearestSemanticEvidenceDespiteAlphabeticallyEarlierLexicalDistractors()
	{
		await ResetAsync();
		float[] query = new float[OnnxEmbeddingService.EmbeddingDimension]; query[0] = 1;
		var nearest = new NormalizedDescriptionEntity
		{
			Id = Guid.NewGuid(),
			CanonicalName = "ZZZ Vienna sausages 8 count",
			Status = NormalizedDescriptionStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow,
			Embedding = new Vector(query),
			EmbeddingModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
		};
		await using (var db = fixture.CreateDbContext())
		{
			db.NormalizedDescriptions.Add(nearest);
			for (int index = 0; index < 30; index++)
			{
				float[] distant = new float[query.Length]; distant[1] = 1;
				db.NormalizedDescriptions.Add(new()
				{
					Id = Guid.NewGuid(),
					CanonicalName = $"AAA Beef franks distractor {index:D2}",
					Status = NormalizedDescriptionStatus.Active,
					CreatedAt = DateTimeOffset.UtcNow,
					Embedding = new Vector(distant),
					EmbeddingModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
				});
			}
			for (int index = 1; index <= 10; index++)
			{
				float[] neighbor = new float[query.Length]; neighbor[0] = 1; neighbor[1] = index / 10f;
				db.NormalizedDescriptions.Add(new()
				{
					Id = Guid.NewGuid(),
					CanonicalName = $"YYY Sausage variant {11 - index:D2}",
					Status = NormalizedDescriptionStatus.Active,
					CreatedAt = DateTimeOffset.UtcNow,
					Embedding = new Vector(neighbor),
					EmbeddingModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
				});
			}
			await db.SaveChangesAsync();
		}
		var embedding = new Mock<IEmbeddingService>(MockBehavior.Strict);
		embedding.SetupGet(x => x.IsConfigured).Returns(true);
		embedding.Setup(x => x.GenerateEmbeddingAsync("Beef franks", It.IsAny<CancellationToken>())).ReturnsAsync(query);
		var service = new NormalizationCatalogService(new Factory(fixture), TimeProvider.System, publisher.Object, embedding.Object);
		var candidates = await service.GetCandidatesAsync(Actor, Source with { Description = "Beef franks" }, CancellationToken.None);
		candidates.Should().HaveCount(20);
		candidates.First().Id.Should().Be(nearest.Id, "candidate rank must preserve the closest pgvector neighbor, not lexical name order");
		candidates.Single(x => x.Id == nearest.Id).Channels.Should().Contain("semantic");
		candidates.Should().Contain(x => x.Channels.Contains("lexical"), "both retrieval channels must contribute evidence");
		embedding.VerifyAll();
	}
}
