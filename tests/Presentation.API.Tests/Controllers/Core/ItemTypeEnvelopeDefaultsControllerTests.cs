using API.Controllers.Core;
using API.Generated.Dtos;
using API.Http;
using Application.Commands.ItemTypeEnvelopeDefault.Delete;
using Application.Commands.ItemTypeEnvelopeDefault.Set;
using Application.Envelopes;
using Application.Queries.Core.ItemTypeEnvelopeDefault;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using ItemTypeEnvelopeDefault = Application.Interfaces.Services.ItemTypeEnvelopeDefault;

namespace Presentation.API.Tests.Controllers.Core;

public class ItemTypeEnvelopeDefaultsControllerTests
{
	private readonly Mock<IMediator> _mediator = new();
	private readonly ItemTypeEnvelopeDefaultsController _controller;

	public ItemTypeEnvelopeDefaultsControllerTests() => _controller = new(_mediator.Object);

	[Fact]
	public async Task GetDefaults_MapsEveryRow()
	{
		// Arrange
		ItemTypeEnvelopeDefault row = new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
		_mediator.Setup(m => m.Send(It.IsAny<GetItemTypeEnvelopeDefaultsQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync([row]);

		// Act
		Ok<ItemTypeEnvelopeDefaultListResponse> actual = await _controller.GetItemTypeEnvelopeDefaults();

		// Assert
		actual.Value!.Data.Should().ContainSingle().Which.Should().BeEquivalentTo(new { row.CategoryId, row.EnvelopeId, row.UpdatedAt });
	}

	[Fact]
	public async Task SetDefault_SendsRouteCategoryAndBodyEnvelope()
	{
		// Arrange
		Guid categoryId = Guid.NewGuid(), envelopeId = Guid.NewGuid();
		ItemTypeEnvelopeDefault row = new(categoryId, envelopeId, DateTimeOffset.UtcNow);
		_mediator.Setup(m => m.Send(new SetItemTypeEnvelopeDefaultCommand(categoryId, envelopeId), It.IsAny<CancellationToken>())).ReturnsAsync(row);

		// Act
		Results<Ok<ItemTypeEnvelopeDefaultResponse>, BadRequestProblem> actual =
			await _controller.SetItemTypeEnvelopeDefault(categoryId, new SetItemTypeEnvelopeDefaultRequest { EnvelopeId = envelopeId });

		// Assert
		actual.Result.Should().BeOfType<Ok<ItemTypeEnvelopeDefaultResponse>>().Which.Value!
			.Should().BeEquivalentTo(new { CategoryId = categoryId, EnvelopeId = envelopeId, row.UpdatedAt });
	}

	[Fact]
	public async Task SetDefault_RejectedChoice_ReturnsBadRequestProblem()
	{
		// Arrange
		_mediator.Setup(m => m.Send(It.IsAny<SetItemTypeEnvelopeDefaultCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new EnvelopeException(400, "Envelope must exist and be active."));

		// Act
		Results<Ok<ItemTypeEnvelopeDefaultResponse>, BadRequestProblem> actual =
			await _controller.SetItemTypeEnvelopeDefault(Guid.NewGuid(), new SetItemTypeEnvelopeDefaultRequest { EnvelopeId = Guid.NewGuid() });

		// Assert
		actual.Result.Should().BeOfType<BadRequestProblem>().Which.Value!.Detail.Should().Be("Envelope must exist and be active.");
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task DeleteDefault_ReturnsNoContentOrNotFound(bool deleted)
	{
		// Arrange
		Guid categoryId = Guid.NewGuid();
		_mediator.Setup(m => m.Send(new DeleteItemTypeEnvelopeDefaultCommand(categoryId), It.IsAny<CancellationToken>())).ReturnsAsync(deleted);

		// Act
		Results<NoContent, NotFound> actual = await _controller.DeleteItemTypeEnvelopeDefault(categoryId);

		// Assert
		if (deleted)
		{
			actual.Result.Should().BeOfType<NoContent>();
		}
		else
		{
			actual.Result.Should().BeOfType<NotFound>();
		}
	}
}
