using Application.Models.Ynab;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Services;
using Moq;

namespace Infrastructure.Tests.Services;

public class YnabSyncEventServiceTests
{
	private readonly Mock<IYnabSyncEventRepository> _repositoryMock = new();
	private readonly YnabSyncEventService _service;

	public YnabSyncEventServiceTests()
	{
		_service = new YnabSyncEventService(_repositoryMock.Object);
	}

	[Fact]
	public async Task RecordAsync_WritesEntityWithAllFieldsAndGeneratesId()
	{
		// Arrange
		Guid txId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();
		YnabSyncEventEntity? captured = null;

		_repositoryMock.Setup(r => r.CreateAsync(It.IsAny<YnabSyncEventEntity>(), It.IsAny<CancellationToken>()))
			.Callback<YnabSyncEventEntity, CancellationToken>((e, _) => captured = e)
			.ReturnsAsync((YnabSyncEventEntity e, CancellationToken _) => e);

		// Act
		await _service.RecordAsync(
			YnabSyncType.TransactionPush,
			YnabSyncStatus.Synced,
			txId,
			receiptId,
			"budget-123",
			"ynab-tx-456",
			errorMessage: null,
			CancellationToken.None);

		// Assert
		captured.Should().NotBeNull();
		captured!.Id.Should().NotBe(Guid.Empty);
		captured.EventType.Should().Be(YnabSyncType.TransactionPush);
		captured.Outcome.Should().Be(YnabSyncStatus.Synced);
		captured.LocalTransactionId.Should().Be(txId);
		captured.ReceiptId.Should().Be(receiptId);
		captured.YnabBudgetId.Should().Be("budget-123");
		captured.YnabTransactionId.Should().Be("ynab-tx-456");
		captured.ErrorMessage.Should().BeNull();
		captured.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task RecordAsync_WithFailure_PreservesErrorMessage()
	{
		YnabSyncEventEntity? captured = null;
		_repositoryMock.Setup(r => r.CreateAsync(It.IsAny<YnabSyncEventEntity>(), It.IsAny<CancellationToken>()))
			.Callback<YnabSyncEventEntity, CancellationToken>((e, _) => captured = e)
			.ReturnsAsync((YnabSyncEventEntity e, CancellationToken _) => e);

		await _service.RecordAsync(
			YnabSyncType.TransactionPush,
			YnabSyncStatus.Failed,
			localTransactionId: Guid.NewGuid(),
			receiptId: Guid.NewGuid(),
			ynabBudgetId: null,
			ynabTransactionId: null,
			errorMessage: "401 Unauthorized",
			CancellationToken.None);

		captured!.Outcome.Should().Be(YnabSyncStatus.Failed);
		captured.ErrorMessage.Should().Be("401 Unauthorized");
	}

	[Fact]
	public async Task ListAsync_ProjectsEntitiesToDtosAndPreservesTotalCount()
	{
		// Arrange
		YnabSyncEventEntity entity = new()
		{
			Id = Guid.NewGuid(),
			OccurredAt = DateTimeOffset.UtcNow,
			EventType = YnabSyncType.TransactionPush,
			Outcome = YnabSyncStatus.Synced,
			LocalTransactionId = Guid.NewGuid(),
			ReceiptId = Guid.NewGuid(),
			YnabBudgetId = "b1",
			YnabTransactionId = "ynab-1",
			ErrorMessage = null,
		};

		_repositoryMock.Setup(r => r.ListAsync(0, 50, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(((IReadOnlyList<YnabSyncEventEntity>)[entity], 42));

		// Act
		YnabSyncEventsPage page = await _service.ListAsync(0, 50, null, CancellationToken.None);

		// Assert
		page.TotalCount.Should().Be(42);
		page.Events.Should().HaveCount(1);
		YnabSyncEventDto dto = page.Events[0];
		dto.Id.Should().Be(entity.Id);
		dto.EventType.Should().Be(YnabSyncType.TransactionPush);
		dto.Outcome.Should().Be(YnabSyncStatus.Synced);
		dto.YnabBudgetId.Should().Be("b1");
		dto.YnabTransactionId.Should().Be("ynab-1");
	}

	[Fact]
	public async Task ListAsync_PassesOutcomeFilterThrough()
	{
		_repositoryMock.Setup(r => r.ListAsync(10, 25, YnabSyncStatus.Failed, It.IsAny<CancellationToken>()))
			.ReturnsAsync(((IReadOnlyList<YnabSyncEventEntity>)[], 0));

		await _service.ListAsync(10, 25, YnabSyncStatus.Failed, CancellationToken.None);

		_repositoryMock.Verify(r => r.ListAsync(10, 25, YnabSyncStatus.Failed, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetLatestOccurrenceAsync_DelegatesToRepository()
	{
		DateTimeOffset expected = DateTimeOffset.UtcNow.AddHours(-2);
		_repositoryMock.Setup(r => r.GetLatestOccurrenceAsync(YnabSyncStatus.Synced, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		DateTimeOffset? actual = await _service.GetLatestOccurrenceAsync(YnabSyncStatus.Synced, CancellationToken.None);

		actual.Should().Be(expected);
	}

	[Fact]
	public async Task CountSinceAsync_DelegatesToRepository()
	{
		DateTimeOffset since = DateTimeOffset.UtcNow.AddDays(-7);
		_repositoryMock.Setup(r => r.CountSinceAsync(since, YnabSyncStatus.Failed, It.IsAny<CancellationToken>()))
			.ReturnsAsync(13);

		int count = await _service.CountSinceAsync(since, YnabSyncStatus.Failed, CancellationToken.None);

		count.Should().Be(13);
	}
}
