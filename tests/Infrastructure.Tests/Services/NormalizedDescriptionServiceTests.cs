using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Common;
using Domain.NormalizedDescriptions;
using FluentAssertions;
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
