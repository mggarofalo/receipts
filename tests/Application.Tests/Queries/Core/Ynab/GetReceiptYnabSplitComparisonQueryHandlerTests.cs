using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using Common;
using Domain;
using Domain.Aggregates;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetReceiptYnabSplitComparisonQueryHandlerTests
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
	private readonly Mock<ILogger<GetReceiptYnabSplitComparisonQueryHandler>> _loggerMock = new();
	private readonly GetReceiptYnabSplitComparisonQueryHandler _handler;

	private readonly Guid _receiptId = Guid.NewGuid();
	private readonly Guid _accountId = Guid.NewGuid();
	private readonly Guid _transactionId = Guid.NewGuid();
	private readonly string _budgetId = "budget-123";
	private readonly string _ynabAccountId = "ynab-acc-1";

	public GetReceiptYnabSplitComparisonQueryHandlerTests()
	{
		_handler = new GetReceiptYnabSplitComparisonQueryHandler(
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
			_loggerMock.Object);
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

		_categoryMappingServiceMock.Setup(s => s.GetByBudgetIdAsync(_budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([
				new YnabCategoryMappingDto(Guid.NewGuid(), "Groceries", "ynab-cat-1", "Groceries", "Food", _budgetId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			]);

		_budgetSelectionServiceMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(_budgetId);

		_accountMappingServiceMock.Setup(s => s.GetByBudgetIdAsync(_budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([
				new YnabAccountMappingDto(Guid.NewGuid(), _accountId, _ynabAccountId, "Checking", _budgetId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			]);

		_splitCalculatorMock.Setup(s => s.ComputeWaterfallSplits(It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(), It.IsAny<Dictionary<string, string>>()))
			.Returns(new YnabSplitResult([
				new YnabTransactionSplit(_transactionId, -11000, [new YnabSubTransactionSplit("ynab-cat-1", -11000)]),
			]));

		_ynabApiClientMock.Setup(s => s.GetCategoriesAsync(_budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([
				new YnabCategory("ynab-cat-1", "Groceries", "group-1", "Food", false),
			]);

		_syncRecordServiceMock.Setup(s => s.GetByTransactionTypeAndBudgetAsync(_transactionId, YnabSyncType.TransactionPush, _budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabSyncRecordDto?)null);
	}

	[Fact]
	public async Task Handle_ReceiptNotFound_ReturnsCannotCompute()
	{
		_receiptServiceMock.Setup(s => s.GetByIdAsync(_receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((Domain.Core.Receipt?)null);

		ReceiptYnabSplitComparisonResult result = await _handler.Handle(
			new GetReceiptYnabSplitComparisonQuery(_receiptId), CancellationToken.None);

		result.CanComputeExpected.Should().BeFalse();
		result.ExpectedUnavailableReason.Should().Contain("Receipt not found");
		result.TransactionComparisons.Should().BeEmpty();
	}

	[Fact]
	public async Task Handle_UnmappedCategories_ReturnsCannotComputeWithList()
	{
		SetupHappyPath();
		// Override items to include an unmapped category
		_receiptItemServiceMock.Setup(s => s.GetByReceiptIdAsync(_receiptId, 0, 10000, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.ReceiptItem>([
				new(Guid.NewGuid(), null, "A", 1, new Money(5m), new Money(5m), "Groceries", null),
				new(Guid.NewGuid(), null, "B", 1, new Money(3m), new Money(3m), "Gas", null),
			], 2, 0, 10000));

		ReceiptYnabSplitComparisonResult result = await _handler.Handle(
			new GetReceiptYnabSplitComparisonQuery(_receiptId), CancellationToken.None);

		result.CanComputeExpected.Should().BeFalse();
		result.ExpectedUnavailableReason.Should().Contain("Unmapped");
		result.UnmappedCategories.Should().Contain("Gas");
	}

	[Fact]
	public async Task Handle_NoBudgetSelected_ReturnsCannotCompute()
	{
		SetupHappyPath();
		_budgetSelectionServiceMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		ReceiptYnabSplitComparisonResult result = await _handler.Handle(
			new GetReceiptYnabSplitComparisonQuery(_receiptId), CancellationToken.None);

		result.CanComputeExpected.Should().BeFalse();
		result.ExpectedUnavailableReason.Should().Contain("No YNAB budget");
	}

	[Fact]
	public async Task Handle_HappyPath_NotPushedYet_ReturnsExpectedOnly()
	{
		SetupHappyPath();

		ReceiptYnabSplitComparisonResult result = await _handler.Handle(
			new GetReceiptYnabSplitComparisonQuery(_receiptId), CancellationToken.None);

		result.CanComputeExpected.Should().BeTrue();
		result.TransactionComparisons.Should().HaveCount(1);
		TransactionSplitComparison comp = result.TransactionComparisons[0];
		comp.LocalTransactionId.Should().Be(_transactionId);
		comp.AccountName.Should().Be("Checking");
		comp.Expected.Should().HaveCount(1);
		comp.Expected[0].YnabCategoryId.Should().Be("ynab-cat-1");
		comp.Expected[0].CategoryName.Should().Be("Groceries");
		comp.Expected[0].Milliunits.Should().Be(-11000);
		comp.Actual.Should().BeNull();
		comp.Matches.Should().BeNull();
		comp.ActualFetchError.Should().BeNull();
	}

	[Fact]
	public async Task Handle_HappyPath_Synced_ReturnsExpectedAndActual_Matches()
	{
		SetupHappyPath();
		_syncRecordServiceMock.Setup(s => s.GetByTransactionTypeAndBudgetAsync(_transactionId, YnabSyncType.TransactionPush, _budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(Guid.NewGuid(), _transactionId, "ynab-tx-1", _budgetId, _ynabAccountId, YnabSyncType.TransactionPush, YnabSyncStatus.Synced, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		_ynabApiClientMock.Setup(s => s.GetTransactionAsync(_budgetId, "ynab-tx-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabTransaction(
				"ynab-tx-1",
				DateOnly.FromDateTime(DateTime.Today),
				-11000,
				null,
				"cleared",
				false,
				_ynabAccountId,
				"ynab-cat-1",
				"Store",
				"Groceries",
				null));

		ReceiptYnabSplitComparisonResult result = await _handler.Handle(
			new GetReceiptYnabSplitComparisonQuery(_receiptId), CancellationToken.None);

		result.CanComputeExpected.Should().BeTrue();
		TransactionSplitComparison comp = result.TransactionComparisons.Single();
		comp.Actual.Should().NotBeNull();
		comp.Actual!.Should().HaveCount(1);
		comp.Actual[0].YnabCategoryId.Should().Be("ynab-cat-1");
		comp.Actual[0].Milliunits.Should().Be(-11000);
		comp.Matches.Should().BeTrue();
		comp.ActualFetchError.Should().BeNull();
	}

	[Fact]
	public async Task Handle_HappyPath_Synced_ActualDiffersFromExpected_ReturnsMatchesFalse()
	{
		SetupHappyPath();
		_syncRecordServiceMock.Setup(s => s.GetByTransactionTypeAndBudgetAsync(_transactionId, YnabSyncType.TransactionPush, _budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(Guid.NewGuid(), _transactionId, "ynab-tx-1", _budgetId, _ynabAccountId, YnabSyncType.TransactionPush, YnabSyncStatus.Synced, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		// YNAB returns a split with a different category than expected
		_ynabApiClientMock.Setup(s => s.GetTransactionAsync(_budgetId, "ynab-tx-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabTransaction(
				"ynab-tx-1",
				DateOnly.FromDateTime(DateTime.Today),
				-11000,
				null,
				"cleared",
				false,
				_ynabAccountId,
				null,
				"Store",
				null,
				[
					new YnabSubTransactionRead("sub-1", -6000, null, "ynab-cat-1", "Groceries"),
					new YnabSubTransactionRead("sub-2", -5000, null, "ynab-cat-2", "Dining"),
				]));

		ReceiptYnabSplitComparisonResult result = await _handler.Handle(
			new GetReceiptYnabSplitComparisonQuery(_receiptId), CancellationToken.None);

		TransactionSplitComparison comp = result.TransactionComparisons.Single();
		comp.Actual.Should().HaveCount(2);
		comp.Matches.Should().BeFalse();
	}

	[Fact]
	public async Task Handle_HappyPath_Synced_YnabFetchThrows_SetsActualFetchError()
	{
		SetupHappyPath();
		_syncRecordServiceMock.Setup(s => s.GetByTransactionTypeAndBudgetAsync(_transactionId, YnabSyncType.TransactionPush, _budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(Guid.NewGuid(), _transactionId, "ynab-tx-1", _budgetId, _ynabAccountId, YnabSyncType.TransactionPush, YnabSyncStatus.Synced, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		_ynabApiClientMock.Setup(s => s.GetTransactionAsync(_budgetId, "ynab-tx-1", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("YNAB API down"));

		ReceiptYnabSplitComparisonResult result = await _handler.Handle(
			new GetReceiptYnabSplitComparisonQuery(_receiptId), CancellationToken.None);

		TransactionSplitComparison comp = result.TransactionComparisons.Single();
		comp.Actual.Should().BeNull();
		comp.ActualFetchError.Should().Contain("YNAB API down");
		comp.Matches.Should().BeNull();
		// Expected is still present even when actual fetch fails
		comp.Expected.Should().HaveCount(1);
	}

	[Fact]
	public async Task Handle_UnmappedAccount_ReturnsCannotCompute()
	{
		SetupHappyPath();
		_accountMappingServiceMock.Setup(s => s.GetByBudgetIdAsync(_budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		ReceiptYnabSplitComparisonResult result = await _handler.Handle(
			new GetReceiptYnabSplitComparisonQuery(_receiptId), CancellationToken.None);

		result.CanComputeExpected.Should().BeFalse();
		result.ExpectedUnavailableReason.Should().Contain("not mapped");
	}

	[Fact]
	public async Task Handle_SplitCalculatorThrows_ReturnsCannotCompute()
	{
		SetupHappyPath();
		_splitCalculatorMock.Setup(s => s.ComputeWaterfallSplits(It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(), It.IsAny<Dictionary<string, string>>()))
			.Throws(new InvalidOperationException("Transaction exceeds category totals."));

		ReceiptYnabSplitComparisonResult result = await _handler.Handle(
			new GetReceiptYnabSplitComparisonQuery(_receiptId), CancellationToken.None);

		result.CanComputeExpected.Should().BeFalse();
		result.ExpectedUnavailableReason.Should().Contain("exceeds category totals");
	}
}
