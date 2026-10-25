namespace Infrastructure.Entities.Taxonomy;

public sealed class PurchaseClassificationEntity
{
	public Guid Id { get; set; }
	public Guid ReceiptItemId { get; set; }
	public long Sequence { get; set; }
	public Guid? PreviousAssignmentId { get; set; }
	public Guid? CategoryId { get; set; }
	public Guid TaxonomyRevisionId { get; set; }
	public DateTimeOffset RecordedAt { get; set; }
	public string Actor { get; set; } = "";
	public string Source { get; set; } = "manual";
	public string Reason { get; set; } = "";
	public Guid OperationId { get; set; }
}
public sealed class CurrentClassificationEntity
{
	public Guid ReceiptItemId { get; set; }
	public Guid AssignmentId { get; set; }
}
public sealed class ClassificationOperationEntity
{
	public string Actor { get; set; } = "";
	public Guid OperationId { get; set; }
	public string RequestHash { get; set; } = "";
	public Guid AssignmentId { get; set; }
}
