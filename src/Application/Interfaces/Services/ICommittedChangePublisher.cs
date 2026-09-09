using Application.Models.CommittedChanges;

namespace Application.Interfaces.Services;

/// <summary>
/// Publishes projection changes only after their underlying database write has committed.
/// Publication is best-effort cache-coherence signaling, not part of the database transaction.
/// </summary>
public interface ICommittedChangePublisher
{
	Task PublishAsync(CommittedEntityChange change);
}
