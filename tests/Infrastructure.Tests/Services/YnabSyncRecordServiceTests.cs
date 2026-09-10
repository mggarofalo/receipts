using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
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
	private readonly Mock<ICommittedChangePublisher> _publisherMock = new();
	private readonly YnabSyncRecordService _service;

	private static readonly Guid Receipt1 = Guid.NewGuid();
	private static readonly Guid Receipt2 = Guid.NewGuid();
	private static readonly Guid Receipt3 = Guid.NewGuid();
	private const string SelectedBudgetId = "budget-1";

	public YnabSyncRecordServiceTests()
	{
		_service = new YnabSyncRecordService(_repositoryMock.Object, _publisherMock.Object);
	}

	[Fact]
	public async Task CreateAsync_PublishesCreatedRecord()
	{
		Guid id = Guid.NewGuid();
		_repositoryMock.Setup(r => r.CreateAsync(It.IsAny<YnabSyncRecordEntity>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabSyncRecordEntity row, CancellationToken _) => { row.Id = id; return row; });

		_ = await _service.CreateAsync(Guid.NewGuid(), "budget", YnabSyncType.TransactionPush, CancellationToken.None);

		VerifyPublished(CommittedChangeType.Created, id, Times.Once());
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task UpdateStatusAsync_PublishesOnlyWhenRepositoryUpdated(bool updated)
	{
		Guid id = Guid.NewGuid();
		_repositoryMock.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordEntity { Id = id, YnabBudgetId = "budget" });
		_repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<YnabSyncRecordEntity>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(updated);

		await _service.UpdateStatusAsync(id, YnabSyncStatus.Synced, "remote", null, CancellationToken.None);

		VerifyPublished(CommittedChangeType.Updated, id, updated ? Times.Once() : Times.Never());
	}

	private void VerifyPublished(CommittedChangeType type, Guid id, Times times) =>
		_publisherMock.Verify(p => p.PublishAsync(It.Is<CommittedEntityChange>(change =>
			change.EntityType == CommittedEntityType.YnabSyncRecord
			&& change.ChangeType == type
			&& change.EntityId == id)), times);

	[Fact]
	public async Task GetByTransactionTypeAndBudgetAsync_UsesBudgetAsPartOfSyncIdentity()
	{
		Guid transactionId = Guid.NewGuid();
		YnabSyncRecordEntity expected = new()
		{
			Id = Guid.NewGuid(),
			LocalTransactionId = transactionId,
			SyncType = YnabSyncType.TransactionPush,
			YnabBudgetId = SelectedBudgetId,
		};
		_repositoryMock.Setup(r => r.GetByTransactionTypeAndBudgetAsync(
			transactionId, YnabSyncType.TransactionPush, SelectedBudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		YnabSyncRecordDto? result = await _service.GetByTransactionTypeAndBudgetAsync(
			transactionId, YnabSyncType.TransactionPush, SelectedBudgetId, CancellationToken.None);

		result.Should().NotBeNull();
		result!.Id.Should().Be(expected.Id);
		result.YnabBudgetId.Should().Be(SelectedBudgetId);
	}

	[Fact]
	public async Task GetLatestSuccessfulSyncTimestampAsync_QueriesSelectedBudgetOnly()
	{
		DateTimeOffset expected = DateTimeOffset.UtcNow;
		_repositoryMock.Setup(r => r.GetLatestSuccessfulSyncTimestampAsync(
			SelectedBudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		DateTimeOffset? result = await _service.GetLatestSuccessfulSyncTimestampAsync(
			SelectedBudgetId, CancellationToken.None);

		result.Should().Be(expected);
	}

	[Fact]
	public async Task PreparePushOperationAsync_PersistsCompleteImmutableRequestBeforeReturningIt()
	{
		Guid transactionId = Guid.NewGuid();
		YnabCreateTransactionRequest request = new(
			"account-a",
			new DateOnly(2026, 9, 10),
			-12345,
			"original memo",
			"Original payee",
			"category-a",
			false,
			[new YnabSubTransaction(-12345, "category-a", "split memo")],
			"YNAB:original:1");
		YnabSyncRecordEntity? proposed = null;
		_repositoryMock.Setup(r => r.GetOrCreatePushOperationAsync(
				It.IsAny<YnabSyncRecordEntity>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabSyncRecordEntity row, CancellationToken _) =>
			{
				proposed = row;
				row.Id = Guid.NewGuid();
				return new PreparedYnabPushOperation(row, true);
			});

		YnabPushOperation operation = await _service.PreparePushOperationAsync(
			transactionId, SelectedBudgetId, request, "source-v1", CancellationToken.None);

		proposed.Should().NotBeNull();
		proposed!.LocalTransactionId.Should().Be(transactionId);
		proposed.YnabBudgetId.Should().Be(SelectedBudgetId);
		proposed.YnabAccountId.Should().Be("account-a");
		proposed.ImportId.Should().Be("YNAB:original:1");
		proposed.RequestPayloadJson.Should().NotBeNullOrWhiteSpace();
		proposed.PayloadHash.Should().MatchRegex("^[0-9a-f]{64}$");
		proposed.SourceVersion.Should().Be("source-v1");
		proposed.SyncStatus.Should().Be(YnabSyncStatus.Pending);
		operation.Request.Should().BeEquivalentTo(request);
		operation.PayloadHash.Should().Be(proposed.PayloadHash);
	}

	[Fact]
	public async Task PreparePushOperationAsync_LocalEdits_ReturnsPreviouslyPersistedSnapshot()
	{
		Guid transactionId = Guid.NewGuid();
		YnabCreateTransactionRequest original = new(
			"account-original", new DateOnly(2026, 9, 1), -1000, "original", "Original store",
			"category-original", false, ImportId: "YNAB:original:1");
		YnabCreateTransactionRequest edited = original with
		{
			AccountId = "account-edited",
			Date = new DateOnly(2026, 9, 2),
			Amount = -2000,
			Memo = "edited",
			ImportId = "YNAB:edited:1",
		};
		YnabSyncRecordEntity? persisted = null;
		_repositoryMock.Setup(r => r.GetOrCreatePushOperationAsync(
				It.IsAny<YnabSyncRecordEntity>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabSyncRecordEntity proposed, CancellationToken _) =>
			{
				if (persisted is null)
				{
					proposed.Id = Guid.NewGuid();
					persisted = proposed;
					return new PreparedYnabPushOperation(persisted, true);
				}

				return new PreparedYnabPushOperation(persisted, false);
			});

		YnabPushOperation first = await _service.PreparePushOperationAsync(
			transactionId, SelectedBudgetId, original, "source-original", CancellationToken.None);
		YnabPushOperation retry = await _service.PreparePushOperationAsync(
			transactionId, SelectedBudgetId, edited, "source-edited", CancellationToken.None);

		retry.SyncRecordId.Should().Be(first.SyncRecordId);
		retry.Request.Should().BeEquivalentTo(original);
		retry.SourceVersion.Should().Be("source-original");
		retry.PayloadHash.Should().Be(first.PayloadHash);
		VerifyPublished(CommittedChangeType.Created, first.SyncRecordId, Times.Once());
	}

	[Fact]
	public async Task CompletePushOperationAsync_PublishesOnlyAfterClaimedUpdateCommits()
	{
		Guid id = Guid.NewGuid();
		Guid claimToken = Guid.NewGuid();
		_repositoryMock.SetupSequence(r => r.CompletePushOperationAsync(
			id, claimToken, YnabSyncStatus.Unknown, null, "ambiguous response",
			It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(false)
			.ReturnsAsync(true);

		bool rejected = await _service.CompletePushOperationAsync(
			id, claimToken, YnabSyncStatus.Unknown, null, "ambiguous response", CancellationToken.None);
		VerifyPublished(CommittedChangeType.Updated, id, Times.Never());

		bool committed = await _service.CompletePushOperationAsync(
			id, claimToken, YnabSyncStatus.Unknown, null, "ambiguous response", CancellationToken.None);

		rejected.Should().BeFalse();
		committed.Should().BeTrue();
		VerifyPublished(CommittedChangeType.Updated, id, Times.Once());
	}

	[Fact]
	public async Task GetSyncStatusesByReceiptIdsAsync_NoSyncRecords_ReturnsNotSyncedForAll()
	{
		// Arrange
		List<Guid> receiptIds = [Receipt1, Receipt2];
		_repositoryMock.Setup(r => r.GetByReceiptIdsAndBudgetAsync(receiptIds, SelectedBudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAndBudgetAsync(receiptIds, SelectedBudgetId, CancellationToken.None);

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

		_repositoryMock.Setup(r => r.GetByReceiptIdsAndBudgetAsync(receiptIds, SelectedBudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(
			[
				CreateSyncRecord(txId, Receipt1, YnabSyncStatus.Synced),
			]);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAndBudgetAsync(receiptIds, SelectedBudgetId, CancellationToken.None);

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

		_repositoryMock.Setup(r => r.GetByReceiptIdsAndBudgetAsync(receiptIds, SelectedBudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(
			[
				CreateSyncRecord(tx1, Receipt1, YnabSyncStatus.Synced),
				CreateSyncRecord(tx2, Receipt1, YnabSyncStatus.Pending),
			]);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAndBudgetAsync(receiptIds, SelectedBudgetId, CancellationToken.None);

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

		_repositoryMock.Setup(r => r.GetByReceiptIdsAndBudgetAsync(receiptIds, SelectedBudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(
			[
				CreateSyncRecord(tx1, Receipt1, YnabSyncStatus.Synced),
				CreateSyncRecord(tx2, Receipt1, YnabSyncStatus.Pending),
				CreateSyncRecord(tx3, Receipt1, YnabSyncStatus.Failed),
			]);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAndBudgetAsync(receiptIds, SelectedBudgetId, CancellationToken.None);

		// Assert
		result.Should().ContainSingle().Which.SyncStatus.Should().Be(ReceiptSyncStatusValue.Failed);
	}

	[Fact]
	public async Task GetSyncStatusesByReceiptIdsAsync_UnknownOutcome_IsVisible()
	{
		List<Guid> receiptIds = [Receipt1];
		_repositoryMock.Setup(r => r.GetByReceiptIdsAndBudgetAsync(
				receiptIds, SelectedBudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([CreateSyncRecord(Guid.NewGuid(), Receipt1, YnabSyncStatus.Unknown)]);

		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAndBudgetAsync(
			receiptIds, SelectedBudgetId, CancellationToken.None);

		result.Should().ContainSingle().Which.SyncStatus.Should().Be(ReceiptSyncStatusValue.Unknown);
	}

	[Fact]
	public async Task GetSyncStatusesByReceiptIdsAsync_MultipleReceipts_ReturnsCorrectStatusPerReceipt()
	{
		// Arrange
		List<Guid> receiptIds = [Receipt1, Receipt2, Receipt3];
		Guid tx1 = Guid.NewGuid();
		Guid tx2 = Guid.NewGuid();

		_repositoryMock.Setup(r => r.GetByReceiptIdsAndBudgetAsync(receiptIds, SelectedBudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(
			[
				CreateSyncRecord(tx1, Receipt1, YnabSyncStatus.Synced),
				CreateSyncRecord(tx2, Receipt2, YnabSyncStatus.Failed),
			]);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAndBudgetAsync(receiptIds, SelectedBudgetId, CancellationToken.None);

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
		_repositoryMock.Setup(r => r.GetByReceiptIdsAndBudgetAsync(receiptIds, SelectedBudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAndBudgetAsync(receiptIds, SelectedBudgetId, CancellationToken.None);

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

		_repositoryMock.Setup(r => r.GetByReceiptIdsAndBudgetAsync(receiptIds, SelectedBudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([orphanRecord]);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await _service.GetSyncStatusesByReceiptIdsAndBudgetAsync(receiptIds, SelectedBudgetId, CancellationToken.None);

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
