using Application.Models.Ynab;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Services;
using Moq;

namespace Infrastructure.Tests.Services;

public class YnabSyncRecordServiceTests
{
	private readonly Mock<IYnabSyncRecordRepository> _repositoryMock = new();
	private readonly YnabSyncRecordService _service;

	private static readonly Guid Receipt1 = Guid.NewGuid();
	private static readonly Guid Receipt2 = Guid.NewGuid();
	private static readonly Guid Receipt3 = Guid.NewGuid();

	public YnabSyncRecordServiceTests()
	{
		_service = new YnabSyncRecordService(_repositoryMock.Object);
	}

	[Fact]
	public async Task GetSyncStatusesByReceiptIdsAsync_NoSyncRecords_ReturnsNotSyncedForAll()
	{
		// Arrange
		List<Guid> receiptIds = [Receipt1, Receipt2];
		_repositoryMock.Setup(r => r.GetByReceiptIdsAsync(receiptIds, It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAsync(receiptIds, CancellationToken.None);

		// Assert
		result.Should().HaveCount(2);
		result.Should().AllSatisfy(s => s.SyncStatus.Should().Be(ReceiptSyncStatusValue.NotSynced));
	}

	[Fact]
	public async Task GetSyncStatusesByReceiptIdsAsync_AllSynced_ReturnsSynced()
	{
		// Arrange
		List<Guid> receiptIds = [Receipt1];
		Guid txId = Guid.NewGuid();

		_repositoryMock.Setup(r => r.GetByReceiptIdsAsync(receiptIds, It.IsAny<CancellationToken>()))
			.ReturnsAsync(
			[
				CreateSyncRecord(txId, Receipt1, YnabSyncStatus.Synced),
			]);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAsync(receiptIds, CancellationToken.None);

		// Assert
		result.Should().ContainSingle().Which.SyncStatus.Should().Be(ReceiptSyncStatusValue.Synced);
	}

	[Fact]
	public async Task GetSyncStatusesByReceiptIdsAsync_MixedStatuses_WorstStatusWins()
	{
		// Arrange
		List<Guid> receiptIds = [Receipt1];
		Guid tx1 = Guid.NewGuid();
		Guid tx2 = Guid.NewGuid();

		_repositoryMock.Setup(r => r.GetByReceiptIdsAsync(receiptIds, It.IsAny<CancellationToken>()))
			.ReturnsAsync(
			[
				CreateSyncRecord(tx1, Receipt1, YnabSyncStatus.Synced),
				CreateSyncRecord(tx2, Receipt1, YnabSyncStatus.Pending),
			]);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAsync(receiptIds, CancellationToken.None);

		// Assert
		result.Should().ContainSingle().Which.SyncStatus.Should().Be(ReceiptSyncStatusValue.Pending);
	}

	[Fact]
	public async Task GetSyncStatusesByReceiptIdsAsync_AnyFailed_ReturnsFailed()
	{
		// Arrange
		List<Guid> receiptIds = [Receipt1];
		Guid tx1 = Guid.NewGuid();
		Guid tx2 = Guid.NewGuid();
		Guid tx3 = Guid.NewGuid();

		_repositoryMock.Setup(r => r.GetByReceiptIdsAsync(receiptIds, It.IsAny<CancellationToken>()))
			.ReturnsAsync(
			[
				CreateSyncRecord(tx1, Receipt1, YnabSyncStatus.Synced),
				CreateSyncRecord(tx2, Receipt1, YnabSyncStatus.Pending),
				CreateSyncRecord(tx3, Receipt1, YnabSyncStatus.Failed),
			]);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAsync(receiptIds, CancellationToken.None);

		// Assert
		result.Should().ContainSingle().Which.SyncStatus.Should().Be(ReceiptSyncStatusValue.Failed);
	}

	[Fact]
	public async Task GetSyncStatusesByReceiptIdsAsync_MultipleReceipts_ReturnsCorrectStatusPerReceipt()
	{
		// Arrange
		List<Guid> receiptIds = [Receipt1, Receipt2, Receipt3];
		Guid tx1 = Guid.NewGuid();
		Guid tx2 = Guid.NewGuid();

		_repositoryMock.Setup(r => r.GetByReceiptIdsAsync(receiptIds, It.IsAny<CancellationToken>()))
			.ReturnsAsync(
			[
				CreateSyncRecord(tx1, Receipt1, YnabSyncStatus.Synced),
				CreateSyncRecord(tx2, Receipt2, YnabSyncStatus.Failed),
			]);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAsync(receiptIds, CancellationToken.None);

		// Assert
		result.Should().HaveCount(3);
		result.First(r => r.ReceiptId == Receipt1).SyncStatus.Should().Be(ReceiptSyncStatusValue.Synced);
		result.First(r => r.ReceiptId == Receipt2).SyncStatus.Should().Be(ReceiptSyncStatusValue.Failed);
		result.First(r => r.ReceiptId == Receipt3).SyncStatus.Should().Be(ReceiptSyncStatusValue.NotSynced);
	}

	[Fact]
	public async Task GetSyncStatusesByReceiptIdsAsync_EmptyReceiptIds_ReturnsEmpty()
	{
		// Arrange
		List<Guid> receiptIds = [];
		_repositoryMock.Setup(r => r.GetByReceiptIdsAsync(receiptIds, It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAsync(receiptIds, CancellationToken.None);

		// Assert
		result.Should().BeEmpty();
	}

	[Fact]
	public async Task GetSyncStatusesByReceiptIdsAsync_NullTransaction_SkipsRecord()
	{
		// Arrange
		List<Guid> receiptIds = [Receipt1];

		YnabSyncRecordEntity orphanRecord = new()
		{
			Id = Guid.NewGuid(),
			LocalTransactionId = Guid.NewGuid(),
			SyncStatus = YnabSyncStatus.Synced,
			SyncType = YnabSyncType.TransactionPush,
			YnabBudgetId = "budget-1",
			Transaction = null,
		};

		_repositoryMock.Setup(r => r.GetByReceiptIdsAsync(receiptIds, It.IsAny<CancellationToken>()))
			.ReturnsAsync([orphanRecord]);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAsync(receiptIds, CancellationToken.None);

		// Assert
		result.Should().ContainSingle().Which.SyncStatus.Should().Be(ReceiptSyncStatusValue.NotSynced);
	}

	private static YnabSyncRecordEntity CreateSyncRecord(Guid transactionId, Guid receiptId, YnabSyncStatus status) => new()
	{
		Id = Guid.NewGuid(),
		LocalTransactionId = transactionId,
		SyncStatus = status,
		SyncType = YnabSyncType.TransactionPush,
		YnabBudgetId = "budget-1",
		Transaction = new TransactionEntity
		{
			Id = transactionId,
			ReceiptId = receiptId,
			Amount = 10.00m,
			AmountCurrency = Currency.USD,
			Date = new DateOnly(2024, 1, 15),
		},
	};
}
