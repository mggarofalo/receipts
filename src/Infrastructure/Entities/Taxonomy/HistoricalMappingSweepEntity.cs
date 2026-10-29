namespace Infrastructure.Entities.Taxonomy;

// Operational progress only. A restore generation change invalidates this checkpoint.
internal sealed class HistoricalMappingSweepEntity
{
	public Guid Id { get; set; }
	public string RequestHash { get; set; } = "";
	public Guid RestoreGeneration { get; set; }
	public Guid? Cursor { get; set; }
	public Guid? PendingOperationId { get; set; }
	public bool Completed { get; set; }
}
