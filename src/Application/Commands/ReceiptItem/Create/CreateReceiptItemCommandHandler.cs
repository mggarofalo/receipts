using Application.Interfaces.Services;
using Domain.Core;
using Mediator;

namespace Application.Commands.ReceiptItem.Create;

public class CreateReceiptItemCommandHandler(
	IReceiptItemService receiptitemService,
	IReceiptService receiptService,
	IItemTemplateService itemTemplateService) : IRequestHandler<CreateReceiptItemCommand, List<Domain.Core.ReceiptItem>>
{
	public async ValueTask<List<Domain.Core.ReceiptItem>> Handle(CreateReceiptItemCommand request, CancellationToken cancellationToken)
	{
		// Guard against creating a child under a missing or soft-deleted receipt (RECEIPTS-763).
		// ExistsAsync respects the soft-delete query filter, so a trashed receipt reads as absent
		// and we reject with 404 instead of orphaning an active receipt item under it (or letting
		// a nonexistent-id FK violation surface as a 500).
		if (!await receiptService.ExistsAsync(request.ReceiptId, cancellationToken))
		{
			throw new KeyNotFoundException($"Receipt {request.ReceiptId} not found.");
		}

		await StampTemplateDescriptionsAsync(request, cancellationToken);

		return await receiptitemService.CreateAsync([.. request.ReceiptItems], request.ReceiptId, cancellationToken);
	}

	/// <summary>
	/// Stamps items entered from a template with that template's canonical description
	/// (RECEIPTS-881), so the background resolver never looks at them.
	/// </summary>
	/// <remarks>
	/// This is where the issue's "the template wins at entry time" rule is actually enforced. The
	/// resolver only picks up items <c>WHERE NormalizedDescriptionId IS NULL</c>, so writing the
	/// FK here is what makes it skip them — no second predicate is needed, and none should be
	/// added: a template-stamped item that later needs re-resolving can simply have its FK
	/// cleared.
	///
	/// Every failure mode is a no-op rather than an error. An unknown template id, a soft-deleted
	/// template, or one whose canonical entry has not been created yet all leave the item
	/// unstamped, and it falls through to the resolver exactly as before. The id is a hint about
	/// where the text came from, not a constraint the receipt has to satisfy — refusing to save
	/// somebody's receipt over a stale template id trades a working feature for a bookkeeping one.
	/// </remarks>
	private async Task StampTemplateDescriptionsAsync(CreateReceiptItemCommand request, CancellationToken cancellationToken)
	{
		if (request.ItemTemplateIds.Count == 0)
		{
			return;
		}

		// One lookup per distinct template, not per item: a receipt commonly has several lines
		// from the same template.
		Dictionary<Guid, Guid?> canonicalByTemplate = [];

		for (int i = 0; i < request.ReceiptItems.Count; i++)
		{
			Guid? templateId = request.ItemTemplateIds[i];
			if (templateId is not { } id)
			{
				continue;
			}

			if (!canonicalByTemplate.TryGetValue(id, out Guid? canonicalId))
			{
				Domain.Core.ItemTemplate? template = await itemTemplateService.GetByIdAsync(id, cancellationToken);
				canonicalId = template?.NormalizedDescriptionId;
				canonicalByTemplate[id] = canonicalId;
			}

			if (canonicalId is not null)
			{
				request.ReceiptItems[i].NormalizedDescriptionId = canonicalId;
				// No match score. The score column records a cosine similarity, and nothing was
				// compared — this is a declaration, not a match. A fabricated 1.0 would be
				// indistinguishable from a perfect ANN hit in every threshold-impact preview,
				// which buckets items by exactly this column.
				request.ReceiptItems[i].NormalizedDescriptionMatchScore = null;
			}
		}
	}
}
