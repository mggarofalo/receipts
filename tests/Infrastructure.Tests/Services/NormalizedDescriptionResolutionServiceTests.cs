using System.Threading.Channels;
using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Infrastructure.Tests.Services;

[Trait("Category", "Unit")]
public class NormalizedDescriptionResolutionServiceTests
{
	private readonly Mock<IEmbeddingService> _embeddingServiceMock;
	private readonly Mock<INormalizedDescriptionService> _normalizedServiceMock;
	private readonly Mock<IDescriptionChangeSignal> _signalMock;
	private readonly Mock<ILogger<NormalizedDescriptionResolutionService>> _loggerMock;
	private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
	private readonly Mock<IServiceScope> _scopeMock;
	private readonly Mock<IServiceProvider> _serviceProviderMock;
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;

	public NormalizedDescriptionResolutionServiceTests()
	{
		_embeddingServiceMock = new Mock<IEmbeddingService>();
		_normalizedServiceMock = new Mock<INormalizedDescriptionService>();
		_signalMock = new Mock<IDescriptionChangeSignal>();
		// The service Subscribe()s once at construction for its own wake-up reader.
		_signalMock
			.Setup(s => s.Subscribe())
			.Returns(Channel.CreateBounded<bool>(1).Reader);
		_loggerMock = new Mock<ILogger<NormalizedDescriptionResolutionService>>();
		_scopeFactoryMock = new Mock<IServiceScopeFactory>();
		_scopeMock = new Mock<IServiceScope>();
		_serviceProviderMock = new Mock<IServiceProvider>();

		(_contextFactory, MockCurrentUserAccessor accessor) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		accessor.UserId = "test-user";

		_serviceProviderMock
			.Setup(sp => sp.GetService(typeof(IEmbeddingService)))
			.Returns(_embeddingServiceMock.Object);
		_serviceProviderMock
			.Setup(sp => sp.GetService(typeof(INormalizedDescriptionService)))
			.Returns(_normalizedServiceMock.Object);
		_serviceProviderMock
			.Setup(sp => sp.GetService(typeof(IDbContextFactory<ApplicationDbContext>)))
			.Returns(_contextFactory);
		_scopeMock
			.Setup(s => s.ServiceProvider)
			.Returns(_serviceProviderMock.Object);
		_scopeFactoryMock
			.Setup(f => f.CreateScope())
			.Returns(_scopeMock.Object);
	}

	private NormalizedDescriptionResolutionService CreateService() => new(
		_scopeFactoryMock.Object,
		_signalMock.Object,
		_loggerMock.Object);

	private async Task SeedReceiptAndItemsAsync(params ReceiptItemEntity[] items)
	{
		using ApplicationDbContext seed = _contextFactory.CreateDbContext();
		// Use a single ReceiptEntity for FK consistency — the resolver only reads from
		// ReceiptItems and doesn't care about the receipt, but EF's relationship graph
		// rejects orphan items on SaveChangesAsync.
		Guid receiptId = Guid.NewGuid();
		foreach (ReceiptItemEntity item in items)
		{
			item.ReceiptId = receiptId;
		}

		seed.Receipts.Add(new ReceiptEntity
		{
			Id = receiptId,
			Date = new DateOnly(2026, 4, 19),
			Location = "Test",
			TaxAmount = 0m,
			TaxAmountCurrency = Common.Currency.USD,
		});
		seed.ReceiptItems.AddRange(items);
		await seed.SaveChangesAsync();
	}

	private static ReceiptItemEntity BuildItem(string description, Guid? receiptId = null, DateTimeOffset? deletedAt = null)
	{
		return new ReceiptItemEntity
		{
			Id = Guid.NewGuid(),
			ReceiptId = receiptId ?? Guid.NewGuid(),
			Description = description,
			Quantity = 1m,
			UnitPrice = 1m,
			UnitPriceCurrency = Common.Currency.USD,
			TotalAmount = 1m,
			TotalAmountCurrency = Common.Currency.USD,
			Category = "Test",
			DeletedAt = deletedAt,
		};
	}

