using Application.Models.Ocr;

namespace Application.Interfaces.Services;

/// <summary>
/// Resolves VLM-extracted payment tenders to pre-populated Transaction rows.
/// The wizard consumes the result to skip the manual card/account picker round-trip
/// when the receipt's last-four uniquely matches a card the user already owns.
/// </summary>
public interface IProposedTransactionResolver
{
	/// <summary>
	/// Build one <see cref="ProposedTransaction"/> per <see cref="ParsedPayment"/>.
	/// </summary>
	/// <param name="payments">VLM-extracted tenders from the parsed receipt.</param>
	/// <param name="receiptDate">
	/// Falls back into each <see cref="ProposedTransaction.Date"/> because the current VLM
	/// schema does not extract per-transaction dates. <see cref="ConfidenceLevel.None"/> when
	/// the receipt date itself is absent.
	/// </param>
	/// <param name="cancellationToken">Caller-provided cancellation token.</param>
	Task<List<ProposedTransaction>> ResolveAsync(
		IReadOnlyList<ParsedPayment> payments,
		FieldConfidence<DateOnly> receiptDate,
		CancellationToken cancellationToken);
}
