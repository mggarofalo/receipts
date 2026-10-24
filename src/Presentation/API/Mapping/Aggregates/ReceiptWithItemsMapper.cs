using API.Generated.Dtos;
using API.Mapping.Core;
using Domain.Aggregates;
using Riok.Mapperly.Abstractions;

namespace API.Mapping.Aggregates;

[Mapper]
public partial class ReceiptWithItemsMapper
{
	private readonly ReceiptMapper _receiptMapper = new();
	private readonly ReceiptItemMapper _receiptItemMapper = new();
	private readonly AdjustmentMapper _adjustmentMapper = new();

	public ReceiptWithItemsResponse ToResponse(ReceiptWithItems source)
	{
		return new ReceiptWithItemsResponse
		{
			Receipt = _receiptMapper.ToResponse(source.Receipt),
			VerifiedItemCount = source.Items.Count(item => item.NormalizationReviewState == Domain.NormalizedDescriptions.NormalizationReviewState.Verified),
			NeedsReviewItemCount = source.Items.Count(item => item.NormalizationReviewState == Domain.NormalizedDescriptions.NormalizationReviewState.NeedsReview),
			ExcludedItemCount = source.Items.Count(item => item.NormalizationReviewState == Domain.NormalizedDescriptions.NormalizationReviewState.Excluded),
			Items = [.. source.Items.Select(_receiptItemMapper.ToResponse)],
			Adjustments = [.. source.Adjustments.Select(_adjustmentMapper.ToResponse)],
			Subtotal = (double)source.Subtotal.Amount,
			AdjustmentTotal = (double)source.AdjustmentTotal.Amount,
			ExpectedTotal = (double)source.ExpectedTotal.Amount,
			Warnings = [.. source.GetWarnings().Select(MapWarning)]
		};
	}

	private static ValidationWarningResponse MapWarning(Domain.ValidationWarning w) => new()
	{
		Property = w.Property,
		Message = w.Message,
		Severity = (int)w.Severity
	};
}
