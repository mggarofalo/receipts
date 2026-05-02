using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ocr;
using Domain.Core;
using FluentAssertions;
using Infrastructure.Services;
using Moq;

namespace Infrastructure.Tests.Services;

public class ProposedTransactionResolverTests
{
	private readonly Mock<ICardService> _cardService = new();
	private readonly ProposedTransactionResolver _resolver;

	private static readonly Guid VisaCardId = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid VisaAccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
	private static readonly Guid AmexCardId = Guid.Parse("33333333-3333-3333-3333-333333333333");
	private static readonly Guid AmexAccountId = Guid.Parse("44444444-4444-4444-4444-444444444444");
	private static readonly Guid AltVisaCardId = Guid.Parse("55555555-5555-5555-5555-555555555555");
	private static readonly Guid AltVisaAccountId = Guid.Parse("66666666-6666-6666-6666-666666666666");
	private static readonly DateOnly ReceiptDate = new(2026, 4, 10);

	public ProposedTransactionResolverTests()
	{
		_resolver = new ProposedTransactionResolver(_cardService.Object);
	}

	private void StubActiveCards(params Card[] cards)
	{
		PagedResult<Card> paged = new([.. cards], cards.Length, 0, int.MaxValue);
		_cardService
			.Setup(s => s.GetAllAsync(0, int.MaxValue, It.IsAny<SortParams>(), true, It.IsAny<CancellationToken>()))
			.ReturnsAsync(paged);
	}

	private static FieldConfidence<DateOnly> ReceiptDateField =>
		FieldConfidence<DateOnly>.High(ReceiptDate);

