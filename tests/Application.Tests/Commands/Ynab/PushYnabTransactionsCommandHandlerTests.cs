using Application.Commands.Ynab.PushTransactions;
using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ynab;
using Application.Utilities;
using Common;
using Domain;
using Domain.Aggregates;
using FluentAssertions;
using Infrastructure.Ynab;
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
	private readonly Mock<IYnabSyncEventService> _ynabSyncEventServiceMock = new();
	private readonly Mock<IYnabResponseContext> _ynabResponseContextMock = new();
	private readonly PushYnabTransactionsCommandHandler _handler;

	private readonly Guid _receiptId = Guid.NewGuid();
	private readonly Guid _accountId = Guid.NewGuid();
	private readonly Guid _transactionId = Guid.NewGuid();
	private readonly string _budgetId = "budget-123";
	private readonly string _ynabAccountId = "ynab-acc-1";

	public PushYnabTransactionsCommandHandlerTests()
	{
		_syncRecordServiceMock.SetupPushOperationDefaults();
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
			_ynabSyncEventServiceMock.Object,
			_ynabResponseContextMock.Object,
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

		_syncRecordServiceMock.Setup(s => s.GetByTransactionTypeAndBudgetAsync(_transactionId, YnabSyncType.TransactionPush, _budgetId, It.IsAny<CancellationToken>()))
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

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Handle_DefiniteCreateRejection_IsFailedButRetryReconciliationAuthFailureIsUnknown(bool retry)
	{
		SetupHappyPath();
		if (retry)
		{
			Guid operationId = Guid.NewGuid();
			YnabCreateTransactionRequest snapshot = new(
				_ynabAccountId, DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), -11000,
				"memo", "Store", "ynab-cat-1", false, ImportId: "YNAB:retry:1");
			YnabPushOperation operation = new(
				operationId, YnabSyncStatus.Unknown, snapshot, "source", "hash", 1, null, "prior ambiguity");
			_syncRecordServiceMock.Setup(s => s.PreparePushOperationAsync(
					_transactionId, _budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<string>(),
					It.IsAny<CancellationToken>()))
				.ReturnsAsync(operation);
			_syncRecordServiceMock.Setup(s => s.TryClaimPushOperationAsync(operationId, It.IsAny<CancellationToken>()))
				.ReturnsAsync(new YnabPushOperationClaim(operation with { AttemptCount = 2 }, Guid.NewGuid()));
			_ynabApiClientMock.Setup(s => s.FindTransactionByImportIdAsync(
					_budgetId, snapshot.AccountId, snapshot.ImportId!, snapshot.Date.AddDays(-1), It.IsAny<CancellationToken>()))
				.ThrowsAsync(new YnabAuthException("token expired during reconciliation"));
		}
		else
		{
			_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(
					_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
				.ThrowsAsync(new YnabNotFoundException("destination account not found"));
		}

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		YnabSyncStatus expected = retry ? YnabSyncStatus.Unknown : YnabSyncStatus.Failed;
		result.Success.Should().BeFalse();
		result.OperationStatus.Should().Be(expected);
		_syncRecordServiceMock.Verify(s => s.CompletePushOperationAsync(
			It.IsAny<Guid>(), It.IsAny<Guid>(), expected, null, It.IsAny<string>(),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Theory]
	[InlineData(YnabSyncStatus.Unknown, true)]
	[InlineData(YnabSyncStatus.Pending, false)]
	public async Task Handle_AmbiguousPredecessorThenDefiniteRetryRejection_RemainsUnknown(
		YnabSyncStatus predecessorStatus,
		bool useAuthRejection)
	{
		SetupHappyPath();
		Guid operationId = Guid.NewGuid();
		YnabCreateTransactionRequest snapshot = new(
			_ynabAccountId, DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), -11000,
			"memo", "Store", "ynab-cat-1", false, ImportId: YnabImportId.Generate(_transactionId));
		YnabPushOperation operation = new(
			operationId, predecessorStatus, snapshot, "source", "hash", 1, null, "unresolved prior send");
		_syncRecordServiceMock.Setup(s => s.PreparePushOperationAsync(
				_transactionId, _budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<string>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(operation);
		_syncRecordServiceMock.Setup(s => s.TryClaimPushOperationAsync(operationId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabPushOperationClaim(operation with { AttemptCount = 2 }, Guid.NewGuid()));
		_ynabApiClientMock.Setup(s => s.FindTransactionByImportIdAsync(
				_budgetId, snapshot.AccountId, snapshot.ImportId!, snapshot.Date.AddDays(-1), It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);
		Exception rejection = useAuthRejection
			? new YnabAuthException("token expired")
			: new YnabNotFoundException("destination account missing");
		_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(
				_budgetId, snapshot, It.IsAny<CancellationToken>()))
			.ThrowsAsync(rejection);

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.OperationStatus.Should().Be(YnabSyncStatus.Unknown);
		_syncRecordServiceMock.Verify(s => s.CompletePushOperationAsync(
			It.IsAny<Guid>(), It.IsAny<Guid>(), YnabSyncStatus.Unknown, null,
			It.Is<string>(error => error.Contains("may have accepted")), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_NewEqualSplit_UsesTransactionIdentityWhileLegacyReceiptIdsRemainReserved()
	{
		SetupHappyPath();
		Guid transactionBId = Guid.NewGuid();
		Guid insertedTransactionId = Guid.NewGuid();
		DateOnly date = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
		Domain.Core.Account account = new(_accountId, "Checking", true);
		List<Domain.Core.Transaction> transactions = new[] { _transactionId, insertedTransactionId, transactionBId }
			.Select(id => new Domain.Core.Transaction(id, Guid.NewGuid(), new Money(11.00m), date)
			{
				AccountId = _accountId,
				ReceiptId = _receiptId,
			})
			.ToList();
		_transactionServiceMock.Setup(s => s.GetTransactionAccountsByReceiptIdAsync(
				_receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(transactions.Select(transaction => new TransactionAccount
			{
				Transaction = transaction,
				Account = account,
			}).ToList());
		_syncRecordServiceMock.Setup(s => s.GetByTransactionTypeAndBudgetAsync(
				It.IsAny<Guid>(), YnabSyncType.TransactionPush, _budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabSyncRecordDto?)null);
		string importA = YnabImportId.Generate(-11000, date, _receiptId, 1);
		string importB = YnabImportId.Generate(-11000, date, _receiptId, 2);
		_syncRecordServiceMock.Setup(s => s.GetPushOperationIdentitiesByReceiptAsync(
				_receiptId, _budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([
				new YnabPushOperationIdentity(_transactionId, importA),
				new YnabPushOperationIdentity(transactionBId, importB),
			]);
		_splitCalculatorMock.Setup(s => s.ComputeWaterfallSplits(
				It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(),
				It.IsAny<Dictionary<string, string>>()))
			.Returns(new YnabSplitResult(transactions.Select(transaction =>
				new YnabTransactionSplit(transaction.Id, -11000,
					[new YnabSubTransactionSplit("ynab-cat-1", -11000)])).ToList()));
		List<string> sentImportIds = [];
		int remoteId = 0;
		_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(
				_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.Callback<string, YnabCreateTransactionRequest, CancellationToken>((_, request, _) =>
				sentImportIds.Add(request.ImportId!))
			.ReturnsAsync(() => new YnabCreateTransactionResponse($"remote-{++remoteId}"));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeTrue();
		sentImportIds.Should().Equal(
			importA,
			YnabImportId.Generate(insertedTransactionId),
			importB);
	}

	[Fact]
	public async Task Handle_DuplicatePersistedImportIdentity_BlocksBeforeClaimOrRemoteSend()
	{
		SetupHappyPath();
		Guid otherTransactionId = Guid.NewGuid();
		_syncRecordServiceMock.Setup(s => s.GetPushOperationIdentitiesByReceiptAsync(
				_receiptId, _budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([
				new YnabPushOperationIdentity(_transactionId, "YNAB:duplicate:1"),
				new YnabPushOperationIdentity(otherTransactionId, "YNAB:duplicate:1"),
			]);

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.OperationStatus.Should().Be(YnabSyncStatus.Unknown);
		result.Error.Should().Contain("same immutable YNAB import ID");
		_syncRecordServiceMock.Verify(s => s.TryClaimPushOperationAsync(
			It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
		_ynabApiClientMock.Verify(s => s.CreateTransactionAsync(
			It.IsAny<string>(), It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_PersistsSnapshotBeforeSend_AndUsesPersistedRequestAsAuthority()
	{
		SetupHappyPath();
		Guid operationId = Guid.NewGuid();
		Guid claimToken = Guid.NewGuid();
		bool snapshotPersisted = false;
		YnabCreateTransactionRequest persistedRequest = new(
			_ynabAccountId, new DateOnly(2026, 8, 31), -9999, "persisted memo", "Persisted payee",
			"ynab-cat-1", false, ImportId: "YNAB:persisted:1");
		YnabPushOperation operation = new(
			operationId, YnabSyncStatus.Pending, persistedRequest, "source-original", "hash-original", 0, null, null);
		_syncRecordServiceMock.Setup(s => s.PreparePushOperationAsync(
				_transactionId, _budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<string>(),
				It.IsAny<CancellationToken>()))
			.Callback(() => snapshotPersisted = true)
			.ReturnsAsync(operation);
		_syncRecordServiceMock.Setup(s => s.TryClaimPushOperationAsync(operationId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabPushOperationClaim(operation with { AttemptCount = 1 }, claimToken));
		YnabCreateTransactionRequest? sent = null;
		_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(
				_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.Callback<string, YnabCreateTransactionRequest, CancellationToken>((_, request, _) =>
			{
				snapshotPersisted.Should().BeTrue();
				sent = request;
			})
			.ReturnsAsync(new YnabCreateTransactionResponse("ynab-persisted"));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeTrue();
		sent.Should().BeEquivalentTo(persistedRequest);
		_syncRecordServiceMock.Verify(s => s.CompletePushOperationAsync(
			operationId, claimToken, YnabSyncStatus.Synced, "ynab-persisted", null,
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_LostAcceptedResponseThenLocalEdit_ReconcilesOriginalImportWithoutSecondSend()
	{
		SetupHappyPath();
		Guid operationId = Guid.NewGuid();
		YnabPushOperation? persisted = null;
		List<YnabCreateTransactionRequest> proposed = [];
		int attempt = 0;
		_syncRecordServiceMock.Setup(s => s.PreparePushOperationAsync(
				_transactionId, _budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<string>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync((Guid _, string _, YnabCreateTransactionRequest request, string sourceVersion, CancellationToken _) =>
			{
				proposed.Add(request);
				return persisted ??= new YnabPushOperation(
					operationId, YnabSyncStatus.Pending, request, sourceVersion, "hash-original", 0, null, null);
			});
		_syncRecordServiceMock.Setup(s => s.TryClaimPushOperationAsync(operationId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(() => new YnabPushOperationClaim(
				persisted! with { AttemptCount = ++attempt }, Guid.NewGuid()));
		_syncRecordServiceMock.Setup(s => s.CompletePushOperationAsync(
				operationId, It.IsAny<Guid>(), It.IsAny<YnabSyncStatus>(), It.IsAny<string?>(),
				It.IsAny<string?>(), It.IsAny<CancellationToken>()))
			.Callback<Guid, Guid, YnabSyncStatus, string?, string?, CancellationToken>(
				(_, _, status, remoteId, error, _) => persisted = persisted! with
				{
					SyncStatus = status,
					YnabTransactionId = remoteId,
					LastError = error,
				})
			.ReturnsAsync(true);
		_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(
				_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("response lost after acceptance"));

		PushYnabTransactionsResult first = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);
		first.Success.Should().BeFalse();
		persisted!.SyncStatus.Should().Be(YnabSyncStatus.Unknown);

		_splitCalculatorMock.Setup(s => s.ComputeWaterfallSplits(
				It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(),
				It.IsAny<Dictionary<string, string>>()))
			.Returns(new YnabSplitResult([
				new YnabTransactionSplit(_transactionId, -22000, [new YnabSubTransactionSplit("ynab-cat-1", -22000)]),
			]));
		_ynabApiClientMock.Setup(s => s.FindTransactionByImportIdAsync(
				_budgetId, persisted.Request!.AccountId, persisted.Request.ImportId!,
				persisted.Request.Date.AddDays(-1), It.IsAny<CancellationToken>()))
			.ReturnsAsync("ynab-accepted");

		PushYnabTransactionsResult retry = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		retry.Success.Should().BeTrue();
		retry.PushedTransactions.Should().ContainSingle().Which.YnabTransactionId.Should().Be("ynab-accepted");
		proposed.Should().HaveCount(2);
		proposed[1].Amount.Should().Be(-22000);
		proposed[1].ImportId.Should().Be(proposed[0].ImportId);
		_ynabApiClientMock.Verify(s => s.CreateTransactionAsync(
			_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ConcurrentRetries_OnlyClaimWinnerCanSend()
	{
		SetupHappyPath();
		Guid operationId = Guid.NewGuid();
		YnabPushOperation? operation = null;
		_syncRecordServiceMock.Setup(s => s.PreparePushOperationAsync(
				_transactionId, _budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<string>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync((Guid _, string _, YnabCreateTransactionRequest request, string sourceVersion, CancellationToken _) =>
				operation ??= new YnabPushOperation(
					operationId, YnabSyncStatus.Pending, request, sourceVersion, "hash", 0, null, null));
		int claims = 0;
		_syncRecordServiceMock.Setup(s => s.TryClaimPushOperationAsync(operationId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(() => Interlocked.Increment(ref claims) == 1
				? new YnabPushOperationClaim(operation! with { AttemptCount = 1 }, Guid.NewGuid())
				: null);

		PushYnabTransactionsResult[] results = await Task.WhenAll(
			_handler.Handle(new PushYnabTransactionsCommand(_receiptId), CancellationToken.None).AsTask(),
			_handler.Handle(new PushYnabTransactionsCommand(_receiptId), CancellationToken.None).AsTask());

		results.Should().ContainSingle(result => result.Success);
		results.Should().ContainSingle(result => !result.Success && result.Error!.Contains("claim changed"));
		_ynabApiClientMock.Verify(s => s.CreateTransactionAsync(
			_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_RemoteAcceptedButClaimCompletionIsLost_RetryReconcilesWithoutSecondSend()
	{
		SetupHappyPath();
		Guid operationId = Guid.NewGuid();
		YnabPushOperation? operation = null;
		int attempts = 0;
		_syncRecordServiceMock.Setup(s => s.PreparePushOperationAsync(
				_transactionId, _budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<string>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync((Guid _, string _, YnabCreateTransactionRequest request, string sourceVersion, CancellationToken _) =>
				operation ??= new YnabPushOperation(
					operationId, YnabSyncStatus.Pending, request, sourceVersion, "hash", 0, null, null));
		_syncRecordServiceMock.Setup(s => s.TryClaimPushOperationAsync(operationId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(() => new YnabPushOperationClaim(
				operation! with { AttemptCount = ++attempts }, Guid.NewGuid()));
		_syncRecordServiceMock.SetupSequence(s => s.CompletePushOperationAsync(
				It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<YnabSyncStatus>(), It.IsAny<string?>(),
				It.IsAny<string?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(false)
			.ReturnsAsync(true)
			.ReturnsAsync(true);

		PushYnabTransactionsResult first = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);
		first.Success.Should().BeTrue();
		first.Error.Should().Contain("claim was lost");
		_ynabApiClientMock.Setup(s => s.FindTransactionByImportIdAsync(
				_budgetId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync("ynab-tx-1");
		_syncRecordServiceMock.SetupSequence(s => s.GetByTransactionTypeAndBudgetAsync(
				_transactionId, YnabSyncType.TransactionPush, _budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabSyncRecordDto?)null)
			.ReturnsAsync(new YnabSyncRecordDto(
				operationId, _transactionId, "ynab-tx-1", _budgetId, _ynabAccountId,
				YnabSyncType.TransactionPush, YnabSyncStatus.Synced, DateTimeOffset.UtcNow, null,
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		PushYnabTransactionsResult retry = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		retry.Success.Should().BeTrue();
		_ynabApiClientMock.Verify(s => s.CreateTransactionAsync(
			_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
		_ynabApiClientMock.Verify(s => s.FindTransactionByImportIdAsync(
			_budgetId, operation!.Request!.AccountId, operation.Request.ImportId!,
			operation.Request.Date.AddDays(-1), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_WithCapturedBudget_DoesNotRereadSelectionAndScopesThePushToCapturedBudget()
	{
		SetupHappyPath();

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId, _budgetId), CancellationToken.None);

		result.Success.Should().BeTrue();
		_budgetSelectionServiceMock.Verify(
			s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()), Times.Never);
		_categoryMappingServiceMock.Verify(
			s => s.GetByBudgetIdAsync(_budgetId, It.IsAny<CancellationToken>()), Times.Once);
		_accountMappingServiceMock.Verify(
			s => s.GetByBudgetIdAsync(_budgetId, It.IsAny<CancellationToken>()), Times.Once);
		_syncRecordServiceMock.Verify(s => s.GetByTransactionTypeAndBudgetAsync(
			_transactionId, YnabSyncType.TransactionPush, _budgetId, It.IsAny<CancellationToken>()), Times.Once);
		_ynabApiClientMock.Verify(s => s.CreateTransactionAsync(
			_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_HappyPath_WritesPushSuccessEvent()
	{
		SetupHappyPath();

		await _handler.Handle(new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		_ynabSyncEventServiceMock.Verify(s => s.WriteAsync(
			YnabSyncEventType.Push,
			true,
			_receiptId,
			_transactionId,
			It.IsAny<int?>(),
			It.IsAny<string?>(),
			It.IsAny<string?>(),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_PushFails_WritesPushFailureEvent()
	{
		SetupHappyPath();
		_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("boom"));

		await _handler.Handle(new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		_ynabSyncEventServiceMock.Verify(s => s.WriteAsync(
			YnabSyncEventType.Push,
			false,
			_receiptId,
			_transactionId,
			It.IsAny<int?>(),
			It.IsAny<string?>(),
			It.IsAny<string?>(),
			It.IsAny<CancellationToken>()), Times.Once);
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
		_budgetSelectionServiceMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(_budgetId);

		// Only "Groceries" is mapped, "Gas" is not
		_categoryMappingServiceMock.Setup(s => s.GetByBudgetIdAsync(_budgetId, It.IsAny<CancellationToken>()))
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
		_syncRecordServiceMock.Setup(s => s.GetByTransactionTypeAndBudgetAsync(_transactionId, YnabSyncType.TransactionPush, _budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(Guid.NewGuid(), _transactionId, "ynab-tx-old", _budgetId, null, YnabSyncType.TransactionPush, YnabSyncStatus.Synced, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		// Bug 1 fix: already-synced transactions are skipped, not rejected
		result.Success.Should().BeTrue();
		result.PushedTransactions.Should().BeEmpty();
		_ynabApiClientMock.Verify(s => s.CreateTransactionAsync(It.IsAny<string>(), It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_PriorBudgetSyncAndMappings_DoNotSkipOrLeakIntoSelectedBudgetPush()
	{
		const string priorBudgetId = "budget-A";
		SetupHappyPath();
		_syncRecordServiceMock.Setup(s => s.GetByTransactionTypeAndBudgetAsync(
			_transactionId, YnabSyncType.TransactionPush, priorBudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(
				Guid.NewGuid(), _transactionId, "ynab-A-transaction", priorBudgetId, "ynab-A-account",
				YnabSyncType.TransactionPush, YnabSyncStatus.Synced, DateTimeOffset.UtcNow, null,
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
		_categoryMappingServiceMock.Setup(s => s.GetByBudgetIdAsync(_budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([
				new YnabCategoryMappingDto(Guid.NewGuid(), "Groceries", "ynab-B-category", "Current groceries", "Current group", _budgetId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			]);
		_accountMappingServiceMock.Setup(s => s.GetByBudgetIdAsync(_budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([
				new YnabAccountMappingDto(Guid.NewGuid(), _accountId, "ynab-B-account", "Current checking", _budgetId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			]);
		_splitCalculatorMock.Setup(s => s.ComputeWaterfallSplits(
			It.IsAny<ReceiptWithItems>(), It.IsAny<List<Domain.Core.Transaction>>(),
			It.IsAny<Dictionary<string, string>>()))
			.Returns(new YnabSplitResult([
				new YnabTransactionSplit(_transactionId, -11000, [new YnabSubTransactionSplit("ynab-B-category", -11000)]),
			]));
		YnabCreateTransactionRequest? sent = null;
		_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(
			_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.Callback<string, YnabCreateTransactionRequest, CancellationToken>((_, request, _) => sent = request)
			.ReturnsAsync(new YnabCreateTransactionResponse("ynab-B-transaction"));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeTrue();
		result.PushedTransactions.Should().ContainSingle();
		sent.Should().NotBeNull();
		sent!.AccountId.Should().Be("ynab-B-account");
		sent.CategoryId.Should().Be("ynab-B-category");
		_syncRecordServiceMock.Verify(s => s.GetByTransactionTypeAndBudgetAsync(
			_transactionId, YnabSyncType.TransactionPush, _budgetId, It.IsAny<CancellationToken>()), Times.Once);
		_syncRecordServiceMock.Verify(s => s.PreparePushOperationAsync(
			_transactionId, _budgetId, It.Is<YnabCreateTransactionRequest>(request =>
				request.AccountId == "ynab-B-account" && request.CategoryId == "ynab-B-category"),
			It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
		_categoryMappingServiceMock.Verify(s => s.GetByBudgetIdAsync(
			_budgetId, It.IsAny<CancellationToken>()), Times.Once);
		_accountMappingServiceMock.Verify(s => s.GetByBudgetIdAsync(
			_budgetId, It.IsAny<CancellationToken>()), Times.Once);
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
		_syncRecordServiceMock.Setup(s => s.GetByTransactionTypeAndBudgetAsync(_transactionId, YnabSyncType.TransactionPush, _budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(Guid.NewGuid(), _transactionId, "ynab-tx-old", _budgetId, null, YnabSyncType.TransactionPush, YnabSyncStatus.Synced, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
		// TX2 is not synced
		_syncRecordServiceMock.Setup(s => s.GetByTransactionTypeAndBudgetAsync(transactionId2, YnabSyncType.TransactionPush, _budgetId, It.IsAny<CancellationToken>()))
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

		// Only TX2 needs an immutable operation; the already-synced sibling remains skipped.
		_syncRecordServiceMock.Verify(s => s.PreparePushOperationAsync(
			_transactionId, It.IsAny<string>(), It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<string>(),
			It.IsAny<CancellationToken>()), Times.Never);
		_syncRecordServiceMock.Verify(s => s.PreparePushOperationAsync(
			transactionId2, _budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<string>(),
			It.IsAny<CancellationToken>()), Times.Once);
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
		_accountMappingServiceMock.Setup(s => s.GetByBudgetIdAsync(_budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.Error.Should().Contain("not mapped to YNAB accounts");
	}

	[Fact]
	public async Task Handle_AmbiguousYnabApiFailure_MarksOperationUnknownAndReturnsSafeRetryError()
	{
		SetupHappyPath();
		_ynabApiClientMock.Setup(s => s.CreateTransactionAsync(_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("YNAB API down"));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.Error.Should().Contain("YNAB API down");
		_syncRecordServiceMock.Verify(s => s.CompletePushOperationAsync(
			It.IsAny<Guid>(), It.IsAny<Guid>(), YnabSyncStatus.Unknown, null,
			It.Is<string>(message => message.Contains("YNAB API down") && message.Contains("may have accepted")),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_PrepareOperationFailure_ReturnsFalseWithoutCallingYnabApi()
	{
		SetupHappyPath();
		_syncRecordServiceMock.Setup(s => s.PreparePushOperationAsync(
				_transactionId, _budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<string>(),
				It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("DB connection lost"));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeFalse();
		result.Error.Should().Contain("DB connection lost");
		_ynabApiClientMock.Verify(s => s.CreateTransactionAsync(It.IsAny<string>(), It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_StatusUpdateLosesClaimAfterYnabSuccess_ReturnsUnknownWarning()
	{
		// If completing the durable claim throws after YNAB accepted the transaction,
		// the result should be Success=true with a warning, not Failed
		SetupHappyPath();
		_syncRecordServiceMock.SetupSequence(s => s.CompletePushOperationAsync(
				It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<YnabSyncStatus>(), It.IsAny<string?>(),
				It.IsAny<string?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(false)
			.ReturnsAsync(true);

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeTrue();
		result.PushedTransactions.Should().HaveCount(1);
		result.Error.Should().Contain("sync record update failed");
		result.Error.Should().Contain("claim was lost");
		result.OperationStatus.Should().Be(YnabSyncStatus.Unknown);
	}

	[Fact]
	public async Task Handle_ClaimUnavailableButAuthoritativeRecordIsSynced_ReturnsSyncedWithoutRemoteSend()
	{
		SetupHappyPath();
		Guid operationId = Guid.NewGuid();
		YnabCreateTransactionRequest snapshot = new(
			_ynabAccountId, DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), -11000,
			"memo", "Store", "ynab-cat-1", false, ImportId: YnabImportId.Generate(_transactionId));
		YnabPushOperation operation = new(
			operationId, YnabSyncStatus.Pending, snapshot, "source", "hash", 0, null, null);
		_syncRecordServiceMock.Setup(s => s.PreparePushOperationAsync(
				_transactionId, _budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<string>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(operation);
		_syncRecordServiceMock.Setup(s => s.TryClaimPushOperationAsync(operationId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabPushOperationClaim?)null);
		_syncRecordServiceMock.SetupSequence(s => s.GetByTransactionTypeAndBudgetAsync(
				_transactionId, YnabSyncType.TransactionPush, _budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabSyncRecordDto?)null)
			.ReturnsAsync(new YnabSyncRecordDto(
				operationId, _transactionId, "ynab-authoritative", _budgetId, _ynabAccountId,
				YnabSyncType.TransactionPush, YnabSyncStatus.Synced, DateTimeOffset.UtcNow, null,
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeTrue();
		result.OperationStatus.Should().Be(YnabSyncStatus.Synced);
		_ynabApiClientMock.Verify(s => s.CreateTransactionAsync(
			It.IsAny<string>(), It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_SyncsRecordOnSuccess()
	{
		SetupHappyPath();

		await _handler.Handle(new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		_syncRecordServiceMock.Verify(s => s.PreparePushOperationAsync(
			_transactionId, _budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<string>(),
			It.IsAny<CancellationToken>()), Times.Once);

		_syncRecordServiceMock.Verify(s => s.CompletePushOperationAsync(
			It.IsAny<Guid>(), It.IsAny<Guid>(), YnabSyncStatus.Synced, "ynab-tx-1", null,
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_HappyPath_PassesStableTransactionImportIdToCreateTransaction()
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
		capturedRequest.ImportId.Should().Be(YnabImportId.Generate(_transactionId));
	}

	[Fact]
	public async Task Handle_MultipleSplitsWithSameAmountAndDate_UsesDistinctTransactionIdentities()
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

		_syncRecordServiceMock.Setup(s => s.GetByTransactionTypeAndBudgetAsync(transactionId2, YnabSyncType.TransactionPush, _budgetId, It.IsAny<CancellationToken>()))
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
		capturedRequests[0].ImportId.Should().Be(YnabImportId.Generate(_transactionId));
		capturedRequests[1].ImportId.Should().Be(YnabImportId.Generate(transactionId2));
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

		// No immutable operation should be prepared since the error occurs before the push loop.
		_syncRecordServiceMock.Verify(s => s.PreparePushOperationAsync(
			It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<string>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}
}
