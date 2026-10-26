using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task AssignmentPublishesAfterCommitAndRollbackAndReplayStaySilent()
	{
		var seed = await SeedAsync();
		ClassificationCommand command = new(seed.Root, seed.Revision, seed.Assignment, Guid.NewGuid(), "Notification acceptance");
		Mock<ICommittedChangePublisher> publisher = new(MockBehavior.Strict);
		publisher.Setup(x => x.PublishAsync(It.Is<CommittedEntityChange>(x => x.EntityType == CommittedEntityType.ReceiptItem && x.EntityId == seed.Item && x.ChangeType == CommittedChangeType.Updated)))
			.Returns(async () =>
			{
				await using var reader = fixture.CreateDbContext();
				var assignment = await reader.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == seed.Item);
				assignment.AssignmentId.Should().NotBe(seed.Assignment);
			});
		var options = Options.Create(new ClassificationOptions { WritesEnabled = true });
		ClassificationService failing = new(new FailureFactory(fixture), TimeProvider.System, options, publisher.Object);
		Func<Task> rollback = () => failing.AssignAsync(Actor, seed.Item, command);
		await rollback.Should().ThrowAsync<InvalidOperationException>();
		publisher.VerifyNoOtherCalls();
		ClassificationService service = new(new Factory(fixture), TimeProvider.System, options, publisher.Object);
		var saved = await service.AssignAsync(Actor, seed.Item, command);
		(await service.AssignAsync(Actor, seed.Item, command)).Should().BeEquivalentTo(saved);
		publisher.Verify(x => x.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Once);
		publisher.VerifyNoOtherCalls();
	}
}
