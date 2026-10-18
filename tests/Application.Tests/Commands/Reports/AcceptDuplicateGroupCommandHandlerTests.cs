using Application.Commands.Reports;
using Application.Duplicates;
using Application.Interfaces.Services;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Reports;

public class AcceptDuplicateGroupCommandHandlerTests
{
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(3)]
	public async Task Handle_SubmitsDistinctCanonicalPairsAndReturnsActualInsertCount(int inserted)
	{
		Guid[] ids = [Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002"), Guid.Parse("00000000-0000-0000-0000-000000000003")];
		DateTimeOffset now = new(2025, 3, 1, 0, 0, 0, TimeSpan.Zero);
		Mock<TimeProvider> clock = new();
		clock.Setup(c => c.GetUtcNow()).Returns(now);
		Mock<IDuplicateAcceptanceStore> store = new();
		store.Setup(s => s.AcceptAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<IReadOnlyList<(Guid A, Guid B)>>(), now, It.IsAny<CancellationToken>()))
			.Callback<IReadOnlyList<Guid>, IReadOnlyList<(Guid A, Guid B)>, DateTimeOffset, CancellationToken>((actualIds, pairs, _, _) =>
			{
				actualIds.Should().Equal(ids);
				pairs.Should().Equal((ids[0], ids[1]), (ids[0], ids[2]), (ids[1], ids[2]));
			}).ReturnsAsync(new DuplicateAcceptanceWriteResult(inserted, []));
		AcceptDuplicateGroupCommandHandler handler = new(store.Object, clock.Object);
		int result = await handler.Handle(new([ids[2], ids[0], ids[1], ids[0]]), CancellationToken.None);
		result.Should().Be(inserted);
		store.VerifyAll();
	}

	[Fact]
	public async Task Handle_MissingReceipts_ReportsBoundedNotFoundReason()
	{
		Guid[] missing = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
		Mock<IDuplicateAcceptanceStore> store = new();
		store.Setup(s => s.AcceptAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<IReadOnlyList<(Guid A, Guid B)>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new DuplicateAcceptanceWriteResult(0, missing));
		AcceptDuplicateGroupCommandHandler handler = new(store.Object, TimeProvider.System);
		Func<Task> act = async () => await handler.Handle(new([.. missing]), CancellationToken.None);
		KeyNotFoundException error = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which;
		error.Message.Should().Contain(missing[0].ToString()).And.Contain("(+2 more)").And.NotContain(missing[11].ToString());
	}

	[Fact]
	public async Task Handle_FewerThanTwoDistinctReceipts_DoesNotWrite()
	{
		Mock<IDuplicateAcceptanceStore> store = new(MockBehavior.Strict);
		Guid id = Guid.NewGuid();
		int result = await new AcceptDuplicateGroupCommandHandler(store.Object, TimeProvider.System).Handle(new([id, id]), CancellationToken.None);
		result.Should().Be(0);
		store.VerifyNoOtherCalls();
	}
}