	private static GetOrCreateResult NewResult(string canonicalName, double? matchScore)
	{
		NormalizedDescription domain = new(
			Guid.NewGuid(),
			canonicalName,
			NormalizedDescriptionStatus.Active,
			DateTimeOffset.UtcNow);
		return new GetOrCreateResult(domain, matchScore);
	}

	// ── RECEIPTS-876: tombstoned text ──────────────────────────────────────────────────

	[Fact]
	public async Task ProcessPendingResolutionsAsync_TombstonedDescription_IsNeverEvenAskedAbout()
	{
		// Arrange — one item whose text a reviewer already rejected, one ordinary item.
		Guid receiptId = Guid.NewGuid();
		ReceiptItemEntity rejected = BuildItem("MISC 4.99", receiptId);
		ReceiptItemEntity ordinary = BuildItem("Bananas", receiptId);
		await SeedReceiptAndItemsAsync(rejected, ordinary);

		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = Guid.NewGuid(),
				// Different casing from the receipt text on purpose: the tombstone has to bite
				// case-insensitively, matching the unique index on lower("CanonicalName").
				CanonicalName = "misc 4.99",
				Status = NormalizedDescriptionStatus.Rejected,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_normalizedServiceMock
			.Setup(s => s.GetOrCreateAsync("Bananas", It.IsAny<CancellationToken>()))
			.ReturnsAsync(NewResult("Bananas", null));

		// Act
		NormalizedDescriptionResolutionService.ResolutionSummary summary =
			await CreateService().ProcessPendingResolutionsAsync(CancellationToken.None);

		// Assert — the rejected text never reaches the service at all. Excluding it at the query
		// is what stops it occupying the batch window on every cycle forever; declining it later
		// would leave it re-selected each time and eventually starve real work.
		_normalizedServiceMock.Verify(
			s => s.GetOrCreateAsync("MISC 4.99", It.IsAny<CancellationToken>()),
			Times.Never);
		summary.Linked.Should().Be(1);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		verify.ReceiptItems.IgnoreAutoIncludes().Single(r => r.Id == rejected.Id)
			.NormalizedDescriptionId.Should().BeNull();
		verify.ReceiptItems.IgnoreAutoIncludes().Single(r => r.Id == ordinary.Id)
			.NormalizedDescriptionId.Should().NotBeNull();
	}

	[Fact]
	public async Task ProcessPendingResolutionsAsync_ServiceReturnsRejectedRow_DoesNotLink()
	{
		// Arrange — the race the query filter cannot close: the text is tombstoned between
		// building the batch and resolving the group.
		ReceiptItemEntity item = BuildItem("MISC 4.99");
		await SeedReceiptAndItemsAsync(item);

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);

		NormalizedDescription tombstone = new(
			Guid.NewGuid(),
			"MISC 4.99",
			NormalizedDescriptionStatus.Rejected,
			DateTimeOffset.UtcNow);
		_normalizedServiceMock
			.Setup(s => s.GetOrCreateAsync("MISC 4.99", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new GetOrCreateResult(tombstone, MatchScore: null));

		// Act
		NormalizedDescriptionResolutionService.ResolutionSummary summary =
			await CreateService().ProcessPendingResolutionsAsync(CancellationToken.None);

		// Assert
		summary.Linked.Should().Be(0);
		summary.Skipped.Should().Be(1);
		// Emphatically not linked to the tombstone: that would re-attach the very items the
		// reviewer detached, and hide their spend from "(Not Normalized)".
		summary.NewEntriesCreated.Should().Be(0);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		ReceiptItemEntity saved = verify.ReceiptItems.IgnoreAutoIncludes().Single(r => r.Id == item.Id);
		saved.NormalizedDescriptionId.Should().BeNull();
		saved.NormalizedDescriptionMatchScore.Should().BeNull();
	}

