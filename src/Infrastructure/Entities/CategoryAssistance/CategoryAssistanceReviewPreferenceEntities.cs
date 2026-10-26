namespace Infrastructure.Entities.CategoryAssistance;

public sealed class CategoryAssistanceReviewPreferenceEntity
{
	public string Actor { get; set; } = "";
	public Guid AttemptId { get; set; }
	public string State { get; set; } = "active";
	public Guid Revision { get; set; }
	public string ExcludedItemIdsJson { get; set; } = "[]";
}

public sealed class CategoryAssistanceReviewOperationEntity
{
	public string Actor { get; set; } = "";
	public Guid OperationId { get; set; }
	public Guid AttemptId { get; set; }
	public string RequestHash { get; set; } = "";
	public string ResultJson { get; set; } = "";
	public DateTimeOffset RecordedAt { get; set; }
}
