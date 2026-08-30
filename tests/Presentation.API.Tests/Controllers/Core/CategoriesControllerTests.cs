using API.Controllers.Core;
using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.Category.Create;
using Application.Commands.Category.Delete;
using Application.Commands.Category.Update;
using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.Category;
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

public class CategoriesControllerTests
{
	private readonly CategoryMapper _mapper;
	private readonly Mock<IMediator> _mediatorMock;
	private readonly Mock<ILogger<CategoriesController>> _loggerMock;
	private readonly Mock<IEntityChangeNotifier> _notifierMock;
	private readonly Mock<ICategoryService> _categoryServiceMock;
	private readonly CategoriesController _controller;

	public CategoriesControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_mapper = new CategoryMapper();
		_loggerMock = ControllerTestHelpers.GetLoggerMock<CategoriesController>();
		_notifierMock = new Mock<IEntityChangeNotifier>();
		_categoryServiceMock = new Mock<ICategoryService>();
		_controller = new CategoriesController(_mediatorMock.Object, _mapper, _loggerMock.Object, _notifierMock.Object, _categoryServiceMock.Object);
		_controller.ControllerContext = new ControllerContext
		{
			HttpContext = new DefaultHttpContext()
		};
	}

	// ── GetCategoryById ─────────────────────────────────────

	[Fact]
	public async Task GetCategoryById_ReturnsOkResult_WhenCategoryExists()
	{
		Category category = CategoryGenerator.Generate();
		CategoryResponse expectedReturn = _mapper.ToResponse(category);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetCategoryByIdQuery>(q => q.Id == category.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(category);

		Results<Ok<CategoryResponse>, NotFound> result = await _controller.GetCategoryById(category.Id);

		Ok<CategoryResponse> okResult = Assert.IsType<Ok<CategoryResponse>>(result.Result);
		CategoryResponse actualReturn = Assert.IsType<CategoryResponse>(okResult.Value);
		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task GetCategoryById_ReturnsNotFound_WhenCategoryDoesNotExist()
	{
		Guid missingId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetCategoryByIdQuery>(q => q.Id == missingId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync((Category?)null);

		Results<Ok<CategoryResponse>, NotFound> result = await _controller.GetCategoryById(missingId);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task GetCategoryById_ThrowsException_WhenMediatorFails()
	{
		Guid id = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetCategoryByIdQuery>(q => q.Id == id),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.GetCategoryById(id);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── GetAllCategories ────────────────────────────────────

	[Fact]
	public async Task GetAllCategories_ReturnsOkResult_WithListOfCategories()
	{
		List<Category> categories = CategoryGenerator.GenerateList(2);
		List<CategoryResponse> expectedReturn = [.. categories.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllCategoriesQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Category>(categories, categories.Count, 0, 50));

		Results<Ok<CategoryListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllCategories(null, null, 0, 50, null, null);

		Ok<CategoryListResponse> result = Assert.IsType<Ok<CategoryListResponse>>(rawResult.Result);
		CategoryListResponse actualReturn = result.Value!;
		actualReturn.Data.Should().BeEquivalentTo(expectedReturn);
		actualReturn.Total.Should().Be(categories.Count);
		actualReturn.Offset.Should().Be(0);
		actualReturn.Limit.Should().Be(50);
	}

	[Theory]
	[InlineData(-1, 50)]
	[InlineData(-100, 50)]
	public async Task GetAllCategories_ReturnsBadRequest_WhenOffsetIsNegative(int offset, int limit)
	{
		Results<Ok<CategoryListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllCategories(null, null, offset, limit, null, null);

		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("offset must be >= 0");
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(0, -1)]
	[InlineData(0, 501)]
	public async Task GetAllCategories_ReturnsBadRequest_WhenLimitIsOutOfRange(int offset, int limit)
	{
		Results<Ok<CategoryListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllCategories(null, null, offset, limit, null, null);

		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task GetAllCategories_PassesIsActiveFilter_ToQuery(bool isActive)
	{
		List<Category> categories = CategoryGenerator.GenerateList(1);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllCategoriesQuery>(q => q.IsActive == isActive && q.Q == "food"),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Category>(categories, categories.Count, 0, 50));

		Results<Ok<CategoryListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllCategories(isActive, "food", 0, 50, null, null);

		Ok<CategoryListResponse> result = Assert.IsType<Ok<CategoryListResponse>>(rawResult.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetAllCategoriesQuery>(q => q.IsActive == isActive && q.Q == "food"),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetAllCategories_PassesNullIsActive_WhenNotProvided()
	{
		List<Category> categories = CategoryGenerator.GenerateList(1);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllCategoriesQuery>(q => q.IsActive == null),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Category>(categories, categories.Count, 0, 50));

		Results<Ok<CategoryListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllCategories(null, null, 0, 50, null, null);

		Assert.IsType<Ok<CategoryListResponse>>(rawResult.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetAllCategoriesQuery>(q => q.IsActive == null),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetAllCategories_ThrowsException_WhenMediatorFails()
	{
		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllCategoriesQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.GetAllCategories(null, null, 0, 50, null, null);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── CreateCategory ──────────────────────────────────────

	[Fact]
	public async Task CreateCategory_ReturnsOkResult_WithCreatedCategory()
	{
		Category category = CategoryGenerator.Generate();
		CategoryResponse expectedReturn = _mapper.ToResponse(category);

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateCategoryCommand>(c => c.Categories.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync([category]);

		CreateCategoryRequest controllerInput = CategoryDtoGenerator.GenerateCreateRequest();

		Ok<CategoryResponse> result = await _controller.CreateCategory(controllerInput);

		CategoryResponse actualReturn = result.Value!;
		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task CreateCategory_ThrowsException_WhenMediatorFails()
	{
		CreateCategoryRequest controllerInput = CategoryDtoGenerator.GenerateCreateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateCategoryCommand>(c => c.Categories.Count == 1),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.CreateCategory(controllerInput);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── CreateCategories (batch) ────────────────────────────

	[Fact]
	public async Task CreateCategories_ReturnsOkResult_WithCreatedCategories()
	{
		List<Category> categories = CategoryGenerator.GenerateList(2);
		List<CategoryResponse> expectedReturn = [.. categories.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateCategoryCommand>(c => c.Categories.Count == categories.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(categories);

		List<CreateCategoryRequest> controllerInput = CategoryDtoGenerator.GenerateCreateRequestList(2);

		Ok<List<CategoryResponse>> result = await _controller.CreateCategories(controllerInput);

		List<CategoryResponse> actualReturn = result.Value!;
		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task CreateCategories_ThrowsException_WhenMediatorFails()
	{
		List<CreateCategoryRequest> controllerInput = CategoryDtoGenerator.GenerateCreateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateCategoryCommand>(c => c.Categories.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.CreateCategories(controllerInput);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── UpdateCategory ──────────────────────────────────────

	[Fact]
	public async Task UpdateCategory_ReturnsNoContent_WhenUpdateSucceeds()
	{
		UpdateCategoryRequest controllerInput = CategoryDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateCategoryCommand>(c => c.Categories.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		Results<NoContent, NotFound> result = await _controller.UpdateCategory(controllerInput.Id, controllerInput);

		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateCategory_ReturnsNotFound_WhenUpdateFails()
	{
		UpdateCategoryRequest controllerInput = CategoryDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateCategoryCommand>(c => c.Categories.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		Results<NoContent, NotFound> result = await _controller.UpdateCategory(controllerInput.Id, controllerInput);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateCategory_ThrowsException_WhenMediatorFails()
	{
		UpdateCategoryRequest controllerInput = CategoryDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateCategoryCommand>(c => c.Categories.Count == 1),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.UpdateCategory(controllerInput.Id, controllerInput);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── UpdateCategories (batch) ────────────────────────────

	[Fact]
	public async Task UpdateCategories_ReturnsNoContent_WhenUpdateSucceeds()
	{
		List<UpdateCategoryRequest> controllerInput = CategoryDtoGenerator.GenerateUpdateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateCategoryCommand>(c => c.Categories.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		Results<NoContent, NotFound> result = await _controller.UpdateCategories(controllerInput);

		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateCategories_ReturnsNotFound_WhenUpdateFails()
	{
		List<UpdateCategoryRequest> controllerInput = CategoryDtoGenerator.GenerateUpdateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateCategoryCommand>(c => c.Categories.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		Results<NoContent, NotFound> result = await _controller.UpdateCategories(controllerInput);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateCategories_ThrowsException_WhenMediatorFails()
	{
		List<UpdateCategoryRequest> controllerInput = CategoryDtoGenerator.GenerateUpdateRequestList(2);
		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateCategoryCommand>(c => c.Categories.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.UpdateCategories(controllerInput);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── DeleteCategory ──────────────────────────────────────

	[Fact]
	public async Task DeleteCategory_ReturnsNoContent_WhenDeleteSucceeds()
	{
		Category category = CategoryGenerator.Generate();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetCategoryByIdQuery>(q => q.Id == category.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(category);

		_categoryServiceMock.Setup(s => s.GetReceiptItemCountByCategoryNameAsync(category.Name, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		_categoryServiceMock.Setup(s => s.GetSubcategoryNamesAsync(category.Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		_categoryServiceMock.Setup(s => s.GetReceiptItemCountBySubcategoryNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteCategoryCommand>(c => c.Ids.Contains(category.Id)),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await _controller.DeleteCategory(category.Id);

		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task DeleteCategory_ReturnsNotFound_WhenCategoryDoesNotExist()
	{
		Guid id = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetCategoryByIdQuery>(q => q.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync((Category?)null);

		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await _controller.DeleteCategory(id);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task DeleteCategory_ReturnsConflict_WhenReceiptItemsReferenceCategoryName()
	{
		Category category = CategoryGenerator.Generate();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetCategoryByIdQuery>(q => q.Id == category.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(category);

		_categoryServiceMock.Setup(s => s.GetReceiptItemCountByCategoryNameAsync(category.Name, It.IsAny<CancellationToken>()))
			.ReturnsAsync(5);

		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await _controller.DeleteCategory(category.Id);

		Assert.IsType<Conflict<ProblemDetails>>(result.Result);
	}

	[Fact]
	public async Task DeleteCategory_ReturnsConflict_WhenReceiptItemsReferenceSubcategoryNames()
	{
		Category category = CategoryGenerator.Generate();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetCategoryByIdQuery>(q => q.Id == category.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(category);

		_categoryServiceMock.Setup(s => s.GetReceiptItemCountByCategoryNameAsync(category.Name, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		_categoryServiceMock.Setup(s => s.GetSubcategoryNamesAsync(category.Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(["Produce", "Dairy"]);

		_categoryServiceMock.Setup(s => s.GetReceiptItemCountBySubcategoryNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(3);

		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await _controller.DeleteCategory(category.Id);

		Assert.IsType<Conflict<ProblemDetails>>(result.Result);
	}

	[Fact]
	public async Task DeleteCategory_ThrowsException_WhenMediatorFails()
	{
		Category category = CategoryGenerator.Generate();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetCategoryByIdQuery>(q => q.Id == category.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(category);

		_categoryServiceMock.Setup(s => s.GetReceiptItemCountByCategoryNameAsync(category.Name, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		_categoryServiceMock.Setup(s => s.GetSubcategoryNamesAsync(category.Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		_categoryServiceMock.Setup(s => s.GetReceiptItemCountBySubcategoryNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteCategoryCommand>(c => c.Ids.Contains(category.Id)),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.DeleteCategory(category.Id);

		await act.Should().ThrowAsync<Exception>();
	}
}