	[Fact]
	public async Task ProcessPendingResolutionsAsync_BatchDedup_OneCallPerUniqueDescription()
	{
		// Arrange — two items share a description; a third has its own.
		Guid receiptId = Guid.NewGuid();
		ReceiptItemEntity dup1 = BuildItem("Organic Milk", receiptId);
		ReceiptItemEntity dup2 = BuildItem("Organic Milk", receiptId);
		ReceiptItemEntity other = BuildItem("Bananas", receiptId);
		await SeedReceiptAndItemsAsync(dup1, dup2, other);

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);

		GetOrCreateResult milkResult = NewResult("Organic Milk", 0.95);
		GetOrCreateResult bananaResult = NewResult("Bananas", null);

		_normalizedServiceMock
			.Setup(s => s.GetOrCreateAsync("Organic Milk", It.IsAny<CancellationToken>()))
			.ReturnsAsync(milkResult);
		_normalizedServiceMock
			.Setup(s => s.GetOrCreateAsync("Bananas", It.IsAny<CancellationToken>()))
			.ReturnsAsync(bananaResult);

		NormalizedDescriptionResolutionService service = CreateService();

		// Act
		var summary = await service.ProcessPendingResolutionsAsync(CancellationToken.None);

		// Assert — one call per unique description.
		_normalizedServiceMock.Verify(
			s => s.GetOrCreateAsync("Organic Milk", It.IsAny<CancellationToken>()),
			Times.Once);
		_normalizedServiceMock.Verify(
			s => s.GetOrCreateAsync("Bananas", It.IsAny<CancellationToken>()),
			Times.Once);

		summary.Linked.Should().Be(3);
		summary.NewEntriesCreated.Should().Be(1, "the Bananas result had no match score, indicating a new canonical entry");

		// Both dup items point at the same NormalizedDescriptionId; the banana item at its own.
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		List<ReceiptItemEntity> items = await verify.ReceiptItems.AsNoTracking().ToListAsync();
		items.Should().HaveCount(3);

		ReceiptItemEntity rDup1 = items.Single(i => i.Id == dup1.Id);
		ReceiptItemEntity rDup2 = items.Single(i => i.Id == dup2.Id);
		ReceiptItemEntity rOther = items.Single(i => i.Id == other.Id);

