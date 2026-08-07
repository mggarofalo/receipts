using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Common;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Moq;
using Pgvector;

namespace Infrastructure.Tests.Services;

[Trait("Category", "Unit")]
public class NormalizedDescriptionServiceTests
{
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
	private readonly Mock<IEmbeddingService> _embeddingServiceMock;
	private readonly NormalizedDescriptionMapper _mapper;
	private readonly NormalizedDescriptionSettingsMapper _settingsMapper;

	public NormalizedDescriptionServiceTests()
	{
		(_contextFactory, MockCurrentUserAccessor accessor) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		accessor.UserId = "test-user";
		_embeddingServiceMock = new Mock<IEmbeddingService>();
		_mapper = new NormalizedDescriptionMapper();
		_settingsMapper = new NormalizedDescriptionSettingsMapper();
	}

	[Fact]
	public async Task GetOrCreateAsync_ExactCaseInsensitiveMatch_ReturnsExisting()
	{
		// Arrange — seed an existing canonical entry.
		Guid existingId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = existingId,
				CanonicalName = "Organic Milk",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act — query with different casing; it should short-circuit without generating an embedding.
		GetOrCreateResult result = await service.GetOrCreateAsync("organic MILK", CancellationToken.None);

		// Assert
		result.Description.Id.Should().Be(existingId);
		result.Description.CanonicalName.Should().Be("Organic Milk");
		// Exact-match short-circuit surfaces a perfect similarity score so the resolver
		// can persist it on the ReceiptItem without a second embedding roundtrip.
		result.MatchScore.Should().Be(1.0);
		_embeddingServiceMock.Verify(
			e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task GetOrCreateAsync_EmbeddingServiceUnavailable_CreatesActiveEntryWithNoEmbedding()
	{
		// Arrange
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(false);
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act
		GetOrCreateResult result = await service.GetOrCreateAsync("New Item", CancellationToken.None);

		// Assert — a new Active row was created, and no embedding was generated.
		result.Description.Status.Should().Be(NormalizedDescriptionStatus.Active);
		result.Description.CanonicalName.Should().Be("New Item");
		// No embedding → no ANN search → no MatchScore to surface.
		result.MatchScore.Should().BeNull();
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		NormalizedDescriptionEntity stored = await verify.NormalizedDescriptions.SingleAsync(e => e.Id == result.Description.Id);
		stored.Embedding.Should().BeNull();
		stored.EmbeddingModelVersion.Should().BeNull();
		_embeddingServiceMock.Verify(
			e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task GetOrCreateAsync_AboveAutoAcceptThreshold_ReturnsAnnMatchWithoutInserting()
	{
		// Arrange — seed an existing Active entry that the fake ANN will return as the top-1.
		Guid matchedId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = matchedId,
				CanonicalName = "Gallon of Milk",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingAsync("Whole Milk", It.IsAny<CancellationToken>()))
			.ReturnsAsync(CreateFakeEmbedding());

		TestableNormalizedDescriptionService service = new(
			_contextFactory,
			_embeddingServiceMock.Object,
			_mapper,
			_settingsMapper,
			matchedId,
			similarity: NormalizedDescriptionService.InitialAutoAcceptThreshold + 0.01);

		// Act
		GetOrCreateResult result = await service.GetOrCreateAsync("Whole Milk", CancellationToken.None);

		// Assert — returned the ANN match; no new row inserted.
		result.Description.Id.Should().Be(matchedId);
		result.Description.CanonicalName.Should().Be("Gallon of Milk");
		// AutoAccept branch returns the ANN similarity so the resolver can persist it.
		result.MatchScore.Should().Be(NormalizedDescriptionService.InitialAutoAcceptThreshold + 0.01);
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		int count = await verify.NormalizedDescriptions.CountAsync();
		count.Should().Be(1);
	}

	[Fact]
	public async Task GetOrCreateAsync_BetweenThresholds_CreatesPendingReviewWithInputText()
	{
		// Arrange — seed a close-but-not-exact match that the fake ANN will return.
		Guid matchedId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = matchedId,
				CanonicalName = "Gallon of Milk",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingAsync("Milky Thing", It.IsAny<CancellationToken>()))
			.ReturnsAsync(CreateFakeEmbedding());

		double between = (NormalizedDescriptionService.InitialAutoAcceptThreshold + NormalizedDescriptionService.InitialPendingReviewThreshold) / 2;
		TestableNormalizedDescriptionService service = new(
			_contextFactory,
			_embeddingServiceMock.Object,
			_mapper,
			_settingsMapper,
			matchedId,
			similarity: between);

		// Act
		GetOrCreateResult result = await service.GetOrCreateAsync("Milky Thing", CancellationToken.None);

		// Assert — a new PendingReview row was created with the input text as canonical name.
		result.Description.Status.Should().Be(NormalizedDescriptionStatus.PendingReview);
		result.Description.CanonicalName.Should().Be("Milky Thing");
		result.Description.Id.Should().NotBe(matchedId);
		result.MatchScore.Should().Be(between);
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		int count = await verify.NormalizedDescriptions.CountAsync();
		count.Should().Be(2);
	}

	[Fact]
	public async Task GetOrCreateAsync_BelowPendingReviewThreshold_CreatesActiveEntry()
	{
		// Arrange — seed an unrelated Active entry that the fake ANN will return as the top-1 match
		// but with a similarity below the PendingReview threshold.
		Guid matchedId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = matchedId,
				CanonicalName = "Unrelated Item",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingAsync("Totally Different", It.IsAny<CancellationToken>()))
			.ReturnsAsync(CreateFakeEmbedding());

		TestableNormalizedDescriptionService service = new(
			_contextFactory,
			_embeddingServiceMock.Object,
			_mapper,
			_settingsMapper,
			matchedId,
			similarity: NormalizedDescriptionService.InitialPendingReviewThreshold - 0.1);

		// Act
		GetOrCreateResult result = await service.GetOrCreateAsync("Totally Different", CancellationToken.None);

		// Assert — a new Active entry was created with the input text as canonical.
		result.Description.Status.Should().Be(NormalizedDescriptionStatus.Active);
		result.Description.CanonicalName.Should().Be("Totally Different");
		// Below pending-review floor → a brand-new canonical entry; no similarity to persist.
		result.MatchScore.Should().BeNull();
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		int count = await verify.NormalizedDescriptions.CountAsync();
		count.Should().Be(2);
	}

	[Fact]
	public async Task GetOrCreateAsync_EmptyOrWhitespace_Throws()
	{
		// Arrange
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act + Assert
		await service.Invoking(s => s.GetOrCreateAsync("   ", CancellationToken.None))
			.Should().ThrowAsync<ArgumentException>();
	}

	[Fact]
	public async Task MergeAsync_ReLinksReceiptItemsAndDeletesDiscard()
	{
		// Arrange — two NormalizedDescriptions plus two ReceiptItems pointing at the discard entry.
		Guid keepId = Guid.NewGuid();
		Guid discardId = Guid.NewGuid();
		Guid itemAId = Guid.NewGuid();
		Guid itemBId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.AddRange(
				new NormalizedDescriptionEntity { Id = keepId, CanonicalName = "Milk", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow },
				new NormalizedDescriptionEntity { Id = discardId, CanonicalName = "Mlik", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
			seed.ReceiptItems.AddRange(
				BuildReceiptItem(itemAId, receiptId, "Mlik", discardId),
				BuildReceiptItem(itemBId, receiptId, "Mlik", discardId));
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act
		int moved = await service.MergeAsync(keepId, discardId, CancellationToken.None);

		// Assert
		moved.Should().Be(2);
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		(await verify.NormalizedDescriptions.AnyAsync(e => e.Id == discardId)).Should().BeFalse();
		(await verify.NormalizedDescriptions.AnyAsync(e => e.Id == keepId)).Should().BeTrue();
		List<Guid?> linkedIds = await verify.ReceiptItems
			.IgnoreAutoIncludes()
			.Where(r => r.Id == itemAId || r.Id == itemBId)
			.Select(r => r.NormalizedDescriptionId)
			.ToListAsync();
		linkedIds.Should().OnlyContain(id => id == keepId);
	}

	// RECEIPTS-892. A merge repoints items at the surviving row but used to leave
	// NormalizedDescriptionMatchScore alone, so the score described a comparison against a row
	// that had just been deleted. PreviewThresholdImpactAsync buckets items by exactly that
	// column, which made every threshold preview run after a merge partly fictional.
	public sealed class MergeRescoring : NormalizedDescriptionServiceTests
	{
		private (Guid KeepId, Guid DiscardId, Guid ReceiptId) SeedMergePair(
			string keepName,
			string discardName,
			bool keepHasEmbedding = true)
		{
			Guid keepId = Guid.NewGuid();
			Guid discardId = Guid.NewGuid();
			Guid receiptId = Guid.NewGuid();

			using ApplicationDbContext seed = _contextFactory.CreateDbContext();
			seed.NormalizedDescriptions.AddRange(
				new NormalizedDescriptionEntity
				{
					Id = keepId,
					CanonicalName = keepName,
					Status = NormalizedDescriptionStatus.Active,
					CreatedAt = DateTimeOffset.UtcNow,
					Embedding = keepHasEmbedding ? new Vector(CreateFakeEmbedding()) : null,
				},
				new NormalizedDescriptionEntity
				{
					Id = discardId,
					CanonicalName = discardName,
					Status = NormalizedDescriptionStatus.Active,
					CreatedAt = DateTimeOffset.UtcNow,
				});
			seed.SaveChanges();

			return (keepId, discardId, receiptId);
		}

		[Fact]
		public async Task MergeAsync_ReplacesTheDiscardedRowsScoreWithOneMeasuredAgainstTheSurvivor()
		{
			(Guid keepId, Guid discardId, Guid receiptId) = SeedMergePair("Milk", "Mlik");

			using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
			{
				// 0.91 was the similarity to "Mlik", which is about to stop existing.
				seed.ReceiptItems.Add(BuildReceiptItemWithScore(receiptId, "Mlik 2%", 0.91, discardId));
				await seed.SaveChangesAsync();
			}

			_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
			_embeddingServiceMock
				.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
				.ReturnsAsync(CreateFakeEmbedding());

			RescoringNormalizedDescriptionService service = new(
				_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper, similarity: 0.77);

			await service.MergeAsync(keepId, discardId, CancellationToken.None);

			using ApplicationDbContext verify = _contextFactory.CreateDbContext();
			ReceiptItemEntity item = await verify.ReceiptItems.IgnoreAutoIncludes().SingleAsync();
			item.NormalizedDescriptionId.Should().Be(keepId);
			item.NormalizedDescriptionMatchScore.Should().Be(0.77);
		}

		[Fact]
		public async Task MergeAsync_ScoresAnExactNameMatchAsOneWithoutAnEmbedding()
		{
			(Guid keepId, Guid discardId, Guid receiptId) = SeedMergePair("Milk", "Mlik");

			using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
			{
				// Casing and surrounding space must not matter — GetOrCreateAsync trims and
				// compares case-insensitively before it will pay for an embedding.
				seed.ReceiptItems.Add(BuildReceiptItemWithScore(receiptId, "  milk  ", 0.42, discardId));
				await seed.SaveChangesAsync();
			}

			_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);

			NormalizedDescriptionService service = new(
				_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

			await service.MergeAsync(keepId, discardId, CancellationToken.None);

			using ApplicationDbContext verify = _contextFactory.CreateDbContext();
			ReceiptItemEntity item = await verify.ReceiptItems.IgnoreAutoIncludes().SingleAsync();
			item.NormalizedDescriptionMatchScore.Should().Be(1.0);
			_embeddingServiceMock.Verify(
				e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
				Times.Never);
		}

		[Fact]
		public async Task MergeAsync_RescoresSoftDeletedItemsToo()
		{
			(Guid keepId, Guid discardId, Guid receiptId) = SeedMergePair("Milk", "Mlik");

			using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
			{
				ReceiptItemEntity trashed = BuildReceiptItemWithScore(receiptId, "Mlik 2%", 0.91, discardId);
				trashed.DeletedAt = DateTimeOffset.UtcNow;
				seed.ReceiptItems.Add(trashed);
				await seed.SaveChangesAsync();
			}

			_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
			_embeddingServiceMock
				.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
				.ReturnsAsync(CreateFakeEmbedding());

			RescoringNormalizedDescriptionService service = new(
				_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper, similarity: 0.64);

			await service.MergeAsync(keepId, discardId, CancellationToken.None);

			// A trashed item restored from the recycle bin must not bring a stale score back
			// with it — the same stranding class as RECEIPTS-801.
			using ApplicationDbContext verify = _contextFactory.CreateDbContext();
			ReceiptItemEntity item = await verify.ReceiptItems
				.IgnoreQueryFilters()
				.IgnoreAutoIncludes()
				.SingleAsync();
			item.NormalizedDescriptionId.Should().Be(keepId);
			item.NormalizedDescriptionMatchScore.Should().Be(0.64);
		}

		[Fact]
		public async Task MergeAsync_NullsTheScoreWhenNoHonestSimilarityCanBeComputed()
		{
			(Guid keepId, Guid discardId, Guid receiptId) = SeedMergePair("Milk", "Mlik", keepHasEmbedding: false);

			using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
			{
				seed.ReceiptItems.Add(BuildReceiptItemWithScore(receiptId, "Mlik 2%", 0.91, discardId));
				await seed.SaveChangesAsync();
			}

			_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(false);

			NormalizedDescriptionService service = new(
				_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

			await service.MergeAsync(keepId, discardId, CancellationToken.None);

			// Null reads as "unresolved", which is honest. Keeping 0.91 would assert a
			// similarity to a row that no longer exists.
			using ApplicationDbContext verify = _contextFactory.CreateDbContext();
			ReceiptItemEntity item = await verify.ReceiptItems.IgnoreAutoIncludes().SingleAsync();
			item.NormalizedDescriptionId.Should().Be(keepId);
			item.NormalizedDescriptionMatchScore.Should().BeNull();
		}

		[Fact]
		public async Task MergeAsync_EmbedsEachDistinctDescriptionOnlyOnce()
		{
			(Guid keepId, Guid discardId, Guid receiptId) = SeedMergePair("Milk", "Mlik");

			using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
			{
				seed.ReceiptItems.AddRange(
					BuildReceiptItemWithScore(receiptId, "Mlik 2%", 0.91, discardId),
					BuildReceiptItemWithScore(receiptId, "Mlik 2%", 0.91, discardId),
					BuildReceiptItemWithScore(receiptId, "Mlik whole", 0.88, discardId));
				await seed.SaveChangesAsync();
			}

			_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
			_embeddingServiceMock
				.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
				.ReturnsAsync(CreateFakeEmbedding());

			RescoringNormalizedDescriptionService service = new(
				_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper, similarity: 0.5);

			await service.MergeAsync(keepId, discardId, CancellationToken.None);

			// Three items, two distinct descriptions — grouping keeps this proportional to
			// vocabulary rather than row count.
			_embeddingServiceMock.Verify(
				e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
				Times.Exactly(2));
		}

		[Fact]
		public async Task MergeAsync_RecordsTheRescoredCountOnBothAuditEntries()
		{
			(Guid keepId, Guid discardId, Guid receiptId) = SeedMergePair("Milk", "Mlik");

			using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
			{
				seed.ReceiptItems.Add(BuildReceiptItemWithScore(receiptId, "Mlik 2%", 0.91, discardId));
				await seed.SaveChangesAsync();
			}

			_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
			_embeddingServiceMock
				.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
				.ReturnsAsync(CreateFakeEmbedding());

			RescoringNormalizedDescriptionService service = new(
				_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper, similarity: 0.77);

			await service.MergeAsync(keepId, discardId, CancellationToken.None);

			using ApplicationDbContext verify = _contextFactory.CreateDbContext();
			List<AuditLogEntity> merges = await verify.AuditLogs
				.Where(a => a.Action == AuditAction.Merge)
				.ToListAsync();

			merges.Should().HaveCount(2);
			foreach (AuditLogEntity entry in merges)
			{
				Dictionary<string, string?> fields = entry.GetChanges()
					.ToDictionary(c => c.FieldName, c => c.NewValue);
				fields["rescoredItemCount"].Should().Be("1");
			}
		}

		// Overrides only the per-row similarity lookup, which needs pgvector's `<=>`. Everything
		// above it — grouping, the exact-name short circuit, the null fallbacks — is the real code.
		private sealed class RescoringNormalizedDescriptionService(
			IDbContextFactory<ApplicationDbContext> contextFactory,
			IEmbeddingService embeddingService,
			NormalizedDescriptionMapper mapper,
			NormalizedDescriptionSettingsMapper settingsMapper,
			double similarity) : NormalizedDescriptionService(contextFactory, embeddingService, mapper, settingsMapper)
		{
			private readonly double _similarity = similarity;

			protected override Task<double?> SimilarityToAsync(
				ApplicationDbContext context, Vector queryVector, Guid targetId, CancellationToken cancellationToken)
				=> Task.FromResult<double?>(_similarity);
		}
	}

	[Fact]
	public async Task SplitAsync_CreatesNewActiveEntryAndRepointsReceiptItem()
	{
		// Arrange — an existing NormalizedDescription shared by a ReceiptItem.
		Guid sharedId = Guid.NewGuid();
		Guid itemId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = sharedId,
				CanonicalName = "Shared Name",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			seed.ReceiptItems.Add(BuildReceiptItem(itemId, receiptId, "Specific Raw Text", sharedId));
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(false);
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act
		NormalizedDescriptionDetail created = await service.SplitAsync(itemId, CancellationToken.None);

		// Assert — a new Active entry was created with the ReceiptItem's raw text, and the
		// ReceiptItem now points at the new entry.
		created.Description.Status.Should().Be(NormalizedDescriptionStatus.Active);
		created.Description.CanonicalName.Should().Be("Specific Raw Text");
		created.Description.Id.Should().NotBe(sharedId);
		// The split row owns exactly the item it was carved out for, and reports it as such
		// rather than the 0 a non-projecting implementation would return (RECEIPTS-873).
		created.LinkedItemCount.Should().Be(1);
		created.SampleRawDescriptions.Should().ContainSingle().Which.Should().Be("Specific Raw Text");
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		ReceiptItemEntity updatedItem = await verify.ReceiptItems
			.IgnoreAutoIncludes()
			.SingleAsync(r => r.Id == itemId);
		updatedItem.NormalizedDescriptionId.Should().Be(created.Description.Id);
	}

	[Fact]
	public async Task UpdateStatusAsync_ChangesPendingReviewToActive()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = id,
				CanonicalName = "Pending Entry",
				Status = NormalizedDescriptionStatus.PendingReview,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act
		bool changed = await service.UpdateStatusAsync(id, NormalizedDescriptionStatus.Active, CancellationToken.None);

		// Assert
		changed.Should().BeTrue();
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		NormalizedDescriptionEntity stored = await verify.NormalizedDescriptions.SingleAsync(e => e.Id == id);
		stored.Status.Should().Be(NormalizedDescriptionStatus.Active);
	}

	[Fact]
	public async Task UpdateStatusAsync_NoChange_ReturnsFalse()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = id,
				CanonicalName = "Already Active",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act
		bool changed = await service.UpdateStatusAsync(id, NormalizedDescriptionStatus.Active, CancellationToken.None);

		// Assert
		changed.Should().BeFalse();
	}

	[Fact]
	public async Task GetByIdAsync_ReturnsEntity_WhenPresent()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = id,
				CanonicalName = "Findable",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act
		NormalizedDescriptionDetail? result = await service.GetByIdAsync(id, CancellationToken.None);

		// Assert
		result.Should().NotBeNull();
		result!.Description.CanonicalName.Should().Be("Findable");
	}

	[Fact]
	public async Task GetAllAsync_FilterByStatus_ReturnsOnlyMatching()
	{
		// Arrange
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.AddRange(
				new NormalizedDescriptionEntity { Id = Guid.NewGuid(), CanonicalName = "Active A", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow },
				new NormalizedDescriptionEntity { Id = Guid.NewGuid(), CanonicalName = "Pending B", Status = NormalizedDescriptionStatus.PendingReview, CreatedAt = DateTimeOffset.UtcNow },
				new NormalizedDescriptionEntity { Id = Guid.NewGuid(), CanonicalName = "Active C", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act
		List<NormalizedDescriptionDetail> pending = await service.GetAllAsync(NormalizedDescriptionStatus.PendingReview, CancellationToken.None);
		List<NormalizedDescriptionDetail> all = await service.GetAllAsync(null, CancellationToken.None);

		// Assert
		pending.Should().ContainSingle();
		pending[0].Description.CanonicalName.Should().Be("Pending B");
		all.Should().HaveCount(3);
	}

	// ── RECEIPTS-580: settings / test-match / threshold-impact ─────────────────

	[Fact]
	public async Task GetSettingsAsync_NoRow_BootstrapsSingletonWithInitialDefaults()
	{
		// Arrange — no seed row. On InMemory we hit the self-heal path.
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act
		NormalizedDescriptionSettings result = await service.GetSettingsAsync(CancellationToken.None);

		// Assert — initial values are the same as the migration seed defaults.
		result.AutoAcceptThreshold.Should().Be(NormalizedDescriptionService.InitialAutoAcceptThreshold);
		result.PendingReviewThreshold.Should().Be(NormalizedDescriptionService.InitialPendingReviewThreshold);
		result.Id.Should().Be(new Guid("00000000-0000-0000-0000-000000000001"));
	}

	[Fact]
	public async Task GetSettingsAsync_WithSeededRow_ReturnsStoredValues()
	{
		// Arrange
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptionSettings.Add(new NormalizedDescriptionSettingsEntity
			{
				Id = new Guid("00000000-0000-0000-0000-000000000001"),
				AutoAcceptThreshold = 0.9,
				PendingReviewThreshold = 0.5,
				UpdatedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
			});
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act
		NormalizedDescriptionSettings result = await service.GetSettingsAsync(CancellationToken.None);

		// Assert
		result.AutoAcceptThreshold.Should().Be(0.9);
		result.PendingReviewThreshold.Should().Be(0.5);
	}

	[Fact]
	public async Task UpdateSettingsAsync_ValidBounds_PersistsAndReturnsNewValues()
	{
		// Arrange
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);
		DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);

		// Act
		NormalizedDescriptionSettings updated = await service.UpdateSettingsAsync(0.95, 0.5, CancellationToken.None);

		// Assert — returned values match input, UpdatedAt advanced, row was persisted.
		updated.AutoAcceptThreshold.Should().Be(0.95);
		updated.PendingReviewThreshold.Should().Be(0.5);
		updated.UpdatedAt.Should().BeAfter(before);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		NormalizedDescriptionSettingsEntity stored = await verify.NormalizedDescriptionSettings.SingleAsync();
		stored.AutoAcceptThreshold.Should().Be(0.95);
		stored.PendingReviewThreshold.Should().Be(0.5);
	}

	[Theory]
	[InlineData(-0.01, 0.5)]
	[InlineData(1.01, 0.5)]
	[InlineData(0.8, -0.01)]
	[InlineData(0.8, 1.01)]
	[InlineData(0.5, 0.8)] // pending >= auto
	[InlineData(0.8, 0.8)] // pending == auto (must be strictly less)
	public async Task UpdateSettingsAsync_InvalidBounds_Throws(double autoAccept, double pendingReview)
	{
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		await service.Invoking(s => s.UpdateSettingsAsync(autoAccept, pendingReview, CancellationToken.None))
			.Should().ThrowAsync<ArgumentException>();
	}

	[Fact]
	public async Task TestMatchAsync_ExactCaseInsensitiveMatch_ReturnsAutoAcceptWithTarget()
	{
		// Arrange
		Guid existingId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = existingId,
				CanonicalName = "Whole Milk",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act — lowercase variant should short-circuit via exact-match path.
		MatchTestResult result = await service.TestMatchAsync("whole milk", topN: 5, null, null, CancellationToken.None);

		// Assert — exact match collapses to a single synthetic candidate with similarity = 1.
		result.SimulatedOutcome.Should().Be(MatchTestOutcomes.AutoAccept);
		result.SimulatedTargetId.Should().Be(existingId);
		result.Candidates.Should().ContainSingle();
		result.Candidates[0].CosineSimilarity.Should().Be(1.0);
	}

	[Fact]
	public async Task TestMatchAsync_EmbeddingUnavailable_ReturnsEmbeddingUnavailableOutcome()
	{
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(false);
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		MatchTestResult result = await service.TestMatchAsync("Brand New Item", topN: 5, null, null, CancellationToken.None);

		result.SimulatedOutcome.Should().Be(MatchTestOutcomes.EmbeddingUnavailable);
		result.SimulatedTargetId.Should().BeNull();
		result.Candidates.Should().BeEmpty();
	}

	[Fact]
	public async Task TestMatchAsync_AutoAcceptBranch_WithOverride()
	{
		// Arrange — seed a candidate the fake ANN returns at similarity = 0.9.
		Guid topId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = topId,
				CanonicalName = "Similar Thing",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(CreateFakeEmbedding());

		TestableNormalizedDescriptionService service = new(
			_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper, topId, similarity: 0.9);

		// Override auto-accept to 0.85 → 0.9 is above, so auto-accept wins.
		MatchTestResult result = await service.TestMatchAsync(
			"Fresh Input",
			topN: 5,
			autoAcceptThresholdOverride: 0.85,
			pendingReviewThresholdOverride: 0.5,
			CancellationToken.None);

		result.SimulatedOutcome.Should().Be(MatchTestOutcomes.AutoAccept);
		result.SimulatedTargetId.Should().Be(topId);
		result.Candidates.Should().ContainSingle();
		result.Candidates[0].CosineSimilarity.Should().Be(0.9);
	}

	[Fact]
	public async Task TestMatchAsync_PendingReviewBranch_ReturnsPendingWithNullTarget()
	{
		Guid topId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = topId,
				CanonicalName = "Near Match",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(CreateFakeEmbedding());

		TestableNormalizedDescriptionService service = new(
			_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper, topId, similarity: 0.75);

		// Default DB settings (0.81 / 0.68) → 0.75 lands between the thresholds.
		MatchTestResult result = await service.TestMatchAsync(
			"Something Close",
			topN: 5,
			autoAcceptThresholdOverride: null,
			pendingReviewThresholdOverride: null,
			CancellationToken.None);

		result.SimulatedOutcome.Should().Be(MatchTestOutcomes.PendingReview);
		result.SimulatedTargetId.Should().BeNull();
		result.Candidates.Should().ContainSingle();
	}

	[Fact]
	public async Task TestMatchAsync_BelowPendingFloor_ReturnsCreateNewOutcome()
	{
		Guid topId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = topId,
				CanonicalName = "Distant Thing",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(CreateFakeEmbedding());

		TestableNormalizedDescriptionService service = new(
			_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper, topId, similarity: 0.3);

		MatchTestResult result = await service.TestMatchAsync("Very Different", topN: 5, null, null, CancellationToken.None);

		result.SimulatedOutcome.Should().Be(MatchTestOutcomes.CreateNew);
		result.SimulatedTargetId.Should().BeNull();
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task TestMatchAsync_EmptyInput_Throws(string input)
	{
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		await service.Invoking(s => s.TestMatchAsync(input, topN: 5, null, null, CancellationToken.None))
			.Should().ThrowAsync<ArgumentException>();
	}

	[Theory]
	[InlineData(0)]
	[InlineData(21)]
	[InlineData(-1)]
	public async Task TestMatchAsync_TopNOutOfRange_Throws(int topN)
	{
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		await service.Invoking(s => s.TestMatchAsync("desc", topN, null, null, CancellationToken.None))
			.Should().ThrowAsync<ArgumentException>();
	}

	[Fact]
	public async Task PreviewThresholdImpactAsync_CountsItemsByScore()
	{
		// Arrange — seed receipt items across all threshold bands plus a below-floor scored
		// row AND a structurally-unresolved row. Default settings: auto = 0.81, pending = 0.68.
		Guid receiptId = Guid.NewGuid();
		Guid linkedId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.ReceiptItems.AddRange(
				BuildReceiptItemWithScore(receiptId, "A", score: 0.95, normalizedId: Guid.NewGuid()), // auto-accepted (current)
				BuildReceiptItemWithScore(receiptId, "B", score: 0.82, normalizedId: Guid.NewGuid()), // auto-accepted (current)
				BuildReceiptItemWithScore(receiptId, "C", score: 0.70, normalizedId: Guid.NewGuid()), // pending-review (current)
				BuildReceiptItemWithScore(receiptId, "D", score: 0.50, normalizedId: linkedId),        // below-floor scored → "unresolved-by-threshold" (current)
				BuildReceiptItemWithScore(receiptId, "E", score: null, normalizedId: null));           // structurally unresolved
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act — propose lowering both thresholds so some items shift bucket.
		ThresholdImpactPreview preview = await service.PreviewThresholdImpactAsync(
			autoAcceptThreshold: 0.6,
			pendingReviewThreshold: 0.4,
			CancellationToken.None);

		// Assert current classification: A(0.95) + B(0.82) auto; C(0.70) pending; D below
		// floor + E null FK → both unresolved.
		preview.Current.AutoAccepted.Should().Be(2);
		preview.Current.PendingReview.Should().Be(1);
		preview.Current.Unresolved.Should().Be(2);

		// Under proposed (0.6 / 0.4): A(0.95) B(0.82) C(0.70) all auto; D(0.50) pending-review
		// (scored and ≥ 0.4); E still structurally unresolved.
		preview.Proposed.AutoAccepted.Should().Be(3);
		preview.Proposed.PendingReview.Should().Be(1);
		preview.Proposed.Unresolved.Should().Be(1);

		// Deltas: C moves pending → auto; D moves unresolved-by-threshold → pending. A/B stay
		// auto, E stays structurally unresolved.
		preview.Deltas.PendingToAuto.Should().Be(1);
		preview.Deltas.UnresolvedToPending.Should().Be(1);
		preview.Deltas.AutoToPending.Should().Be(0);
		preview.Deltas.UnresolvedToAuto.Should().Be(0);
	}

	[Fact]
	public async Task PreviewThresholdImpactAsync_InvalidBounds_Throws()
	{
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		await service.Invoking(s => s.PreviewThresholdImpactAsync(0.5, 0.5, CancellationToken.None))
			.Should().ThrowAsync<ArgumentException>();
	}

	// ── RECEIPTS-873: review-queue evidence ───────────────────────────────────

	[Fact]
	public async Task GetOrCreateAsync_BetweenThresholds_RecordsTheNearMissThatCausedTheReview()
	{
		// The whole point of the Review Queue is that an admin can see *why* a row is pending.
		// Before this, only the score survived — on the ReceiptItem — so the API could not answer
		// "what did this nearly match?".
		Guid matchedId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = matchedId,
				CanonicalName = "Gallon of Milk",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingAsync("Milky Thing", It.IsAny<CancellationToken>()))
			.ReturnsAsync(CreateFakeEmbedding());

		double between = (NormalizedDescriptionService.InitialAutoAcceptThreshold + NormalizedDescriptionService.InitialPendingReviewThreshold) / 2;
		TestableNormalizedDescriptionService service = new(
			_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper, matchedId, similarity: between);

		GetOrCreateResult result = await service.GetOrCreateAsync("Milky Thing", CancellationToken.None);

		result.Description.NearestNeighbourId.Should().Be(matchedId);
		result.Description.NearestNeighbourSimilarity.Should().Be(between);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		NormalizedDescriptionEntity stored = await verify.NormalizedDescriptions.SingleAsync(e => e.Id == result.Description.Id);
		stored.NearestNeighbourId.Should().Be(matchedId);
		stored.NearestNeighbourSimilarity.Should().Be(between);
	}

	[Theory]
	// Auto-accept reuses the matched row outright, so there is no *new* row to annotate.
	[InlineData(true)]
	// Below the floor a brand-new canonical entry is created against no meaningful candidate.
	[InlineData(false)]
	public async Task GetOrCreateAsync_OutsideThePendingBand_LeavesTheNearMissUnrecorded(bool aboveAutoAccept)
	{
		Guid matchedId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = matchedId,
				CanonicalName = "Seeded Row",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(CreateFakeEmbedding());

		double similarity = aboveAutoAccept
			? NormalizedDescriptionService.InitialAutoAcceptThreshold + 0.01
			: NormalizedDescriptionService.InitialPendingReviewThreshold - 0.1;

		TestableNormalizedDescriptionService service = new(
			_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper, matchedId, similarity);

		GetOrCreateResult result = await service.GetOrCreateAsync("Some Other Text", CancellationToken.None);

		// Null, not 0 — the UI contract distinguishes "no comparison recorded" from a zero score.
		result.Description.NearestNeighbourId.Should().BeNull();
		result.Description.NearestNeighbourSimilarity.Should().BeNull();
	}

	[Fact]
	public async Task GetOrCreateAsync_NoEmbeddingService_LeavesTheNearMissUnrecorded()
	{
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(false);
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		GetOrCreateResult result = await service.GetOrCreateAsync("Unembeddable", CancellationToken.None);

		result.Description.NearestNeighbourId.Should().BeNull();
		result.Description.NearestNeighbourSimilarity.Should().BeNull();
	}

	[Fact]
	public async Task GetAllAsync_ReturnsLinkedItemCountAndDistinctSamples()
	{
		Guid pendingId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = pendingId,
				CanonicalName = "Strawberry Preserves",
				Status = NormalizedDescriptionStatus.PendingReview,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			// Five items across four distinct texts: the count reports every linked item, while
			// the samples de-duplicate and cap at MaxSampleRawDescriptions.
			seed.ReceiptItems.AddRange(
				BuildReceiptItem(Guid.NewGuid(), receiptId, "STRAWBERRY PRES", pendingId),
				BuildReceiptItem(Guid.NewGuid(), receiptId, "STRAWBERRY PRES", pendingId),
				BuildReceiptItem(Guid.NewGuid(), receiptId, "STRWBRY PRESERVE", pendingId),
				BuildReceiptItem(Guid.NewGuid(), receiptId, "Strawberry Jam Jar", pendingId),
				BuildReceiptItem(Guid.NewGuid(), receiptId, "ZZZ Preserves", pendingId));
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		List<NormalizedDescriptionDetail> rows = await service.GetAllAsync(NormalizedDescriptionStatus.PendingReview, CancellationToken.None);

		NormalizedDescriptionDetail row = rows.Should().ContainSingle().Subject;
		row.LinkedItemCount.Should().Be(5);
		row.SampleRawDescriptions.Should().HaveCount(NormalizedDescriptionService.MaxSampleRawDescriptions);
		row.SampleRawDescriptions.Should().OnlyHaveUniqueItems();
		row.SampleRawDescriptions.Should().BeSubsetOf(["STRAWBERRY PRES", "STRWBRY PRESERVE", "Strawberry Jam Jar", "ZZZ Preserves"]);

		// The samples are ordered before the cap so the evidence an admin sees does not reshuffle
		// between refreshes. Asserting repeatability rather than a specific sequence keeps the test
		// honest: the actual collation is the database's, and InMemory does not share it.
		List<NormalizedDescriptionDetail> secondCall = await service.GetAllAsync(NormalizedDescriptionStatus.PendingReview, CancellationToken.None);
		secondCall.Single().SampleRawDescriptions.Should().Equal(row.SampleRawDescriptions);
	}

	[Fact]
	public async Task GetAllAsync_ExcludesSoftDeletedItemsFromTheLinkedCount()
	{
		Guid pendingId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = pendingId,
				CanonicalName = "Half Deleted",
				Status = NormalizedDescriptionStatus.PendingReview,
				CreatedAt = DateTimeOffset.UtcNow,
			});

