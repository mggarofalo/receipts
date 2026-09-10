namespace Application.Models.CommittedChanges;

public enum CommittedEntityType
{
	ReceiptItem,
	ItemEmbedding,
	NormalizedDescription,
	NormalizedDescriptionSettings,
	YnabBudget,
	YnabMapping,
	YnabSyncRecord,
	YnabSyncEvent,
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
	Guid? EntityId = null,
	bool SuppressToast = false);
