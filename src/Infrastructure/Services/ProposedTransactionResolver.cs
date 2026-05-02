using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ocr;
using Domain.Core;

namespace Infrastructure.Services;

/// <summary>
/// Resolves VLM-extracted payment tenders to <see cref="ProposedTransaction"/> rows by
/// matching the extracted last-four digits against the user's active cards (matched on
/// <see cref="Card.CardCode"/>). The current schema does not carry a dedicated last-four
/// column, so we treat <see cref="Card.CardCode"/> as the matching key — it is the
/// user-facing card identifier and is commonly populated with the last four digits.
/// Users with a different scheme will simply see no auto-match and fall through to the
/// manual picker, which is the correct degenerate behaviour.
/// </summary>
public class ProposedTransactionResolver(ICardService cardService) : IProposedTransactionResolver
{
	public async Task<List<ProposedTransaction>> ResolveAsync(
		IReadOnlyList<ParsedPayment> payments,
		FieldConfidence<DateOnly> receiptDate,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(payments);

		List<ProposedTransaction> result = new(payments.Count);
		if (payments.Count == 0)
		{
			return result;
		}

		// Materialise the active-card list once. The number of cards is small (single-user
		// scope), so an in-memory linear scan per payment is fine and avoids N round-trips
		// to the repository.
		PagedResult<Card> activeCards = await cardService.GetAllAsync(
			offset: 0,
			limit: int.MaxValue,
			sort: SortParams.Default,
			isActive: true,
			cancellationToken);

		FieldConfidence<DateOnly?> dateForRow = receiptDate.IsPresent
			? new FieldConfidence<DateOnly?>(receiptDate.Value, receiptDate.Confidence)
			: FieldConfidence<DateOnly?>.None();

		foreach (ParsedPayment payment in payments)
		{
			result.Add(ResolveOne(payment, activeCards.Data, dateForRow));
		}

		return result;
	}

	private static ProposedTransaction ResolveOne(
		ParsedPayment payment,
		IReadOnlyList<Card> activeCards,
		FieldConfidence<DateOnly?> dateForRow)
	{
		FieldConfidence<decimal?> amount = payment.Amount;
		FieldConfidence<string?> methodSnapshot = payment.Method;

		string? lastFour = payment.LastFour.Value;
		bool hasLastFour = !string.IsNullOrWhiteSpace(lastFour);

		// No last-four → no card resolution possible. CardId/AccountId are absent
		// (None confidence): the wizard pre-fills the Amount/Date/method but leaves
		// the user to pick a card and account manually.
		if (!hasLastFour)
		{
			return new ProposedTransaction(
				CardId: FieldConfidence<Guid?>.None(),
				AccountId: FieldConfidence<Guid?>.None(),
				Amount: amount,
				Date: dateForRow,
				MethodSnapshot: methodSnapshot);
		}

		// Linear scan: cards-per-user is small. Case-insensitive compare so a card stored
		// as "3409" still matches a VLM-extracted "3409", regardless of how the upstream
		// canonicalises the string.
		List<Card> matches = [..
			activeCards.Where(c => string.Equals(c.CardCode, lastFour, StringComparison.OrdinalIgnoreCase))];

		if (matches.Count == 1)
		{
			Card card = matches[0];
			return new ProposedTransaction(
				CardId: FieldConfidence<Guid?>.High(card.Id),
				AccountId: FieldConfidence<Guid?>.High(card.AccountId),
				Amount: amount,
				Date: dateForRow,
				MethodSnapshot: methodSnapshot);
		}

		if (matches.Count > 1)
		{
			// Ambiguous match: surface as Low confidence on cardId (and accountId) so the
			// wizard renders a review chip and forces the user to disambiguate. Don't
			// guess — picking the wrong card would silently file a transaction against
			// the wrong account.
			return new ProposedTransaction(
				CardId: FieldConfidence<Guid?>.Low(null),
				AccountId: FieldConfidence<Guid?>.Low(null),
				Amount: amount,
				Date: dateForRow,
				MethodSnapshot: methodSnapshot);
		}

		// No matches: this card last-four is new to the user. None confidence means the
		// chip omits a badge entirely; the wizard simply requires manual selection.
		return new ProposedTransaction(
			CardId: FieldConfidence<Guid?>.None(),
			AccountId: FieldConfidence<Guid?>.None(),
			Amount: amount,
			Date: dateForRow,
			MethodSnapshot: methodSnapshot);
	}
}
