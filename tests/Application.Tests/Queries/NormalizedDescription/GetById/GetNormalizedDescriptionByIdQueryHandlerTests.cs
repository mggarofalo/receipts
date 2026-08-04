using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Application.Queries.NormalizedDescription.GetById;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.NormalizedDescription.GetById;

public class GetNormalizedDescriptionByIdQueryHandlerTests
{
	[Fact]
	public async Task Handle_Found_ReturnsEntityFromService()
	{
		// Arrange
		Mock<INormalizedDescriptionService> mockService = new();
		Guid id = Guid.NewGuid();
		NormalizedDescriptionDetail expected = new(
			new Domain.NormalizedDescriptions.NormalizedDescription(
				id,
				"cherry cola",
				NormalizedDescriptionStatus.Active,
				new DateTimeOffset(2026, 4, 19, 0, 0, 0, TimeSpan.Zero)),
			LinkedItemCount: 2,
			NearestNeighbourName: null,
			["CHERRY COLA 12PK", "cherry cola"]);

		mockService
			.Setup(s => s.GetByIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetNormalizedDescriptionByIdQueryHandler handler = new(mockService.Object);
		GetNormalizedDescriptionByIdQuery query = new(id);

		// Act
		NormalizedDescriptionDetail? actual = await handler.Handle(query, CancellationToken.None);

		// Assert
		actual.Should().BeSameAs(expected);
		mockService.Verify(s => s.GetByIdAsync(id, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_NotFound_ReturnsNull()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		Guid id = Guid.NewGuid();

		mockService
			.Setup(s => s.GetByIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync((NormalizedDescriptionDetail?)null);

		GetNormalizedDescriptionByIdQueryHandler handler = new(mockService.Object);
		GetNormalizedDescriptionByIdQuery query = new(id);

		NormalizedDescriptionDetail? actual = await handler.Handle(query, CancellationToken.None);

		actual.Should().BeNull();
	}

	[Fact]
	public void Query_EmptyGuid_ThrowsArgumentException()
	{
		// The query rejects Guid.Empty up-front so a malformed request never reaches the service.
		Action act = () => _ = new GetNormalizedDescriptionByIdQuery(Guid.Empty);
		act.Should().Throw<ArgumentException>()
			.WithMessage(GetNormalizedDescriptionByIdQuery.IdCannotBeEmptyExceptionMessage + "*");
	}
}