	[Fact]
	public async Task ResolveAsync_EmptyPayments_ReturnsEmptyList_AndDoesNotQueryCards()
	{
		// Arrange — no payments to resolve. The resolver must short-circuit so we don't
		// pay the cost of materializing the active-card list when there's nothing to do.

		// Act
		List<ProposedTransaction> result = await _resolver.ResolveAsync(
			[], ReceiptDateField, CancellationToken.None);

		// Assert
		result.Should().BeEmpty();
		_cardService.Verify(
			s => s.GetAllAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<SortParams>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task ResolveAsync_SingleMatchByLastFour_PopulatesCardIdAndAccountIdWithHighConfidence()
	{
		// Arrange — happy path: one VLM payment with last-four "3409", one user card whose
		// CardCode is "3409". The resolved row should carry both Ids with high confidence
		// so the wizard pre-fills without asking the user to disambiguate.
		Card card = new(VisaCardId, "3409", "Chase Visa", VisaAccountId);
		StubActiveCards(card);

		List<ParsedPayment> payments =
		[
			new ParsedPayment(
				FieldConfidence<string?>.High("VISA"),
				FieldConfidence<decimal?>.High(70.43m),
				FieldConfidence<string?>.High("3409")),
		];

		// Act
		List<ProposedTransaction> result = await _resolver.ResolveAsync(
			payments, ReceiptDateField, CancellationToken.None);

		// Assert
		result.Should().HaveCount(1);
		ProposedTransaction txn = result[0];
		txn.CardId.Value.Should().Be(VisaCardId);
		txn.CardId.Confidence.Should().Be(ConfidenceLevel.High);
		txn.AccountId.Value.Should().Be(VisaAccountId);
		txn.AccountId.Confidence.Should().Be(ConfidenceLevel.High);
		txn.Amount.Value.Should().Be(70.43m);
		txn.Amount.Confidence.Should().Be(ConfidenceLevel.High);
		txn.Date.Value.Should().Be(ReceiptDate);
		txn.MethodSnapshot.Value.Should().Be("VISA");
	}

	[Fact]
	public async Task ResolveAsync_NoMatchByLastFour_LeavesCardIdNullWithNoneConfidence()
	{
		// Arrange — VLM extracted last-four "3409" but the user has no card with that
		// code. The resolver MUST NOT pick a different card; it leaves the row with
		// None confidence so the wizard surfaces no badge and forces manual selection.
		Card card = new(AmexCardId, "9876", "Amex", AmexAccountId);
		StubActiveCards(card);

		List<ParsedPayment> payments =
		[
			new ParsedPayment(
				FieldConfidence<string?>.High("VISA"),
				FieldConfidence<decimal?>.High(70.43m),
				FieldConfidence<string?>.High("3409")),
		];

		// Act
		List<ProposedTransaction> result = await _resolver.ResolveAsync(
			payments, ReceiptDateField, CancellationToken.None);

		// Assert
		result.Should().HaveCount(1);
		ProposedTransaction txn = result[0];
		txn.CardId.Value.Should().BeNull();
		txn.CardId.Confidence.Should().Be(ConfidenceLevel.None);
		txn.AccountId.Value.Should().BeNull();
		txn.AccountId.Confidence.Should().Be(ConfidenceLevel.None);
		// Amount and date still propagate so the wizard pre-fills what it can.
		txn.Amount.Value.Should().Be(70.43m);
		txn.Date.Value.Should().Be(ReceiptDate);
	}

	[Fact]
	public async Task ResolveAsync_AmbiguousLastFour_LeavesCardIdNullWithLowConfidence()
	{
		// Arrange — RECEIPTS-657 acceptance: when two active cards share the same last
		// four, we MUST NOT silently pick one. Picking the wrong card would file a
		// transaction against the wrong account. Instead we surface Low confidence
		// so the wizard renders a review chip and forces the user to disambiguate.
		Card card1 = new(VisaCardId, "3409", "Chase Visa", VisaAccountId);
		Card card2 = new(AltVisaCardId, "3409", "Chase Visa Spouse", AltVisaAccountId);
		StubActiveCards(card1, card2);

		List<ParsedPayment> payments =
		[
			new ParsedPayment(
				FieldConfidence<string?>.High("VISA"),
				FieldConfidence<decimal?>.High(70.43m),
				FieldConfidence<string?>.High("3409")),
		];

		// Act
		List<ProposedTransaction> result = await _resolver.ResolveAsync(
			payments, ReceiptDateField, CancellationToken.None);

		// Assert
		result.Should().HaveCount(1);
		ProposedTransaction txn = result[0];
		txn.CardId.Value.Should().BeNull();
		txn.CardId.Confidence.Should().Be(ConfidenceLevel.Low);
		txn.AccountId.Value.Should().BeNull();
		txn.AccountId.Confidence.Should().Be(ConfidenceLevel.Low);
	}

	[Fact]
	public async Task ResolveAsync_PaymentWithoutLastFour_LeavesCardIdNoneAndPreservesAmount()
	{
		// Arrange — cash and gift-card tenders typically don't carry a last-four. The
		// resolver must NOT crash on the missing field; it produces a row with None on
		// CardId/AccountId but populates the amount and method snapshot so the wizard
		// can still pre-fill an Amount cell.
		Card card = new(VisaCardId, "3409", "Chase Visa", VisaAccountId);
		StubActiveCards(card);

		List<ParsedPayment> payments =
		[
			new ParsedPayment(
				FieldConfidence<string?>.High("CASH"),
				FieldConfidence<decimal?>.High(20m),
				FieldConfidence<string?>.None()),
		];

		// Act
		List<ProposedTransaction> result = await _resolver.ResolveAsync(
			payments, ReceiptDateField, CancellationToken.None);

		// Assert
		result.Should().HaveCount(1);
		ProposedTransaction txn = result[0];
		txn.CardId.Confidence.Should().Be(ConfidenceLevel.None);
		txn.AccountId.Confidence.Should().Be(ConfidenceLevel.None);
		txn.Amount.Value.Should().Be(20m);
		txn.MethodSnapshot.Value.Should().Be("CASH");
	}

	[Fact]
	public async Task ResolveAsync_DateAbsent_FallsBackToNoneOnTransactionDate()
	{
		// Arrange — the receipt date itself is absent (None). Per-transaction dates are
		// not extracted, so the resolver propagates None: the wizard's TransactionsSection
		// uses defaultDate (the user-edited receipt date) as a separate fallback.
		Card card = new(VisaCardId, "3409", "Chase Visa", VisaAccountId);
		StubActiveCards(card);

		List<ParsedPayment> payments =
		[
			new ParsedPayment(
				FieldConfidence<string?>.High("VISA"),
				FieldConfidence<decimal?>.High(70.43m),
				FieldConfidence<string?>.High("3409")),
		];

		// Act
		List<ProposedTransaction> result = await _resolver.ResolveAsync(
			payments, FieldConfidence<DateOnly>.None(), CancellationToken.None);

		// Assert
		result.Should().HaveCount(1);
		ProposedTransaction txn = result[0];
		txn.Date.Value.Should().BeNull();
		txn.Date.Confidence.Should().Be(ConfidenceLevel.None);
		// CardId still resolves — the date branch shouldn't gate card matching.
		txn.CardId.Value.Should().Be(VisaCardId);
	}

	[Fact]
	public async Task ResolveAsync_MultipleTenders_ResolvesEachIndependently()
	{
		// Arrange — split-tender receipt: one card payment with a known last-four, plus
		// a cash payment without a last-four. Each should resolve on its own merits.
		Card visa = new(VisaCardId, "3409", "Chase Visa", VisaAccountId);
		StubActiveCards(visa);

		List<ParsedPayment> payments =
		[
			new ParsedPayment(
				FieldConfidence<string?>.High("VISA"),
				FieldConfidence<decimal?>.High(50m),
				FieldConfidence<string?>.High("3409")),
			new ParsedPayment(
				FieldConfidence<string?>.High("CASH"),
				FieldConfidence<decimal?>.High(20m),
				FieldConfidence<string?>.None()),
		];

		// Act
		List<ProposedTransaction> result = await _resolver.ResolveAsync(
			payments, ReceiptDateField, CancellationToken.None);

		// Assert
		result.Should().HaveCount(2);
		result[0].CardId.Value.Should().Be(VisaCardId);
		result[0].AccountId.Value.Should().Be(VisaAccountId);
		result[0].Amount.Value.Should().Be(50m);
		result[1].CardId.Confidence.Should().Be(ConfidenceLevel.None);
		result[1].Amount.Value.Should().Be(20m);
	}

	[Fact]
	public async Task ResolveAsync_QueriesCardsOnlyOnceForMultiplePayments()
	{
		// Arrange — the resolver must materialize the active-card list once (small,
		// single-user scope) and reuse it across payments, not re-query per payment.
		// A regression here would balloon DB round-trips on split-tender receipts.
		Card card = new(VisaCardId, "3409", "Chase Visa", VisaAccountId);
		StubActiveCards(card);

		List<ParsedPayment> payments =
		[
			new ParsedPayment(FieldConfidence<string?>.High("VISA"), FieldConfidence<decimal?>.High(50m), FieldConfidence<string?>.High("3409")),
			new ParsedPayment(FieldConfidence<string?>.High("VISA"), FieldConfidence<decimal?>.High(20m), FieldConfidence<string?>.High("3409")),
			new ParsedPayment(FieldConfidence<string?>.High("CASH"), FieldConfidence<decimal?>.High(5m), FieldConfidence<string?>.None()),
		];

		// Act
		await _resolver.ResolveAsync(payments, ReceiptDateField, CancellationToken.None);

		// Assert
		_cardService.Verify(
			s => s.GetAllAsync(0, int.MaxValue, It.IsAny<SortParams>(), true, It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task ResolveAsync_RequestsActiveCardsOnly()
	{
		// Arrange — inactive cards (closed accounts) must not auto-match. The resolver
		// asks the card service for active cards only by passing `isActive: true`.
		StubActiveCards();

		List<ParsedPayment> payments =
		[
			new ParsedPayment(
				FieldConfidence<string?>.High("VISA"),
				FieldConfidence<decimal?>.High(10m),
				FieldConfidence<string?>.High("3409")),
		];

		// Act
		await _resolver.ResolveAsync(payments, ReceiptDateField, CancellationToken.None);

		// Assert
		_cardService.Verify(
			s => s.GetAllAsync(0, int.MaxValue, It.IsAny<SortParams>(), true, It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task ResolveAsync_LastFourMatchIsCaseInsensitive()
	{
		// Arrange — defence against upstream canonicalisation drift. CardCode might be
		// stored as "3409a" (with a check letter) and the VLM might return "3409A"; an
		// exact-case compare would silently miss this.
		Card card = new(VisaCardId, "3409a", "Chase Visa", VisaAccountId);
		StubActiveCards(card);

		List<ParsedPayment> payments =
		[
			new ParsedPayment(
				FieldConfidence<string?>.High("VISA"),
				FieldConfidence<decimal?>.High(50m),
				FieldConfidence<string?>.High("3409A")),
		];

		// Act
		List<ProposedTransaction> result = await _resolver.ResolveAsync(
			payments, ReceiptDateField, CancellationToken.None);

		// Assert
		result[0].CardId.Value.Should().Be(VisaCardId);
	}

	[Fact]
	public async Task ResolveAsync_PropagatesCancellationTokenToCardService()
	{
		// Arrange — RECEIPTS-647 contract applied to the new fan-out point.
		Card card = new(VisaCardId, "3409", "Chase Visa", VisaAccountId);
		StubActiveCards(card);

		using CancellationTokenSource cts = new();
		CancellationToken expected = cts.Token;

		List<ParsedPayment> payments =
		[
			new ParsedPayment(
				FieldConfidence<string?>.High("VISA"),
				FieldConfidence<decimal?>.High(10m),
				FieldConfidence<string?>.High("3409")),
		];

		// Act
		await _resolver.ResolveAsync(payments, ReceiptDateField, expected);

		// Assert
		_cardService.Verify(
			s => s.GetAllAsync(0, int.MaxValue, It.IsAny<SortParams>(), true, It.Is<CancellationToken>(t => t == expected)),
			Times.Once);
	}

	[Fact]
	public async Task ResolveAsync_NullPayments_ThrowsArgumentNullException()
	{
		// Act
		Func<Task> act = () => _resolver.ResolveAsync(null!, ReceiptDateField, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<ArgumentNullException>();
	}
}
