using Application.Interfaces.Services;
using Application.Models.CommittedChanges;

namespace API.Services;

public sealed class CommittedChangePublisher(IEntityChangeNotifier notifier) : ICommittedChangePublisher
{
	public Task PublishAsync(CommittedEntityChange change)
	{
		string entityType = change.EntityType switch
		{
			CommittedEntityType.NormalizedDescription => "normalized-description",
			CommittedEntityType.NormalizedDescriptionSettings => "normalized-description-settings",
			_ => throw new ArgumentOutOfRangeException(nameof(change), change.EntityType, "Unknown committed entity type."),
		};

		string changeType = change.ChangeType switch
		{
			CommittedChangeType.Created => "created",
			CommittedChangeType.Updated => "updated",
			CommittedChangeType.Deleted => "deleted",
			_ => throw new ArgumentOutOfRangeException(nameof(change), change.ChangeType, "Unknown committed change type."),
		};

		if (change.EntityId is not Guid id)
		{
			return notifier.NotifyAllChanged(entityType, changeType);
		}

		return change.ChangeType switch
		{
			CommittedChangeType.Created => notifier.NotifyCreated(entityType, id),
			CommittedChangeType.Updated => notifier.NotifyUpdated(entityType, id),
			CommittedChangeType.Deleted => notifier.NotifyDeleted(entityType, id),
			_ => throw new ArgumentOutOfRangeException(nameof(change), change.ChangeType, "Unknown committed change type."),
		};
	}
}
