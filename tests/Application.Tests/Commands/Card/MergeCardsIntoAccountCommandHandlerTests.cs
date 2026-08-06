using Application.Commands.Card.Merge;
using Application.Interfaces.Services;
using Application.Models.Merge;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Card;

public class MergeCardsIntoAccountCommandHandlerTests
{
	[Fact]
	public async Task Handle_WithValidCommand_DelegatesToServiceAndReturnsResult()
	{
		Mock<IAccountMergeService> mockService = new();
		MergeCardsResult expected = new(1, 2, 7, null);
		mockService
			.Setup(s => s.MergeCardsAsync(
				It.IsAny<Guid>(),
				It.IsAny<IReadOnlyList<Guid>>(),
				It.IsAny<Guid?>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		MergeCardsIntoAccountCommandHandler handler = new(mockService.Object);
		MergeCardsIntoAccountCommand command = new(Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()]);

		MergeCardsResult result = await handler.Handle(command, CancellationToken.None);

		result.Should().BeSameAs(expected);
		mockService.Verify(s => s.MergeCardsAsync(
			command.TargetAccountId,
			command.SourceCardIds,
			command.YnabMappingWinnerAccountId,
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_WithConflictsFromService_ReturnsConflicts()
	{
		Mock<IAccountMergeService> mockService = new();
		List<YnabMappingConflict> conflicts =
		[
			new(Guid.NewGuid(), "A", "b1", "y1", "Y1"),
			new(Guid.NewGuid(), "B", "b1", "y2", "Y2"),
		];
		MergeCardsResult expected = MergeCardsResult.Conflicted(conflicts);
		mockService
			.Setup(s => s.MergeCardsAsync(
				It.IsAny<Guid>(),
				It.IsAny<IReadOnlyList<Guid>>(),
				It.IsAny<Guid?>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		MergeCardsIntoAccountCommandHandler handler = new(mockService.Object);
		MergeCardsIntoAccountCommand command = new(Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()]);

		MergeCardsResult result = await handler.Handle(command, CancellationToken.None);

		result.Conflicts.Should().BeEquivalentTo(conflicts);
		result.IsNoOp.Should().BeFalse();
	}
}
