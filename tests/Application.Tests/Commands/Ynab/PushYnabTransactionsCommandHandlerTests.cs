using Application.Commands.Ynab.PushTransactions;
using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ynab;
using Common;
using Domain;
using Domain.Aggregates;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class PushYnabTransactionsCommandHandlerTests
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
	private readonly Mock<IYnabSyncEventService> _syncEventServiceMock = new();
	private readonly PushYnabTransactionsCommandHandler _handler;

	private readonly Guid _receiptId = Guid.NewGuid();
	private readonly Guid _accountId = Guid.NewGuid();
	private readonly Guid _transactionId = Guid.NewGuid();
	private readonly string _budgetId = "budget-123";
	private readonly string _ynabAccountId = "ynab-acc-1";

	public PushYnabTransactionsCommandHandlerTests()
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
			_syncEventServiceMock.Object);
	}

	private void SetupHappyPath()
	{
		Domain.Core.Receipt receipt = new(_receiptId, "Store", DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), new Money(1.00m));
		_receiptServiceMock.Setup(s => s.GetByIdAsync(_receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(receipt);

		List<Domain.Core.ReceiptItem> items =
		[
			new(Guid.NewGuid(), null, "Item1", 1, new Money(10.00m), new Money(10.00m), "Groceries", null),
		];
		_receiptItemServiceMock.Setup(s => s.GetByReceiptIdAsync(_receiptId, 0, 10000, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.ReceiptItem>(items, items.Count, 0, 10000));

		_adjustmentServiceMock.Setup(s => s.GetByReceiptIdAsync(_receiptId, 0, 10000, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.Adjustment>([], 0, 0, 10000));

		Domain.Core.Transaction tx = new(_transactionId, Guid.NewGuid(), new Money(11.00m), DateOnly.FromDateTime(DateTime.Today.AddDays(-1)));
		tx.AccountId = _accountId;
		tx.ReceiptId = _receiptId;

		Domain.Core.Account account = new(_accountId, "Checking", true);
		List<TransactionAccount> txAccounts =
		[
			new() { Transaction = tx, Account = account },
		];
		_transactionServiceMock.Setup(s => s.GetTransactionAccountsByReceiptIdAsync(_receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(txAccounts);

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

		_syncRecordServiceMock.Setup(s => s.GetByTransactionAndTypeAsync(_transactionId, YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabSyncRecordDto?)null);

		Guid syncRecordId = Guid.NewGuid();
		_syncRecordServiceMock.Setup(s => s.CreateAsync(_transactionId, _budgetId, YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(syncRecordId, _transactionId, null, _budgetId, null, YnabSyncType.TransactionPush, YnabSyncStatus.Pending, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		_splitCalculatorMock.Setup(s => s.ComputeWaterfallSplits(It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(), It.IsAny<Dictionary<string, string>>()))
			.Returns(new YnabSplitResult([
				new YnabTransactionSplit(_transactionId, -11000, [new YnabSubTransactionSplit("ynab-cat-1", -11000)]),
			]));

		_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabCreateTransactionResponse("ynab-tx-1"));
	}

	[Fact]
	public async Task Handle_HappyPath_PushesTransactionSuccessfully()
	{
		SetupHappyPath();

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeTrue();
		result.PushedTransactions.Should().HaveCount(1);
		result.PushedTransactions[0].LocalTransactionId.Should().Be(_transactionId);
		result.PushedTransactions[0].YnabTransactionId.Should().Be("ynab-tx-1");
		result.PushedTransactions[0].Milliunits.Should().Be(-11000);
		result.Error.Should().BeNull();
	}

	[Fact]
	public async Task Handle_ReceiptNotFound_ReturnsError()
	{
		_receiptServiceMock.Setup(s => s.GetByIdAsync(_receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((Domain.Core.Receipt?)null);

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.Error.Should().Contain("Receipt not found");
	}

	[Fact]
	public async Task Handle_NoItems_ReturnsError()
	{
		Domain.Core.Receipt receipt = new(_receiptId, "Store", DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), new Money(0m));
		_receiptServiceMock.Setup(s => s.GetByIdAsync(_receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(receipt);
		_receiptItemServiceMock.Setup(s => s.GetByReceiptIdAsync(_receiptId, 0, 10000, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.ReceiptItem>([], 0, 0, 10000));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.Error.Should().Contain("no items");
	}

	[Fact]
	public async Task Handle_NoTransactions_ReturnsError()
	{
		Domain.Core.Receipt receipt = new(_receiptId, "Store", DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), new Money(0m));
		_receiptServiceMock.Setup(s => s.GetByIdAsync(_receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(receipt);
		_receiptItemServiceMock.Setup(s => s.GetByReceiptIdAsync(_receiptId, 0, 10000, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.ReceiptItem>([new(Guid.NewGuid(), null, "Item", 1, new Money(5m), new Money(5m), "Groceries", null)], 1, 0, 10000));
		_adjustmentServiceMock.Setup(s => s.GetByReceiptIdAsync(_receiptId, 0, 10000, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.Adjustment>([], 0, 0, 10000));
		_transactionServiceMock.Setup(s => s.GetTransactionAccountsByReceiptIdAsync(_receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.Error.Should().Contain("no transactions");
	}

	[Fact]
	public async Task Handle_UnmappedCategories_ReturnsUnmappedList()
	{
		Domain.Core.Receipt receipt = new(_receiptId, "Store", DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), new Money(0m));
		_receiptServiceMock.Setup(s => s.GetByIdAsync(_receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(receipt);

		_receiptItemServiceMock.Setup(s => s.GetByReceiptIdAsync(_receiptId, 0, 10000, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.ReceiptItem>([
				new(Guid.NewGuid(), null, "Item", 1, new Money(5m), new Money(5m), "Groceries", null),
				new(Guid.NewGuid(), null, "Gas", 1, new Money(3m), new Money(3m), "Gas", null),
			], 2, 0, 10000));

		_adjustmentServiceMock.Setup(s => s.GetByReceiptIdAsync(_receiptId, 0, 10000, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.Adjustment>([], 0, 0, 10000));

		Domain.Core.Transaction tx = new(Guid.NewGuid(), Guid.NewGuid(), new Money(8m), DateOnly.FromDateTime(DateTime.Today.AddDays(-1)));
		tx.AccountId = _accountId;
		_transactionServiceMock.Setup(s => s.GetTransactionAccountsByReceiptIdAsync(_receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([new TransactionAccount { Transaction = tx, Account = new Domain.Core.Account(_accountId, "Checking", true) }]);

		// Only "Groceries" is mapped, "Gas" is not
		_categoryMappingServiceMock.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync([
				new YnabCategoryMappingDto(Guid.NewGuid(), "Groceries", "ynab-cat-1", "Groceries", "Food", _budgetId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			]);

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.UnmappedCategories.Should().Contain("Gas");
		result.Error.Should().Contain("Unmapped");
	}

	[Fact]
	public async Task Handle_AlreadySynced_SkipsTransactionAndSucceeds()
	{
		SetupHappyPath();
		_syncRecordServiceMock.Setup(s => s.GetByTransactionAndTypeAsync(_transactionId, YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(Guid.NewGuid(), _transactionId, "ynab-tx-old", _budgetId, null, YnabSyncType.TransactionPush, YnabSyncStatus.Synced, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		// Bug 1 fix: already-synced transactions are skipped, not rejected
		result.Success.Should().BeTrue();
		result.PushedTransactions.Should().BeEmpty();
		_ynabApiClientMock.Verify(s => s.CreateTransactionAsync(It.IsAny<string>(), It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_PartialSync_SkipsSyncedAndPushesRemaining()
	{
		SetupHappyPath();

		// Set up two transactions: TX1 already synced, TX2 not synced
		Guid transactionId2 = Guid.NewGuid();
		Domain.Core.Transaction tx1 = new(_transactionId, Guid.NewGuid(), new Money(11.00m), DateOnly.FromDateTime(DateTime.Today.AddDays(-1)));
		tx1.AccountId = _accountId;
		tx1.ReceiptId = _receiptId;
		Domain.Core.Transaction tx2 = new(transactionId2, Guid.NewGuid(), new Money(5.00m), DateOnly.FromDateTime(DateTime.Today.AddDays(-1)));
		tx2.AccountId = _accountId;
		tx2.ReceiptId = _receiptId;

		Domain.Core.Account account = new(_accountId, "Checking", true);
		_transactionServiceMock.Setup(s => s.GetTransactionAccountsByReceiptIdAsync(_receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([
				new TransactionAccount { Transaction = tx1, Account = account },
				new TransactionAccount { Transaction = tx2, Account = account },
			]);

		// TX1 is already synced
		_syncRecordServiceMock.Setup(s => s.GetByTransactionAndTypeAsync(_transactionId, YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(Guid.NewGuid(), _transactionId, "ynab-tx-old", _budgetId, null, YnabSyncType.TransactionPush, YnabSyncStatus.Synced, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
		// TX2 is not synced
		_syncRecordServiceMock.Setup(s => s.GetByTransactionAndTypeAsync(transactionId2, YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabSyncRecordDto?)null);

		Guid syncRecordId2 = Guid.NewGuid();
		_syncRecordServiceMock.Setup(s => s.CreateAsync(transactionId2, _budgetId, YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(syncRecordId2, transactionId2, null, _budgetId, null, YnabSyncType.TransactionPush, YnabSyncStatus.Pending, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		_splitCalculatorMock.Setup(s => s.ComputeWaterfallSplits(It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(), It.IsAny<Dictionary<string, string>>()))
			.Returns(new YnabSplitResult([
				new YnabTransactionSplit(_transactionId, -11000, [new YnabSubTransactionSplit("ynab-cat-1", -11000)]),
				new YnabTransactionSplit(transactionId2, -5000, [new YnabSubTransactionSplit("ynab-cat-1", -5000)]),
			]));

		_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabCreateTransactionResponse("ynab-tx-2"));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		// TX1 skipped, TX2 pushed
		result.Success.Should().BeTrue();
		result.PushedTransactions.Should().HaveCount(1);
		result.PushedTransactions[0].LocalTransactionId.Should().Be(transactionId2);

		// CreateAsync only called for TX2
		_syncRecordServiceMock.Verify(s => s.CreateAsync(_transactionId, It.IsAny<string>(), It.IsAny<YnabSyncType>(), It.IsAny<CancellationToken>()), Times.Never);
		_syncRecordServiceMock.Verify(s => s.CreateAsync(transactionId2, _budgetId, YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_NoBudgetSelected_ReturnsError()
	{
		SetupHappyPath();
		_budgetSelectionServiceMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.Error.Should().Contain("No YNAB budget selected");
	}

	[Fact]
	public async Task Handle_UnmappedAccount_ReturnsError()
	{
		SetupHappyPath();
		_accountMappingServiceMock.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.Error.Should().Contain("not mapped to YNAB accounts");
	}

	[Fact]
	public async Task Handle_YnabApiFailure_MarksSyncRecordAsFailedAndReturnsError()
	{
		SetupHappyPath();
		_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("YNAB API down"));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.Error.Should().Contain("YNAB API down");
		_syncRecordServiceMock.Verify(s => s.UpdateStatusAsync(
			It.IsAny<Guid>(), YnabSyncStatus.Failed, null, It.Is<string>(msg => msg.Contains("YNAB API down")),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_CreateAsyncFailure_ReturnsFalseWithoutCallingYnabApi()
	{
		// Bug 3: CreateAsync is now inside the try block, so DB failures are caught
		SetupHappyPath();
		_syncRecordServiceMock.Setup(s => s.CreateAsync(_transactionId, _budgetId, YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("DB connection lost"));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.Error.Should().Contain("DB connection lost");
		_ynabApiClientMock.Verify(s => s.CreateTransactionAsync(It.IsAny<string>(), It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_StatusUpdateFailsAfterYnabSuccess_ReturnsSuccessWithWarning()
	{
		// Bug 6: If UpdateStatusAsync(Synced) throws after YNAB TX created,
		// the result should be Success=true with a warning, not Failed
		SetupHappyPath();
		_syncRecordServiceMock.Setup(s => s.UpdateStatusAsync(
				It.IsAny<Guid>(), YnabSyncStatus.Synced, "ynab-tx-1", null,
				It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("DB timeout on status update"));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeTrue();
		result.PushedTransactions.Should().HaveCount(1);
		result.Error.Should().Contain("sync record update failed");
		result.Error.Should().Contain("DB timeout on status update");
	}

	[Fact]
	public async Task Handle_SyncsRecordOnSuccess()
	{
		SetupHappyPath();

		await _handler.Handle(new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		_syncRecordServiceMock.Verify(s => s.CreateAsync(
			_transactionId, _budgetId, YnabSyncType.TransactionPush,
			It.IsAny<CancellationToken>()), Times.Once);

		_syncRecordServiceMock.Verify(s => s.UpdateStatusAsync(
			It.IsAny<Guid>(), YnabSyncStatus.Synced, "ynab-tx-1", null,
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_HappyPath_PassesImportIdWithReceiptPrefixToCreateTransaction()
	{
		SetupHappyPath();
		YnabCreateTransactionRequest? capturedRequest = null;
		_ynabApiClientMock
			.Setup(s => s.CreateTransactionAsync(_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.Callback<string, YnabCreateTransactionRequest, CancellationToken>((_, req, _) => capturedRequest = req)
			.ReturnsAsync(new YnabCreateTransactionResponse("ynab-tx-1"));

		await _handler.Handle(new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		capturedRequest.Should().NotBeNull();
		capturedRequest!.ImportId.Should().NotBeNullOrEmpty();
		capturedRequest.ImportId.Should().StartWith("YNAB:");

		// Import ID should contain the receipt prefix (first 6 hex chars of receipt ID)
		string receiptPrefix = _receiptId.ToString("N")[..6];
		string expected = $"YNAB:-11000:{DateTime.Today.AddDays(-1):yyyy-MM-dd}:{receiptPrefix}:1";
		capturedRequest.ImportId.Should().Be(expected);
	}

	[Fact]
	public async Task Handle_MultipleSplitsWithSameAmountAndDate_IncrementsOccurrence()
	{
		SetupHappyPath();

		// Override split result to return two splits with the same milliunits
		Guid transactionId2 = Guid.NewGuid();
		Domain.Core.Transaction tx2 = new(transactionId2, Guid.NewGuid(), new Money(11.00m), DateOnly.FromDateTime(DateTime.Today.AddDays(-1)));
		tx2.AccountId = _accountId;
		tx2.ReceiptId = _receiptId;

		Domain.Core.Account account = new(_accountId, "Checking", true);
		List<TransactionAccount> txAccounts =
		[
			new() { Transaction = new Domain.Core.Transaction(_transactionId, Guid.NewGuid(), new Money(11.00m), DateOnly.FromDateTime(DateTime.Today.AddDays(-1))) { AccountId = _accountId, ReceiptId = _receiptId }, Account = account },
			new() { Transaction = tx2, Account = account },
		];
		_transactionServiceMock.Setup(s => s.GetTransactionAccountsByReceiptIdAsync(_receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(txAccounts);

		_syncRecordServiceMock.Setup(s => s.GetByTransactionAndTypeAsync(transactionId2, YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabSyncRecordDto?)null);

		_syncRecordServiceMock.Setup(s => s.CreateAsync(transactionId2, _budgetId, YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(Guid.NewGuid(), transactionId2, null, _budgetId, null, YnabSyncType.TransactionPush, YnabSyncStatus.Pending, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		_splitCalculatorMock.Setup(s => s.ComputeWaterfallSplits(It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(), It.IsAny<Dictionary<string, string>>()))
			.Returns(new YnabSplitResult([
				new YnabTransactionSplit(_transactionId, -11000, [new YnabSubTransactionSplit("ynab-cat-1", -11000)]),
				new YnabTransactionSplit(transactionId2, -11000, [new YnabSubTransactionSplit("ynab-cat-1", -11000)]),
			]));

		List<YnabCreateTransactionRequest> capturedRequests = [];
		_ynabApiClientMock
			.Setup(s => s.CreateTransactionAsync(_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.Callback<string, YnabCreateTransactionRequest, CancellationToken>((_, req, _) => capturedRequests.Add(req))
			.ReturnsAsync(new YnabCreateTransactionResponse("ynab-tx-1"));

		await _handler.Handle(new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		capturedRequests.Should().HaveCount(2);
		capturedRequests[0].ImportId.Should().EndWith(":1");
		capturedRequests[1].ImportId.Should().EndWith(":2");
	}

	[Fact]
	public async Task Handle_SplitCalculatorThrowsExhaustedAllocations_ReturnsError()
	{
		SetupHappyPath();
		_splitCalculatorMock
			.Setup(s => s.ComputeWaterfallSplits(It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(), It.IsAny<Dictionary<string, string>>()))
			.Throws(new InvalidOperationException(
				"Transaction abc (−5000 milliunits) could not be categorized. " +
				"All category allocations were exhausted by earlier transactions."));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.Error.Should().Contain("could not be categorized");
		result.Error.Should().Contain("exhausted");

		// No sync record should be created since the error occurs before the push loop
		_syncRecordServiceMock.Verify(s => s.CreateAsync(
			It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<YnabSyncType>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}
}
