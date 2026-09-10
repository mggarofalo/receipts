using API.Services;
using Application.Models.CommittedChanges;
using Moq;

namespace Presentation.API.Tests.Services;

public class CommittedChangePublisherTests
{
	private readonly Mock<IEntityChangeNotifier> _notifier = new();

	[Theory]
	[InlineData(CommittedChangeType.Created)]
	[InlineData(CommittedChangeType.Updated)]
	[InlineData(CommittedChangeType.Deleted)]
	public async Task PublishAsync_EntityIdPresent_PreservesChangeKindAndIdentity(CommittedChangeType changeType)
	{
		Guid id = Guid.NewGuid();
		CommittedChangePublisher publisher = new(_notifier.Object);

		await publisher.PublishAsync(new(
			CommittedEntityType.NormalizedDescription,
			changeType,
			id));

		switch (changeType)
		{
			case CommittedChangeType.Created:
				_notifier.Verify(n => n.NotifyCreated("normalized-description", id), Times.Once);
				break;
			case CommittedChangeType.Updated:
				_notifier.Verify(n => n.NotifyUpdated("normalized-description", id), Times.Once);
				break;
			case CommittedChangeType.Deleted:
				_notifier.Verify(n => n.NotifyDeleted("normalized-description", id), Times.Once);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(changeType));
		}
		_notifier.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData(CommittedEntityType.NormalizedDescription, CommittedChangeType.Updated, "normalized-description", "updated")]
	[InlineData(CommittedEntityType.NormalizedDescriptionSettings, CommittedChangeType.Created, "normalized-description-settings", "created")]
	[InlineData(CommittedEntityType.YnabBudget, CommittedChangeType.Updated, "ynab-budget", "updated")]
	[InlineData(CommittedEntityType.YnabMapping, CommittedChangeType.Deleted, "ynab-mapping", "deleted")]
	[InlineData(CommittedEntityType.YnabSyncRecord, CommittedChangeType.Created, "ynab-sync-record", "created")]
	[InlineData(CommittedEntityType.YnabSyncEvent, CommittedChangeType.Created, "ynab-sync-event", "created")]
	[InlineData(CommittedEntityType.ReceiptItem, CommittedChangeType.Updated, "receipt-item", "updated")]
	[InlineData(CommittedEntityType.ItemEmbedding, CommittedChangeType.Updated, "item-embedding", "updated")]
	public async Task PublishAsync_EntityIdAbsent_PublishesNeutralCollectionRepair(
		CommittedEntityType entityType,
		CommittedChangeType changeType,
		string expectedEntityType,
		string expectedChangeType)
	{
		CommittedChangePublisher publisher = new(_notifier.Object);

		await publisher.PublishAsync(new(entityType, changeType));

		_notifier.Verify(
			n => n.NotifyAllChanged(expectedEntityType, expectedChangeType, false),
			Times.Once);
		_notifier.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task PublishAsync_SilentBroadChange_PreservesSuppressionFlag()
	{
		CommittedChangePublisher publisher = new(_notifier.Object);

		await publisher.PublishAsync(new(
			CommittedEntityType.ItemEmbedding,
			CommittedChangeType.Updated,
			SuppressToast: true));

		_notifier.Verify(
			n => n.NotifyAllChanged("item-embedding", "updated", true),
			Times.Once);
		_notifier.VerifyNoOtherCalls();
	}
}
