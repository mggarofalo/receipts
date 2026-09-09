using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Services;
using Moq;

namespace Infrastructure.Tests.Services;

public class YnabCategoryMappingServiceTests
{
	private readonly Mock<IYnabCategoryMappingRepository> _repository = new();
	private readonly Mock<ICommittedChangePublisher> _publisher = new();

	[Fact]
	public async Task CreateAsync_PublishesCreatedRow()
	{
		Guid id = Guid.NewGuid();
		_repository.Setup(r => r.CreateAsync(It.IsAny<YnabCategoryMappingEntity>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabCategoryMappingEntity row, CancellationToken _) => { row.Id = id; return row; });
		YnabCategoryMappingService service = new(_repository.Object, _publisher.Object);

		_ = await service.CreateAsync("Groceries", "category", "Groceries", "Needs", "budget", CancellationToken.None);

		VerifyPublished(CommittedChangeType.Created, id, Times.Once());
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task UpdateAsync_PublishesOnlyForAnActualUpdate(bool updated)
	{
		Guid id = Guid.NewGuid();
		_repository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabCategoryMappingEntity { Id = id });
		_repository.Setup(r => r.UpdateAsync(It.IsAny<YnabCategoryMappingEntity>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(updated);
		YnabCategoryMappingService service = new(_repository.Object, _publisher.Object);

		await service.UpdateAsync(id, "category", "Groceries", "Needs", "budget", CancellationToken.None);

		VerifyPublished(CommittedChangeType.Updated, id, updated ? Times.Once() : Times.Never());
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task DeleteAsync_PublishesOnlyForAnActualDelete(bool deleted)
	{
		Guid id = Guid.NewGuid();
		_repository.Setup(r => r.DeleteAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(deleted);
		YnabCategoryMappingService service = new(_repository.Object, _publisher.Object);

		await service.DeleteAsync(id, CancellationToken.None);

		VerifyPublished(CommittedChangeType.Deleted, id, deleted ? Times.Once() : Times.Never());
	}

	[Theory]
	[InlineData(2)]
	[InlineData(0)]
	public async Task DeleteStaleMappingsAsync_PublishesOnlyWhenRowsWereDeleted(int count)
	{
		_repository.Setup(r => r.DeleteByBudgetIdNotAsync("current", It.IsAny<CancellationToken>())).ReturnsAsync(count);
		YnabCategoryMappingService service = new(_repository.Object, _publisher.Object);

		await service.DeleteStaleMappingsAsync("current", CancellationToken.None);

		VerifyPublished(CommittedChangeType.Deleted, null, count > 0 ? Times.Once() : Times.Never());
	}

	private void VerifyPublished(CommittedChangeType type, Guid? id, Times times) =>
		_publisher.Verify(p => p.PublishAsync(It.Is<CommittedEntityChange>(change =>
			change.EntityType == CommittedEntityType.YnabMapping
			&& change.ChangeType == type
			&& change.EntityId == id)), times);
}
