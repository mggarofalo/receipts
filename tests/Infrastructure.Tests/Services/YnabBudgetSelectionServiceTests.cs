using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Services;
using Moq;

namespace Infrastructure.Tests.Services;

public class YnabBudgetSelectionServiceTests
{
	private readonly Mock<IYnabBudgetSelectionRepository> _repository = new();
	private readonly Mock<ICommittedChangePublisher> _publisher = new();

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SetSelectedBudgetIdAsync_PublishesOnlyWhenRepositoryChanged(bool repositoryChanged)
	{
		_repository.Setup(r => r.SetSelectedBudgetIdAsync("budget-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(repositoryChanged);
		YnabBudgetSelectionService service = new(_repository.Object, _publisher.Object);

		await service.SetSelectedBudgetIdAsync("budget-1", CancellationToken.None);

		_publisher.Verify(p => p.PublishAsync(It.Is<CommittedEntityChange>(change =>
			change.EntityType == CommittedEntityType.YnabBudget
			&& change.ChangeType == CommittedChangeType.Updated
			&& change.EntityId == null)), repositoryChanged ? Times.Once() : Times.Never());
		_repository.VerifyAll();
	}
}
