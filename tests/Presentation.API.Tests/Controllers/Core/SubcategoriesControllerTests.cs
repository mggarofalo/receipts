using API.Controllers.Core;
using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.Subcategory.Create;
using Application.Commands.Subcategory.Delete;
using Application.Commands.Subcategory.Update;
using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.Subcategory;
using Domain.Core;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SampleData.Domain.Core;
using SampleData.Dtos.Core;

namespace Presentation.API.Tests.Controllers.Core;

public class SubcategoriesControllerTests
{
	private readonly SubcategoryMapper _mapper;
	private readonly Mock<IMediator> _mediatorMock;
	private readonly Mock<ILogger<SubcategoriesController>> _loggerMock;
	private readonly Mock<IEntityChangeNotifier> _notifierMock;
	private readonly Mock<ISubcategoryService> _subcategoryServiceMock;
	private readonly SubcategoriesController _controller;

	public SubcategoriesControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_mapper = new SubcategoryMapper();
		_loggerMock = ControllerTestHelpers.GetLoggerMock<SubcategoriesController>();
		_notifierMock = new Mock<IEntityChangeNotifier>();
		_subcategoryServiceMock = new Mock<ISubcategoryService>();
		_controller = new SubcategoriesController(_mediatorMock.Object, _mapper, _loggerMock.Object, _notifierMock.Object, _subcategoryServiceMock.Object);
		_controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
	}

	// ── GetSubcategoryById ──────────────────────────────────

	[Fact]
	public async Task GetSubcategoryById_ReturnsOkResult_WhenSubcategoryExists()
	{
		Subcategory subcategory = SubcategoryGenerator.Generate();
		SubcategoryResponse expectedReturn = _mapper.ToResponse(subcategory);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSubcategoryByIdQuery>(q => q.Id == subcategory.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(subcategory);

		Results<Ok<SubcategoryResponse>, NotFound> result = await _controller.GetSubcategoryById(subcategory.Id);

		Ok<SubcategoryResponse> okResult = Assert.IsType<Ok<SubcategoryResponse>>(result.Result);
		SubcategoryResponse actualReturn = Assert.IsType<SubcategoryResponse>(okResult.Value);
		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task GetSubcategoryById_ReturnsNotFound_WhenSubcategoryDoesNotExist()
	{
		Guid missingId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSubcategoryByIdQuery>(q => q.Id == missingId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync((Subcategory?)null);

		Results<Ok<SubcategoryResponse>, NotFound> result = await _controller.GetSubcategoryById(missingId);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task GetSubcategoryById_ThrowsException_WhenMediatorFails()
	{
		Guid id = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSubcategoryByIdQuery>(q => q.Id == id),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.GetSubcategoryById(id);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── GetAllSubcategories ─────────────────────────────────

	[Fact]
	public async Task GetAllSubcategories_ReturnsOkResult_WithListOfSubcategories()
	{
		List<Subcategory> subcategories = SubcategoryGenerator.GenerateList(2);
		List<SubcategoryResponse> expectedReturn = [.. subcategories.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllSubcategoriesQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Subcategory>(subcategories, subcategories.Count, 0, 50));

		Results<Ok<SubcategoryListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllSubcategories(null, null, 0, 50, null, null);

		Ok<SubcategoryListResponse> result = Assert.IsType<Ok<SubcategoryListResponse>>(rawResult.Result);
		SubcategoryListResponse actualReturn = result.Value!;
		actualReturn.Data.Should().BeEquivalentTo(expectedReturn);
		actualReturn.Total.Should().Be(subcategories.Count);
		actualReturn.Offset.Should().Be(0);
		actualReturn.Limit.Should().Be(50);
	}

	[Fact]
	public async Task GetAllSubcategories_WithCategoryId_ReturnsFilteredSubcategories()
	{
		Guid categoryId = Guid.NewGuid();
		List<Subcategory> subcategories = SubcategoryGenerator.GenerateList(2);
		List<SubcategoryResponse> expectedReturn = [.. subcategories.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSubcategoriesByCategoryIdQuery>(q => q.CategoryId == categoryId && q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Subcategory>(subcategories, subcategories.Count, 0, 50));

		Results<Ok<SubcategoryListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllSubcategories(categoryId, null, 0, 50, null, null);

		Ok<SubcategoryListResponse> result = Assert.IsType<Ok<SubcategoryListResponse>>(rawResult.Result);
		SubcategoryListResponse actualReturn = result.Value!;
		actualReturn.Data.Should().BeEquivalentTo(expectedReturn);
		actualReturn.Total.Should().Be(subcategories.Count);
	}

	[Theory]
	[InlineData(-1, 50)]
	[InlineData(-100, 50)]
	public async Task GetAllSubcategories_ReturnsBadRequest_WhenOffsetIsNegative(int offset, int limit)
	{
		Results<Ok<SubcategoryListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllSubcategories(null, null, offset, limit, null, null);

		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("offset must be >= 0");
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(0, -1)]
	[InlineData(0, 501)]
	public async Task GetAllSubcategories_ReturnsBadRequest_WhenLimitIsOutOfRange(int offset, int limit)
	{
		Results<Ok<SubcategoryListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllSubcategories(null, null, offset, limit, null, null);

		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task GetAllSubcategories_PassesIsActiveFilter_ToQuery(bool isActive)
	{
		List<Subcategory> subcategories = SubcategoryGenerator.GenerateList(1);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllSubcategoriesQuery>(q => q.IsActive == isActive),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Subcategory>(subcategories, subcategories.Count, 0, 50));

		Results<Ok<SubcategoryListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllSubcategories(null, isActive, 0, 50, null, null);

		Ok<SubcategoryListResponse> result = Assert.IsType<Ok<SubcategoryListResponse>>(rawResult.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetAllSubcategoriesQuery>(q => q.IsActive == isActive),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetAllSubcategories_WithCategoryId_PassesIsActiveFilter()
	{
		Guid categoryId = Guid.NewGuid();
		List<Subcategory> subcategories = SubcategoryGenerator.GenerateList(1);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSubcategoriesByCategoryIdQuery>(q => q.CategoryId == categoryId && q.IsActive == true),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Subcategory>(subcategories, subcategories.Count, 0, 50));

		Results<Ok<SubcategoryListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllSubcategories(categoryId, true, 0, 50, null, null);

		Ok<SubcategoryListResponse> result = Assert.IsType<Ok<SubcategoryListResponse>>(rawResult.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetSubcategoriesByCategoryIdQuery>(q => q.CategoryId == categoryId && q.IsActive == true),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetAllSubcategories_ThrowsException_WhenMediatorFails()
	{
		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllSubcategoriesQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.GetAllSubcategories(null, null, 0, 50, null, null);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── CreateSubcategory ───────────────────────────────────

	[Fact]
	public async Task CreateSubcategory_ReturnsOkResult_WithCreatedSubcategory()
	{
		Subcategory subcategory = SubcategoryGenerator.Generate();
		SubcategoryResponse expectedReturn = _mapper.ToResponse(subcategory);

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateSubcategoryCommand>(c => c.Subcategories.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync([subcategory]);

		CreateSubcategoryRequest controllerInput = SubcategoryDtoGenerator.GenerateCreateRequest();

		Ok<SubcategoryResponse> result = await _controller.CreateSubcategory(controllerInput);

		SubcategoryResponse actualReturn = result.Value!;
		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task CreateSubcategory_ThrowsException_WhenMediatorFails()
	{
		CreateSubcategoryRequest controllerInput = SubcategoryDtoGenerator.GenerateCreateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateSubcategoryCommand>(c => c.Subcategories.Count == 1),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.CreateSubcategory(controllerInput);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── CreateSubcategories (batch) ─────────────────────────

	[Fact]
	public async Task CreateSubcategories_ReturnsOkResult_WithCreatedSubcategories()
	{
		List<Subcategory> subcategories = SubcategoryGenerator.GenerateList(2);
		List<SubcategoryResponse> expectedReturn = [.. subcategories.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateSubcategoryCommand>(c => c.Subcategories.Count == subcategories.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(subcategories);

		List<CreateSubcategoryRequest> controllerInput = SubcategoryDtoGenerator.GenerateCreateRequestList(2);

		Ok<List<SubcategoryResponse>> result = await _controller.CreateSubcategories(controllerInput);

		List<SubcategoryResponse> actualReturn = result.Value!;
		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task CreateSubcategories_ThrowsException_WhenMediatorFails()
	{
		List<CreateSubcategoryRequest> controllerInput = SubcategoryDtoGenerator.GenerateCreateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateSubcategoryCommand>(c => c.Subcategories.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.CreateSubcategories(controllerInput);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── UpdateSubcategory ───────────────────────────────────

	[Fact]
	public async Task UpdateSubcategory_ReturnsNoContent_WhenUpdateSucceeds()
	{
		UpdateSubcategoryRequest controllerInput = SubcategoryDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateSubcategoryCommand>(c => c.Subcategories.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		Results<NoContent, NotFound> result = await _controller.UpdateSubcategory(controllerInput.Id, controllerInput);

		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateSubcategory_ReturnsNotFound_WhenUpdateFails()
	{
		UpdateSubcategoryRequest controllerInput = SubcategoryDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateSubcategoryCommand>(c => c.Subcategories.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		Results<NoContent, NotFound> result = await _controller.UpdateSubcategory(controllerInput.Id, controllerInput);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateSubcategory_ThrowsException_WhenMediatorFails()
	{
		UpdateSubcategoryRequest controllerInput = SubcategoryDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateSubcategoryCommand>(c => c.Subcategories.Count == 1),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.UpdateSubcategory(controllerInput.Id, controllerInput);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── UpdateSubcategories (batch) ─────────────────────────

	[Fact]
	public async Task UpdateSubcategories_ReturnsNoContent_WhenUpdateSucceeds()
	{
		List<UpdateSubcategoryRequest> controllerInput = SubcategoryDtoGenerator.GenerateUpdateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateSubcategoryCommand>(c => c.Subcategories.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		Results<NoContent, NotFound> result = await _controller.UpdateSubcategories(controllerInput);

		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateSubcategories_ReturnsNotFound_WhenUpdateFails()
	{
		List<UpdateSubcategoryRequest> controllerInput = SubcategoryDtoGenerator.GenerateUpdateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateSubcategoryCommand>(c => c.Subcategories.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		Results<NoContent, NotFound> result = await _controller.UpdateSubcategories(controllerInput);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateSubcategories_ThrowsException_WhenMediatorFails()
	{
		List<UpdateSubcategoryRequest> controllerInput = SubcategoryDtoGenerator.GenerateUpdateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateSubcategoryCommand>(c => c.Subcategories.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.UpdateSubcategories(controllerInput);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── DeleteSubcategory ──────────────────────────────────

	[Fact]
	public async Task DeleteSubcategory_ReturnsNoContent_WhenDeleteSucceeds()
	{
		// Arrange
		Subcategory subcategory = SubcategoryGenerator.Generate();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSubcategoryByIdQuery>(q => q.Id == subcategory.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(subcategory);

		_subcategoryServiceMock.Setup(s => s.GetReceiptItemCountBySubcategoryNameAsync(subcategory.Name, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteSubcategoryCommand>(c => c.Ids.Contains(subcategory.Id)),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await _controller.DeleteSubcategory(subcategory.Id);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task DeleteSubcategory_ReturnsNotFound_WhenSubcategoryDoesNotExist()
	{
		// Arrange
		Guid id = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSubcategoryByIdQuery>(q => q.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync((Subcategory?)null);

		// Act
		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await _controller.DeleteSubcategory(id);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task DeleteSubcategory_ReturnsConflict_WhenReceiptItemsExist()
	{
		// Arrange
		Subcategory subcategory = SubcategoryGenerator.Generate();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSubcategoryByIdQuery>(q => q.Id == subcategory.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(subcategory);

		_subcategoryServiceMock.Setup(s => s.GetReceiptItemCountBySubcategoryNameAsync(subcategory.Name, It.IsAny<CancellationToken>()))
			.ReturnsAsync(5);

		_subcategoryServiceMock.Setup(s => s.GetAffectedReceiptsBySubcategoryNameAsync(subcategory.Name, 20, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new List<(Guid ReceiptId, DateOnly Date, string Location)>
			{
				(Guid.NewGuid(), new DateOnly(2026, 1, 15), "Store A"),
				(Guid.NewGuid(), new DateOnly(2026, 2, 20), "Store B"),
			});

		// Act
		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await _controller.DeleteSubcategory(subcategory.Id);

		// Assert
		Assert.IsType<Conflict<ProblemDetails>>(result.Result);
	}

	[Fact]
	public async Task DeleteSubcategory_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Subcategory subcategory = SubcategoryGenerator.Generate();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSubcategoryByIdQuery>(q => q.Id == subcategory.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(subcategory);

		_subcategoryServiceMock.Setup(s => s.GetReceiptItemCountBySubcategoryNameAsync(subcategory.Name, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteSubcategoryCommand>(c => c.Ids.Contains(subcategory.Id)),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.DeleteSubcategory(subcategory.Id);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}
}
