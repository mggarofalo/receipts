using Application.Models.Ocr;

namespace Application.Commands.Receipt.Scan;

/// <summary>
/// Result of a receipt scan.
/// </summary>
/// <param name="ParsedReceipt">The receipt extracted from the source file.</param>
/// <param name="DroppedPageCount">
/// Number of source pages that were silently ignored during extraction. For PDFs,
/// only the first page is rasterized and sent to the VLM; pages 2..N are dropped.
/// This count lets callers warn the user that the proposal represents only part of
/// the document. Always 0 for single-page sources (images or single-page PDFs).
/// </param>
/// <param name="ProposedTransactions">
/// Pre-resolved Transaction rows derived from the VLM-extracted payments. Each entry's
/// last-four is matched against the user's active cards; on a single match
/// <see cref="ProposedTransaction.CardId"/> and <see cref="ProposedTransaction.AccountId"/>
/// are populated so the wizard can pre-fill a row. See <see cref="Application.Interfaces.Services.IProposedTransactionResolver"/>.
/// </param>
public record ScanReceiptResult(
	ParsedReceipt ParsedReceipt,
	int DroppedPageCount = 0,
	IReadOnlyList<ProposedTransaction>? ProposedTransactions = null);
