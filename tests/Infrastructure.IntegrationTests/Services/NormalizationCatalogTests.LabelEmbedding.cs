using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pgvector;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task LabelEmbedding_RenameAddsALabelVectorWithoutAnotherRevision()
	{
		await ResetAsync();
		// Each text embeds to its own axis, so a similarity shows which vector matched. Seeded templates share axis 3.
		Dictionary<string, int> axes = new(StringComparer.Ordinal) { ["KS ORG CHKN BRST"] = 0, ["Chicken breast"] = 1, ["Other name"] = 2 };
		var vectors = new Mock<IEmbeddingService>();
		vectors.SetupGet(x => x.IsConfigured).Returns(true);
		vectors.Setup(x => x.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((List<string> texts, CancellationToken _) => texts.Select(text => Axis(axes.GetValueOrDefault(text, 3))).ToList());
		var target = await CanonicalAsync("KS ORG CHKN BRST");
		await CanonicalAsync("Other name");
		var services = new ServiceCollection();
		services.AddSingleton(vectors.Object);
		services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
		using var provider = services.BuildServiceProvider();
		using var worker = new EmbeddingGenerationService(provider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<EmbeddingGenerationService>.Instance, publisher.Object);
		var names = new NormalizedDescriptionService(new Factory(fixture), vectors.Object, new NormalizedDescriptionMapper());
		await DrainAsync(worker);
		var built = await ReadTargetAsync(target.Id);
		(await names.GetEmbeddingCoverageAsync(CancellationToken.None)).CanonicalReady.Should().Be(2);
		(await LabelSimilarityAsync(target.Id)).Should().Be(0, "an unlabelled name matches through its canonical name only");

		await names.RenameAsync(target.Id, "Chicken breast", CancellationToken.None);

		var renamed = await ReadTargetAsync(target.Id);
		renamed.NormalizationRevision.Should().NotBe(built.NormalizationRevision, "a rename is a business edit");
		(await names.GetEmbeddingCoverageAsync(CancellationToken.None)).CanonicalReady.Should().Be(1);
		(await LabelSimilarityAsync(target.Id)).Should().Be(0, "no label vector exists until the worker builds it");

		(await worker.ProcessPendingEmbeddingsAsync(CancellationToken.None)).Should().Be(1, "only the new label is stale");

		var rebuilt = await ReadTargetAsync(target.Id);
		rebuilt.NormalizationRevision.Should().Be(renamed.NormalizationRevision, "a label vector is a projection");
		rebuilt.Embedding!.ToArray()[0].Should().Be(1, "the canonical vector is kept beside the label's");
		(await names.GetEmbeddingCoverageAsync(CancellationToken.None)).CanonicalReady.Should().Be(2);
		(await LabelSimilarityAsync(target.Id)).Should().BeApproximately(1, 1e-6);
		await using (var db = fixture.CreateDbContext())
		{
			(await NearestNeighbourSearch.ToListAsync(db.Database, CatalogSemanticNeighbours.Nearest(db.Database, new Vector(Axis(1)), 20),
				NullLogger.Instance, CancellationToken.None)).First().Id.Should().Be(target.Id, "the label vector is searched");
		}
		(await worker.ProcessPendingEmbeddingsAsync(CancellationToken.None)).Should().Be(0);

		await names.RenameAsync(target.Id, "Chicken breasts", CancellationToken.None);
		(await names.GetEmbeddingCoverageAsync(CancellationToken.None)).CanonicalReady.Should().Be(1);
		(await LabelSimilarityAsync(target.Id)).Should().Be(0, "a vector of a replaced label no longer counts");
		await names.RenameAsync(target.Id, null, CancellationToken.None);
		(await names.GetEmbeddingCoverageAsync(CancellationToken.None)).CanonicalReady.Should().Be(2, "a cleared label needs no vector");
		(await worker.ProcessPendingEmbeddingsAsync(CancellationToken.None)).Should().Be(0);
	}

	private static async Task DrainAsync(EmbeddingGenerationService worker)
	{
		for (int cycle = 0; cycle < 10 && await worker.ProcessPendingEmbeddingsAsync(CancellationToken.None) > 0; cycle++) { }
	}

	private async Task<NormalizedDescriptionEntity> ReadTargetAsync(Guid id)
	{
		await using var db = fixture.CreateDbContext();
		return await db.NormalizedDescriptions.AsNoTracking().SingleAsync(x => x.Id == id);
	}

	/// <summary>The target's similarity to the label's axis: 1 through a current label vector, 0 through the canonical one.</summary>
	private async Task<double> LabelSimilarityAsync(Guid id)
	{
		await using var db = fixture.CreateDbContext();
		return await CatalogSemanticNeighbours.SimilarityOf(db.Database, new Vector(Axis(1)), id).SingleAsync();
	}

	private static float[] Axis(int axis)
	{
		var values = new float[OnnxEmbeddingService.EmbeddingDimension];
		values[axis] = 1;
		return values;
	}
}
