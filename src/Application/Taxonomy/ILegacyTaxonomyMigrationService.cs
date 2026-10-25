namespace Application.Taxonomy;

public sealed record LegacyMigrationCapture(string SourceDataset, Guid OperationId, string Actor, DateTimeOffset ObservedAt);
public sealed record LegacyMigrationStatus(bool Backfilled, Guid? RevisionId, DateTimeOffset? HistoryBoundary);

public static class LegacyMigrationFreeze
{
	public static LegacyMigrationRequest Source(LegacyMigrationPlan plan) => new(plan.SourceDataset, plan.OperationId,
		plan.OperationKind, plan.Actor, plan.ObservedAt,
		plan.Crosswalks.Where(row => row.Source == LegacyTaxonomySource.Category).Select(row => row.Category!).ToArray(),
		plan.Crosswalks.Where(row => row.Source == LegacyTaxonomySource.Subcategory).Select(row => row.Subcategory!).ToArray(),
		plan.Items.Select(row => row.Evidence).ToArray());
}

public interface ILegacyTaxonomyMigrationService
{
	Task<LegacyMigrationPlan> PreflightAsync(LegacyMigrationCapture capture, CancellationToken cancellationToken = default);
	Task<LegacyMigrationPlan> ApplyAsync(LegacyMigrationRequest frozenSource, CancellationToken cancellationToken = default);
	Task<LegacyMigrationStatus> ReadStatusAsync(CancellationToken cancellationToken = default);
}
