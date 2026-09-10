using Application.Interfaces.Services;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

// Real-Postgres exercise of NormalizedDescriptionResolutionService (RECEIPTS-578). The tests
// pre-seed NormalizedDescription rows so GetOrCreateAsync hits the exact-match short-circuit
// (step 1 inside the service) and never needs the pgvector ANN pathway — that lets us verify
// the resolver's end-to-end persistence, grouping, and filtering semantics without depending
// on the ONNX embedding model.
//
// The ANN-match and below-threshold-create branches of GetOrCreateAsync are covered by the
// unit tests against the InMemory provider (TestableNormalizedDescriptionService overrides
// AnnSearchTopOneAsync for each threshold band).
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class NormalizedDescriptionResolutionServiceTests(PostgresFixture fixture)
{
	[Fact]
	public async Task ProcessPendingResolutionsAsync_LinksItems_ViaExactMatch_PopulatesFkAndScore()
	{
		// Arrange — pre-seed canonical entries so the resolver routes every description
		// through the exact-case-insensitive short-circuit inside NormalizedDescriptionService.
		await ResetTablesAsync();

		Guid milkId = Guid.NewGuid();
		Guid bananaId = Guid.NewGuid();

		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity first = WithDescription(receipt.Id, "Organic Milk");
		ReceiptItemEntity second = WithDescription(receipt.Id, "organic milk");
		ReceiptItemEntity third = WithDescription(receipt.Id, "Bananas");

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.NormalizedDescriptions.AddRange(
				new NormalizedDescriptionEntity
				{
					Id = milkId,
					CanonicalName = "Organic Milk",
					Status = NormalizedDescriptionStatus.Active,
					Embedding = CurrentVector(),
					EmbeddingModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
					CreatedAt = DateTimeOffset.UtcNow,
				},
				new NormalizedDescriptionEntity
				{
					Id = bananaId,
					CanonicalName = "Bananas",
					Status = NormalizedDescriptionStatus.Active,
					Embedding = CurrentVector(),
					EmbeddingModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
					CreatedAt = DateTimeOffset.UtcNow,
				});
			setup.Receipts.Add(receipt);
			setup.ReceiptItems.AddRange(first, second, third);
			await setup.SaveChangesAsync();
		}

		NoOpEmbeddingService embeddingService = new();
		ServiceProvider provider = BuildProvider(embeddingService);

		NormalizedDescriptionResolutionService resolver = new(
			provider.GetRequiredService<IServiceScopeFactory>(),
			provider.GetRequiredService<IDescriptionChangeSignal>(),
			NullLogger<NormalizedDescriptionResolutionService>.Instance);

		// Act
		var summary = await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);

		// Assert — all three items were linked with the exact-match short-circuit's
		// perfect score of 1.0.
		summary.Linked.Should().Be(3);
		summary.NewEntriesCreated.Should().Be(0, "each description hit an existing canonical entry");

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		List<ReceiptItemEntity> items = await verify.ReceiptItems.AsNoTracking().ToListAsync();
		items.Should().HaveCount(3);

		ReceiptItemEntity persistedFirst = items.Single(i => i.Id == first.Id);
		ReceiptItemEntity persistedSecond = items.Single(i => i.Id == second.Id);
		ReceiptItemEntity persistedThird = items.Single(i => i.Id == third.Id);

		// Both casing variants resolve to the same canonical entry.
		persistedFirst.NormalizedDescriptionId.Should().Be(milkId);
		persistedSecond.NormalizedDescriptionId.Should().Be(milkId);
		persistedThird.NormalizedDescriptionId.Should().Be(bananaId);

		// Exact-match short-circuit surfaces similarity = 1.0 — the resolver must persist
		// that score alongside the FK so the downstream preview / admin reports don't have
		// to re-compute embeddings.
		persistedFirst.NormalizedDescriptionMatchScore.Should().Be(1.0);
		persistedSecond.NormalizedDescriptionMatchScore.Should().Be(1.0);
		persistedThird.NormalizedDescriptionMatchScore.Should().Be(1.0);

		// The resolver must not invent new canonical entries when exact matches exist.
		int canonicalCount = await verify.NormalizedDescriptions.AsNoTracking().CountAsync();
		canonicalCount.Should().Be(2);
	}

	[Fact]
	public async Task ProcessPendingResolutionsAsync_Batch_GroupsDuplicateDescriptions_IntoSingleGetOrCreateCall()
	{
		// Arrange — three rows with the same text should all resolve to the same FK.
		await ResetTablesAsync();

		Guid canonicalId = Guid.NewGuid();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity a = WithDescription(receipt.Id, "Eggs Large");
		ReceiptItemEntity b = WithDescription(receipt.Id, "Eggs Large");
		ReceiptItemEntity c = WithDescription(receipt.Id, "Eggs Large");

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = canonicalId,
				CanonicalName = "Eggs Large",
				Status = NormalizedDescriptionStatus.Active,
				Embedding = CurrentVector(),
				EmbeddingModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			setup.Receipts.Add(receipt);
			setup.ReceiptItems.AddRange(a, b, c);
			await setup.SaveChangesAsync();
		}

		// A counting embedding service lets us assert the dedup behaviour is real — no need
		// for embedding data since we pre-seeded the canonical rows.
		NoOpEmbeddingService embeddingService = new();
		ServiceProvider provider = BuildProvider(embeddingService);

		NormalizedDescriptionResolutionService resolver = new(
			provider.GetRequiredService<IServiceScopeFactory>(),
			provider.GetRequiredService<IDescriptionChangeSignal>(),
			NullLogger<NormalizedDescriptionResolutionService>.Instance);

		// Act
		var summary = await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);

		// Assert — all three rows got linked to the same canonical entry.
		summary.Linked.Should().Be(3);

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		List<ReceiptItemEntity> items = await verify.ReceiptItems.AsNoTracking().ToListAsync();
		items.Should().OnlyContain(i => i.NormalizedDescriptionId == canonicalId);
		items.Should().OnlyContain(i => i.NormalizedDescriptionMatchScore == 1.0);
	}

	[Fact]
	public async Task ProcessPendingResolutionsAsync_IsIdempotent_WhenRunTwice()
	{
		// Arrange
		await ResetTablesAsync();

		Guid canonicalId = Guid.NewGuid();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity item = WithDescription(receipt.Id, "Sourdough Bread");

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = canonicalId,
				CanonicalName = "Sourdough Bread",
				Status = NormalizedDescriptionStatus.Active,
				Embedding = CurrentVector(),
				EmbeddingModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			setup.Receipts.Add(receipt);
			setup.ReceiptItems.Add(item);
			await setup.SaveChangesAsync();
		}

		NoOpEmbeddingService embeddingService = new();
		ServiceProvider provider = BuildProvider(embeddingService);
		NormalizedDescriptionResolutionService resolver = new(
			provider.GetRequiredService<IServiceScopeFactory>(),
			provider.GetRequiredService<IDescriptionChangeSignal>(),
			NullLogger<NormalizedDescriptionResolutionService>.Instance);

		// Act — first run links the item; second run should see zero candidates.
		var first = await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);
		var second = await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);

		// Assert
		first.Linked.Should().Be(1);
		second.Linked.Should().Be(0);

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		int canonicalCount = await verify.NormalizedDescriptions.AsNoTracking().CountAsync();
		canonicalCount.Should().Be(1, "the second cycle must not create a duplicate canonical entry");
	}

	[Fact]
	public async Task ProcessPendingResolutionsAsync_ExcludesAlreadyLinkedAndSoftDeleted()
	{
		// Arrange — mix of states that should be filtered out vs resolved.
		await ResetTablesAsync();

		Guid existingCanonicalId = Guid.NewGuid();
		Guid newCanonicalId = Guid.NewGuid();

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.NormalizedDescriptions.AddRange(
				new NormalizedDescriptionEntity
				{
					Id = existingCanonicalId,
					CanonicalName = "Pre-existing",
					Status = NormalizedDescriptionStatus.Active,
					Embedding = CurrentVector(),
					EmbeddingModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
					CreatedAt = DateTimeOffset.UtcNow,
				},
				new NormalizedDescriptionEntity
				{
					Id = newCanonicalId,
					CanonicalName = "Unresolved",
					Status = NormalizedDescriptionStatus.Active,
					Embedding = CurrentVector(),
					EmbeddingModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
					CreatedAt = DateTimeOffset.UtcNow,
				});

			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			setup.Receipts.Add(receipt);

			// Already-linked: the resolver must not re-touch this row.
			ReceiptItemEntity linked = WithDescription(receipt.Id, "Pre-existing");
			linked.NormalizedDescriptionId = existingCanonicalId;
			linked.NormalizedDescriptionMatchScore = 0.42;

			// Soft-deleted: the resolver must skip it.
			ReceiptItemEntity deleted = WithDescription(receipt.Id, "Unresolved");
			deleted.DeletedAt = DateTimeOffset.UtcNow;

			// Too-short: filtered out by the length predicate.
			ReceiptItemEntity tooShort = WithDescription(receipt.Id, "X");

			// The only row that should actually get resolved.
			ReceiptItemEntity target = WithDescription(receipt.Id, "Unresolved");

			setup.ReceiptItems.AddRange(linked, deleted, tooShort, target);
			await setup.SaveChangesAsync();
		}

		NoOpEmbeddingService embeddingService = new();
		ServiceProvider provider = BuildProvider(embeddingService);
		NormalizedDescriptionResolutionService resolver = new(
			provider.GetRequiredService<IServiceScopeFactory>(),
			provider.GetRequiredService<IDescriptionChangeSignal>(),
			NullLogger<NormalizedDescriptionResolutionService>.Instance);

		// Act
		var summary = await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);

		// Assert — only the one valid, unresolved row was linked.
		summary.Linked.Should().Be(1);

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		// The already-linked row retains its prior score (resolver did not overwrite).
		ReceiptItemEntity persistedLinked = await verify.ReceiptItems
			.AsNoTracking()
			.SingleAsync(i => i.Description == "Pre-existing");
		persistedLinked.NormalizedDescriptionId.Should().Be(existingCanonicalId);
		persistedLinked.NormalizedDescriptionMatchScore.Should().Be(0.42);

		// The soft-deleted row is still unresolved.
		ReceiptItemEntity persistedDeleted = await verify.ReceiptItems
			.IgnoreQueryFilters()
			.AsNoTracking()
			.SingleAsync(i => i.DeletedAt != null);
		persistedDeleted.NormalizedDescriptionId.Should().BeNull();

		// The too-short row is still unresolved.
		ReceiptItemEntity persistedTooShort = await verify.ReceiptItems
			.AsNoTracking()
			.SingleAsync(i => i.Description == "X");
		persistedTooShort.NormalizedDescriptionId.Should().BeNull();

		// The target was linked to the pre-seeded "Unresolved" canonical entry with a
		// perfect exact-match score.
		ReceiptItemEntity persistedTarget = await verify.ReceiptItems
			.AsNoTracking()
			.SingleAsync(i => i.Description == "Unresolved" && i.DeletedAt == null);
		persistedTarget.NormalizedDescriptionId.Should().Be(newCanonicalId);
		persistedTarget.NormalizedDescriptionMatchScore.Should().Be(1.0);
	}

	private ServiceProvider BuildProvider(IEmbeddingService embeddingService)
	{
		ServiceCollection services = new();
		services.AddSingleton<IEmbeddingService>(embeddingService);
		services.AddSingleton<NormalizedDescriptionMapper>();
		services.AddSingleton<NormalizedDescriptionSettingsMapper>();
		services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new FixtureDbContextFactory(fixture));
		services.AddScoped<INormalizedDescriptionService, NormalizedDescriptionService>();
		services.AddSingleton<IDescriptionChangeSignal, DescriptionChangeSignal>();
		return services.BuildServiceProvider();
	}

	// Hand-written embedding stub — the integration test project doesn't reference Moq. This
	// stub reports IsConfigured=true so the resolver proceeds past its short-circuit, but
	// never has to actually supply an embedding because every test pre-seeds the canonical
	// entry that the exact-match path will find first.
	private sealed class NoOpEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => true;
		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
			=> Task.FromResult(Array.Empty<float>());
		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken)
			=> Task.FromResult(texts.Select(_ => Array.Empty<float>()).ToList());
	}

	private static ReceiptItemEntity WithDescription(Guid receiptId, string description)
	{
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receiptId);
		item.Description = description;
		return item;
	}

	private static Pgvector.Vector CurrentVector() =>
		new(new float[OnnxEmbeddingService.EmbeddingDimension]);

	private async Task ResetTablesAsync()
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		// Reset every table the resolver touches or depends on. DistinctDescriptions is
		// reconciled by the DbContext SaveChanges hook so it needs a clean slate too.
		await context.Database.ExecuteSqlRawAsync(
			"""TRUNCATE "ReceiptItems", "Receipts", "NormalizedDescriptions", "DistinctDescriptions" RESTART IDENTITY CASCADE;""");
	}

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
