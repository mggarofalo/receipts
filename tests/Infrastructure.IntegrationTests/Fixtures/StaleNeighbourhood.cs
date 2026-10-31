using System.Data.Common;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pgvector;

namespace Infrastructure.IntegrationTests.Fixtures;

/// <summary>
/// RECEIPTS-1097: a catalog part-way through re-embedding. Sixty percent of the rows carry a stale
/// fingerprint and every one of them is nearer the query than the fresh target, so an HNSW probe
/// that filters after the scan sees only stale rows and returns nothing useful.
/// <para>
/// Whether the planner picks the index depends on catalog size and statistics: on the production
/// schema with 60% stale rows it chose HNSW at about 6,600 and 11,600 rows and returned 18 and 14 of
/// 20. <see cref="IndexPlanFactory"/> pins that choice so the test does not depend on the planner.
/// </para>
/// </summary>
internal static class StaleNeighbourhood
{
	private const int StaleRows = 600;
	private const int FreshFillers = 399;

	/// <summary>The query vector: the first axis.</summary>
	public static float[] Query => Toward(Axis(0), 0, 0);

	/// <summary>Seeds the catalog and returns the fresh target, cosine ~0.96 with the query.</summary>
	public static async Task<NormalizedDescriptionEntity> SeedAsync(PostgresFixture fixture, string targetName)
	{
		// Dense vectors, as real embeddings are: sparse ones compress and make a sequential scan look free.
		Random random = new(1097);
		NormalizedDescriptionEntity target = Row(targetName, Toward(Axis(0), 0.3f, 0.01f, random), OnnxEmbeddingService.EmbeddingSpaceFingerprint);
		List<NormalizedDescriptionEntity> rows = [target];
		// Stale near-duplicates of the query: cosine ~0.999, against ~0.96 for the target.
		rows.AddRange(Enumerable.Range(0, StaleRows).Select(i =>
			Row($"Stale neighbour {i:D4}", Toward(Axis(0), 0, 0.05f, random), "obsolete-space")));
		// Fresh rows far from the query.
		rows.AddRange(Enumerable.Range(0, FreshFillers).Select(i =>
			Row($"Fresh filler {i:D4}", Toward(new float[OnnxEmbeddingService.EmbeddingDimension], 0, 1, random), OnnxEmbeddingService.EmbeddingSpaceFingerprint)));
		await using ApplicationDbContext db = fixture.CreateDbContext();
		db.NormalizedDescriptions.AddRange(rows);
		await db.SaveChangesAsync();
		return target;
	}

	/// <summary>Contexts whose connections plan the kNN through the HNSW index, as a large catalog does.</summary>
	public static IDbContextFactory<ApplicationDbContext> IndexPlanFactory(PostgresFixture fixture) =>
		new Factory(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
			.AddInterceptors(new IndexPlans()).Options);

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
	}

	private sealed class IndexPlans : DbConnectionInterceptor
	{
		public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
			CancellationToken cancellationToken = default)
		{
			await using DbCommand command = connection.CreateCommand();
			command.CommandText = "SET enable_seqscan = off";
			await command.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	private static float[] Axis(int axis)
	{
		float[] vector = new float[OnnxEmbeddingService.EmbeddingDimension];
		vector[axis] = 1;
		return vector;
	}

	/// <summary>Adds <paramref name="lean"/> along the second axis and dense noise whose norm is about <paramref name="noise"/>.</summary>
	private static float[] Toward(float[] start, float lean, float noise, Random? random = null)
	{
		float[] vector = (float[])start.Clone();
		vector[1] += lean;
		float component = noise * MathF.Sqrt(3f / vector.Length);
		for (int i = 0; random is not null && i < vector.Length; i++)
		{
			vector[i] += component * (float)(random.NextDouble() * 2 - 1);
		}
		return vector;
	}

	private static NormalizedDescriptionEntity Row(string name, float[] vector, string fingerprint) => new()
	{
		Id = Guid.NewGuid(),
		CanonicalName = name,
		Status = NormalizedDescriptionStatus.Active,
		CreatedAt = DateTimeOffset.UtcNow,
		Embedding = new Vector(vector),
		EmbeddingModelVersion = fingerprint,
	};
}
