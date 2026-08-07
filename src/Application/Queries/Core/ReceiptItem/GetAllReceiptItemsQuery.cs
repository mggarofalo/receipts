using Application.Interfaces;
using Application.Models;

namespace Application.Queries.Core.ReceiptItem;

// NormalizedDescriptionId narrows the page to the receipt items linked to one canonical row
// (RECEIPTS-877), which is what lets the review queue's split dialog see every linked item rather
// than whichever of them happen to land in the newest page of the unfiltered list.
public record GetAllReceiptItemsQuery(
	int Offset,
	int Limit,
	SortParams Sort,
	string? Q = null,
	Guid? NormalizedDescriptionId = null) : IQuery<PagedResult<Domain.Core.ReceiptItem>>;