		rDup1.NormalizedDescriptionId.Should().Be(milkResult.Description.Id);
		rDup2.NormalizedDescriptionId.Should().Be(milkResult.Description.Id);
		rOther.NormalizedDescriptionId.Should().Be(bananaResult.Description.Id);
	}

	[Fact]
	public async Task ProcessPendingResolutionsAsync_WritesMatchScore_AlongsideFk()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		ReceiptItemEntity matched = BuildItem("Eggs Large", receiptId);
		ReceiptItemEntity fresh = BuildItem("Mystery Widget", receiptId);
		await SeedReceiptAndItemsAsync(matched, fresh);

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);

		GetOrCreateResult matchedResult = NewResult("Eggs", 0.87);
		GetOrCreateResult freshResult = NewResult("Mystery Widget", null);

		_normalizedServiceMock
			.Setup(s => s.GetOrCreateAsync("Eggs Large", It.IsAny<CancellationToken>()))
			.ReturnsAsync(matchedResult);
		_normalizedServiceMock
			.Setup(s => s.GetOrCreateAsync("Mystery Widget", It.IsAny<CancellationToken>()))
			.ReturnsAsync(freshResult);

		NormalizedDescriptionResolutionService service = CreateService();

		// Act
		await service.ProcessPendingResolutionsAsync(CancellationToken.None);

		// Assert
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		ReceiptItemEntity persistedMatched = await verify.ReceiptItems.AsNoTracking().SingleAsync(i => i.Id == matched.Id);
		ReceiptItemEntity persistedFresh = await verify.ReceiptItems.AsNoTracking().SingleAsync(i => i.Id == fresh.Id);

		persistedMatched.NormalizedDescriptionId.Should().Be(matchedResult.Description.Id);
		persistedMatched.NormalizedDescriptionMatchScore.Should().Be(0.87);

		// null MatchScore signals "brand-new canonical entry" — we still link the FK but
		// don't invent a score.
		persistedFresh.NormalizedDescriptionId.Should().Be(freshResult.Description.Id);
		persistedFresh.NormalizedDescriptionMatchScore.Should().BeNull();
	}

	[Fact]
	public async Task ProcessPendingResolutionsAsync_SkipsCycle_WhenEmbeddingServiceNotConfigured()
	{
		// Arrange
		await SeedReceiptAndItemsAsync(BuildItem("Would Be Resolved"));
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(false);

		NormalizedDescriptionResolutionService service = CreateService();

		// Act
		var summary = await service.ProcessPendingResolutionsAsync(CancellationToken.None);

		// Assert
		summary.Linked.Should().Be(0);
		_normalizedServiceMock.Verify(
			s => s.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task ProcessPendingResolutionsAsync_FiltersOutShortDescriptions()
	{
		// Arrange — "X" is below MinDescriptionLength; empty is filtered separately.
		ReceiptItemEntity tooShort = BuildItem("X");
		ReceiptItemEntity empty = BuildItem(string.Empty);
		ReceiptItemEntity good = BuildItem("Bread");
		await SeedReceiptAndItemsAsync(tooShort, empty, good);

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_normalizedServiceMock
			.Setup(s => s.GetOrCreateAsync("Bread", It.IsAny<CancellationToken>()))
			.ReturnsAsync(NewResult("Bread", 0.99));

		NormalizedDescriptionResolutionService service = CreateService();

		// Act
		var summary = await service.ProcessPendingResolutionsAsync(CancellationToken.None);

		// Assert — only the valid row was processed.
		summary.Linked.Should().Be(1);
		_normalizedServiceMock.Verify(
			s => s.GetOrCreateAsync("X", It.IsAny<CancellationToken>()),
			Times.Never);
		_normalizedServiceMock.Verify(
			s => s.GetOrCreateAsync(string.Empty, It.IsAny<CancellationToken>()),
			Times.Never);
		_normalizedServiceMock.Verify(
			s => s.GetOrCreateAsync("Bread", It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task ProcessPendingResolutionsAsync_ExcludesSoftDeletedItems()
	{
		// Arrange
		ReceiptItemEntity deleted = BuildItem("Soft Deleted", deletedAt: DateTimeOffset.UtcNow);
		ReceiptItemEntity live = BuildItem("Live Item");
		await SeedReceiptAndItemsAsync(deleted, live);

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_normalizedServiceMock
			.Setup(s => s.GetOrCreateAsync("Live Item", It.IsAny<CancellationToken>()))
			.ReturnsAsync(NewResult("Live Item", 0.9));

		NormalizedDescriptionResolutionService service = CreateService();

		// Act
		await service.ProcessPendingResolutionsAsync(CancellationToken.None);

		// Assert — soft-deleted item was never offered to the normalized service.
		_normalizedServiceMock.Verify(
			s => s.GetOrCreateAsync("Soft Deleted", It.IsAny<CancellationToken>()),
			Times.Never);
		_normalizedServiceMock.Verify(
			s => s.GetOrCreateAsync("Live Item", It.IsAny<CancellationToken>()),
			Times.Once);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		ReceiptItemEntity persistedDeleted = await verify.ReceiptItems
			.IgnoreQueryFilters()
			.AsNoTracking()
			.SingleAsync(i => i.Id == deleted.Id);
		persistedDeleted.NormalizedDescriptionId.Should().BeNull();
	}

	[Fact]
	public async Task ProcessPendingResolutionsAsync_ExcludesItemsWithFkAlreadySet()
	{
		// Arrange — already-linked rows must not be re-processed.
		Guid existingFk = Guid.NewGuid();

		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = existingFk,
				CanonicalName = "Pre-linked",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}

		ReceiptItemEntity alreadyLinked = BuildItem("Already Linked");
		alreadyLinked.NormalizedDescriptionId = existingFk;
		ReceiptItemEntity unresolved = BuildItem("Unresolved");
		await SeedReceiptAndItemsAsync(alreadyLinked, unresolved);

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_normalizedServiceMock
			.Setup(s => s.GetOrCreateAsync("Unresolved", It.IsAny<CancellationToken>()))
			.ReturnsAsync(NewResult("Unresolved", 0.85));

		NormalizedDescriptionResolutionService service = CreateService();

		// Act
		await service.ProcessPendingResolutionsAsync(CancellationToken.None);

		// Assert
		_normalizedServiceMock.Verify(
			s => s.GetOrCreateAsync("Already Linked", It.IsAny<CancellationToken>()),
			Times.Never);
		_normalizedServiceMock.Verify(
			s => s.GetOrCreateAsync("Unresolved", It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task ProcessPendingResolutionsAsync_ExceptionInGetOrCreate_LogsAndContinues()
	{
		// Arrange — one group throws, the other succeeds.
		Guid receiptId = Guid.NewGuid();
		ReceiptItemEntity bad = BuildItem("Problematic", receiptId);
		ReceiptItemEntity good = BuildItem("Works Fine", receiptId);
		await SeedReceiptAndItemsAsync(bad, good);

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_normalizedServiceMock
			.Setup(s => s.GetOrCreateAsync("Problematic", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("boom"));
		_normalizedServiceMock
			.Setup(s => s.GetOrCreateAsync("Works Fine", It.IsAny<CancellationToken>()))
			.ReturnsAsync(NewResult("Works Fine", 0.9));

		NormalizedDescriptionResolutionService service = CreateService();

		// Act
		var summary = await service.ProcessPendingResolutionsAsync(CancellationToken.None);

		// Assert — the exception is logged, not rethrown; the good group persisted.
		summary.Linked.Should().Be(1);
		summary.Skipped.Should().Be(1);

		_loggerMock.Verify(
			x => x.Log(
				LogLevel.Error,
				It.IsAny<EventId>(),
				It.IsAny<It.IsAnyType>(),
				It.IsAny<Exception>(),
				It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
			Times.AtLeastOnce);

		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		ReceiptItemEntity persistedBad = await verify.ReceiptItems.AsNoTracking().SingleAsync(i => i.Id == bad.Id);
		ReceiptItemEntity persistedGood = await verify.ReceiptItems.AsNoTracking().SingleAsync(i => i.Id == good.Id);

		persistedBad.NormalizedDescriptionId.Should().BeNull("the failing group must not leave partial state");
		persistedGood.NormalizedDescriptionId.Should().NotBeNull();
	}

	[Fact]
	public async Task ExecuteAsync_StopsGracefully_OnCancellation()
	{
		// Arrange — cancel immediately so the 10-second initial delay is interrupted.
		NormalizedDescriptionResolutionService service = CreateService();
		using CancellationTokenSource cts = new();

		// Act
		await service.StartAsync(cts.Token);
		cts.Cancel();
		Func<Task> act = async () => await service.StopAsync(CancellationToken.None);

		// Assert
		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task ExecuteAsync_DoesNotThrow_WhenScopeFactoryThrows()
	{
		// Arrange — a repeated per-cycle exception must be swallowed and logged,
		// not propagated up through the BackgroundService host.
		TaskCompletionSource invoked = new();
		Mock<IServiceScopeFactory> throwingFactory = new();
		throwingFactory
			.Setup(f => f.CreateScope())
			.Callback(() => invoked.TrySetResult())
			.Throws(new InvalidOperationException("scope creation failed"));

		NormalizedDescriptionResolutionService service = new(
			throwingFactory.Object,
			_signalMock.Object,
			_loggerMock.Object);

		using CancellationTokenSource cts = new();

		// Act — start the service, wait for the first scope attempt (which throws), then cancel.
		await service.StartAsync(cts.Token);
		await invoked.Task.WaitAsync(TimeSpan.FromSeconds(15));
		cts.Cancel();
		Func<Task> act = async () => await service.StopAsync(CancellationToken.None);

		// Assert — the service's try/catch absorbed the exception.
		await act.Should().NotThrowAsync();
		_loggerMock.Verify(
			x => x.Log(
				LogLevel.Error,
				It.IsAny<EventId>(),
				It.IsAny<It.IsAnyType>(),
				It.IsAny<Exception>(),
				It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
			Times.AtLeastOnce);
	}
}
