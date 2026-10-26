namespace Infrastructure.Entities.CategoryAssistance;

// Deployment-local enrollment record. Portable backups intentionally omit this queue state.
public sealed class CategoryAssistanceHistoricalRequestEntity
{
	public Guid OperationId { get; set; }
	public string Actor { get; set; } = "";
	public string RequestHash { get; set; } = "";
	public string GroupsJson { get; set; } = "";
	public int ItemCount { get; set; }
	public Guid SettingsRevision { get; set; }
	public Guid RestoreGeneration { get; set; }
	public Guid TaxonomyRevision { get; set; }
	public Guid ConnectionGeneration { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
}
