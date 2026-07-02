using System.Net;
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

public class PushYnabTransactions409ReconcileTests
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
	private readonly Guid _transactionId = Guid.NewGuid();
	private readonly Guid _syncRecordId = Guid.NewGuid();
	private readonly string _budgetId = "budget-123";
	private readonly string _ynabAccountId = "ynab-acc-1";

	public PushYnabTransactions409ReconcileTests()
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
			Mock.Of<Application.Interfaces.Services.IYnabSyncEventService>(),
			Mock.Of<Application.Interfaces.Services.IYnabResponseContext>(),
			Mock.Of<Microsoft.Extensions.Logging.ILogger<PushYnabTransactionsCommandHandler>>());
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

		_syncRecordServiceMock.Setup(s => s.CreateAsync(_transactionId, _budgetId, YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(_syncRecordId, _transactionId, null, _budgetId, null, YnabSyncType.TransactionPush, YnabSyncStatus.Pending, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		_splitCalculatorMock.Setup(s => s.ComputeWaterfallSplits(It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(), It.IsAny<Dictionary<string, string>>()))
			.Returns(new YnabSplitResult([
				new YnabTransactionSplit(_transactionId, -11000, [new YnabSubTransactionSplit("ynab-cat-1", -11000)]),
			]));
	}

	[Fact]
	public async Task Handle_CreateTransaction409_ImportIdFound_ReconcilesToSuccess()
	{
		SetupHappyPath();

		_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("conflict", null, HttpStatusCode.Conflict));

		_ynabApiClientMock.Setup(s => s.FindTransactionByImportIdAsync(
				_budgetId, _ynabAccountId, It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync("ynab-tx-recovered");

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeTrue();
		result.PushedTransactions.Should().HaveCount(1);
		result.PushedTransactions[0].YnabTransactionId.Should().Be("ynab-tx-recovered");
		result.PushedTransactions[0].LocalTransactionId.Should().Be(_transactionId);
		result.Error.Should().BeNull();

		_syncRecordServiceMock.Verify(s => s.UpdateStatusAsync(
			_syncRecordId, YnabSyncStatus.Synced, "ynab-tx-recovered", null, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_CreateTransaction409_ImportIdNotFound_ReturnsFailure()
	{
		SetupHappyPath();

		_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("conflict", null, HttpStatusCode.Conflict));

		_ynabApiClientMock.Setup(s => s.FindTransactionByImportIdAsync(
				_budgetId, _ynabAccountId, It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		_syncRecordServiceMock.Verify(s => s.UpdateStatusAsync(
			_syncRecordId, YnabSyncStatus.Failed, null, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_CreateTransaction409_LookupThrows_ReturnsFailure()
	{
		SetupHappyPath();

		_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("conflict", null, HttpStatusCode.Conflict));

		_ynabApiClientMock.Setup(s => s.FindTransactionByImportIdAsync(
				_budgetId, _ynabAccountId, It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("lookup failed"));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.Error.Should().Contain("lookup failed");
		_syncRecordServiceMock.Verify(s => s.UpdateStatusAsync(
			_syncRecordId, YnabSyncStatus.Failed, null, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
	}
}
