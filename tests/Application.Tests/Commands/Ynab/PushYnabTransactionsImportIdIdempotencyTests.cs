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

// RECEIPTS-803 — hardens the RECEIPTS-752 import_id idempotency fix.
//
// The existing regression tests (PushYnabTransactionsImportIdStabilityTests, etc.) MOCK the split
// calculator, so the two-pass occurrence-numbering guarantees are effectively asserted against a
// hand-authored split list. These tests instead drive the handler through the REAL
// Infrastructure.Services.YnabSplitCalculator (mocking only the external YNAB HTTP client), so the
// occurrence suffix scheme and its dependence on the calculator's stable ordering are exercised
// end-to-end. A future refactor that reintroduces the data-loss bug must break one of these.
public class PushYnabTransactionsImportIdIdempotencyTests
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

	// The REAL calculator — the point of these tests is that its stable ordering plus the handler's
	// two-pass occurrence numbering produce deterministic, unique import_ids.
	private readonly IYnabSplitCalculator _splitCalculator = new Infrastructure.Services.YnabSplitCalculator();

	private readonly PushYnabTransactionsCommandHandler _handler;

	private readonly Guid _receiptId = Guid.NewGuid();
	private readonly Guid _accountId = Guid.NewGuid();
	private readonly string _budgetId = "budget-123";
	private readonly string _ynabAccountId = "ynab-acc-1";
	private readonly DateOnly _date = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));

	public PushYnabTransactionsImportIdIdempotencyTests()
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
			_splitCalculator,
			Mock.Of<IYnabSyncEventService>(),
			Mock.Of<IYnabResponseContext>(),
			Mock.Of<Microsoft.Extensions.Logging.ILogger<PushYnabTransactionsCommandHandler>>());
	}

	private Domain.Core.Transaction MakeTransaction(Guid id, decimal amount)
		=> new(id, Guid.NewGuid(), new Money(amount), _date) { AccountId = _accountId, ReceiptId = _receiptId };

	// Wires the full pipeline for a fresh (nothing-yet-synced) push of the supplied transactions.
	// A single "Groceries" category totalling `categoryTotal` (== sum of transaction amounts, no tax
	// or adjustments) so the waterfall allocates cleanly. Returns the transactions in the exact order
	// they are supplied to the handler, which the stable calculator preserves for equal amounts.
	private List<Domain.Core.Transaction> SetupFreshPush(IReadOnlyList<(Guid Id, decimal Amount)> txSpecs, decimal categoryTotal)
	{
		Domain.Core.Receipt receipt = new(_receiptId, "Store", _date, new Money(0.00m));
		_receiptServiceMock.Setup(s => s.GetByIdAsync(_receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(receipt);

		List<Domain.Core.ReceiptItem> items =
		[
			new(Guid.NewGuid(), null, "Item1", 1, new Money(categoryTotal), new Money(categoryTotal), "Groceries", null),
		];
		_receiptItemServiceMock.Setup(s => s.GetByReceiptIdAsync(_receiptId, 0, 10000, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.ReceiptItem>(items, items.Count, 0, 10000));

		_adjustmentServiceMock.Setup(s => s.GetByReceiptIdAsync(_receiptId, 0, 10000, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.Adjustment>([], 0, 0, 10000));

		Domain.Core.Account account = new(_accountId, "Checking", true);
		List<Domain.Core.Transaction> transactions = [.. txSpecs.Select(spec => MakeTransaction(spec.Id, spec.Amount))];
		_transactionServiceMock.Setup(s => s.GetTransactionAccountsByReceiptIdAsync(_receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync([.. transactions.Select(t => new TransactionAccount { Transaction = t, Account = account })]);

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

		// Fresh state: nothing synced yet — every transaction gets a new Pending record.
		_syncRecordServiceMock.Setup(s => s.GetByTransactionAndTypeAsync(It.IsAny<Guid>(), YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabSyncRecordDto?)null);
		_syncRecordServiceMock.Setup(s => s.CreateAsync(It.IsAny<Guid>(), _budgetId, YnabSyncType.TransactionPush, It.IsAny<CancellationToken>()))
			.ReturnsAsync((Guid txId, string budget, YnabSyncType type, CancellationToken _) =>
				new YnabSyncRecordDto(Guid.NewGuid(), txId, null, budget, null, type, YnabSyncStatus.Pending, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		return transactions;
	}

	// Captures the import_id of every CreateTransaction call and returns a YNAB id derived from it
	// (guaranteed distinct per split, so the double-bind guard never trips on unrelated splits).
	private List<string> CaptureSuccessfulCreates()
	{
		List<string> captured = [];
		_ynabApiClientMock
			.Setup(s => s.CreateTransactionAsync(_budgetId, It.IsAny<YnabCreateTransactionRequest>(), It.IsAny<CancellationToken>()))
			.Callback<string, YnabCreateTransactionRequest, CancellationToken>((_, req, _) => captured.Add(req.ImportId!))
			.ReturnsAsync((string _, YnabCreateTransactionRequest req, CancellationToken _) =>
				new YnabCreateTransactionResponse($"ynab::{req.ImportId}"));
		return captured;
	}

	// ── Test 3: 3+ same-amount / same-date splits keep unique occurrence suffixes ───────────────

	[Fact]
	public async Task Handle_ThreeSameAmountSameDateSplits_AssignsDistinctSequentialImportIds()
	{
		// Three $50.00 transactions on the same date, one $150 category. Their import_ids share
		// amount+date and so must be disambiguated by occurrence 1, 2 AND 3 — exercising suffix
		// uniqueness at index 3+, not just the two-transaction case the other tests cover.
		Guid txA = Guid.NewGuid();
		Guid txB = Guid.NewGuid();
		Guid txC = Guid.NewGuid();
		SetupFreshPush([(txA, 50.00m), (txB, 50.00m), (txC, 50.00m)], categoryTotal: 150.00m);
		List<string> capturedImportIds = CaptureSuccessfulCreates();

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeTrue();
		result.Error.Should().BeNull();

		const long milliunits = -50000; // $50 outflow, negated for YNAB
		string occ1 = YnabImportId.Generate(milliunits, _date, _receiptId, 1);
		string occ2 = YnabImportId.Generate(milliunits, _date, _receiptId, 2);
		string occ3 = YnabImportId.Generate(milliunits, _date, _receiptId, 3);

		capturedImportIds.Should().HaveCount(3);
		capturedImportIds.Should().OnlyHaveUniqueItems("three identical amount+date splits must not collide on import_id");
		capturedImportIds.Should().BeEquivalentTo([occ1, occ2, occ3]);
		capturedImportIds.Should().Contain(id => id.EndsWith(":3"), "occurrence numbering must remain unique at index 3+");
	}

	// ── Test 4: first push — two-pass numbering matches the old single-pass ─────────────────────

	[Fact]
	public async Task Handle_FirstPush_TwoPassImportIdsMatchSinglePass()
	{
		// The RECEIPTS-752 fix moved import_id assignment into a first pass that counts EVERY split
		// (including ones that will be skipped as already-synced) before pushing. On a first push
		// nothing is skipped, so the resulting import_ids must be byte-for-byte identical to what the
		// old assign-as-you-push single pass produced — no occurrence drift for the common case.
		Guid tx1 = Guid.NewGuid();
		Guid tx2 = Guid.NewGuid();
		Guid tx3 = Guid.NewGuid();
		// A same-amount pair ($50, $50) plus a distinct amount ($30); $130 category total.
		List<Domain.Core.Transaction> transactions =
			SetupFreshPush([(tx1, 50.00m), (tx2, 50.00m), (tx3, 30.00m)], categoryTotal: 130.00m);
		List<string> capturedImportIds = CaptureSuccessfulCreates();

		// Independently compute the OLD single-pass import_ids: run the same real calculator, then walk
		// its split order assigning occurrences as we go (exactly what the pre-fix handler did when
		// nothing was skipped).
		ReceiptWithItems receiptWithItems = new()
		{
			Receipt = new Domain.Core.Receipt(_receiptId, "Store", _date, new Money(0.00m)),
			Items = [new(Guid.NewGuid(), null, "Item1", 1, new Money(130.00m), new Money(130.00m), "Groceries", null)],
			Adjustments = [],
		};
		Dictionary<string, string> categoryToYnabId = new() { ["Groceries"] = "ynab-cat-1" };
		YnabSplitResult splits = _splitCalculator.ComputeWaterfallSplits(receiptWithItems, transactions, categoryToYnabId);

		Dictionary<(long, DateOnly), int> occurrences = [];
		List<string> expectedSinglePass = [];
		foreach (YnabTransactionSplit split in splits.TransactionSplits)
		{
			DateOnly txDate = transactions.First(t => t.Id == split.LocalTransactionId).Date;
			(long, DateOnly) key = (split.TotalMilliunits, txDate);
			int occurrence = occurrences.TryGetValue(key, out int current) ? current + 1 : 1;
			occurrences[key] = occurrence;
			expectedSinglePass.Add(YnabImportId.Generate(split.TotalMilliunits, txDate, _receiptId, occurrence));
		}

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		result.Success.Should().BeTrue();
		result.Error.Should().BeNull();
		capturedImportIds.Should().HaveCount(3);

		// The two-pass first push produces exactly the single-pass import_id set (no drift), and the
		// $50 pair still resolves to occurrences 1 and 2 while the $30 transaction is occurrence 1.
		capturedImportIds.Should().BeEquivalentTo(expectedSinglePass);
		capturedImportIds.Should().BeEquivalentTo(
		[
			YnabImportId.Generate(-50000, _date, _receiptId, 1),
			YnabImportId.Generate(-50000, _date, _receiptId, 2),
			YnabImportId.Generate(-30000, _date, _receiptId, 1),
		]);
	}

	// ── Test 2: positive recovery — the double-bind guard does not over-reject ──────────────────

	[Fact]
	public async Task Handle_409RecoversOwnDistinctYnabId_BindsAndReportsSuccess()
	{
		// Two same-amount ($50) transactions, both fresh. The occurrence-1 split creates successfully
		// (binding "ynab-own-1"); the occurrence-2 split 409s and recovery resolves to its OWN, still
		// unbound YNAB id ("ynab-own-2"). Because that id is NOT already bound to a sibling, the
		// double-bind guard must let it through — proving the guard doesn't produce a false positive
		// and reject a legitimate recovery.
		Guid txA = Guid.NewGuid();
		Guid txB = Guid.NewGuid();
		SetupFreshPush([(txA, 50.00m), (txB, 50.00m)], categoryTotal: 100.00m);

		// The occurrence-2 split conflicts; every other split creates cleanly.
		_ynabApiClientMock
			.Setup(s => s.CreateTransactionAsync(_budgetId, It.Is<YnabCreateTransactionRequest>(r => r.ImportId!.EndsWith(":2")), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("conflict", null, HttpStatusCode.Conflict));
		_ynabApiClientMock
			.Setup(s => s.CreateTransactionAsync(_budgetId, It.Is<YnabCreateTransactionRequest>(r => !r.ImportId!.EndsWith(":2")), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabCreateTransactionResponse("ynab-own-1"));

		// Recovery resolves to the conflicting split's OWN distinct id (not the sibling's "ynab-own-1").
		_ynabApiClientMock
			.Setup(s => s.FindTransactionByImportIdAsync(_budgetId, _ynabAccountId, It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync("ynab-own-2");

		PushYnabTransactionsResult result = await _handler.Handle(
			new PushYnabTransactionsCommand(_receiptId), CancellationToken.None);

		// Both transactions succeed — the recovered own-id is bound, not rejected.
		result.Success.Should().BeTrue();
		result.Error.Should().BeNull();
		result.PushedTransactions.Should().HaveCount(2);
		result.PushedTransactions.Select(p => p.YnabTransactionId).Should().BeEquivalentTo(["ynab-own-1", "ynab-own-2"]);

		// The conflicting split was stamped Synced to its own recovered id ...
		_syncRecordServiceMock.Verify(s => s.UpdateStatusAsync(
			It.IsAny<Guid>(), YnabSyncStatus.Synced, "ynab-own-2", null, It.IsAny<CancellationToken>()), Times.Once);
		// ... and nothing was marked Failed (the guard did not over-reject).
		_syncRecordServiceMock.Verify(s => s.UpdateStatusAsync(
			It.IsAny<Guid>(), YnabSyncStatus.Failed, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}
