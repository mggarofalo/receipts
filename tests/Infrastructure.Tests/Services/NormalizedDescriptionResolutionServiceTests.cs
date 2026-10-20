using System.Threading.Channels;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Normalization;
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
	private readonly Mock<IDescriptionChangeSignal> _signalMock = new();
	private readonly Mock<ILogger<NormalizedDescriptionResolutionService>> _loggerMock = new();
	private readonly Mock<IServiceScopeFactory> _scopeFactoryMock = new();
	private readonly Mock<ICommittedChangePublisher> _publisherMock = new();
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
	public NormalizedDescriptionResolutionServiceTests()
	{
		(_contextFactory, _) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		_signalMock.Setup(s => s.Subscribe()).Returns(Channel.CreateBounded<bool>(1).Reader);
		ServiceCollection services = new();
		// Deliberately omit model and canonical-service dependencies: approved scope works offline.
		services.AddSingleton(_contextFactory);
		var provider = services.BuildServiceProvider();
		_scopeFactoryMock.Setup(f => f.CreateScope()).Returns(() => provider.CreateScope());
	}
	private NormalizedDescriptionResolutionService CreateService() => new(_scopeFactoryMock.Object, _signalMock.Object, _loggerMock.Object, _publisherMock.Object);

	private async Task<Guid> ApproveAsync(string description, NormalizedDescriptionStatus status = NormalizedDescriptionStatus.Active)
	{
		await using var context = _contextFactory.CreateDbContext();
		Guid id = Guid.NewGuid();
		context.NormalizedDescriptions.Add(new() { Id = id, CanonicalName = "Identity " + description, Status = status, CreatedAt = DateTimeOffset.UtcNow });
		var key = NormalizationCatalogPolicy.Normalize(new("Test", description, null));
		context.NormalizationSourceMappings.Add(new() { Id = Guid.NewGuid(), SourceHash = NormalizationCatalogPolicy.Hash(key), Location = key.Location, Description = key.Description, TargetId = id, ApprovedBy = "reviewer", ApprovedAt = DateTimeOffset.UtcNow });
		await context.SaveChangesAsync();
		return id;
	}

	[Fact]
	public async Task ApprovedScope_LinksDuplicatesOfflineWithoutInventingScores_AndPublishesOnce()
	{
		var first = BuildItem("Milk"); var second = BuildItem("Milk");
		await SeedReceiptAndItemsAsync(first, second);
		Guid target = await ApproveAsync("Milk");
		_publisherMock.Setup(p => p.PublishAsync(It.IsAny<CommittedEntityChange>())).Returns(async () =>
		{
			await using var committed = _contextFactory.CreateDbContext();
			(await committed.ReceiptItems.CountAsync(i => i.NormalizedDescriptionId == target)).Should().Be(2);
		});
		using var service = CreateService();
		(await service.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(2);
		(await service.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(0);
		await using var verify = _contextFactory.CreateDbContext();
		verify.ReceiptItems.Should().OnlyContain(i => i.NormalizedDescriptionId == target && i.NormalizedDescriptionMatchScore == null);
		verify.NormalizedDescriptions.Should().ContainSingle();
		_publisherMock.Verify(p => p.PublishAsync(It.Is<CommittedEntityChange>(c => c.EntityType == CommittedEntityType.ReceiptItem && c.ChangeType == CommittedChangeType.Updated && c.SuppressToast)), Times.Once);
	}

	[Fact]
	public async Task GlobalExactNameWithoutScope_DoesNotAuthorizeOrPublish()
	{
		var item = BuildItem("Milk"); await SeedReceiptAndItemsAsync(item);
		await using (var context = _contextFactory.CreateDbContext())
		{
			context.NormalizedDescriptions.Add(new() { Id = Guid.NewGuid(), CanonicalName = "Milk", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
			await context.SaveChangesAsync();
		}
		using var service = CreateService();
		(await service.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(0);
		await using var verify = _contextFactory.CreateDbContext();
		(await verify.ReceiptItems.SingleAsync()).NormalizedDescriptionId.Should().BeNull();
		_publisherMock.Verify(p => p.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Never);
	}

	[Theory]
	[InlineData("deleted")]
	[InlineData("linked")]
	[InlineData("rejected-target")]
	[InlineData("different-case")]
	public async Task IneligibleAssignment_RemainsUntouched(string reason)
	{
		var item = BuildItem(reason == "different-case" ? "milk" : "Milk", deletedAt: reason == "deleted" ? DateTimeOffset.UtcNow : null);
		Guid historicalTarget = Guid.NewGuid();
		if (reason == "linked")
		{
			await using var context = _contextFactory.CreateDbContext();
			context.NormalizedDescriptions.Add(new() { Id = historicalTarget, CanonicalName = "Historical identity", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
			await context.SaveChangesAsync();
			item.NormalizedDescriptionId = historicalTarget;
			item.NormalizedDescriptionMatchScore = 0.42;
		}
		await SeedReceiptAndItemsAsync(item);
		await ApproveAsync("Milk", reason == "rejected-target" ? NormalizedDescriptionStatus.Rejected : NormalizedDescriptionStatus.Active);
		using var service = CreateService();
		(await service.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(0);
		await using var verify = _contextFactory.CreateDbContext();
		var stored = await verify.ReceiptItems.IgnoreQueryFilters().SingleAsync();
		stored.NormalizedDescriptionId.Should().Be(reason == "linked" ? historicalTarget : null);
		stored.NormalizedDescriptionMatchScore.Should().Be(reason == "linked" ? 0.42 : null);
		_publisherMock.Verify(p => p.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Never);
	}

	[Fact]
	public async Task ShortApprovedDescription_IsValid()
	{
		await SeedReceiptAndItemsAsync(BuildItem("X")); await ApproveAsync("X");
		using var service = CreateService();
		(await service.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(1);
	}

	[Fact]
	public async Task EmptyBatch_DoesNotPublish()
	{
		using var service = CreateService();
		(await service.ProcessPendingResolutionsAsync(CancellationToken.None)).Should().Be(NormalizedDescriptionResolutionService.ResolutionSummary.Empty);
		_publisherMock.Verify(p => p.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Never);
	}

	[Fact]
	public async Task ScopeFailure_DoesNotPublish()
	{
		_scopeFactoryMock.Setup(f => f.CreateScope()).Throws(new InvalidOperationException("unavailable"));
		using var service = CreateService();
		Func<Task> run = async () => await service.ProcessPendingResolutionsAsync(CancellationToken.None);
		await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("unavailable");
		_publisherMock.Verify(p => p.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Never);
	}

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
