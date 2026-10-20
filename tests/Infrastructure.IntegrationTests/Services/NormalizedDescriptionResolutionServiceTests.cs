using Application.Interfaces.Services;
using Application.Normalization;
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

// Approved scoped mappings resolve offline; canonical spelling is never sufficient authority.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class NormalizedDescriptionResolutionServiceTests(PostgresFixture fixture)
{
	[Fact]
	public async Task ProcessPendingResolutionsAsync_OfflineApprovedScopeDoesNotInventMatchesForUnknownDescriptions()
	{
		await ResetTablesAsync();
		Guid canonicalId = Guid.NewGuid();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity exact = WithDescription(receipt.Id, "Organic Milk");
		ReceiptItemEntity unknown = WithDescription(receipt.Id, "UNFAMILIAR PRODUCT");
		var originalFacts = (exact.Description, exact.Quantity, exact.UnitPrice, exact.UnitPriceCurrency,
			exact.TotalAmount, exact.TotalAmountCurrency);
		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = canonicalId,
				CanonicalName = "Organic Milk",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			setup.Receipts.Add(receipt);
			setup.ReceiptItems.AddRange(exact, unknown);
			await setup.SaveChangesAsync();
		}

		await ApproveMatchingSourcesAsync();
		using ServiceProvider provider = BuildProvider(new UnavailableEmbeddingService());
		NormalizedDescriptionResolutionService resolver = new(
			provider.GetRequiredService<IServiceScopeFactory>(),
			provider.GetRequiredService<IDescriptionChangeSignal>(),
			NullLogger<NormalizedDescriptionResolutionService>.Instance);
		await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);
		await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		ReceiptItemEntity saved = await verify.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == exact.Id);
		saved.NormalizedDescriptionId.Should().Be(canonicalId,
			"an approved scoped mapping must remain usable without ONNX or a provider request");
		(saved.Description, saved.Quantity, saved.UnitPrice, saved.UnitPriceCurrency,
			saved.TotalAmount, saved.TotalAmountCurrency).Should().Be(originalFacts);
		(await verify.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == unknown.Id))
			.NormalizedDescriptionId.Should().BeNull("missing inference is not evidence for a new identity");
		(await verify.NormalizedDescriptions.CountAsync()).Should().Be(1);
	}

	[Fact]
	public async Task ProcessPendingResolutionsAsync_LinksItems_ViaSeparatelyApprovedCaseSensitiveScopes_WithoutSimilarityScores()
	{
		// Arrange — pre-seed canonical entries so the resolver routes every description
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
		await ApproveMatchingSourcesAsync();
		ServiceProvider provider = BuildProvider(embeddingService);

		NormalizedDescriptionResolutionService resolver = new(
			provider.GetRequiredService<IServiceScopeFactory>(),
			provider.GetRequiredService<IDescriptionChangeSignal>(),
			NullLogger<NormalizedDescriptionResolutionService>.Instance);

		// Act
		var summary = await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);

		summary.Linked.Should().Be(3);

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		List<ReceiptItemEntity> items = await verify.ReceiptItems.AsNoTracking().ToListAsync();
		items.Should().HaveCount(3);

		ReceiptItemEntity persistedFirst = items.Single(i => i.Id == first.Id);
		ReceiptItemEntity persistedSecond = items.Single(i => i.Id == second.Id);
		ReceiptItemEntity persistedThird = items.Single(i => i.Id == third.Id);

		persistedFirst.NormalizedDescriptionId.Should().Be(milkId);
		persistedSecond.NormalizedDescriptionId.Should().Be(milkId);
		persistedThird.NormalizedDescriptionId.Should().Be(bananaId);

		persistedFirst.NormalizedDescriptionMatchScore.Should().BeNull();
		persistedSecond.NormalizedDescriptionMatchScore.Should().BeNull();
		persistedThird.NormalizedDescriptionMatchScore.Should().BeNull();

		int canonicalCount = await verify.NormalizedDescriptions.AsNoTracking().CountAsync();
		canonicalCount.Should().Be(2);
	}

	[Fact]
	public async Task ProcessPendingResolutionsAsync_Batch_AppliesApprovedScopeToEveryMatchingPurchase()
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

		NoOpEmbeddingService embeddingService = new();
		await ApproveMatchingSourcesAsync();
		ServiceProvider provider = BuildProvider(embeddingService);

		NormalizedDescriptionResolutionService resolver = new(
			provider.GetRequiredService<IServiceScopeFactory>(),
			provider.GetRequiredService<IDescriptionChangeSignal>(),
			NullLogger<NormalizedDescriptionResolutionService>.Instance);

		// Act
		var summary = await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);

		summary.Linked.Should().Be(3);

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		List<ReceiptItemEntity> items = await verify.ReceiptItems.AsNoTracking().ToListAsync();
		items.Should().OnlyContain(i => i.NormalizedDescriptionId == canonicalId);
		items.Should().OnlyContain(i => i.NormalizedDescriptionMatchScore == null);
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
		await ApproveMatchingSourcesAsync();
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

			ReceiptItemEntity tooShort = WithDescription(receipt.Id, "X");

			// The only row that should actually get resolved.
			ReceiptItemEntity target = WithDescription(receipt.Id, "Unresolved");

			setup.ReceiptItems.AddRange(linked, deleted, tooShort, target);
			await setup.SaveChangesAsync();
		}

		NoOpEmbeddingService embeddingService = new();
		await ApproveMatchingSourcesAsync();
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

		ReceiptItemEntity persistedTooShort = await verify.ReceiptItems
			.AsNoTracking()
			.SingleAsync(i => i.Description == "X");
		persistedTooShort.NormalizedDescriptionId.Should().BeNull();

		ReceiptItemEntity persistedTarget = await verify.ReceiptItems
			.AsNoTracking()
			.SingleAsync(i => i.Description == "Unresolved" && i.DeletedAt == null);
		persistedTarget.NormalizedDescriptionId.Should().Be(newCanonicalId);
		persistedTarget.NormalizedDescriptionMatchScore.Should().BeNull();
	}

	private async Task ApproveMatchingSourcesAsync()
	{
		await using var context = fixture.CreateDbContext();
		var items = await context.ReceiptItems.IgnoreQueryFilters().AsNoTracking().ToListAsync();
		var receipts = await context.Receipts.AsNoTracking().ToDictionaryAsync(r => r.Id);
		var targets = await context.NormalizedDescriptions.AsNoTracking().ToListAsync();
		HashSet<string> seen = [];
		foreach (var item in items)
		{
			var target = targets.SingleOrDefault(t => string.Equals(t.CanonicalName, item.Description, StringComparison.OrdinalIgnoreCase));
			if (target is null) { continue; }
			var key = NormalizationCatalogPolicy.Normalize(new(receipts[item.ReceiptId].Location, item.Description, item.ReceiptItemCode));
			string hash = NormalizationCatalogPolicy.Hash(key);
			if (!seen.Add(hash)) { continue; }
			context.NormalizationSourceMappings.Add(new() { Id = Guid.NewGuid(), SourceHash = hash, Location = key.Location, Description = key.Description, ItemCode = key.ItemCode, TargetId = target.Id, ApprovedBy = "reviewer", ApprovedAt = DateTimeOffset.UtcNow });
		}
		await context.SaveChangesAsync();
	}

	private ServiceProvider BuildProvider(IEmbeddingService embeddingService)
	{
		ServiceCollection services = new();
		services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new FixtureDbContextFactory(fixture));
		services.AddSingleton<IDescriptionChangeSignal, DescriptionChangeSignal>();
		return services.BuildServiceProvider();
	}

	private sealed class NoOpEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => true;
		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken) => throw new InvalidOperationException("Scoped resolution never calls a model.");
		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken) => throw new InvalidOperationException("Scoped resolution never calls a model.");
	}
	private sealed class UnavailableEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => false;
		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken) => throw new InvalidOperationException("Scoped resolution works offline.");
		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken) => throw new InvalidOperationException("Scoped resolution works offline.");
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
			"""TRUNCATE matching."NormalizationSourceMappings", "ReceiptItems", "Receipts", "NormalizedDescriptions", "DistinctDescriptions" RESTART IDENTITY CASCADE;""");
	}

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
