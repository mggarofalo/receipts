using Application.Models;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;

namespace Application.Interfaces.Services;

public interface INormalizedDescriptionService
{
	Task<GetOrCreateResult> GetOrCreateAsync(string rawDescription, CancellationToken cancellationToken);

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

	// RECEIPTS-883. Preview is read-only and is also how a caller verifies the run afterwards.
	// RequeuePendingAsync returns null when expectedFingerprint does not match the live pending
	// set — the caller previewed a different world and must re-read before destroying anything.
	Task<RequeuePendingPreview> PreviewRequeuePendingAsync(CancellationToken cancellationToken);
	Task<RequeuePendingResult?> RequeuePendingAsync(string expectedFingerprint, CancellationToken cancellationToken);

	Task<NormalizedDescriptionSettings> GetSettingsAsync(CancellationToken cancellationToken);
	Task<NormalizedDescriptionSettings> UpdateSettingsAsync(double autoAcceptThreshold, double pendingReviewThreshold, CancellationToken cancellationToken);
	Task<MatchTestResult> TestMatchAsync(string description, int topN, double? autoAcceptThresholdOverride, double? pendingReviewThresholdOverride, CancellationToken cancellationToken);
	Task<ThresholdImpactPreview> PreviewThresholdImpactAsync(double autoAcceptThreshold, double pendingReviewThreshold, CancellationToken cancellationToken);
}
