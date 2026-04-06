using Application.Interfaces.Services;
using Application.Models.Ynab;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace Infrastructure.Tests.Services;

public class YnabMemoSyncServiceTests
{
	private readonly Mock<IYnabApiClient> _ynabClientMock = new();
	private readonly Mock<IYnabBudgetSelectionService> _budgetSelectionMock = new();
	private readonly Mock<IYnabSyncRecordService> _syncRecordServiceMock = new();
	private readonly Mock<ITransactionRepository> _transactionRepoMock = new();
	private readonly Mock<IReceiptRepository> _receiptRepoMock = new();
	private readonly Mock<ILogger<YnabMemoSyncService>> _loggerMock = new();
	private readonly YnabMemoSyncService _service;

	private const string BudgetId = "budget-1";
	private static readonly Guid ReceiptId = Guid.NewGuid();
	private static readonly Guid TransactionId = Guid.NewGuid();

	public YnabMemoSyncServiceTests()
	{
		_service = new YnabMemoSyncService(
			_ynabClientMock.Object,
			_budgetSelectionMock.Object,
			_syncRecordServiceMock.Object,
			_transactionRepoMock.Object,
			_receiptRepoMock.Object,
			_loggerMock.Object);
	}

	#region FormatMemo Tests

	[Fact]
	public void FormatMemo_EmptyExisting_ReturnsReceiptTag()
	{
		string result = YnabMemoSyncService.FormatMemo(null, "/receipts/abc-123");
		result.Should().Be("Receipt: /receipts/abc-123");
	}

	[Fact]
	public void FormatMemo_WithExistingMemo_AppendsSeparatorAndTag()
	{
		string result = YnabMemoSyncService.FormatMemo("Groceries run", "/receipts/abc-123");
		result.Should().Be("Groceries run | Receipt: /receipts/abc-123");
	}

	[Fact]
	public void FormatMemo_CombinedExceedsLimit_TruncatesExisting()
	{
		string longMemo = new('A', 180);
		string result = YnabMemoSyncService.FormatMemo(longMemo, "/receipts/abc-123");
		result.Length.Should().BeLessThanOrEqualTo(200);
		result.Should().EndWith("Receipt: /receipts/abc-123");
	}

	[Fact]
	public void FormatMemo_ExactlyAtLimit_DoesNotTruncate()
	{
		string tag = "Receipt: /receipts/abc-123";
		int separator = " | ".Length;
		int available = 200 - tag.Length - separator;
		string memo = new('X', available);
		string result = YnabMemoSyncService.FormatMemo(memo, "/receipts/abc-123");
		result.Length.Should().Be(200);
		result.Should().Contain(memo);
	}

	[Fact]
	public void FormatMemo_WhitespaceOnlyExisting_TreatsAsEmpty()
	{
		string result = YnabMemoSyncService.FormatMemo("   ", "/receipts/abc-123");
		result.Should().Be("Receipt: /receipts/abc-123");
	}

	#endregion

	#region IsPayeeMatch Tests

	[Fact]
	public void IsPayeeMatch_ExactMatch_ReturnsTrue()
	{
		YnabMemoSyncService.IsPayeeMatch("Walmart", "Walmart").Should().BeTrue();
	}

	[Fact]
	public void IsPayeeMatch_CaseInsensitive_ReturnsTrue()
	{
		YnabMemoSyncService.IsPayeeMatch("WALMART", "walmart").Should().BeTrue();
	}

	[Fact]
	public void IsPayeeMatch_ContainsMatch_ReturnsTrue()
	{
		YnabMemoSyncService.IsPayeeMatch("Walmart Supercenter", "Walmart").Should().BeTrue();
	}

	[Fact]
	public void IsPayeeMatch_ReverseContains_ReturnsTrue()
	{
		YnabMemoSyncService.IsPayeeMatch("Walmart", "Walmart Supercenter #1234").Should().BeTrue();
	}

	[Fact]
	public void IsPayeeMatch_NullYnabPayee_ReturnsFalse()
	{
		YnabMemoSyncService.IsPayeeMatch("Walmart", null).Should().BeFalse();
	}

	[Fact]
	public void IsPayeeMatch_EmptyYnabPayee_ReturnsFalse()
	{
		YnabMemoSyncService.IsPayeeMatch("Walmart", "  ").Should().BeFalse();
	}

	[Fact]
	public void IsPayeeMatch_TotallyDifferent_ReturnsFalse()
	{
		YnabMemoSyncService.IsPayeeMatch("Walmart", "Starbucks Coffee").Should().BeFalse();
	}

