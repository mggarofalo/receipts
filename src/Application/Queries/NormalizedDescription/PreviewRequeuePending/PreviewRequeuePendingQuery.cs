using Application.Interfaces;
using Application.Models.NormalizedDescriptions;

namespace Application.Queries.NormalizedDescription.PreviewRequeuePending;

// Read-only blast-radius report for the pending-description requeue (RECEIPTS-883). Also the
// post-run verification: every count reads zero once the requeue has succeeded.
public record PreviewRequeuePendingQuery : IQuery<RequeuePendingPreview>;
