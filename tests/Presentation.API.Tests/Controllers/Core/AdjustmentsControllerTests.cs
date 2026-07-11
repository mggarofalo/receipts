using API.Controllers.Core;
using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.Adjustment.Create;
using Application.Commands.Adjustment.Delete;
using Application.Commands.Adjustment.Restore;
using Application.Commands.Adjustment.Update;
using Application.Models;
using Application.Queries.Core.Adjustment;
using Domain.Core;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SampleData.Domain.Core;
using SampleData.Dtos.Core;

namespace Presentation.API.Tests.Controllers.Core;

public class AdjustmentsControllerTests
{
	private readonly AdjustmentMapper _mapper;
	private readonly Mock<IMediator> _mediatorMock;
	private readonly Mock<ILogger<AdjustmentsController>> _loggerMock;
	private readonly Mock<IEntityChangeNotifier> _notifierMock;
	private readonly AdjustmentsController _controller;

	public AdjustmentsControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_mapper = new AdjustmentMapper();
		_loggerMock = ControllerTestHelpers.GetLoggerMock<AdjustmentsController>();
		_notifierMock = new Mock<IEntityChangeNotifier>();
		_controller = new AdjustmentsController(_mediatorMock.Object, _mapper, _loggerMock.Object, _notifierMock.Object);
	}

	[Fact]
	public async Task GetAdjustmentById_ReturnsOkResult_WhenAdjustmentExists()
	{
		// Arrange
		Adjustment adjustment = AdjustmentGenerator.Generate();
		AdjustmentResponse expectedReturn = _mapper.ToResponse(adjustment);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAdjustmentByIdQuery>(q => q.Id == adjustment.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(adjustment);

		// Act
		Results<Ok<AdjustmentResponse>, NotFound> result = await _controller.GetAdjustmentById(adjustment.Id);

		// Assert
		Ok<AdjustmentResponse> okResult = Assert.IsType<Ok<AdjustmentResponse>>(result.Result);
		AdjustmentResponse actualReturn = Assert.IsType<AdjustmentResponse>(okResult.Value);
		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task GetAdjustmentById_ReturnsNotFound_WhenAdjustmentDoesNotExist()
	{
		// Arrange
		Guid missingId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAdjustmentByIdQuery>(q => q.Id == missingId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync((Adjustment?)null);

		// Act
		Results<Ok<AdjustmentResponse>, NotFound> result = await _controller.GetAdjustmentById(missingId);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task GetAdjustmentById_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid id = AdjustmentGenerator.Generate().Id;

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAdjustmentByIdQuery>(q => q.Id == id),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetAdjustmentById(id);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetAllAdjustments_ReturnsOkResult_WithListOfAdjustments()
	{
		// Arrange
		List<Adjustment> adjustments = AdjustmentGenerator.GenerateList(2);
		List<AdjustmentResponse> expectedReturn = [.. adjustments.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllAdjustmentsQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Adjustment>(adjustments, adjustments.Count, 0, 50));

		// Act
		Results<Ok<AdjustmentListResponse>, BadRequest<string>> rawResult = await _controller.GetAllAdjustments(null, 0, 50, null, null);

		// Assert
		Ok<AdjustmentListResponse> result = Assert.IsType<Ok<AdjustmentListResponse>>(rawResult.Result);
		AdjustmentListResponse actualReturn = result.Value!;
		actualReturn.Data.Should().BeEquivalentTo(expectedReturn);
		actualReturn.Total.Should().Be(adjustments.Count);
		actualReturn.Offset.Should().Be(0);
		actualReturn.Limit.Should().Be(50);
	}

	[Theory]
	[InlineData(-1, 50)]
	[InlineData(-100, 50)]
	public async Task GetAllAdjustments_ReturnsBadRequest_WhenOffsetIsNegative(int offset, int limit)
	{
		// Act
		Results<Ok<AdjustmentListResponse>, BadRequest<string>> result = await _controller.GetAllAdjustments(null, offset, limit, null, null);

		// Assert
		BadRequest<string> badRequestResult = Assert.IsType<BadRequest<string>>(result.Result);
		badRequestResult.Value.Should().Be("offset must be >= 0");
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(0, -1)]
	[InlineData(0, 501)]
	public async Task GetAllAdjustments_ReturnsBadRequest_WhenLimitIsOutOfRange(int offset, int limit)
	{
		// Act
		Results<Ok<AdjustmentListResponse>, BadRequest<string>> result = await _controller.GetAllAdjustments(null, offset, limit, null, null);

		// Assert
		BadRequest<string> badRequestResult = Assert.IsType<BadRequest<string>>(result.Result);
		badRequestResult.Value.Should().Be("limit must be between 1 and 500");
	}

	[Theory]
	[InlineData(-1, 50)]
	[InlineData(-100, 50)]
	public async Task GetDeletedAdjustments_ReturnsBadRequest_WhenOffsetIsNegative(int offset, int limit)
	{
		// Act
		Results<Ok<AdjustmentListResponse>, BadRequest<string>> result = await _controller.GetDeletedAdjustments(offset, limit, null, null);

		// Assert
		BadRequest<string> badRequestResult = Assert.IsType<BadRequest<string>>(result.Result);
		badRequestResult.Value.Should().Be("offset must be >= 0");
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(0, -1)]
	[InlineData(0, 501)]
	public async Task GetDeletedAdjustments_ReturnsBadRequest_WhenLimitIsOutOfRange(int offset, int limit)
	{
		// Act
		Results<Ok<AdjustmentListResponse>, BadRequest<string>> result = await _controller.GetDeletedAdjustments(offset, limit, null, null);

		// Assert
		BadRequest<string> badRequestResult = Assert.IsType<BadRequest<string>>(result.Result);
		badRequestResult.Value.Should().Be("limit must be between 1 and 500");
	}

	[Fact]
	public async Task GetAllAdjustments_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllAdjustmentsQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetAllAdjustments(null, 0, 50, null, null);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetDeletedAdjustments_ReturnsOkResult_WithListOfAdjustments()
	{
		// Arrange
		List<Adjustment> adjustments = AdjustmentGenerator.GenerateList(2);
		List<AdjustmentResponse> expectedReturn = [.. adjustments.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetDeletedAdjustmentsQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Adjustment>(adjustments, adjustments.Count, 0, 50));

		// Act
		Results<Ok<AdjustmentListResponse>, BadRequest<string>> rawResult = await _controller.GetDeletedAdjustments(0, 50, null, null);

		// Assert
		Ok<AdjustmentListResponse> result = Assert.IsType<Ok<AdjustmentListResponse>>(rawResult.Result);
		AdjustmentListResponse actualReturn = result.Value!;
		actualReturn.Data.Should().BeEquivalentTo(expectedReturn);
		actualReturn.Total.Should().Be(adjustments.Count);
		actualReturn.Offset.Should().Be(0);
		actualReturn.Limit.Should().Be(50);
	}

	[Fact]
	public async Task GetDeletedAdjustments_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.Is<GetDeletedAdjustmentsQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetDeletedAdjustments(0, 50, null, null);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetAllAdjustments_WithReceiptId_ReturnsOkResult_WhenReceiptExists()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<Adjustment> adjustments = AdjustmentGenerator.GenerateList(2);
		List<AdjustmentResponse> expectedReturn = [.. adjustments.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAdjustmentsByReceiptIdQuery>(q => q.ReceiptId == receiptId && q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Adjustment>(adjustments, adjustments.Count, 0, 50));

		// Act
		Results<Ok<AdjustmentListResponse>, BadRequest<string>> rawResult = await _controller.GetAllAdjustments(receiptId, 0, 50, null, null);

		// Assert
		Ok<AdjustmentListResponse> result = Assert.IsType<Ok<AdjustmentListResponse>>(rawResult.Result);
		AdjustmentListResponse actualReturn = result.Value!;
		actualReturn.Data.Should().BeEquivalentTo(expectedReturn);
		actualReturn.Total.Should().Be(adjustments.Count);
		actualReturn.Offset.Should().Be(0);
		actualReturn.Limit.Should().Be(50);
	}

	[Fact]
	public async Task GetAllAdjustments_WithReceiptId_ReturnsEmptyList_WhenReceiptDoesNotExist()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAdjustmentsByReceiptIdQuery>(q => q.ReceiptId == receiptId && q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Adjustment>([], 0, 0, 50));

		// Act
		Results<Ok<AdjustmentListResponse>, BadRequest<string>> rawResult = await _controller.GetAllAdjustments(receiptId, 0, 50, null, null);

		// Assert
		Ok<AdjustmentListResponse> result = Assert.IsType<Ok<AdjustmentListResponse>>(rawResult.Result);
		AdjustmentListResponse actualReturn = result.Value!;
		actualReturn.Data.Should().BeEmpty();
	}

	[Fact]
	public async Task GetAllAdjustments_WithReceiptId_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAdjustmentsByReceiptIdQuery>(q => q.ReceiptId == receiptId && q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetAllAdjustments(receiptId, 0, 50, null, null);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task CreateAdjustment_ReturnsOkResult_WithCreatedAdjustment()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Adjustment adjustment = AdjustmentGenerator.Generate();
		AdjustmentResponse expectedReturn = _mapper.ToResponse(adjustment);

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateAdjustmentCommand>(c => c.Adjustments.Count == 1 && c.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync([adjustment]);

		CreateAdjustmentRequest controllerInput = AdjustmentDtoGenerator.GenerateCreateRequest();

		// Act
		Ok<AdjustmentResponse> result = await _controller.CreateAdjustment(controllerInput, receiptId);

		// Assert
		AdjustmentResponse actualReturn = result.Value!;
		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task CreateAdjustment_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		CreateAdjustmentRequest controllerInput = AdjustmentDtoGenerator.GenerateCreateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateAdjustmentCommand>(c => c.Adjustments.Count == 1),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.CreateAdjustment(controllerInput, receiptId);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task UpdateAdjustment_ReturnsNoContent_WhenUpdateSucceeds()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		UpdateAdjustmentRequest controllerInput = AdjustmentDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateAdjustmentCommand>(c => c.Adjustments.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateAdjustment(controllerInput, id);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateAdjustment_ReturnsNotFound_WhenUpdateFails()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		UpdateAdjustmentRequest controllerInput = AdjustmentDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateAdjustmentCommand>(c => c.Adjustments.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateAdjustment(controllerInput, id);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateAdjustment_RouteIdIsAuthoritative_WhenBodyIdMismatches()
	{
		// Arrange — RECEIPTS-793: the URL names resource A but the body carries a different
		// id B. The route id must win so the PUT can't silently overwrite the wrong resource.
		Guid routeId = Guid.NewGuid();
		UpdateAdjustmentRequest controllerInput = AdjustmentDtoGenerator.GenerateUpdateRequest();
		controllerInput.Id = Guid.NewGuid(); // body id B, distinct from the route id A

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<UpdateAdjustmentCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateAdjustment(controllerInput, routeId);

		// Assert — the dispatched command targets the route id, and the notification names it too
		Assert.IsType<NoContent>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<UpdateAdjustmentCommand>(c => c.Adjustments[0].Id == routeId),
			It.IsAny<CancellationToken>()), Times.Once);
		_notifierMock.Verify(n => n.NotifyUpdated("adjustment", routeId), Times.Once);
	}

	[Fact]
	public async Task UpdateAdjustment_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		UpdateAdjustmentRequest controllerInput = AdjustmentDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateAdjustmentCommand>(c => c.Adjustments.Count == 1),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.UpdateAdjustment(controllerInput, id);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task DeleteAdjustments_ReturnsNoContent_WhenDeleteSucceeds()
	{
		// Arrange
		List<Guid> ids = [.. AdjustmentGenerator.GenerateList(2).Select(a => a.Id)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteAdjustmentCommand>(c => c.Ids.SequenceEqual(ids)),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.DeleteAdjustments(ids);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task DeleteAdjustments_ReturnsNotFound_WhenDeleteFails()
	{
		// Arrange
		List<Guid> ids = [AdjustmentGenerator.Generate().Id];

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteAdjustmentCommand>(c => c.Ids.SequenceEqual(ids)),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.DeleteAdjustments(ids);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task DeleteAdjustments_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		List<Guid> ids = [.. AdjustmentGenerator.GenerateList(2).Select(a => a.Id)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteAdjustmentCommand>(c => c.Ids.SequenceEqual(ids)),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.DeleteAdjustments(ids);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task RestoreAdjustment_ReturnsNoContent_WhenSuccessful()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.Is<RestoreAdjustmentCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.RestoreAdjustment(id);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task RestoreAdjustment_ReturnsNotFound_WhenEntityDoesNotExist()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.Is<RestoreAdjustmentCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.RestoreAdjustment(id);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task RestoreAdjustment_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.Is<RestoreAdjustmentCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.RestoreAdjustment(id);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}
}
