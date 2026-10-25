using Domain.Taxonomy;

namespace Application.Taxonomy;

public sealed record ClassificationCommand(Guid? CategoryId, Guid TaxonomyRevisionId, Guid? ExpectedAssignmentId, Guid OperationId, string Reason);
public sealed record PurchaseClassification(Guid Id, Guid ReceiptItemId, long Sequence, Guid? PreviousAssignmentId,
	Guid? CategoryId, Guid TaxonomyRevisionId, DateTimeOffset RecordedAt, string Actor, string Source, string Reason,
	Guid OperationId, IReadOnlyList<TaxonomyNode> Path);
public sealed record ClassificationHistory(IReadOnlyList<PurchaseClassification> Items, long? NextBeforeSequence);
public sealed class ClassificationOptions { public bool WritesEnabled { get; set; } }
public interface IClassificationService
{
	Task<PurchaseClassification> AssignAsync(string actor, Guid itemId, ClassificationCommand command, CancellationToken ct = default);
	Task<ClassificationHistory> HistoryAsync(Guid itemId, long? beforeSequence, int limit, CancellationToken ct = default);
}
