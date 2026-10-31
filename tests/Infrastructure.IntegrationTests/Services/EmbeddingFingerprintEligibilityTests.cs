using System.Data.Common;
using Application.Interfaces.Services;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class EmbeddingFingerprintEligibilityTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	[Theory]
	[InlineData(false, 0.0)]
	[InlineData(true, 1.0)]
	public async Task GetSimilarItemsAsync_HybridSearch_UsesOnlyCurrentFingerprint(
		bool currentFingerprint,
		double expectedSemanticSimilarity)
	{
		await SeededTemplateCleanup.RemoveFromFreshDatabaseAsync(fixture);

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

	[Fact]
	public async Task GetSimilarItemsAsync_HybridSearch_IgnoresCurrentFingerprintForOldSourceText()
	{
		await SeededTemplateCleanup.RemoveFromFreshDatabaseAsync(fixture);

		Guid templateId = Guid.NewGuid();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.ItemTemplates.Add(new ItemTemplateEntity { Id = templateId, Name = "Renamed Milk" });
			seed.ItemEmbeddings.Add(new ItemEmbeddingEntity
			{
				Id = Guid.NewGuid(),
				EntityType = "ItemTemplate",
				EntityId = templateId,
				EntityText = "Old Milk Name",
				Embedding = UnitVector(),
				ModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}
		ItemTemplateSimilarityService service = new(
			new FixtureDbContextFactory(fixture),
			new FixedEmbeddingService(),
			NullLogger<ItemTemplateSimilarityService>.Instance);

		List<Application.Queries.Core.ItemTemplate.GetSimilarItems.SimilarItemResult> results =
			await service.GetSimilarItemsAsync("Renamed Milk", 5, 0, true, CancellationToken.None);

		results.Should().ContainSingle();
		results[0].SemanticSimilarity.Should().Be(0,
			"a current-space vector for an old source name is still a stale projection");
	}

	[Fact]
	public async Task GetEmbeddingCoverageAsync_ConcurrentCanonicalInsert_ReturnsOneCoherentSnapshot()
	{
		await SeededTemplateCleanup.RemoveFromFreshDatabaseAsync(fixture);

		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(CurrentCanonical("Original canonical"));
			await seed.SaveChangesAsync();
		}
		CoverageCountBarrier barrier = new();
		DbContextOptions<ApplicationDbContext> options =
			new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
				.AddInterceptors(barrier)
				.Options;
		NormalizedDescriptionService service = new(
			new OptionsDbContextFactory(options),
			new FixedEmbeddingService(),
			new NormalizedDescriptionMapper());

		Task<Application.Models.NormalizedDescriptions.EmbeddingCoverage> read =
			service.GetEmbeddingCoverageAsync(CancellationToken.None);
		await barrier.FirstCanonicalCountReturned.Task.WaitAsync(TimeSpan.FromSeconds(10));
		await using (ApplicationDbContext concurrent = fixture.CreateDbContext())
		{
			concurrent.NormalizedDescriptions.Add(CurrentCanonical("Concurrent canonical"));
			await concurrent.SaveChangesAsync();
		}
		barrier.Release.TrySetResult();

		Application.Models.NormalizedDescriptions.EmbeddingCoverage coverage =
			await read.WaitAsync(TimeSpan.FromSeconds(10));

		coverage.CanonicalTotal.Should().Be(1);
		coverage.CanonicalReady.Should().Be(1);
		coverage.CanonicalPending.Should().Be(0);
		coverage.IsComplete.Should().BeTrue();
	}

	private static NormalizedDescriptionEntity CurrentCanonical(string name) => new()
	{
		Id = Guid.NewGuid(),
		CanonicalName = name,
		Status = NormalizedDescriptionStatus.Active,
		Embedding = UnitVector(),
		EmbeddingModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
		CreatedAt = DateTimeOffset.UtcNow,
	};

	private static Vector UnitVector()
	{
		float[] values = new float[OnnxEmbeddingService.EmbeddingDimension];
		values[0] = 1;
		return new Vector(values);
	}

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}

	private sealed class OptionsDbContextFactory(DbContextOptions<ApplicationDbContext> options)
		: IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
	}

	private sealed class CoverageCountBarrier : DbCommandInterceptor
	{
		private int _seen;
		public TaskCompletionSource FirstCanonicalCountReturned { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override async ValueTask<DbDataReader> ReaderExecutedAsync(
			DbCommand command,
			CommandExecutedEventData eventData,
			DbDataReader result,
			CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("matching.\"NormalizedDescriptions\"", StringComparison.Ordinal)
				&& command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase)
				&& Interlocked.Exchange(ref _seen, 1) == 0)
			{
				FirstCanonicalCountReturned.TrySetResult();
				await Release.Task.WaitAsync(cancellationToken);
			}

			return result;
		}
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
