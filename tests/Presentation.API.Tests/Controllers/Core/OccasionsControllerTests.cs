using API.Controllers.Core;
using API.Generated.Dtos;
using API.Http;
using API.Mapping.Core;
using Application.Commands.Occasion.Create;
using Application.Commands.Occasion.Delete;
using Application.Commands.Occasion.Update;
using Application.Envelopes;
using Application.Exceptions;
using Application.Queries.Core.Occasion;
using Domain.Envelopes;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;

namespace Presentation.API.Tests.Controllers.Core;

public class OccasionsControllerTests
{
	private readonly Mock<IMediator> _mediator = new();
	private readonly OccasionsController _controller;

	public OccasionsControllerTests() => _controller = new(_mediator.Object, new EnvelopeMapper());

	[Theory]
	[InlineData(null)]
	[InlineData(true)]
	[InlineData(false)]
	public async Task GetOccasions_PassesActiveFilterAndMapsRows(bool? isActive)
	{
		// Arrange
		Occasion occasion = new(Guid.NewGuid(), "Groceries", isActive ?? true);
		_mediator.Setup(m => m.Send(new GetAllOccasionsQuery(isActive), It.IsAny<CancellationToken>())).ReturnsAsync([occasion]);

		// Act
		Ok<OccasionListResponse> actual = await _controller.GetOccasions(isActive);

		// Assert
		actual.Value!.Data.Should().ContainSingle().Which.Should().BeEquivalentTo(new { occasion.Id, occasion.Name, occasion.IsActive });
	}

	[Fact]
	public async Task GetOccasionById_Found_ReturnsOk()
	{
		// Arrange
		Occasion occasion = new(Guid.NewGuid(), "Groceries");
		_mediator.Setup(m => m.Send(new GetOccasionByIdQuery(occasion.Id), It.IsAny<CancellationToken>())).ReturnsAsync(occasion);

		// Act
		Results<Ok<OccasionResponse>, NotFound> actual = await _controller.GetOccasionById(occasion.Id);

		// Assert
		actual.Result.Should().BeOfType<Ok<OccasionResponse>>().Which.Value!.Id.Should().Be(occasion.Id);
	}

	[Fact]
	public async Task GetOccasionById_Missing_ReturnsBodilessNotFound()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediator.Setup(m => m.Send(new GetOccasionByIdQuery(id), It.IsAny<CancellationToken>())).ReturnsAsync((Occasion?)null);

		// Act
		Results<Ok<OccasionResponse>, NotFound> actual = await _controller.GetOccasionById(id);

		// Assert
		actual.Result.Should().BeOfType<NotFound>();
	}

	[Fact]
	public async Task CreateOccasion_ReturnsCreatedWithLocation()
	{
		// Arrange
		Occasion created = new(Guid.NewGuid(), "Groceries", false);
		_mediator.Setup(m => m.Send(It.Is<CreateOccasionCommand>(c => c.Occasion.Name == "Groceries" && !c.Occasion.IsActive),
			It.IsAny<CancellationToken>())).ReturnsAsync(created);

		// Act
		Results<Created<OccasionResponse>, ConflictProblem> actual =
			await _controller.CreateOccasion(new CreateOccasionRequest { Name = "Groceries", IsActive = false });

		// Assert
		Created<OccasionResponse> result = actual.Result.Should().BeOfType<Created<OccasionResponse>>().Subject;
		result.Location.Should().Be($"/api/occasions/{created.Id}");
		result.Value!.Id.Should().Be(created.Id);
	}

	[Fact]
	public async Task CreateOccasion_DuplicateName_ReturnsConflictProblem()
	{
		// Arrange
		_mediator.Setup(m => m.Send(It.IsAny<CreateOccasionCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new DuplicateEntityException("An occasion named 'Groceries' already exists."));

		// Act
		Results<Created<OccasionResponse>, ConflictProblem> actual = await _controller.CreateOccasion(new CreateOccasionRequest { Name = "Groceries" });

		// Assert
		actual.Result.Should().BeOfType<ConflictProblem>().Which.Value!.Detail.Should().Contain("already exists");
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task UpdateOccasion_SendsRouteIdAndReturnsNoContentOrNotFound(bool updated)
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediator.Setup(m => m.Send(It.Is<UpdateOccasionCommand>(c => c.Occasion.Id == id && c.Occasion.Name == "Food" && !c.Occasion.IsActive),
			It.IsAny<CancellationToken>())).ReturnsAsync(updated);

		// Act
		Results<NoContent, NotFound, ConflictProblem> actual = await _controller.UpdateOccasion(id, new UpdateOccasionRequest { Name = "Food", IsActive = false });

		// Assert
		if (updated)
		{
			actual.Result.Should().BeOfType<NoContent>();
		}
		else
		{
			actual.Result.Should().BeOfType<NotFound>();
		}
	}

	[Fact]
	public async Task UpdateOccasion_DuplicateName_ReturnsConflictProblem()
	{
		// Arrange
		_mediator.Setup(m => m.Send(It.IsAny<UpdateOccasionCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new DuplicateEntityException("An occasion named 'Food' already exists."));

		// Act
		Results<NoContent, NotFound, ConflictProblem> actual = await _controller.UpdateOccasion(Guid.NewGuid(), new UpdateOccasionRequest { Name = "Food" });

		// Assert
		actual.Result.Should().BeOfType<ConflictProblem>();
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task DeleteOccasion_ReturnsNoContentOrNotFound(bool deleted)
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediator.Setup(m => m.Send(new DeleteOccasionCommand(id), It.IsAny<CancellationToken>())).ReturnsAsync(deleted);

		// Act
		Results<NoContent, NotFound, ConflictProblem> actual = await _controller.DeleteOccasion(id);

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

	[Fact]
	public async Task DeleteOccasion_Referenced_ReturnsConflictWithCountsAsExtensions()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediator.Setup(m => m.Send(new DeleteOccasionCommand(id), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new EnvelopeException(409, "Cannot delete — the occasion is used.",
				new Dictionary<string, object?> { ["itemCount"] = 2, ["receiptCount"] = 1 }));

		// Act
		Results<NoContent, NotFound, ConflictProblem> actual = await _controller.DeleteOccasion(id);

		// Assert
		ProblemDetails problem = actual.Result.Should().BeOfType<ConflictProblem>().Subject.Value!;
		problem.Detail.Should().Contain("Cannot delete");
		problem.Extensions["itemCount"].Should().Be(2);
		problem.Extensions["receiptCount"].Should().Be(1);
	}

	[Fact]
	public async Task DeleteOccasion_NonConflictEnvelopeException_IsNotSwallowed()
	{
		// Arrange
		_mediator.Setup(m => m.Send(It.IsAny<DeleteOccasionCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new EnvelopeException(400, "bad"));

		// Act
		Func<Task> act = () => _controller.DeleteOccasion(Guid.NewGuid());

		// Assert
		await act.Should().ThrowAsync<EnvelopeException>();
	}
}