			ReceiptItemEntity live = BuildReceiptItem(Guid.NewGuid(), receiptId, "LIVE ITEM", pendingId);
			ReceiptItemEntity deleted = BuildReceiptItem(Guid.NewGuid(), receiptId, "DELETED ITEM", pendingId);
			deleted.DeletedAt = DateTimeOffset.UtcNow;
			seed.ReceiptItems.AddRange(live, deleted);
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		List<NormalizedDescriptionDetail> rows = await service.GetAllAsync(NormalizedDescriptionStatus.PendingReview, CancellationToken.None);

		// A count that included soft-deleted rows would overstate how much data an
		// Approve/Merge/Split decision actually moves.
		NormalizedDescriptionDetail row = rows.Should().ContainSingle().Subject;
		row.LinkedItemCount.Should().Be(1);
		row.SampleRawDescriptions.Should().BeEquivalentTo(["LIVE ITEM"]);
	}

	[Fact]
	public async Task GetAllAsync_ResolvesTheNearestNeighbourName()
	{
		Guid neighbourId = Guid.NewGuid();
		Guid pendingId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.AddRange(
				new NormalizedDescriptionEntity
				{
					Id = neighbourId,
					CanonicalName = "Strawberry Jam",
					Status = NormalizedDescriptionStatus.Active,
					CreatedAt = DateTimeOffset.UtcNow,
				},
				new NormalizedDescriptionEntity
				{
					Id = pendingId,
					CanonicalName = "Strawberry Preserves",
					Status = NormalizedDescriptionStatus.PendingReview,
					CreatedAt = DateTimeOffset.UtcNow,
					NearestNeighbourId = neighbourId,
					NearestNeighbourSimilarity = 0.86,
				});
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		List<NormalizedDescriptionDetail> rows = await service.GetAllAsync(NormalizedDescriptionStatus.PendingReview, CancellationToken.None);

		NormalizedDescriptionDetail row = rows.Should().ContainSingle().Subject;
		row.NearestNeighbourName.Should().Be("Strawberry Jam");
		row.Description.NearestNeighbourSimilarity.Should().Be(0.86);
		// No linked receipt items — the count is a truthful 0 and the samples are empty, which is
		// a different statement from "no comparison recorded".
		row.LinkedItemCount.Should().Be(0);
		row.SampleRawDescriptions.Should().BeEmpty();
	}

	[Fact]
	public async Task GetAllAsync_NoRecordedNeighbour_ReturnsNullsRatherThanZero()
	{
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = Guid.NewGuid(),
				CanonicalName = "Legacy Pending Row",
				Status = NormalizedDescriptionStatus.PendingReview,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		List<NormalizedDescriptionDetail> rows = await service.GetAllAsync(NormalizedDescriptionStatus.PendingReview, CancellationToken.None);

		// Rows that predate RECEIPTS-873 (and rows whose neighbour was merged away) must surface as
		// "no comparison recorded" — a 0.0 default here would read as "scored zero against
		// something", which is a different and false claim.
		NormalizedDescriptionDetail row = rows.Should().ContainSingle().Subject;
		row.NearestNeighbourName.Should().BeNull();
		row.Description.NearestNeighbourSimilarity.Should().BeNull();
	}

	[Fact]
	public async Task PreviewRequeuePendingAsync_ReportsBlastRadiusAndCatchUpEstimate()
	{
		Guid pendingId = Guid.NewGuid();
		Guid activeId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.AddRange(
				BuildDescription(pendingId, "Pending Row", NormalizedDescriptionStatus.PendingReview),
				BuildDescription(activeId, "Active Row", NormalizedDescriptionStatus.Active));
			seed.ReceiptItems.AddRange(
				BuildReceiptItemWithScore(receiptId, "milk", 0.71, pendingId),
				// No score: counts toward LinkedItemCount but not StaleMatchScoreCount.
				BuildReceiptItemWithScore(receiptId, "bread", null, pendingId),
				// Linked to an Active row — must not be counted at all.
				BuildReceiptItemWithScore(receiptId, "eggs", 0.98, activeId));
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		RequeuePendingPreview preview = await service.PreviewRequeuePendingAsync(CancellationToken.None);

		preview.PendingDescriptionCount.Should().Be(1);
		preview.PendingFingerprint.Should().Be(FingerprintOf(pendingId), "the digest identifies the exact set the caller was shown");
		preview.LinkedItemCount.Should().Be(2);
		preview.StaleMatchScoreCount.Should().Be(1);
		// Two items fit inside one 50-item batch, so a single 30-second cycle drains them.
		preview.EstimatedResolverCycles.Should().Be(1);
		preview.EstimatedCatchUpSeconds.Should().Be(30);
	}

	[Fact]
	public async Task PreviewRequeuePendingAsync_NothingPending_ReportsAllZeroes()
	{
		Guid activeId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(BuildDescription(activeId, "Active Row", NormalizedDescriptionStatus.Active));
			seed.ReceiptItems.Add(BuildReceiptItemWithScore(Guid.NewGuid(), "eggs", 0.98, activeId));
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		RequeuePendingPreview preview = await service.PreviewRequeuePendingAsync(CancellationToken.None);

		// This all-zero shape is also the post-run verification the issue asks for: no receipt
		// item left holding a match score with no description behind it.
		preview.PendingDescriptionCount.Should().Be(0);
		preview.PendingFingerprint.Should().Be(FingerprintOf(), "the empty set still has a stable digest");
		preview.LinkedItemCount.Should().Be(0);
		preview.StaleMatchScoreCount.Should().Be(0);
		preview.EstimatedResolverCycles.Should().Be(0);
		preview.EstimatedCatchUpSeconds.Should().Be(0);
	}

	[Fact]
	public async Task RequeuePendingAsync_DeletesPendingRowsAndClearsFkAndScore()
	{
		Guid pendingId = Guid.NewGuid();
		Guid activeId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.AddRange(
				BuildDescription(pendingId, "Pending Row", NormalizedDescriptionStatus.PendingReview),
				BuildDescription(activeId, "Active Row", NormalizedDescriptionStatus.Active));
			seed.ReceiptItems.AddRange(
				BuildReceiptItemWithScore(receiptId, "milk", 0.71, pendingId),
				BuildReceiptItemWithScore(receiptId, "eggs", 0.98, activeId));
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		RequeuePendingResult? result = await service.RequeuePendingAsync(FingerprintOf(pendingId), CancellationToken.None);

		result.Should().NotBeNull();
		result!.DeletedDescriptionCount.Should().Be(1);
		result.UnlinkedItemCount.Should().Be(1);
		result.ClearedMatchScoreCount.Should().Be(1);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		verify.NormalizedDescriptions.Should().ContainSingle().Which.Id.Should().Be(activeId);

		ReceiptItemEntity requeued = await verify.ReceiptItems.IgnoreAutoIncludes()
			.SingleAsync(r => r.Description == "milk");
		// Both halves must be cleared. The FK alone would leave a score with nothing behind it —
		// the delete cascade covers the FK only, which is exactly why this is done explicitly.
		requeued.NormalizedDescriptionId.Should().BeNull();
		requeued.NormalizedDescriptionMatchScore.Should().BeNull();

		ReceiptItemEntity untouched = await verify.ReceiptItems.IgnoreAutoIncludes()
			.SingleAsync(r => r.Description == "eggs");
		untouched.NormalizedDescriptionId.Should().Be(activeId);
		untouched.NormalizedDescriptionMatchScore.Should().Be(0.98);
	}

	[Fact]
	public async Task RequeuePendingAsync_UnlinksTrashedItemsButExcludesThemFromCounts()
	{
		Guid pendingId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		Guid trashedId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(BuildDescription(pendingId, "Pending Row", NormalizedDescriptionStatus.PendingReview));
			ReceiptItemEntity trashed = BuildReceiptItemWithScore(receiptId, "trashed milk", 0.71, pendingId);
			trashed.Id = trashedId;
			trashed.DeletedAt = DateTimeOffset.UtcNow;
			seed.ReceiptItems.AddRange(BuildReceiptItemWithScore(receiptId, "live milk", 0.71, pendingId), trashed);
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		RequeuePendingResult? result = await service.RequeuePendingAsync(FingerprintOf(pendingId), CancellationToken.None);

		// Counts describe live items only, matching MergeAsync's established convention.
		result!.UnlinkedItemCount.Should().Be(1);
		result.ClearedMatchScoreCount.Should().Be(1);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		ReceiptItemEntity trashedAfter = await verify.ReceiptItems
			.IgnoreQueryFilters()
			.IgnoreAutoIncludes()
			.SingleAsync(r => r.Id == trashedId);
		// Repointed even though it isn't counted: restoring it from the recycle bin must not
		// resurrect an item carrying a stale score for a description that no longer exists.
		trashedAfter.NormalizedDescriptionId.Should().BeNull();
		trashedAfter.NormalizedDescriptionMatchScore.Should().BeNull();
	}

	[Fact]
	public async Task RequeuePendingAsync_FingerprintMismatch_DeletesNothing()
	{
		Guid pendingId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(BuildDescription(pendingId, "Pending Row", NormalizedDescriptionStatus.PendingReview));
			seed.ReceiptItems.Add(BuildReceiptItemWithScore(receiptId, "milk", 0.71, pendingId));
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// The caller previewed a different set. Acting anyway would destroy a row nobody reviewed,
		// so the guard refuses and reports nothing happened.
		RequeuePendingResult? result = await service.RequeuePendingAsync(FingerprintOf(Guid.NewGuid()), CancellationToken.None);

		result.Should().BeNull();

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		verify.NormalizedDescriptions.Should().ContainSingle();
		ReceiptItemEntity item = await verify.ReceiptItems.IgnoreAutoIncludes().SingleAsync();
		item.NormalizedDescriptionId.Should().Be(pendingId);
		item.NormalizedDescriptionMatchScore.Should().Be(0.71);
	}

	[Fact]
	public async Task RequeuePendingAsync_NothingPending_IsANoOpNotAnError()
	{
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(BuildDescription(Guid.NewGuid(), "Active Row", NormalizedDescriptionStatus.Active));
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Re-runnable by design — running it twice must not be an error the second time.
		RequeuePendingResult? result = await service.RequeuePendingAsync(FingerprintOf(), CancellationToken.None);

		result.Should().NotBeNull();
		result!.DeletedDescriptionCount.Should().Be(0);
		result.UnlinkedItemCount.Should().Be(0);
		result.ClearedMatchScoreCount.Should().Be(0);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		verify.NormalizedDescriptions.Should().ContainSingle();
	}

	[Fact]
	public async Task MergeAsync_WritesAMergeAuditEntryForBothSides()
	{
		Guid keepId = Guid.NewGuid();
		Guid discardId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.AddRange(
				BuildDescription(keepId, "Strawberry Jam", NormalizedDescriptionStatus.Active),
				BuildDescription(discardId, "Strawbery Jam", NormalizedDescriptionStatus.PendingReview));
			ReceiptItemEntity trashed = BuildReceiptItemWithScore(receiptId, "trashed jam", 0.86, discardId);
			trashed.DeletedAt = DateTimeOffset.UtcNow;
			seed.ReceiptItems.AddRange(
				BuildReceiptItemWithScore(receiptId, "jam a", 0.86, discardId),
				BuildReceiptItemWithScore(receiptId, "jam b", 0.86, discardId),
				trashed);
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		await service.MergeAsync(keepId, discardId, CancellationToken.None);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		List<AuditLogEntity> mergeLogs = await verify.AuditLogs
			.Where(a => a.Action == AuditAction.Merge)
			.ToListAsync();

		// One entry per side: the discarded id is about to stop existing, and an entry filed under
		// it is the only way to answer "what happened to this row?" afterwards.
		mergeLogs.Should().HaveCount(2);
		mergeLogs.Should().OnlyContain(a => a.EntityType == "NormalizedDescription");

		AuditLogEntity keepLog = mergeLogs.Single(a => a.EntityId == keepId.ToString());
		Dictionary<string, string?> keepFields = keepLog.GetChanges().ToDictionary(c => c.FieldName, c => c.NewValue);
		keepLog.GetChanges().Single(c => c.FieldName == "mergedFrom").OldValue.Should().Be("Strawbery Jam");
		keepFields["mergedFrom"].Should().Be("Strawberry Jam");
		keepFields["discardedId"].Should().BeNull();
		keepFields["relinkedItemCount"].Should().Be("2");
		// Trashed re-links are reported separately so the gap between this and the returned count
		// is inspectable rather than looking like a miscount.
		keepFields["relinkedTrashedItemCount"].Should().Be("1");

		AuditLogEntity discardLog = mergeLogs.Single(a => a.EntityId == discardId.ToString());
		Dictionary<string, string?> discardFields = discardLog.GetChanges().ToDictionary(c => c.FieldName, c => c.NewValue);
		discardFields["mergedInto"].Should().Be("Strawberry Jam");
		discardFields["keptId"].Should().Be(keepId.ToString());
	}

	[Fact]
	public async Task MergeAsync_MissingDiscardRow_ThrowsAndWritesNoAuditEntry()
	{
		// RECEIPTS-891: this used to return 0, which the controller answered 200 to — the
		// same answer a real merge with nothing to re-link gets. The registry list an admin
		// merges from can be minutes old, so a stale id is routine and must be told apart.
		Guid keepId = Guid.NewGuid();
		Guid missingId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(BuildDescription(keepId, "Strawberry Jam", NormalizedDescriptionStatus.Active));
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		Func<Task> act = () => service.MergeAsync(keepId, missingId, CancellationToken.None);

		// The message names which id was missing: an admin who merged the wrong way round
		// needs to know which of the two was stale.
		(await act.Should().ThrowAsync<KeyNotFoundException>())
			.WithMessage($"*{missingId}*");

		// No merge happened, so recording one would be a lie in the audit trail.
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		(await verify.AuditLogs.CountAsync(a => a.Action == AuditAction.Merge)).Should().Be(0);
	}

	[Fact]
	public async Task MergeAsync_MissingKeepRow_ThrowsNamingTheKeptId()
	{
		Guid missingKeepId = Guid.NewGuid();
		Guid discardId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(BuildDescription(discardId, "Strawberry Preserve", NormalizedDescriptionStatus.Active));
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		Func<Task> act = () => service.MergeAsync(missingKeepId, discardId, CancellationToken.None);

		(await act.Should().ThrowAsync<KeyNotFoundException>())
			.WithMessage($"*{missingKeepId}*");

		// The row that does exist survives — a rejected merge writes nothing.
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		(await verify.NormalizedDescriptions.CountAsync(d => d.Id == discardId)).Should().Be(1);
	}

	[Fact]
	public async Task MergeAsync_SameIdOnBothSides_ThrowsRatherThanReportingANoOp()
	{
		Guid id = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(BuildDescription(id, "Strawberry Jam", NormalizedDescriptionStatus.Active));
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		Func<Task> act = () => service.MergeAsync(id, id, CancellationToken.None);

		await act.Should().ThrowAsync<ArgumentException>()
			.WithMessage($"{NormalizedDescriptionService.MergeIdsMustDiffer}*");
	}

	[Fact]
	public async Task SplitAsync_WritesASplitAuditEntryForBothSides()
	{
		Guid originId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		Guid itemId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(BuildDescription(originId, "Jam", NormalizedDescriptionStatus.Active));
			seed.ReceiptItems.Add(BuildReceiptItem(itemId, receiptId, "Strawberry Preserves", originId));
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(false);
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		NormalizedDescriptionDetail created = await service.SplitAsync(itemId, CancellationToken.None);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		List<AuditLogEntity> splitLogs = await verify.AuditLogs
			.Where(a => a.Action == AuditAction.Split)
			.ToListAsync();

		splitLogs.Should().HaveCount(2);

		AuditLogEntity newRowLog = splitLogs.Single(a => a.EntityId == created.Description.Id.ToString());
		Dictionary<string, string?> newFields = newRowLog.GetChanges().ToDictionary(c => c.FieldName, c => c.NewValue);
		newRowLog.GetChanges().Single(c => c.FieldName == "splitFrom").OldValue.Should().Be("Jam");
		newFields["splitFrom"].Should().Be("Strawberry Preserves");
		newFields["receiptItemId"].Should().Be(itemId.ToString());

		AuditLogEntity originLog = splitLogs.Single(a => a.EntityId == originId.ToString());
		Dictionary<string, string?> originFields = originLog.GetChanges().ToDictionary(c => c.FieldName, c => c.NewValue);
		originFields["splitToId"].Should().Be(created.Description.Id.ToString());
	}

	[Fact]
	public async Task SplitAsync_UnlinkedItem_WritesOnlyTheNewRowEntry()
	{
		Guid receiptId = Guid.NewGuid();
		Guid itemId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.ReceiptItems.Add(BuildReceiptItem(itemId, receiptId, "Strawberry Preserves", normalizedId: null));
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(false);
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		await service.SplitAsync(itemId, CancellationToken.None);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		List<AuditLogEntity> splitLogs = await verify.AuditLogs
			.Where(a => a.Action == AuditAction.Split)
			.ToListAsync();

		// Splitting an item that was never resolved is legitimate; there is simply no origin row to
		// file a second entry against, and inventing one would point at nothing.
		splitLogs.Should().ContainSingle();
		splitLogs[0].GetChanges().Single(c => c.FieldName == "splitFrom").OldValue.Should().BeNull();
	}

	[Theory]
	[InlineData("jam")]
	[InlineData("  Jam  ")]
	public async Task SplitAsync_ItemAlreadyOnAMatchingRow_RecordsNoSplit(string rawDescription)
	{
		// InsertAsync returns ANY existing row with this canonical name, including the item's own
		// current description when the raw text differs only by case or whitespace. Nothing is
		// detached, so an entry here would record a row as having been split out of itself.
		Guid originId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		Guid itemId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(BuildDescription(originId, "Jam", NormalizedDescriptionStatus.Active));
			seed.ReceiptItems.Add(BuildReceiptItem(itemId, receiptId, rawDescription, originId));
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(false);
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		NormalizedDescriptionDetail result = await service.SplitAsync(itemId, CancellationToken.None);

		// The item stays where it was — SplitAsync is a no-op in this case.
		result.Description.Id.Should().Be(originId);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		(await verify.AuditLogs.CountAsync(a => a.Action == AuditAction.Split)).Should().Be(0);
	}

	[Fact]
	public async Task SplitAsync_LandsOnAPreexistingRow_RecordsThatNoRowWasCreated()
	{
		// A different pre-existing row means the item was re-linked, not split into a new
		// description. Claiming otherwise would imply a row was created when none was.
		Guid originId = Guid.NewGuid();
		Guid existingTargetId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		Guid itemId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.AddRange(
				BuildDescription(originId, "Jam", NormalizedDescriptionStatus.Active),
				BuildDescription(existingTargetId, "Strawberry Preserves", NormalizedDescriptionStatus.Active));
			seed.ReceiptItems.Add(BuildReceiptItem(itemId, receiptId, "Strawberry Preserves", originId));
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(false);
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		await service.SplitAsync(itemId, CancellationToken.None);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		List<AuditLogEntity> splitLogs = await verify.AuditLogs
			.Where(a => a.Action == AuditAction.Split)
			.ToListAsync();

		splitLogs.Should().HaveCount(2);
		splitLogs.Should().OnlyContain(a =>
			a.ChangesJson.Contains("\"FieldName\":\"targetWasExistingRow\"") &&
			a.ChangesJson.Contains("\"NewValue\":\"true\""));
	}

	[Fact]
	public async Task SplitAsync_NewRowCreated_RecordsThatTheTargetIsNew()
	{
		Guid originId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		Guid itemId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(BuildDescription(originId, "Jam", NormalizedDescriptionStatus.Active));
			seed.ReceiptItems.Add(BuildReceiptItem(itemId, receiptId, "Strawberry Preserves", originId));
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(false);
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		await service.SplitAsync(itemId, CancellationToken.None);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		List<AuditLogEntity> splitLogs = await verify.AuditLogs
			.Where(a => a.Action == AuditAction.Split)
			.ToListAsync();

		splitLogs.Should().HaveCount(2);
		splitLogs[0].GetChanges().Single(c => c.FieldName == "targetWasExistingRow").NewValue.Should().Be("false");
	}

	[Fact]
	public async Task RequeuePendingAsync_WritesOneSemanticEntryForTheWholeOperation()
	{
		Guid pendingId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(BuildDescription(pendingId, "Pending Row", NormalizedDescriptionStatus.PendingReview));
			ReceiptItemEntity trashed = BuildReceiptItemWithScore(receiptId, "trashed milk", 0.71, pendingId);
			trashed.DeletedAt = DateTimeOffset.UtcNow;
			seed.ReceiptItems.AddRange(BuildReceiptItemWithScore(receiptId, "live milk", 0.71, pendingId), trashed);
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		await service.RequeuePendingAsync(FingerprintOf(pendingId), CancellationToken.None);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		List<AuditLogEntity> requeueLogs = await verify.AuditLogs
			.Where(a => a.EntityType == "NormalizedDescription" && a.EntityId == string.Empty)
			.ToListAsync();

		// One entry for the operator's single decision — not N entries, which would bury the intent
		// under the same row-by-row noise the semantic entry exists to summarise.
		AuditLogEntity log = requeueLogs.Should().ContainSingle().Subject;
		Dictionary<string, string?> fields = log.GetChanges().ToDictionary(c => c.FieldName, c => c.NewValue);
		fields["operation"].Should().Be("RequeuePending");
		fields["deletedDescriptionCount"].Should().Be("1");
		fields["unlinkedItemCount"].Should().Be("1");
		fields["clearedMatchScoreCount"].Should().Be("1");
		fields["unlinkedTrashedItemCount"].Should().Be("1");
	}

	[Fact]
	public async Task RequeuePendingAsync_RejectedGuard_WritesNoAuditEntry()
	{
		Guid pendingId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(BuildDescription(pendingId, "Pending Row", NormalizedDescriptionStatus.PendingReview));
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		await service.RequeuePendingAsync(FingerprintOf(Guid.NewGuid()), CancellationToken.None);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		// Nothing was destroyed, so nothing may be recorded as destroyed.
		(await verify.AuditLogs.CountAsync(a => a.EntityId == string.Empty && a.EntityType == "NormalizedDescription"))
			.Should().Be(0);
	}

	// The requeue guard takes the digest of the exact pending set the caller previewed, so a test
	// asserting the happy path has to hand back the ids it seeded, and a test asserting refusal
	// hands back anything else.
	private static string FingerprintOf(params Guid[] ids) =>
		NormalizedDescriptionService.ComputePendingFingerprint(ids);

	private static NormalizedDescriptionEntity BuildDescription(Guid id, string canonicalName, NormalizedDescriptionStatus status)
	{
		return new NormalizedDescriptionEntity
		{
			Id = id,
			CanonicalName = canonicalName,
			Status = status,
			CreatedAt = DateTimeOffset.UtcNow,
		};
	}

	private static ReceiptItemEntity BuildReceiptItem(Guid id, Guid receiptId, string description, Guid? normalizedId)
	{
		return new ReceiptItemEntity
		{
			Id = id,
			ReceiptId = receiptId,
			Description = description,
			Quantity = 1,
			UnitPrice = 1,
			UnitPriceCurrency = Currency.USD,
			TotalAmount = 1,
			TotalAmountCurrency = Currency.USD,
			Category = "Groceries",
			NormalizedDescriptionId = normalizedId,
		};
	}

	private static ReceiptItemEntity BuildReceiptItemWithScore(Guid receiptId, string description, double? score, Guid? normalizedId)
	{
		return new ReceiptItemEntity
		{
			Id = Guid.NewGuid(),
			ReceiptId = receiptId,
			Description = description,
			Quantity = 1,
			UnitPrice = 1,
			UnitPriceCurrency = Currency.USD,
			TotalAmount = 1,
			TotalAmountCurrency = Currency.USD,
			Category = "Groceries",
			NormalizedDescriptionId = normalizedId,
			NormalizedDescriptionMatchScore = score,
		};
	}

	// ── RECEIPTS-876: rename and reject ────────────────────────────────────────────────

	[Fact]
	public async Task GetOrCreateAsync_ExactMatchOnTombstone_ReturnsRejectedWithNoScore()
	{
		// Arrange — a reviewer already said this text is not worth a canonical entry.
		Guid tombstoneId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = tombstoneId,
				CanonicalName = "MISC 4.99",
				Status = NormalizedDescriptionStatus.Rejected,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act
		GetOrCreateResult result = await service.GetOrCreateAsync("misc 4.99", CancellationToken.None);

		// Assert
		result.IsRejected.Should().BeTrue();
		result.Description.Id.Should().Be(tombstoneId);

		// Not 1.0 as the ordinary exact-match branch returns. The caller is about to decline to
		// link, and a score recorded against no link is exactly the orphan state RECEIPTS-883
		// exists to prevent.
		result.MatchScore.Should().BeNull();

		// No new row: the whole point of a tombstone is that the resolver stops recreating it.
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		verify.NormalizedDescriptions.Should().HaveCount(1);
	}

	[Fact]
	public async Task UpdateStatusAsync_ToRejected_UnlinksItemsAndClearsScores()
	{
		// Arrange
		Guid descriptionId = Guid.NewGuid();
		Guid liveItemId = Guid.NewGuid();
		Guid trashedItemId = Guid.NewGuid();

		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = descriptionId,
				CanonicalName = "MISC 4.99",
				Status = NormalizedDescriptionStatus.PendingReview,
				CreatedAt = DateTimeOffset.UtcNow,
			});

			ReceiptItemEntity live = BuildReceiptItem(liveItemId, Guid.NewGuid(), "misc 4.99", descriptionId);
			live.NormalizedDescriptionMatchScore = 0.71;

			ReceiptItemEntity trashed = BuildReceiptItem(trashedItemId, Guid.NewGuid(), "misc 4.99", descriptionId);
			trashed.NormalizedDescriptionMatchScore = 0.68;
			trashed.DeletedAt = DateTimeOffset.UtcNow;

			seed.ReceiptItems.AddRange(live, trashed);
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act
		bool changed = await service.UpdateStatusAsync(descriptionId, NormalizedDescriptionStatus.Rejected, CancellationToken.None);

		// Assert
		changed.Should().BeTrue();

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();

		// The row survives — it is the tombstone.
		verify.NormalizedDescriptions.Single(e => e.Id == descriptionId)
			.Status.Should().Be(NormalizedDescriptionStatus.Rejected);

		List<ReceiptItemEntity> items = await verify.ReceiptItems
			.IgnoreQueryFilters()
			.IgnoreAutoIncludes()
			.Where(r => r.Id == liveItemId || r.Id == trashedItemId)
			.ToListAsync();

		items.Should().HaveCount(2);
		items.Should().OnlyContain(r => r.NormalizedDescriptionId == null);

		// The score has to go with the link. A live item carrying a score with nothing to explain
		// it is the inconsistent state RECEIPTS-883 exists to prevent.
		items.Should().OnlyContain(r => r.NormalizedDescriptionMatchScore == null);
	}

	[Fact]
	public async Task RenameAsync_SetsLabelWithoutTouchingMatchTextOrEmbedding()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		float[] original = CreateFakeEmbedding();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = id,
				CanonicalName = "MILK 2% GAL",
				Status = NormalizedDescriptionStatus.Active,
				Embedding = new Vector(original),
				EmbeddingModelVersion = "test-model",
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act
		NormalizedDescriptionDetail result = await service.RenameAsync(id, "  Milk  ", CancellationToken.None);

		// Assert
		result.Description.DisplayLabel.Should().Be("Milk", "the label is trimmed before storing");
		result.Description.DisplayName.Should().Be("Milk");

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		NormalizedDescriptionEntity row = verify.NormalizedDescriptions.Single(e => e.Id == id);

		// The two things a rename must never touch. Re-embedding on rename would let a clean
		// human label match receipt text worse than the messy original, silently degrading
		// resolution for every future receipt.
		row.CanonicalName.Should().Be("MILK 2% GAL");
		row.Embedding!.ToArray().Should().BeEquivalentTo(original);
		row.EmbeddingModelVersion.Should().Be("test-model");

		// No embedding was generated at all — renaming is a metadata write, not a re-resolution.
		_embeddingServiceMock.Verify(
			e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task RenameAsync_LabelMatchingItsOwnMatchText_StoresNullRatherThanADuplicate()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = id,
				CanonicalName = "Bananas",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act — "renaming" a row to what it already displays.
		await service.RenameAsync(id, "bananas", CancellationToken.None);

		// Assert — stored as "not renamed" rather than a redundant copy of the matched text.
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		verify.NormalizedDescriptions.Single(e => e.Id == id).DisplayLabel.Should().BeNull();
	}

	[Fact]
	public async Task RenameAsync_NullLabel_ClearsBackToMatchText()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = id,
				CanonicalName = "MILK 2% GAL",
				DisplayLabel = "Milk",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act
		NormalizedDescriptionDetail result = await service.RenameAsync(id, null, CancellationToken.None);

		// Assert
		result.Description.DisplayLabel.Should().BeNull();
		result.Description.DisplayName.Should().Be("MILK 2% GAL");
	}

	[Fact]
	public async Task RenameAsync_CollidingWithAnotherRowsUnrenamedName_Throws()
	{
		// Arrange — the collision an index on DisplayLabel alone would miss: renaming one row
		// onto a name another row already displays via its matched text.
		Guid targetId = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.AddRange(
				new NormalizedDescriptionEntity
				{
					Id = targetId,
					CanonicalName = "MILK 2% GAL",
					Status = NormalizedDescriptionStatus.Active,
					CreatedAt = DateTimeOffset.UtcNow,
				},
				new NormalizedDescriptionEntity
				{
					Id = Guid.NewGuid(),
					CanonicalName = "Milk",
					Status = NormalizedDescriptionStatus.Active,
					CreatedAt = DateTimeOffset.UtcNow,
				});
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// Act
		Func<Task> act = async () => await service.RenameAsync(targetId, "milk", CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage(NormalizedDescriptionService.DisplayNameAlreadyTaken);
	}

	[Fact]
	public async Task RenameAsync_WhitespaceOnlyLabel_Throws()
	{
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		// A blank text box is far more often a slip than an intent to clear, so it is an error
		// rather than a silent fallback to the matched text.
		Func<Task> act = async () => await service.RenameAsync(Guid.NewGuid(), "   ", CancellationToken.None);

		await act.Should().ThrowAsync<ArgumentException>();
	}

	[Fact]
	public async Task RenameAsync_UnknownId_Throws()
	{
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		Func<Task> act = async () => await service.RenameAsync(Guid.NewGuid(), "Milk", CancellationToken.None);

		await act.Should().ThrowAsync<KeyNotFoundException>();
	}

	private static float[] CreateFakeEmbedding()
	{
		float[] embedding = new float[OnnxEmbeddingService.EmbeddingDimension];
		Random rng = new(42);
		for (int i = 0; i < embedding.Length; i++)
		{
			embedding[i] = (float)(rng.NextDouble() * 2 - 1);
		}

		return embedding;
	}

	// Test subclass that overrides the ANN search to deterministically return a seeded match.
	// This lets us exercise the threshold-band logic against InMemory, which cannot run the
	// pgvector `<=>` operator.
	private sealed class TestableNormalizedDescriptionService(
		IDbContextFactory<ApplicationDbContext> contextFactory,
		IEmbeddingService embeddingService,
		NormalizedDescriptionMapper mapper,
		NormalizedDescriptionSettingsMapper settingsMapper,
		Guid matchId,
		double similarity) : NormalizedDescriptionService(contextFactory, embeddingService, mapper, settingsMapper)
	{
		private readonly Guid _matchId = matchId;
		private readonly double _similarity = similarity;

		protected override async Task<(NormalizedDescriptionEntity? Match, double? Similarity)> AnnSearchTopOneAsync(
			ApplicationDbContext context, Vector queryVector, CancellationToken cancellationToken)
		{
			NormalizedDescriptionEntity? match = await context.NormalizedDescriptions
				.FirstOrDefaultAsync(e => e.Id == _matchId, cancellationToken);
			return (match, _similarity);
		}

		protected override async Task<List<MatchCandidate>> AnnSearchTopNAsync(
			ApplicationDbContext context, Vector queryVector, int topN, CancellationToken cancellationToken)
		{
			NormalizedDescriptionEntity? match = await context.NormalizedDescriptions
				.FirstOrDefaultAsync(e => e.Id == _matchId, cancellationToken);
			if (match is null)
			{
				return [];
			}

			return [new MatchCandidate(match.Id, match.CanonicalName, _similarity, match.Status.ToString())];
		}
	}
}
