using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Services;
using Moq;

namespace Infrastructure.Tests.Services;

public class YnabAccountMappingServiceTests
{
	private readonly Mock<IYnabAccountMappingRepository> _repository = new();
	private readonly Mock<ICommittedChangePublisher> _publisher = new();

	[Fact]
	public async Task CreateAsync_PublishesCreatedRowAfterRepositoryReturns()
	{
		Guid id = Guid.NewGuid();
		_repository.Setup(r => r.CreateAsync(It.IsAny<YnabAccountMappingEntity>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabAccountMappingEntity row, CancellationToken _) => { row.Id = id; return row; });
		YnabAccountMappingService service = new(_repository.Object, _publisher.Object);

		await service.CreateAsync(Guid.NewGuid(), "account", "Checking", "budget", CancellationToken.None);

		VerifyPublished(CommittedChangeType.Created, id, Times.Once());
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task UpdateAsync_PublishesOnlyWhenRepositoryUpdated(bool updated)
	{
		Guid id = Guid.NewGuid();
		_repository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabAccountMappingEntity { Id = id });
		_repository.Setup(r => r.UpdateAsync(It.IsAny<YnabAccountMappingEntity>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(updated);
		YnabAccountMappingService service = new(_repository.Object, _publisher.Object);

		await service.UpdateAsync(id, "account", "Checking", "budget", CancellationToken.None);

		VerifyPublished(CommittedChangeType.Updated, id, updated ? Times.Once() : Times.Never());
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task DeleteAsync_PublishesOnlyWhenRepositoryDeleted(bool deleted)
	{
		Guid id = Guid.NewGuid();
		_repository.Setup(r => r.DeleteAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(deleted);
		YnabAccountMappingService service = new(_repository.Object, _publisher.Object);

		await service.DeleteAsync(id, CancellationToken.None);

		VerifyPublished(CommittedChangeType.Deleted, id, deleted ? Times.Once() : Times.Never());
	}

	[Theory]
	[InlineData(3)]
	[InlineData(0)]
	public async Task DeleteStaleMappingsAsync_PublishesCollectionDeleteOnlyWhenRowsChanged(int count)
	{
		_repository.Setup(r => r.DeleteByBudgetIdNotAsync("current", It.IsAny<CancellationToken>())).ReturnsAsync(count);
		YnabAccountMappingService service = new(_repository.Object, _publisher.Object);

		await service.DeleteStaleMappingsAsync("current", CancellationToken.None);

		VerifyPublished(CommittedChangeType.Deleted, null, count > 0 ? Times.Once() : Times.Never());
	}

	private void VerifyPublished(CommittedChangeType type, Guid? id, Times times) =>
		_publisher.Verify(p => p.PublishAsync(It.Is<CommittedEntityChange>(change =>
			change.EntityType == CommittedEntityType.YnabMapping
			&& change.ChangeType == type
			&& change.EntityId == id)), times);
}
