using Application.Models;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;

namespace Application.Interfaces.Services;

public interface INormalizedDescriptionService
{

	// Template identity metadata is a user-selected catalog name, not receipt verification.
	// No similarity decision or approved scoped mapping is created by this operation.
	Task<Domain.NormalizedDescriptions.NormalizedDescription> GetOrCreateForTemplateAsync(
		string templateName,
		CancellationToken cancellationToken);

	// The three read paths below all serialize to the same NormalizedDescriptionResponse, so they
	// all return the evidence-bearing detail (RECEIPTS-873). If only the list endpoint populated it,
	// the other two would have to report a structurally false LinkedItemCount of 0.
	Task<NormalizedDescriptionDetail?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	// Paginated and searchable since RECEIPTS-879: the registry used to load every Active row and
	// filter in the browser, and grocery receipts generate thousands of distinct descriptions.
	// Search matches display name and matched text, so a renamed entry is findable either way.
	// `statuses` is a set rather than one value (RECEIPTS-878) — null or empty means no filter.
	Task<PagedResult<NormalizedDescriptionDetail>> GetAllAsync(
		IReadOnlyCollection<NormalizedDescriptionStatus>? statuses,
		string? q,
		int offset,
		int limit,
		CancellationToken cancellationToken);
	Task<int> MergeAsync(Guid keepId, Guid discardId, CancellationToken cancellationToken);
	// RECEIPTS-877. Detaches N receipt items into one new canonical entry under a caller-supplied
	// name. The name is not derived from the selection: a multi-item split routinely spans
	// heterogeneous raw text, where no automatic rule produces a name anyone would want.
	// All-or-nothing — an unknown id throws before anything is written.
	Task<NormalizedDescriptionDetail> SplitAsync(IReadOnlyList<Guid> receiptItemIds, string name, CancellationToken cancellationToken);
	Task<bool> UpdateStatusAsync(Guid id, NormalizedDescriptionStatus status, CancellationToken cancellationToken);

	// RECEIPTS-876. Sets or clears the display label only — never CanonicalName, never the
	// embedding — so a rename cannot change which receipt text resolves to this row. Pass null to
	// clear the label and fall back to the matched text.
	Task<NormalizedDescriptionDetail> RenameAsync(Guid id, string? displayLabel, CancellationToken cancellationToken);

	// RECEIPTS-930. Records that a resolver-derived row is an item the user already has a template
	// for. Consolidates rather than just setting the template's FK to descriptionId: that link
	// would not survive the template's next edit (ItemTemplateService re-resolves it from the
	// template's name), and it would leave the row's existing receipt items reporting in their own
	// bucket, which is the thing the caller is trying to fix. Throws on a Rejected row — deleting a
	// tombstone would erase a reviewer's recorded rejection decision.
	Task<LinkTemplateResult> LinkTemplateAsync(Guid descriptionId, Guid itemTemplateId, CancellationToken cancellationToken);

	// RECEIPTS-883. Preview is read-only and is also how a caller verifies the run afterwards.
	// RequeuePendingAsync returns null when expectedFingerprint does not match the live pending
	// set — the caller previewed a different world and must re-read before destroying anything.
	Task<RequeuePendingPreview> PreviewRequeuePendingAsync(CancellationToken cancellationToken);
	Task<RequeuePendingResult?> RequeuePendingAsync(string expectedFingerprint, CancellationToken cancellationToken);

	// Operational view of the bounded vector-rebuild queue. Exact canonical-name matches remain
	// available while this reports incomplete; ANN readers admit only this fingerprint.
	Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken);
}
