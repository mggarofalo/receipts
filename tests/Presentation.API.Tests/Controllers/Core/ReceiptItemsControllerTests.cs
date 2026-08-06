using API.Controllers.Core;
using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.ReceiptItem.Create;
using Application.Commands.ReceiptItem.Delete;
using Application.Commands.ReceiptItem.Restore;
using Application.Commands.ReceiptItem.Update;
using Application.Models;
using Application.Queries.Core.ReceiptItem;
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

public class ReceiptItemsControllerTests
{
	private readonly ReceiptItemMapper _mapper;
	private readonly Mock<IMediator> _mediatorMock;
	private readonly Mock<ILogger<ReceiptItemsController>> _loggerMock;
	private readonly Mock<IEntityChangeNotifier> _notifierMock;
	private readonly ReceiptItemsController _controller;

	public ReceiptItemsControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_mapper = new ReceiptItemMapper();
		_loggerMock = ControllerTestHelpers.GetLoggerMock<ReceiptItemsController>();
		_notifierMock = new Mock<IEntityChangeNotifier>();
		_controller = new ReceiptItemsController(_mediatorMock.Object, _mapper, _loggerMock.Object, _notifierMock.Object);
	}

	[Fact]
	public async Task GetReceiptItemById_ReturnsOkResult_WithReceiptItem()
	{
		// Arrange
		ReceiptItem mediatorReturn = ReceiptItemGenerator.Generate();
		ReceiptItemResponse expectedControllerReturn = _mapper.ToResponse(mediatorReturn);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetReceiptItemByIdQuery>(q => q.Id == mediatorReturn.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(mediatorReturn);

		// Act
		Results<Ok<ReceiptItemResponse>, NotFound> result = await _controller.GetReceiptItemById(mediatorReturn.Id);

		// Assert
		Ok<ReceiptItemResponse> okResult = Assert.IsType<Ok<ReceiptItemResponse>>(result.Result);
		ReceiptItemResponse actualControllerReturn = Assert.IsType<ReceiptItemResponse>(okResult.Value);
		actualControllerReturn.Should().BeEquivalentTo(expectedControllerReturn);
	}

	[Fact]
	public async Task GetReceiptItemById_ReturnsNotFound_WhenReceiptItemDoesNotExist()
	{
		// Arrange
		Guid missingReceiptItemId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetReceiptItemByIdQuery>(q => q.Id == missingReceiptItemId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync((ReceiptItem?)null);

		// Act
		Results<Ok<ReceiptItemResponse>, NotFound> result = await _controller.GetReceiptItemById(missingReceiptItemId);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task GetReceiptItemById_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid id = ReceiptItemGenerator.Generate().Id;

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetReceiptItemByIdQuery>(q => q.Id == id),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetReceiptItemById(id);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetAllReceiptItems_ReturnsOkResult_WithListOfReceiptItems()
	{
		// Arrange
		List<ReceiptItem> mediatorReturn = ReceiptItemGenerator.GenerateList(2);
		List<ReceiptItemResponse> expectedControllerReturn = [.. mediatorReturn.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllReceiptItemsQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ReceiptItem>(mediatorReturn, mediatorReturn.Count, 0, 50));

		// Act
		Results<Ok<ReceiptItemListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllReceiptItems(null, 0, 50, null, null);

		// Assert
		Ok<ReceiptItemListResponse> result = Assert.IsType<Ok<ReceiptItemListResponse>>(rawResult.Result);
		ReceiptItemListResponse actualControllerReturn = result.Value!;

		actualControllerReturn.Data.Should().BeEquivalentTo(expectedControllerReturn);
		actualControllerReturn.Total.Should().Be(mediatorReturn.Count);
		actualControllerReturn.Offset.Should().Be(0);
		actualControllerReturn.Limit.Should().Be(50);
	}

	[Theory]
	[InlineData(-1, 50)]
	[InlineData(-100, 50)]
	public async Task GetAllReceiptItems_ReturnsBadRequest_WhenOffsetIsNegative(int offset, int limit)
	{
		// Act
		Results<Ok<ReceiptItemListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllReceiptItems(null, offset, limit, null, null);

		// Assert
		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("offset must be >= 0");
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(0, -1)]
	[InlineData(0, 501)]
	public async Task GetAllReceiptItems_ReturnsBadRequest_WhenLimitIsOutOfRange(int offset, int limit)
	{
		// Act
		Results<Ok<ReceiptItemListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllReceiptItems(null, offset, limit, null, null);

		// Assert
		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Theory]
	[InlineData(-1, 50)]
	[InlineData(-100, 50)]
	public async Task GetDeletedReceiptItems_ReturnsBadRequest_WhenOffsetIsNegative(int offset, int limit)
	{
		// Act
		Results<Ok<ReceiptItemListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetDeletedReceiptItems(offset, limit, null, null);

		// Assert
		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("offset must be >= 0");
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(0, -1)]
	[InlineData(0, 501)]
	public async Task GetDeletedReceiptItems_ReturnsBadRequest_WhenLimitIsOutOfRange(int offset, int limit)
	{
		// Act
		Results<Ok<ReceiptItemListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetDeletedReceiptItems(offset, limit, null, null);

		// Assert
		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Fact]
	public async Task GetAllReceiptItems_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllReceiptItemsQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetAllReceiptItems(null, 0, 50, null, null);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetAllReceiptItems_ForwardsTrimmedSearchQuery_ToMediator()
	{
		// Arrange
		List<ReceiptItem> mediatorReturn = ReceiptItemGenerator.GenerateList(1);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllReceiptItemsQuery>(q => q.Q == "Apples"),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ReceiptItem>(mediatorReturn, mediatorReturn.Count, 0, 50));

		// Act
		Results<Ok<ReceiptItemListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllReceiptItems(null, 0, 50, null, null, "  Apples  ");

		// Assert
		Assert.IsType<Ok<ReceiptItemListResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetAllReceiptItemsQuery>(q => q.Q == "Apples"),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public async Task GetAllReceiptItems_NormalizesBlankSearchQuery_ToNull(string? q)
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetAllReceiptItemsQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ReceiptItem>([], 0, 0, 50));

		// Act
		await _controller.GetAllReceiptItems(null, 0, 50, null, null, q);

		// Assert
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetAllReceiptItemsQuery>(query => query.Q == null),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetAllReceiptItems_WithReceiptId_ReturnsOkResult_WithReceiptItems()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<ReceiptItem> mediatorReturn = ReceiptItemGenerator.GenerateList(2);
		List<ReceiptItemResponse> expectedControllerReturn = [.. mediatorReturn.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetReceiptItemsByReceiptIdQuery>(q => q.ReceiptId == receiptId && q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ReceiptItem>(mediatorReturn, mediatorReturn.Count, 0, 50));

		// Act
		Results<Ok<ReceiptItemListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllReceiptItems(receiptId, 0, 50, null, null);

		// Assert
		Ok<ReceiptItemListResponse> result = Assert.IsType<Ok<ReceiptItemListResponse>>(rawResult.Result);
		ReceiptItemListResponse actualControllerReturn = result.Value!;

		actualControllerReturn.Data.Should().BeEquivalentTo(expectedControllerReturn);
		actualControllerReturn.Total.Should().Be(mediatorReturn.Count);
		actualControllerReturn.Offset.Should().Be(0);
		actualControllerReturn.Limit.Should().Be(50);
	}

	[Fact]
	public async Task GetAllReceiptItems_WithReceiptId_ReturnsOkResult_WithEmptyList_WhenReceiptFoundButNoItems()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetReceiptItemsByReceiptIdQuery>(q => q.ReceiptId == receiptId && q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ReceiptItem>([], 0, 0, 50));

		// Act
		Results<Ok<ReceiptItemListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllReceiptItems(receiptId, 0, 50, null, null);

		// Assert
		Ok<ReceiptItemListResponse> result = Assert.IsType<Ok<ReceiptItemListResponse>>(rawResult.Result);
		ReceiptItemListResponse actualControllerReturn = result.Value!;

		actualControllerReturn.Data.Should().BeEmpty();
	}

	[Fact]
	public async Task GetAllReceiptItems_WithReceiptId_ReturnsEmptyList_WhenReceiptIdNotFound()
	{
		// Arrange
		Guid missingReceiptId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetReceiptItemsByReceiptIdQuery>(q => q.ReceiptId == missingReceiptId && q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ReceiptItem>([], 0, 0, 50));

		// Act
		Results<Ok<ReceiptItemListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllReceiptItems(missingReceiptId, 0, 50, null, null);

		// Assert
		Ok<ReceiptItemListResponse> result = Assert.IsType<Ok<ReceiptItemListResponse>>(rawResult.Result);
		ReceiptItemListResponse actualReturn = result.Value!;
		actualReturn.Data.Should().BeEmpty();
	}

	[Fact]
	public async Task GetAllReceiptItems_WithReceiptId_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetReceiptItemsByReceiptIdQuery>(q => q.ReceiptId == receiptId && q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetAllReceiptItems(receiptId, 0, 50, null, null);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task CreateReceiptItem_ReturnsOkResult_WithCreatedReceiptItem()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		ReceiptItem receiptItem = ReceiptItemGenerator.Generate();
		ReceiptItemResponse expectedReturn = _mapper.ToResponse(receiptItem);

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateReceiptItemCommand>(c => c.ReceiptItems.Count == 1 && c.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync([receiptItem]);

		CreateReceiptItemRequest controllerInput = ReceiptItemDtoGenerator.GenerateCreateRequest();

		// Act
		Ok<ReceiptItemResponse> result = await _controller.CreateReceiptItem(controllerInput, receiptId);

		// Assert
		ReceiptItemResponse actualReturn = result.Value!;

		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task CreateReceiptItem_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		CreateReceiptItemRequest controllerInput = ReceiptItemDtoGenerator.GenerateCreateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateReceiptItemCommand>(c => c.ReceiptItems.Count == 1 && c.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.CreateReceiptItem(controllerInput, receiptId);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task CreateReceiptItems_ReturnsOkResult_WithCreatedReceiptItems()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<ReceiptItem> mediatorReturn = ReceiptItemGenerator.GenerateList(2);
		List<ReceiptItemResponse> expectedControllerReturn = [.. mediatorReturn.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateReceiptItemCommand>(c => c.ReceiptItems.Count == mediatorReturn.Count && c.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(mediatorReturn);

		List<CreateReceiptItemRequest> controllerInput = ReceiptItemDtoGenerator.GenerateCreateRequestList(2);

		// Act
		Ok<List<ReceiptItemResponse>> result = await _controller.CreateReceiptItems(controllerInput, receiptId);

		// Assert
		List<ReceiptItemResponse> actualControllerReturn = result.Value!;

		actualControllerReturn.Should().BeEquivalentTo(expectedControllerReturn);
	}

	[Fact]
	public async Task CreateReceiptItems_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<CreateReceiptItemRequest> controllerInput = ReceiptItemDtoGenerator.GenerateCreateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateReceiptItemCommand>(c => c.ReceiptItems.Count == controllerInput.Count && c.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.CreateReceiptItems(controllerInput, receiptId);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task UpdateReceiptItem_ReturnsNoContent_WhenUpdateSucceeds()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		UpdateReceiptItemRequest controllerInput = ReceiptItemDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateReceiptItemCommand>(c => c.ReceiptItems.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateReceiptItem(controllerInput, id);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateReceiptItem_ReturnsNotFound_WhenUpdateFails()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		UpdateReceiptItemRequest controllerInput = ReceiptItemDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateReceiptItemCommand>(c => c.ReceiptItems.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateReceiptItem(controllerInput, id);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateReceiptItem_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		UpdateReceiptItemRequest controllerInput = ReceiptItemDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateReceiptItemCommand>(c => c.ReceiptItems.Count == 1),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.UpdateReceiptItem(controllerInput, id);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task UpdateReceiptItems_ReturnsNoContent_WhenUpdateSucceeds()
	{
		// Arrange
		List<UpdateReceiptItemRequest> controllerInput = ReceiptItemDtoGenerator.GenerateUpdateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateReceiptItemCommand>(c => c.ReceiptItems.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateReceiptItems(controllerInput);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateReceiptItems_ReturnsNotFound_WhenUpdateFails()
	{
		// Arrange
		List<UpdateReceiptItemRequest> controllerInput = ReceiptItemDtoGenerator.GenerateUpdateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateReceiptItemCommand>(c => c.ReceiptItems.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateReceiptItems(controllerInput);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateReceiptItems_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		List<UpdateReceiptItemRequest> controllerInput = ReceiptItemDtoGenerator.GenerateUpdateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateReceiptItemCommand>(c => c.ReceiptItems.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.UpdateReceiptItems(controllerInput);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task DeleteReceiptItems_ReturnsNoContent_WhenDeleteSucceeds()
	{
		// Arrange
		List<Guid> controllerInput = [.. ReceiptItemGenerator.GenerateList(2).Select(a => a.Id)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteReceiptItemCommand>(c => c.Ids.SequenceEqual(controllerInput)),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.DeleteReceiptItems(controllerInput);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task DeleteReceiptItems_ReturnsNotFound_WhenDeleteFails()
	{
		// Arrange
		List<Guid> controllerInput = [.. ReceiptItemGenerator.GenerateList(2).Select(ri => ri.Id)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteReceiptItemCommand>(c => c.Ids.SequenceEqual(controllerInput)),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.DeleteReceiptItems(controllerInput);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task DeleteReceiptItems_ReturnsNotFound_WhenMultipleReceiptItemsDeleteFails()
	{
		// Arrange
		List<Guid> controllerInput = [.. ReceiptItemGenerator.GenerateList(2).Select(ri => ri.Id)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteReceiptItemCommand>(c => c.Ids.SequenceEqual(controllerInput)),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.DeleteReceiptItems(controllerInput);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task DeleteReceiptItems_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		List<Guid> controllerInput = [.. ReceiptItemGenerator.GenerateList(2).Select(ri => ri.Id)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteReceiptItemCommand>(c => c.Ids.SequenceEqual(controllerInput)),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.DeleteReceiptItems(controllerInput);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task RestoreReceiptItem_ReturnsNoContent_WhenSuccessful()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.Is<RestoreReceiptItemCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.RestoreReceiptItem(id);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task RestoreReceiptItem_ReturnsNotFound_WhenEntityDoesNotExist()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.Is<RestoreReceiptItemCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.RestoreReceiptItem(id);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task RestoreReceiptItem_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.Is<RestoreReceiptItemCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.RestoreReceiptItem(id);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetReceiptItemById_IncludesNormalizedDescriptionFields_WhenPopulated()
	{
		// Arrange - RECEIPTS-583: normalized description fields surface on the response
		Guid normalizedId = Guid.NewGuid();
		ReceiptItem mediatorReturn = ReceiptItemGenerator.Generate();
		mediatorReturn.NormalizedDescriptionId = normalizedId;
		mediatorReturn.NormalizedDescriptionName = "Grapes";
		mediatorReturn.NormalizedDescriptionMatchScore = 0.87;

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetReceiptItemByIdQuery>(q => q.Id == mediatorReturn.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(mediatorReturn);

		// Act
		Results<Ok<ReceiptItemResponse>, NotFound> result = await _controller.GetReceiptItemById(mediatorReturn.Id);

		// Assert
		Ok<ReceiptItemResponse> okResult = Assert.IsType<Ok<ReceiptItemResponse>>(result.Result);
		ReceiptItemResponse response = Assert.IsType<ReceiptItemResponse>(okResult.Value);
		response.NormalizedDescriptionId.Should().Be(normalizedId);
		response.NormalizedDescriptionName.Should().Be("Grapes");
		response.NormalizedDescriptionMatchScore.Should().Be(0.87);
	}

	[Fact]
	public async Task GetReceiptItemById_NormalizedDescriptionFieldsAreNull_WhenUnresolved()
	{
		// Arrange - RECEIPTS-583: unresolved items expose null normalized fields
		ReceiptItem mediatorReturn = ReceiptItemGenerator.Generate();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetReceiptItemByIdQuery>(q => q.Id == mediatorReturn.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(mediatorReturn);

		// Act
		Results<Ok<ReceiptItemResponse>, NotFound> result = await _controller.GetReceiptItemById(mediatorReturn.Id);

		// Assert
		Ok<ReceiptItemResponse> okResult = Assert.IsType<Ok<ReceiptItemResponse>>(result.Result);
		ReceiptItemResponse response = Assert.IsType<ReceiptItemResponse>(okResult.Value);
		response.NormalizedDescriptionId.Should().BeNull();
		response.NormalizedDescriptionName.Should().BeNull();
		response.NormalizedDescriptionMatchScore.Should().BeNull();
	}
}