	[Fact]
	public void IsPayeeMatch_SimilarNames_ReturnsTrue()
	{
		// "Walmart" vs "Wal-Mart" should have high trigram similarity
		YnabMemoSyncService.IsPayeeMatch("Walmart", "Wal-Mart").Should().BeTrue();
	}

	#endregion

	#region TrigramSimilarity Tests

	[Fact]
	public void TrigramSimilarity_IdenticalStrings_ReturnsOne()
	{
		YnabMemoSyncService.TrigramSimilarity("ABC", "ABC").Should().BeGreaterThanOrEqualTo(0.99);
	}

	[Fact]
	public void TrigramSimilarity_CompletelyDifferent_ReturnsLow()
	{
		double result = YnabMemoSyncService.TrigramSimilarity("ABCDEF", "XYZWVQ");
		result.Should().BeLessThan(0.3);
	}

	[Fact]
	public void TrigramSimilarity_EmptyString_ReturnsZero()
	{
		YnabMemoSyncService.TrigramSimilarity("", "ABC").Should().Be(0.0);
	}

	#endregion

	#region Sign Convention Tests

	[Fact]
	public async Task SyncMemosByReceipt_PositiveLocalAmount_NegatesForYnabMatching()
	{
		// Arrange
		SetupBudgetSelection(BudgetId);
		ReceiptEntity receipt = CreateReceipt("Walmart");
		TransactionEntity transaction = CreateTransaction(receipt.Id, 25.50m);

		SetupReceiptRepo(receipt);
		SetupTransactionRepo(receipt.Id, [transaction]);
		SetupNoExistingSyncRecord(transaction.Id);

		// YNAB amount should be negated: 25.50 * 1000 = 25500, negated = -25500
		YnabTransaction ynabTx = CreateYnabTransaction("yt-1", transaction.Date, -25500, "Walmart");
		_ynabClientMock
			.Setup(c => c.GetTransactionsByDateAsync(BudgetId, transaction.Date, It.IsAny<CancellationToken>()))
			.ReturnsAsync([ynabTx]);

		SetupSyncRecordCreation();
		SetupSyncRecordUpdate();
		_ynabClientMock
			.Setup(c => c.UpdateTransactionMemoAsync(BudgetId, "yt-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		// Act
		List<YnabMemoSyncResult> results = await _service.SyncMemosByReceiptAsync(receipt.Id, CancellationToken.None);

		// Assert
		results.Should().ContainSingle();
		results[0].Outcome.Should().Be(YnabMemoSyncOutcome.Synced);
		results[0].YnabTransactionId.Should().Be("yt-1");
	}

	[Fact]
	public async Task SyncMemosByReceipt_WrongSign_NoMatch()
	{
		// Arrange: local amount 25.50, YNAB amount +25500 (same sign = wrong)
		SetupBudgetSelection(BudgetId);
		ReceiptEntity receipt = CreateReceipt("Walmart");
		TransactionEntity transaction = CreateTransaction(receipt.Id, 25.50m);

		SetupReceiptRepo(receipt);
		SetupTransactionRepo(receipt.Id, [transaction]);
		SetupNoExistingSyncRecord(transaction.Id);

		YnabTransaction ynabTx = CreateYnabTransaction("yt-1", transaction.Date, 25500, "Walmart");
		_ynabClientMock
			.Setup(c => c.GetTransactionsByDateAsync(BudgetId, transaction.Date, It.IsAny<CancellationToken>()))
			.ReturnsAsync([ynabTx]);

		// Act
		List<YnabMemoSyncResult> results = await _service.SyncMemosByReceiptAsync(receipt.Id, CancellationToken.None);

		// Assert
		results.Should().ContainSingle();
		results[0].Outcome.Should().Be(YnabMemoSyncOutcome.NoMatch);
	}

	#endregion

	#region Currency Guard Tests

	[Fact]
	public async Task SyncMemosByReceipt_NonUsdCurrency_ReturnsCurrencySkipped()
	{
		// Arrange
		SetupBudgetSelection(BudgetId);
		ReceiptEntity receipt = CreateReceipt("Store");
		TransactionEntity transaction = new()
		{
			Id = TransactionId,
			ReceiptId = receipt.Id,
			Amount = 10.00m,
			AmountCurrency = (Currency)999, // Non-USD
			Date = DateOnly.FromDateTime(DateTime.Today),
		};

		SetupReceiptRepo(receipt);
		SetupTransactionRepo(receipt.Id, [transaction]);

		// Act
		List<YnabMemoSyncResult> results = await _service.SyncMemosByReceiptAsync(receipt.Id, CancellationToken.None);

		// Assert
		results.Should().ContainSingle();
		results[0].Outcome.Should().Be(YnabMemoSyncOutcome.CurrencySkipped);
	}

	#endregion

	#region Matching Algorithm Tests

	[Fact]
	public async Task SyncMemosByReceipt_AmbiguousMatch_ReturnsAmbiguousWithCandidates()
	{
		// Arrange
		SetupBudgetSelection(BudgetId);
		ReceiptEntity receipt = CreateReceipt("Walmart");
		TransactionEntity transaction = CreateTransaction(receipt.Id, 50.00m);

		SetupReceiptRepo(receipt);
		SetupTransactionRepo(receipt.Id, [transaction]);
		SetupNoExistingSyncRecord(transaction.Id);

		// Two YNAB transactions with same date+amount and fuzzy payee match
		YnabTransaction ynabTx1 = CreateYnabTransaction("yt-1", transaction.Date, -50000, "Walmart");
		YnabTransaction ynabTx2 = CreateYnabTransaction("yt-2", transaction.Date, -50000, "Walmart Supercenter");

		_ynabClientMock
			.Setup(c => c.GetTransactionsByDateAsync(BudgetId, transaction.Date, It.IsAny<CancellationToken>()))
			.ReturnsAsync([ynabTx1, ynabTx2]);

		// Act
		List<YnabMemoSyncResult> results = await _service.SyncMemosByReceiptAsync(receipt.Id, CancellationToken.None);

		// Assert
		results.Should().ContainSingle();
		results[0].Outcome.Should().Be(YnabMemoSyncOutcome.Ambiguous);
		results[0].AmbiguousCandidates.Should().HaveCount(2);
	}

	[Fact]
	public async Task SyncMemosByReceipt_NoMatch_ReturnsNoMatch()
	{
		// Arrange
		SetupBudgetSelection(BudgetId);
		ReceiptEntity receipt = CreateReceipt("Walmart");
		TransactionEntity transaction = CreateTransaction(receipt.Id, 25.50m);

		SetupReceiptRepo(receipt);
		SetupTransactionRepo(receipt.Id, [transaction]);
		SetupNoExistingSyncRecord(transaction.Id);

		// Empty YNAB transactions
		_ynabClientMock
			.Setup(c => c.GetTransactionsByDateAsync(BudgetId, transaction.Date, It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		// Act
		List<YnabMemoSyncResult> results = await _service.SyncMemosByReceiptAsync(receipt.Id, CancellationToken.None);

		// Assert
		results.Should().ContainSingle();
		results[0].Outcome.Should().Be(YnabMemoSyncOutcome.NoMatch);
	}

	#endregion

	#region Idempotency Tests

	[Fact]
	public async Task SyncMemosByReceipt_AlreadySynced_ReturnsAlreadySynced()
	{
		// Arrange
		SetupBudgetSelection(BudgetId);
		ReceiptEntity receipt = CreateReceipt("Walmart");
		TransactionEntity transaction = CreateTransaction(receipt.Id, 25.50m);

		SetupReceiptRepo(receipt);
		SetupTransactionRepo(receipt.Id, [transaction]);

		// Already synced record exists
		_syncRecordServiceMock
			.Setup(s => s.GetByTransactionAndTypeAsync(transaction.Id, YnabSyncType.MemoUpdate, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabSyncRecordDto(
				Guid.NewGuid(), transaction.Id, "yt-1", BudgetId, null,
				YnabSyncType.MemoUpdate, YnabSyncStatus.Synced,
				DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		// Act
		List<YnabMemoSyncResult> results = await _service.SyncMemosByReceiptAsync(receipt.Id, CancellationToken.None);

		// Assert
		results.Should().ContainSingle();
		results[0].Outcome.Should().Be(YnabMemoSyncOutcome.AlreadySynced);
		// Should NOT call YNAB API
		_ynabClientMock.Verify(c => c.GetTransactionsByDateAsync(
			It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task SyncMemosByReceipt_MemoAlreadyContainsLink_ReturnsAlreadySynced()
	{
		// Arrange
		SetupBudgetSelection(BudgetId);
		ReceiptEntity receipt = CreateReceipt("Walmart");
		TransactionEntity transaction = CreateTransaction(receipt.Id, 25.50m);

		SetupReceiptRepo(receipt);
		SetupTransactionRepo(receipt.Id, [transaction]);
		SetupNoExistingSyncRecord(transaction.Id);

		string existingMemo = $"Some memo | Receipt: /receipts/{receipt.Id}";
		YnabTransaction ynabTx = new("yt-1", transaction.Date, -25500, existingMemo, "cleared", true, "acc-1", null, "Walmart");

		_ynabClientMock
			.Setup(c => c.GetTransactionsByDateAsync(BudgetId, transaction.Date, It.IsAny<CancellationToken>()))
			.ReturnsAsync([ynabTx]);

		SetupSyncRecordCreation();
		SetupSyncRecordUpdate();

		// Act
		List<YnabMemoSyncResult> results = await _service.SyncMemosByReceiptAsync(receipt.Id, CancellationToken.None);

		// Assert
		results.Should().ContainSingle();
		results[0].Outcome.Should().Be(YnabMemoSyncOutcome.AlreadySynced);
		// Should NOT call UpdateTransactionMemoAsync
		_ynabClientMock.Verify(c => c.UpdateTransactionMemoAsync(
			It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	#endregion

	#region Error Handling Tests

	[Fact]
	public async Task SyncMemosByReceipt_NoBudgetSelected_ReturnsFailed()
	{
		// Arrange
		SetupBudgetSelection(null);

		// Act
		List<YnabMemoSyncResult> results = await _service.SyncMemosByReceiptAsync(ReceiptId, CancellationToken.None);

		// Assert
		results.Should().ContainSingle();
		results[0].Outcome.Should().Be(YnabMemoSyncOutcome.Failed);
		results[0].Error.Should().Contain("No YNAB budget selected");
	}

	[Fact]
	public async Task SyncMemosByReceipt_ReceiptNotFound_ReturnsFailed()
	{
		// Arrange
		SetupBudgetSelection(BudgetId);
		_receiptRepoMock
			.Setup(r => r.GetByIdAsync(ReceiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((ReceiptEntity?)null);

		// Act
		List<YnabMemoSyncResult> results = await _service.SyncMemosByReceiptAsync(ReceiptId, CancellationToken.None);

		// Assert
		results.Should().ContainSingle();
		results[0].Outcome.Should().Be(YnabMemoSyncOutcome.Failed);
		results[0].Error.Should().Contain("Receipt not found");
	}

	[Fact]
	public async Task SyncMemosByReceipt_YnabApiFails_ReturnsFailed()
	{
		// Arrange
		SetupBudgetSelection(BudgetId);
		ReceiptEntity receipt = CreateReceipt("Walmart");
		TransactionEntity transaction = CreateTransaction(receipt.Id, 25.50m);

		SetupReceiptRepo(receipt);
		SetupTransactionRepo(receipt.Id, [transaction]);
		SetupNoExistingSyncRecord(transaction.Id);

		_ynabClientMock
			.Setup(c => c.GetTransactionsByDateAsync(BudgetId, transaction.Date, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("YNAB API error"));

		// Act
		List<YnabMemoSyncResult> results = await _service.SyncMemosByReceiptAsync(receipt.Id, CancellationToken.None);

		// Assert
		results.Should().ContainSingle();
		results[0].Outcome.Should().Be(YnabMemoSyncOutcome.Failed);
		results[0].Error.Should().Contain("YNAB API error");
	}

	#endregion

	#region Bulk Sync Tests

	[Fact]
	public async Task SyncMemosBulk_MultipleReceipts_ReturnsResultsForAll()
	{
		// Arrange
		SetupBudgetSelection(BudgetId);
		ReceiptEntity receipt1 = CreateReceipt("Walmart");
		ReceiptEntity receipt2 = CreateReceipt("Target");
		TransactionEntity tx1 = CreateTransaction(receipt1.Id, 25.50m);
		TransactionEntity tx2 = CreateTransaction(receipt2.Id, 42.00m);

		_receiptRepoMock
			.Setup(r => r.GetByIdAsync(receipt1.Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(receipt1);
		_receiptRepoMock
			.Setup(r => r.GetByIdAsync(receipt2.Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(receipt2);

		_transactionRepoMock
			.Setup(r => r.GetWithAccountByReceiptIdAsync(receipt1.Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync([tx1]);
		_transactionRepoMock
			.Setup(r => r.GetWithAccountByReceiptIdAsync(receipt2.Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync([tx2]);

		SetupNoExistingSyncRecordForAny();

		_ynabClientMock
			.Setup(c => c.GetTransactionsByDateAsync(BudgetId, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		// Act
		List<YnabMemoSyncResult> results = await _service.SyncMemosBulkAsync(
			[receipt1.Id, receipt2.Id], CancellationToken.None);

		// Assert
		results.Should().HaveCount(2);
	}

	#endregion

	#region Resolve Tests

	[Fact]
	public async Task ResolveMemoSync_ValidTransaction_Syncs()
	{
		// Arrange
		SetupBudgetSelection(BudgetId);
		ReceiptEntity receipt = CreateReceipt("Walmart");
		TransactionEntity transaction = CreateTransaction(receipt.Id, 25.50m);

		_transactionRepoMock
			.Setup(r => r.GetByIdAsync(transaction.Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(transaction);
		SetupReceiptRepo(receipt);

		YnabTransaction ynabTx = CreateYnabTransaction("yt-1", transaction.Date, -25500, "Walmart");
		_ynabClientMock
			.Setup(c => c.GetTransactionAsync(BudgetId, "yt-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(ynabTx);

		SetupSyncRecordCreation();
		SetupSyncRecordUpdate();
		SetupNoExistingSyncRecord(transaction.Id);
		_ynabClientMock
			.Setup(c => c.UpdateTransactionMemoAsync(BudgetId, "yt-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		// Act
		YnabMemoSyncResult result = await _service.ResolveMemoSyncAsync(
			transaction.Id, "yt-1", CancellationToken.None);

		// Assert
		result.Outcome.Should().Be(YnabMemoSyncOutcome.Synced);
		result.YnabTransactionId.Should().Be("yt-1");
	}

	#endregion

	#region Helpers

	private void SetupBudgetSelection(string? budgetId)
	{
		_budgetSelectionMock
			.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(budgetId);
	}

	private ReceiptEntity CreateReceipt(string location)
	{
		return new ReceiptEntity
		{
			Id = Guid.NewGuid(),
			Location = location,
			Date = DateOnly.FromDateTime(DateTime.Today),
		};
	}

	private TransactionEntity CreateTransaction(Guid receiptId, decimal amount)
	{
		return new TransactionEntity
		{
			Id = Guid.NewGuid(),
			ReceiptId = receiptId,
			Amount = amount,
			AmountCurrency = Currency.USD,
			Date = DateOnly.FromDateTime(DateTime.Today),
		};
	}

	private YnabTransaction CreateYnabTransaction(string id, DateOnly date, long amount, string? payeeName)
	{
		return new YnabTransaction(id, date, amount, null, "cleared", true, "acc-1", null, payeeName);
	}

	private void SetupReceiptRepo(ReceiptEntity receipt)
	{
		_receiptRepoMock
			.Setup(r => r.GetByIdAsync(receipt.Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(receipt);
	}

	private void SetupTransactionRepo(Guid receiptId, List<TransactionEntity> transactions)
	{
		_transactionRepoMock
			.Setup(r => r.GetWithAccountByReceiptIdAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(transactions);
	}

	private void SetupNoExistingSyncRecord(Guid transactionId)
	{
		_syncRecordServiceMock
			.Setup(s => s.GetByTransactionAndTypeAsync(transactionId, YnabSyncType.MemoUpdate, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabSyncRecordDto?)null);
	}

	private void SetupNoExistingSyncRecordForAny()
	{
		_syncRecordServiceMock
			.Setup(s => s.GetByTransactionAndTypeAsync(It.IsAny<Guid>(), YnabSyncType.MemoUpdate, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabSyncRecordDto?)null);
	}

	private void SetupSyncRecordCreation()
	{
		_syncRecordServiceMock
			.Setup(s => s.CreateAsync(It.IsAny<Guid>(), It.IsAny<string>(), YnabSyncType.MemoUpdate, It.IsAny<CancellationToken>()))
			.ReturnsAsync((Guid txId, string budget, YnabSyncType type, CancellationToken _) =>
				new YnabSyncRecordDto(
					Guid.NewGuid(), txId, null, budget, null,
					type, YnabSyncStatus.Pending,
					null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
	}

	private void SetupSyncRecordUpdate()
	{
		_syncRecordServiceMock
			.Setup(s => s.UpdateStatusAsync(It.IsAny<Guid>(), It.IsAny<YnabSyncStatus>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
	}

	#endregion
}
