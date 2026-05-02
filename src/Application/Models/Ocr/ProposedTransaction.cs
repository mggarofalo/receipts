namespace Application.Models.Ocr;

/// <summary>
/// A pre-resolved Transaction row derived from a single VLM-extracted payment.
/// Carries the same fields as the wire-shape <c>ProposedTransactionResponse</c>:
/// <list type="bullet">
///   <item><description><see cref="CardId"/> — single matching active card by last four; null when none or ambiguous.</description></item>
///   <item><description><see cref="AccountId"/> — auto-populated from <c>Card.AccountId</c> when the card resolved.</description></item>
///   <item><description><see cref="Amount"/> — extracted payment amount.</description></item>
///   <item><description><see cref="Date"/> — receipt date (per-transaction dates are not currently extracted).</description></item>
///   <item><description><see cref="MethodSnapshot"/> — informational raw method string (e.g. "VISA", "Cash").</description></item>
/// </list>
/// Pairs each scalar with a <see cref="FieldConfidence{T}"/> sibling so the wizard can render
/// review chips. <see cref="ConfidenceLevel.None"/> means the source signal was absent;
/// <see cref="ConfidenceLevel.Low"/> on <see cref="CardId"/> specifically signals an ambiguous
/// last-four match (multiple cards) so the user is prompted to pick.
/// </summary>
public record ProposedTransaction(
	FieldConfidence<Guid?> CardId,
	FieldConfidence<Guid?> AccountId,
	FieldConfidence<decimal?> Amount,
	FieldConfidence<DateOnly?> Date,
	FieldConfidence<string?> MethodSnapshot
);
