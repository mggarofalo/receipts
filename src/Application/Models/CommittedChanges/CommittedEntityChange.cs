namespace Application.Models.CommittedChanges;

public enum CommittedEntityType
{
	NormalizedDescription,
	NormalizedDescriptionSettings,
}

public enum CommittedChangeType
{
	Created,
	Updated,
	Deleted,
}

public sealed record CommittedEntityChange(
	CommittedEntityType EntityType,
	CommittedChangeType ChangeType,
	Guid? EntityId = null);
