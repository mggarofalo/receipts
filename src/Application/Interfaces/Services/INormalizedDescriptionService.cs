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
	Task<List<NormalizedDescriptionDetail>> GetAllAsync(NormalizedDescriptionStatus? filter, CancellationToken cancellationToken);
	Task<int> MergeAsync(Guid keepId, Guid discardId, CancellationToken cancellationToken);
	Task<NormalizedDescriptionDetail> SplitAsync(Guid receiptItemId, CancellationToken cancellationToken);
	Task<bool> UpdateStatusAsync(Guid id, NormalizedDescriptionStatus status, CancellationToken cancellationToken);

	Task<NormalizedDescriptionSettings> GetSettingsAsync(CancellationToken cancellationToken);
	Task<NormalizedDescriptionSettings> UpdateSettingsAsync(double autoAcceptThreshold, double pendingReviewThreshold, CancellationToken cancellationToken);
	Task<MatchTestResult> TestMatchAsync(string description, int topN, double? autoAcceptThresholdOverride, double? pendingReviewThresholdOverride, CancellationToken cancellationToken);
	Task<ThresholdImpactPreview> PreviewThresholdImpactAsync(double autoAcceptThreshold, double pendingReviewThreshold, CancellationToken cancellationToken);
}
