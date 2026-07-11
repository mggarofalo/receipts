using System.Net;
using Application.Commands.Ynab.PushTransactions;
using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ynab;
using Application.Utilities;
using Common;
using Domain;
using Domain.Aggregates;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Ynab;

// RECEIPTS-752: two local transactions on a receipt can share the same amount+date (e.g. a
// receipt split into two $50.00 payments on the same card/date). YNAB import_ids disambiguate
// them with an occurrence counter. On a retry after a partial push, the already-synced sibling
// is skipped — but its occurrence must still be "consumed" so the still-unsynced sibling keeps
// its own distinct import_id. If it didn't, the retry would reuse the synced sibling's import_id,
// YNAB would 409, and the recovery path would bind BOTH local transactions to the sibling's one
// YNAB transaction — silently dropping the second $50 while reporting success.
public class PushYnabTransactionsImportIdStabilityTests
{
	private readonly Mock<IReceiptService> _receiptServiceMock = new();
	private readonly Mock<IReceiptItemService> _receiptItemServiceMock = new();
	private readonly Mock<IAdjustmentService> _adjustmentServiceMock = new();
	private readonly Mock<ITransactionService> _transactionServiceMock = new();
	private readonly Mock<IYnabCategoryMappingService> _categoryMappingServiceMock = new();
	private readonly Mock<IYnabAccountMappingService> _accountMappingServiceMock = new();
	private readonly Mock<IYnabBudgetSelectionService> _budgetSelectionServiceMock = new();
	private readonly Mock<IYnabSyncRecordService> _syncRecordServiceMock = new();
	private readonly Mock<IYnabApiClient> _ynabApiClientMock = new();
	private readonly Mock<IYnabSplitCalculator> _splitCalculatorMock = new();
	private readonly PushYnabTransactionsCommandHandler _handler;

	private readonly Guid _receiptId = Guid.NewGuid();
	private readonly Guid _accountId = Guid.NewGuid();
	private readonly Guid _tx1Id = Guid.NewGuid();
	private readonly Guid _tx2Id = Guid.NewGuid();
	private readonly Guid _syncRecord1Id = Guid.NewGuid();
	private readonly Guid _syncRecord2Id = Guid.NewGuid();
	private readonly string _budgetId = "budget-123";
	private readonly string _ynabAccountId = "ynab-acc-1";

	// Both transactions: same amount ($50.00 → -50000 milliunits) and same date.
	private readonly DateOnly _date = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
	private const long Milliunits = -50000;

	public PushYnabTransactionsImportIdStabilityTests()
	{
		_handler = new PushYnabTransactionsCommandHandler(
			_receiptServiceMock.Object,
			_receiptItemServiceMock.Object,
			_adjustmentServiceMock.Object,
			_transactionServiceMock.Object,
			_categoryMappingServiceMock.Object,
			_accountMappingServiceMock.Object,
			_budgetSelectionServiceMock.Object,
			_syncRecordServiceMock.Object,
			_ynabApiClientMock.Object,
			_splitCalculatorMock.Object,
			Mock.Of<IYnabSyncEventService>(),
			Mock.Of<IYnabResponseContext>(),
			Mock.Of<Microsoft.Extensions.Logging.ILogger<PushYnabTransactionsCommandHandler>>());
	}

