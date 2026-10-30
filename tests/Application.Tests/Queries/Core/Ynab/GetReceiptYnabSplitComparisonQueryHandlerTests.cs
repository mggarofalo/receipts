using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using Application.Taxonomy;
using Application.Tests.Commands.Ynab;
using Common;
using Domain;
using Domain.Aggregates;
using Domain.Envelopes;
using Domain.Taxonomy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetReceiptYnabSplitComparisonQueryHandlerTests
{
	private static readonly Guid NewRootId = Guid.Parse("ba1bda16-22bb-4d08-a833-2eb106600001");
	private static readonly Guid CurrentRevisionId = Guid.Parse("ba1bda16-22bb-4d08-a833-2eb106600002");
	private static readonly Guid CurrentEnvelopeId = Guid.Parse("ba1bda16-22bb-4d08-a833-2eb106600003");
	private readonly Mock<IReceiptService> _receiptServiceMock = new();
	private readonly Mock<IReceiptItemService> _receiptItemServiceMock = new();
	private readonly Mock<IAdjustmentService> _adjustmentServiceMock = new();
	private readonly Mock<ITransactionService> _transactionServiceMock = new();
	private readonly Mock<IYnabEnvelopeMappingService> _envelopeMappingServiceMock = new();
	private readonly Mock<IEnvelopeService> _envelopeServiceMock = new();
	private readonly Mock<IItemTypeEnvelopeDefaultService> _envelopeDefaultServiceMock = new();
	private readonly Mock<IYnabAccountMappingService> _accountMappingServiceMock = new();
	private readonly Mock<IYnabBudgetSelectionService> _budgetSelectionServiceMock = new();
	private readonly Mock<IYnabSyncRecordService> _syncRecordServiceMock = new();
	private readonly Mock<IYnabApiClient> _ynabApiClientMock = new();
	private readonly Mock<IYnabSplitCalculator> _splitCalculatorMock = new();
	private readonly Mock<ITaxonomyService> _taxonomyServiceMock = new();
	private readonly Mock<ILogger<GetReceiptYnabSplitComparisonQueryHandler>> _loggerMock = new();
	private readonly GetReceiptYnabSplitComparisonQueryHandler _handler;

	private readonly Guid _receiptId = Guid.NewGuid();
	private readonly Guid _accountId = Guid.NewGuid();
	private readonly Guid _transactionId = Guid.NewGuid();
	private readonly string _budgetId = "budget-123";
	private readonly string _ynabAccountId = "ynab-acc-1";

	public GetReceiptYnabSplitComparisonQueryHandlerTests()
	{
		_envelopeServiceMock.Setup(s => s.GetAllAsync(null, It.IsAny<CancellationToken>()))
			.ReturnsAsync([YnabRecordedCategoryFixture.GroceriesEnvelope, new Envelope(CurrentEnvelopeId, "Current")]);
		_envelopeDefaultServiceMock.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
		_handler = new GetReceiptYnabSplitComparisonQueryHandler(
			_receiptServiceMock.Object,
			_receiptItemServiceMock.Object,
			_adjustmentServiceMock.Object,
			_transactionServiceMock.Object,
			_envelopeMappingServiceMock.Object,
			_envelopeServiceMock.Object,
			_envelopeDefaultServiceMock.Object,
			_accountMappingServiceMock.Object,
			_budgetSelectionServiceMock.Object,
			_taxonomyServiceMock.Object,
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
						YnabRecordedCategoryFixture.Classified(new(Guid.NewGuid(), null, "Item1", 1, new Money(10.00m), new Money(10.00m), "Groceries", null)),
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

		_envelopeMappingServiceMock.Setup(s => s.GetByBudgetIdAsync(_budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([
								YnabRecordedCategoryFixture.Mapped("ynab-cat-1", "Groceries", "Food", _budgetId),
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

	private void SetupDefault(Guid categoryId, Guid envelopeId)
	{
		_envelopeDefaultServiceMock.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync([new ItemTypeEnvelopeDefault(categoryId, envelopeId, DateTimeOffset.UtcNow)]);
	}

	private void SetupCurrentRevision(IReadOnlyList<TaxonomyNode> nodes)
	{
		_taxonomyServiceMock.Setup(s => s.ReadAsync(null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TaxonomyRevision(CurrentRevisionId, 2, null, DateTimeOffset.UtcNow,
				"test", "test", Guid.NewGuid(), nodes, 0));
	}

	[Fact]
	public async Task Handle_DefaultView_UsesRecordedPathAndDoesNotReadCurrentTaxonomy()
	{
		SetupHappyPath();
		SetupCurrentRevision([
			new TaxonomyNode(NewRootId, null, "New parent", null, false),
			new TaxonomyNode(YnabRecordedCategoryFixture.GroceriesId, NewRootId, "Renamed groceries", null, false),
		]);
		Dictionary<string, string>? targets = null;
		_splitCalculatorMock.Setup(s => s.ComputeWaterfallSplits(
				It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(),
				It.IsAny<Dictionary<string, string>>()))
			.Returns((ReceiptWithItems _, List<Domain.Core.Transaction> _, Dictionary<string, string> mapping) =>
			{
				targets = mapping;
				return new YnabSplitResult([new YnabTransactionSplit(_transactionId, -11000,
					[new YnabSubTransactionSplit("ynab-cat-1", -11000)])]);
			});

		ReceiptYnabSplitComparisonResult result = await _handler.Handle(
			new GetReceiptYnabSplitComparisonQuery(_receiptId), CancellationToken.None);

		result.CanComputeExpected.Should().BeTrue();
		result.CategoryView.Should().Be("asClassified");
		result.CapturedCurrentRevisionId.Should().BeNull();
		targets.Should().ContainSingle().Which.Value.Should().Be("ynab-cat-1");
		_taxonomyServiceMock.Verify(s => s.ReadAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Handle_CurrentHierarchy_DefaultSourcedItemUsesCurrentItemTypeDefaultAfterMove(bool directDefault)
	{
		SetupHappyPath();
		SetupCurrentRevision([
			new TaxonomyNode(NewRootId, null, "New parent", null, false),
			new TaxonomyNode(YnabRecordedCategoryFixture.GroceriesId, NewRootId, "Renamed groceries", null, false),
		]);
		SetupDefault(directDefault ? YnabRecordedCategoryFixture.GroceriesId : NewRootId, CurrentEnvelopeId);
		_envelopeMappingServiceMock.Setup(s => s.GetByBudgetIdAsync(_budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([
				YnabRecordedCategoryFixture.Mapped("ynab-cat-1", "Groceries", "Food", _budgetId),
				new YnabEnvelopeMappingDto(Guid.NewGuid(), CurrentEnvelopeId, "Current", "ynab-current", "Current", "Food",
					_budgetId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			]);
		Dictionary<string, string>? targets = null;
		_splitCalculatorMock.Setup(s => s.ComputeWaterfallSplits(
				It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(),
				It.IsAny<Dictionary<string, string>>()))
			.Returns((ReceiptWithItems _, List<Domain.Core.Transaction> _, Dictionary<string, string> mapping) =>
			{
				targets = mapping;
				return new YnabSplitResult([new YnabTransactionSplit(_transactionId, -11000,
					[new YnabSubTransactionSplit("ynab-current", -11000)])]);
			});

		ReceiptYnabSplitComparisonResult result = await _handler.Handle(
			new GetReceiptYnabSplitComparisonQuery(_receiptId, YnabComparisonCategoryView.CurrentHierarchy),
			CancellationToken.None);

		result.CanComputeExpected.Should().BeTrue();
		result.CategoryView.Should().Be("currentHierarchy");
		result.CapturedCurrentRevisionId.Should().Be(CurrentRevisionId);
		targets.Should().ContainSingle().Which.Value.Should().Be("ynab-current");
	}

	[Fact]
	public async Task Handle_CurrentHierarchy_HistoricalItemKeepsRecordedEnvelope()
	{
		SetupHappyPath();
		Domain.Core.ReceiptItem item = YnabRecordedCategoryFixture.Classified(new(Guid.NewGuid(), null, "Historical", 1,
			new Money(10m), new Money(10m), "Groceries", null));
		item.EnvelopeSource = EnvelopeSource.Historical;
		_receiptItemServiceMock.Setup(s => s.GetByReceiptIdAsync(_receiptId, 0, 10000,
				It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.ReceiptItem>([item], 1, 0, 10000));
		SetupCurrentRevision([new TaxonomyNode(YnabRecordedCategoryFixture.GroceriesId, null, "Renamed", null, false)]);
		SetupDefault(YnabRecordedCategoryFixture.GroceriesId, CurrentEnvelopeId);
		Dictionary<string, string>? targets = null;
		_splitCalculatorMock.Setup(s => s.ComputeWaterfallSplits(
				It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(),
				It.IsAny<Dictionary<string, string>>()))
			.Returns((ReceiptWithItems _, List<Domain.Core.Transaction> _, Dictionary<string, string> mapping) =>
			{
				targets = mapping;
				return new YnabSplitResult([new YnabTransactionSplit(_transactionId, -11000,
					[new YnabSubTransactionSplit("ynab-cat-1", -11000)])]);
			});

		ReceiptYnabSplitComparisonResult result = await _handler.Handle(
			new GetReceiptYnabSplitComparisonQuery(_receiptId, YnabComparisonCategoryView.CurrentHierarchy),
			CancellationToken.None);

		result.CanComputeExpected.Should().BeTrue();
		targets.Should().ContainSingle().Which.Value.Should().Be("ynab-cat-1");
	}

	[Theory]
	[InlineData("missing")]
	[InlineData("unassigned")]
	public async Task Handle_CurrentHierarchy_ItemTypeWithoutCurrentDefaultFailsClosed(string condition)
	{
		SetupHappyPath();
		SetupCurrentRevision(condition == "missing"
			? []
			: [new TaxonomyNode(YnabRecordedCategoryFixture.GroceriesId, null, "Renamed", null, false)]);
		SetupDefault(YnabRecordedCategoryFixture.GroceriesId, CurrentEnvelopeId);
		if (condition == "unassigned")
		{
			Domain.Core.ReceiptItem item = new(Guid.NewGuid(), null, "Unassigned", 1,
				new Money(10m), new Money(10m), "Groceries", null);
			_receiptItemServiceMock.Setup(s => s.GetByReceiptIdAsync(_receiptId, 0, 10000,
					It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
				.ReturnsAsync(new PagedResult<Domain.Core.ReceiptItem>([item], 1, 0, 10000));
		}

		ReceiptYnabSplitComparisonResult result = await _handler.Handle(
			new GetReceiptYnabSplitComparisonQuery(_receiptId, YnabComparisonCategoryView.CurrentHierarchy),
			CancellationToken.None);

		result.CanComputeExpected.Should().BeFalse();
		result.UnmappedCategories.Should().ContainSingle().Which.Should().Contain("Unresolved");
		result.CategoryView.Should().Be("currentHierarchy");
		result.CapturedCurrentRevisionId.Should().Be(CurrentRevisionId);
		_taxonomyServiceMock.Verify(s => s.ReadAsync(null, It.IsAny<CancellationToken>()), Times.Once);
		_splitCalculatorMock.Verify(s => s.ComputeWaterfallSplits(
			It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(),
			It.IsAny<Dictionary<string, string>>()), Times.Never);
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
								YnabRecordedCategoryFixture.Classified(new(Guid.NewGuid(), null, "A", 1, new Money(5m), new Money(5m), "Groceries", null)),
				new(Guid.NewGuid(), null, "B", 1, new Money(3m), new Money(3m), "Gas", null),
			], 2, 0, 10000));

		ReceiptYnabSplitComparisonResult result = await _handler.Handle(
			new GetReceiptYnabSplitComparisonQuery(_receiptId), CancellationToken.None);

		result.CanComputeExpected.Should().BeFalse();
		result.ExpectedUnavailableReason.Should().Contain("Unmapped");
		result.UnmappedCategories.Should().ContainSingle()
				.Which.Should().StartWith("No envelope (item ").And.EndWith(": Unresolved)");
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
