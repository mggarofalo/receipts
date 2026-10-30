using API.Controllers.Core;
using API.Generated.Dtos;
using API.Http;
using API.Mapping.Core;
using Application.Commands.Envelope.Create;
using Application.Commands.Envelope.Delete;
using Application.Commands.Envelope.Update;
using Application.Envelopes;
using Application.Exceptions;
using Application.Queries.Core.Envelope;
using Domain.Envelopes;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;

namespace Presentation.API.Tests.Controllers.Core;

public class EnvelopesControllerTests
{
	private readonly Mock<IMediator> _mediator = new();
	private readonly EnvelopesController _controller;

	public EnvelopesControllerTests() => _controller = new(_mediator.Object, new EnvelopeMapper());

	[Theory]
	[InlineData(null)]
	[InlineData(true)]
	[InlineData(false)]
	public async Task GetEnvelopes_PassesActiveFilterAndMapsRows(bool? isActive)
	{
		// Arrange
		Envelope envelope = new(Guid.NewGuid(), "Groceries", isActive ?? true);
		_mediator.Setup(m => m.Send(new GetAllEnvelopesQuery(isActive), It.IsAny<CancellationToken>())).ReturnsAsync([envelope]);

		// Act
		Ok<EnvelopeListResponse> actual = await _controller.GetEnvelopes(isActive);

		// Assert
		actual.Value!.Data.Should().ContainSingle().Which.Should().BeEquivalentTo(new { envelope.Id, envelope.Name, envelope.IsActive });
	}

	[Fact]
	public async Task GetEnvelopeById_Found_ReturnsOk()
	{
		// Arrange
		Envelope envelope = new(Guid.NewGuid(), "Groceries");
		_mediator.Setup(m => m.Send(new GetEnvelopeByIdQuery(envelope.Id), It.IsAny<CancellationToken>())).ReturnsAsync(envelope);

		// Act
		Results<Ok<EnvelopeResponse>, NotFound> actual = await _controller.GetEnvelopeById(envelope.Id);

		// Assert
		actual.Result.Should().BeOfType<Ok<EnvelopeResponse>>().Which.Value!.Id.Should().Be(envelope.Id);
	}

	[Fact]
	public async Task GetEnvelopeById_Missing_ReturnsBodilessNotFound()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediator.Setup(m => m.Send(new GetEnvelopeByIdQuery(id), It.IsAny<CancellationToken>())).ReturnsAsync((Envelope?)null);

		// Act
		Results<Ok<EnvelopeResponse>, NotFound> actual = await _controller.GetEnvelopeById(id);

		// Assert
		actual.Result.Should().BeOfType<NotFound>();
	}

	[Fact]
	public async Task CreateEnvelope_ReturnsCreatedWithLocation()
	{
		// Arrange
		Envelope created = new(Guid.NewGuid(), "Groceries", false);
		_mediator.Setup(m => m.Send(It.Is<CreateEnvelopeCommand>(c => c.Envelope.Name == "Groceries" && !c.Envelope.IsActive),
			It.IsAny<CancellationToken>())).ReturnsAsync(created);

		// Act
		Results<Created<EnvelopeResponse>, ConflictProblem> actual =
			await _controller.CreateEnvelope(new CreateEnvelopeRequest { Name = "Groceries", IsActive = false });

		// Assert
		Created<EnvelopeResponse> result = actual.Result.Should().BeOfType<Created<EnvelopeResponse>>().Subject;
		result.Location.Should().Be($"/api/envelopes/{created.Id}");
		result.Value!.Id.Should().Be(created.Id);
	}

	[Fact]
	public async Task CreateEnvelope_DuplicateName_ReturnsConflictProblem()
	{
		// Arrange
		_mediator.Setup(m => m.Send(It.IsAny<CreateEnvelopeCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new DuplicateEntityException("An envelope named 'Groceries' already exists."));

		// Act
		Results<Created<EnvelopeResponse>, ConflictProblem> actual = await _controller.CreateEnvelope(new CreateEnvelopeRequest { Name = "Groceries" });

		// Assert
		actual.Result.Should().BeOfType<ConflictProblem>().Which.Value!.Detail.Should().Contain("already exists");
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task UpdateEnvelope_SendsRouteIdAndReturnsNoContentOrNotFound(bool updated)
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediator.Setup(m => m.Send(It.Is<UpdateEnvelopeCommand>(c => c.Envelope.Id == id && c.Envelope.Name == "Food" && !c.Envelope.IsActive),
			It.IsAny<CancellationToken>())).ReturnsAsync(updated);

		// Act
		Results<NoContent, NotFound, ConflictProblem> actual = await _controller.UpdateEnvelope(id, new UpdateEnvelopeRequest { Name = "Food", IsActive = false });

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
	public async Task UpdateEnvelope_DuplicateName_ReturnsConflictProblem()
	{
		// Arrange
		_mediator.Setup(m => m.Send(It.IsAny<UpdateEnvelopeCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new DuplicateEntityException("An envelope named 'Food' already exists."));

		// Act
		Results<NoContent, NotFound, ConflictProblem> actual = await _controller.UpdateEnvelope(Guid.NewGuid(), new UpdateEnvelopeRequest { Name = "Food" });

		// Assert
		actual.Result.Should().BeOfType<ConflictProblem>();
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task DeleteEnvelope_ReturnsNoContentOrNotFound(bool deleted)
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediator.Setup(m => m.Send(new DeleteEnvelopeCommand(id), It.IsAny<CancellationToken>())).ReturnsAsync(deleted);

		// Act
		Results<NoContent, NotFound, ConflictProblem> actual = await _controller.DeleteEnvelope(id);

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
	public async Task DeleteEnvelope_Referenced_ReturnsConflictWithCountsAsExtensions()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediator.Setup(m => m.Send(new DeleteEnvelopeCommand(id), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new EnvelopeException(409, "Cannot delete — the envelope is used.",
				new Dictionary<string, object?> { ["itemCount"] = 2, ["receiptCount"] = 0, ["defaultCount"] = 1 }));

		// Act
		Results<NoContent, NotFound, ConflictProblem> actual = await _controller.DeleteEnvelope(id);

		// Assert
		ProblemDetails problem = actual.Result.Should().BeOfType<ConflictProblem>().Subject.Value!;
		problem.Detail.Should().Contain("Cannot delete");
		problem.Extensions["itemCount"].Should().Be(2);
		problem.Extensions["defaultCount"].Should().Be(1);
	}

	[Fact]
	public async Task DeleteEnvelope_NonConflictEnvelopeException_IsNotSwallowed()
	{
		// Arrange
		_mediator.Setup(m => m.Send(It.IsAny<DeleteEnvelopeCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new EnvelopeException(400, "bad"));

		// Act
		Func<Task> act = () => _controller.DeleteEnvelope(Guid.NewGuid());

		// Assert
		await act.Should().ThrowAsync<EnvelopeException>();
	}
}