	// Sets up a receipt with two same-amount, same-date transactions in a RETRY state:
	// tx1 already Synced (YNAB id "ynab-tx-1"), tx2 previously Failed.
	private void SetupRetryPipeline()
	{
		Domain.Core.Receipt receipt = new(_receiptId, "Store", _date, new Money(1.00m));
		_receiptServiceMock.Setup(s => s.GetByIdAsync(_receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(receipt);

		List<Domain.Core.ReceiptItem> items =
		[
			new(Guid.NewGuid(), null, "Item1", 1, new Money(100.00m), new Money(100.00m), "Groceries", null),
		];
		_receiptItemServiceMock.Setup(s => s.GetByReceiptIdAsync(_receiptId, 0, 10000, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.ReceiptItem>(items, items.Count, 0, 10000));

		_adjustmentServiceMock.Setup(s => s.GetByReceiptIdAsync(_receiptId, 0, 10000, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.Adjustment>([], 0, 0, 10000));

		Domain.Core.Transaction tx1 = new(_tx1Id, Guid.NewGuid(), new Money(50.00m), _date) { AccountId = _accountId, ReceiptId = _receiptId };
		Domain.Core.Transaction tx2 = new(_tx2Id, Guid.NewGuid(), new Money(50.00m), _date) { AccountId = _accountId, ReceiptId = _receiptId };
		Domain.Core.Account account = new(_accountId, "Checking", true);
		_transactionServiceMock.Setup(s => s.GetTransactionAccountsByReceiptIdAsync(_receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(
			[
				new TransactionAccount { Transaction = tx1, Account = account },
				new TransactionAccount { Transaction = tx2, Account = account },
			]);

		_categoryMappingServiceMock.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync([
				new YnabCategoryMappingDto(Guid.NewGuid(), "Groceries", "ynab-cat-1", "Groceries", "Food", _budgetId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			]);

		_budgetSelectionServiceMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(_budgetId);

		_accountMappingServiceMock.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync([
				new YnabAccountMappingDto(Guid.NewGuid(), _accountId, _ynabAccountId, "Checking", _budgetId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			]);

		// Retry state: tx1 already Synced to "ynab-tx-1"; tx2 previously Failed.
		_syncRecordServiceMock.Setup(s => s.GetByTransactionAndTypeAsync(_tx1Id, YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(_syncRecord1Id, _tx1Id, "ynab-tx-1", _budgetId, _ynabAccountId, YnabSyncType.TransactionPush, YnabSyncStatus.Synced, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
		_syncRecordServiceMock.Setup(s => s.GetByTransactionAndTypeAsync(_tx2Id, YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(_syncRecord2Id, _tx2Id, null, _budgetId, null, YnabSyncType.TransactionPush, YnabSyncStatus.Failed, null, "previous failure", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		// tx1 first, tx2 second — same amount and date.
		_splitCalculatorMock.Setup(s => s.ComputeWaterfallSplits(It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(), It.IsAny<Dictionary<string, string>>()))
			.Returns(new YnabSplitResult([
				new YnabTransactionSplit(_tx1Id, Milliunits, [new YnabSubTransactionSplit("ynab-cat-1", Milliunits)]),
				new YnabTransactionSplit(_tx2Id, Milliunits, [new YnabSubTransactionSplit("ynab-cat-1", Milliunits)]),
			]));
	}

	[Fact]
	public async Task Retry_SkippedSyncedSibling_StillConsumesOccurrence_UnsyncedTxGetsDistinctImportId()
	{
		SetupRetryPipeline();

		string? capturedImportId = null;
		int createCallCount = 0;
		_ynabApiClientMock
			.Setup(s => s.CreateTransactionAsync(_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.Callback<string, YnabCreateTransactionRequest, CancellationToken>((_, req, _) => { capturedImportId = req.ImportId; createCallCount++; })
			.ReturnsAsync(new YnabCreateTransactionResponse("ynab-tx-2"));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeTrue();

		// Only the still-unsynced tx2 is pushed; the synced tx1 is skipped.
		createCallCount.Should().Be(1);

		// tx2 must get occurrence 2 (tx1's occurrence is still consumed despite being skipped),
		// NOT occurrence 1 which would collide with the import_id tx1 already used.
		string tx1ImportId = YnabImportId.Generate(Milliunits, _date, _receiptId, 1);
		string tx2ImportId = YnabImportId.Generate(Milliunits, _date, _receiptId, 2);
		capturedImportId.Should().Be(tx2ImportId);
		capturedImportId.Should().NotBe(tx1ImportId);
		capturedImportId.Should().EndWith(":2");

		// tx2's own record is stamped Synced with its OWN (distinct) YNAB transaction.
		_syncRecordServiceMock.Verify(s => s.UpdateStatusAsync(
			_syncRecord2Id, YnabSyncStatus.Synced, "ynab-tx-2", null, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Retry_409RecoversSyncedSiblingsYnabId_DoesNotDoubleBind_ReportsFailure()
	{
		SetupRetryPipeline();

		// Force tx2's create to 409, and have recovery resolve to tx1's ALREADY-BOUND YNAB id.
		// The double-bind guard must reject it rather than stamping tx2 Synced to "ynab-tx-1".
		_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("conflict", null, HttpStatusCode.Conflict));

		_ynabApiClientMock.Setup(s => s.FindTransactionByImportIdAsync(
				_budgetId, _ynabAccountId, It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync("ynab-tx-1");

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		// No false success.
		result.Success.Should().BeFalse();
		result.Error.Should().Contain("ynab-tx-1");

		// tx2 must NOT be bound to tx1's YNAB transaction.
		_syncRecordServiceMock.Verify(s => s.UpdateStatusAsync(
			_syncRecord2Id, YnabSyncStatus.Synced, "ynab-tx-1", null, It.IsAny<CancellationToken>()), Times.Never);
		result.PushedTransactions.Should().NotContain(p => p.YnabTransactionId == "ynab-tx-1");

		// tx2's record is marked Failed with an explanatory error.
		_syncRecordServiceMock.Verify(s => s.UpdateStatusAsync(
			_syncRecord2Id, YnabSyncStatus.Failed, null, It.Is<string>(m => m.Contains("ynab-tx-1")), It.IsAny<CancellationToken>()), Times.Once);
	}
}
