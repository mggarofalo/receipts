using API.Controllers.Core;
using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.ItemTemplate.Create;
using Application.Commands.ItemTemplate.Delete;
using Application.Commands.ItemTemplate.Restore;
using Application.Commands.ItemTemplate.Update;
using Application.Models;
using Application.Queries.Core.ItemTemplate;
using Application.Queries.Core.ItemTemplate.GetHistoryCandidates;
using Domain.Core;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Moq;
using SampleData.Domain.Core;
using SampleData.Dtos.Core;

namespace Presentation.API.Tests.Controllers.Core;

public class ItemTemplatesControllerTests
{
	private readonly ItemTemplateMapper _mapper;
	private readonly Mock<IMediator> _mediatorMock;
	private readonly Mock<ILogger<ItemTemplatesController>> _loggerMock;
	private readonly Mock<IEntityChangeNotifier> _notifierMock;
	private readonly ItemTemplatesController _controller;

	public ItemTemplatesControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_mapper = new ItemTemplateMapper();
		_loggerMock = ControllerTestHelpers.GetLoggerMock<ItemTemplatesController>();
		_notifierMock = new Mock<IEntityChangeNotifier>();
		_controller = new ItemTemplatesController(_mediatorMock.Object, _mapper, _loggerMock.Object, _notifierMock.Object);
	}

	// ── GetItemTemplateById ─────────────────────────────────

	[Fact]
	public async Task GetItemTemplateById_ReturnsOkResult_WhenItemTemplateExists()
	{
		ItemTemplate itemTemplate = ItemTemplateGenerator.Generate();
		ItemTemplateResponse expectedReturn = _mapper.ToResponse(itemTemplate);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetItemTemplateByIdQuery>(q => q.Id == itemTemplate.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(itemTemplate);

		Results<Ok<ItemTemplateResponse>, NotFound> result = await _controller.GetItemTemplateById(itemTemplate.Id);

		Ok<ItemTemplateResponse> okResult = Assert.IsType<Ok<ItemTemplateResponse>>(result.Result);
		ItemTemplateResponse actualReturn = Assert.IsType<ItemTemplateResponse>(okResult.Value);
		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task GetItemTemplateById_ReturnsNotFound_WhenItemTemplateDoesNotExist()
	{
		Guid missingId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetItemTemplateByIdQuery>(q => q.Id == missingId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync((ItemTemplate?)null);

		Results<Ok<ItemTemplateResponse>, NotFound> result = await _controller.GetItemTemplateById(missingId);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task GetItemTemplateById_ThrowsException_WhenMediatorFails()
	{
		Guid id = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetItemTemplateByIdQuery>(q => q.Id == id),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.GetItemTemplateById(id);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── GetAllItemTemplates ─────────────────────────────────

	[Fact]
	public async Task GetAllItemTemplates_ReturnsOkResult_WithListOfItemTemplates()
	{
		List<ItemTemplate> itemTemplates = ItemTemplateGenerator.GenerateList(2);
		List<ItemTemplateResponse> expectedReturn = [.. itemTemplates.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllItemTemplatesQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ItemTemplate>(itemTemplates, itemTemplates.Count, 0, 50));

		Results<Ok<ItemTemplateListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllItemTemplates(0, 50, null, null);

		Ok<ItemTemplateListResponse> result = Assert.IsType<Ok<ItemTemplateListResponse>>(rawResult.Result);
		ItemTemplateListResponse actualReturn = result.Value!;
		actualReturn.Data.Should().BeEquivalentTo(expectedReturn);
		actualReturn.Total.Should().Be(itemTemplates.Count);
		actualReturn.Offset.Should().Be(0);
		actualReturn.Limit.Should().Be(50);
	}

	[Theory]
	[InlineData(-1, 50)]
	[InlineData(-100, 50)]
	public async Task GetAllItemTemplates_ReturnsBadRequest_WhenOffsetIsNegative(int offset, int limit)
	{
		Results<Ok<ItemTemplateListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllItemTemplates(offset, limit, null, null);

		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("offset must be >= 0");
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(0, -1)]
	[InlineData(0, 501)]
	public async Task GetAllItemTemplates_ReturnsBadRequest_WhenLimitIsOutOfRange(int offset, int limit)
	{
		Results<Ok<ItemTemplateListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllItemTemplates(offset, limit, null, null);

		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Theory]
	[InlineData(-1, 50)]
	[InlineData(-100, 50)]
	public async Task GetDeletedItemTemplates_ReturnsBadRequest_WhenOffsetIsNegative(int offset, int limit)
	{
		Results<Ok<ItemTemplateListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetDeletedItemTemplates(offset, limit, null, null);

		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("offset must be >= 0");
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(0, -1)]
	[InlineData(0, 501)]
	public async Task GetDeletedItemTemplates_ReturnsBadRequest_WhenLimitIsOutOfRange(int offset, int limit)
	{
		Results<Ok<ItemTemplateListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetDeletedItemTemplates(offset, limit, null, null);

		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Fact]
	public async Task GetAllItemTemplates_ThrowsException_WhenMediatorFails()
	{
		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllItemTemplatesQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.GetAllItemTemplates(0, 50, null, null);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── GetDeletedItemTemplates ─────────────────────────────

	[Fact]
	public async Task GetDeletedItemTemplates_ReturnsOkResult_WithListOfItemTemplates()
	{
		List<ItemTemplate> itemTemplates = ItemTemplateGenerator.GenerateList(2);
		List<ItemTemplateResponse> expectedReturn = [.. itemTemplates.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetDeletedItemTemplatesQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ItemTemplate>(itemTemplates, itemTemplates.Count, 0, 50));

		Results<Ok<ItemTemplateListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetDeletedItemTemplates(0, 50, null, null);

		Ok<ItemTemplateListResponse> result = Assert.IsType<Ok<ItemTemplateListResponse>>(rawResult.Result);
		ItemTemplateListResponse actualReturn = result.Value!;
		actualReturn.Data.Should().BeEquivalentTo(expectedReturn);
		actualReturn.Total.Should().Be(itemTemplates.Count);
	}

	[Fact]
	public async Task GetDeletedItemTemplates_ThrowsException_WhenMediatorFails()
	{
		_mediatorMock.Setup(m => m.Send(
			It.Is<GetDeletedItemTemplatesQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.GetDeletedItemTemplates(0, 50, null, null);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── CreateItemTemplate ──────────────────────────────────

	[Fact]
	public async Task CreateItemTemplate_ReturnsOkResult_WithCreatedItemTemplate()
	{
		ItemTemplate itemTemplate = ItemTemplateGenerator.Generate();
		ItemTemplateResponse expectedReturn = _mapper.ToResponse(itemTemplate);

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateItemTemplateCommand>(c => c.ItemTemplates.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync([itemTemplate]);

		CreateItemTemplateRequest controllerInput = ItemTemplateDtoGenerator.GenerateCreateRequest();

		Ok<ItemTemplateResponse> result = await _controller.CreateItemTemplate(controllerInput);

		ItemTemplateResponse actualReturn = result.Value!;
		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task CreateItemTemplate_ThrowsException_WhenMediatorFails()
	{
		CreateItemTemplateRequest controllerInput = ItemTemplateDtoGenerator.GenerateCreateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateItemTemplateCommand>(c => c.ItemTemplates.Count == 1),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.CreateItemTemplate(controllerInput);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── UpdateItemTemplate ──────────────────────────────────

	[Fact]
	public async Task UpdateItemTemplate_ReturnsNoContent_WhenUpdateSucceeds()
	{
		UpdateItemTemplateRequest controllerInput = ItemTemplateDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateItemTemplateCommand>(c => c.ItemTemplates.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		Results<NoContent, NotFound> result = await _controller.UpdateItemTemplate(controllerInput.Id, controllerInput);

		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateItemTemplate_ReturnsNotFound_WhenUpdateFails()
	{
		UpdateItemTemplateRequest controllerInput = ItemTemplateDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateItemTemplateCommand>(c => c.ItemTemplates.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		Results<NoContent, NotFound> result = await _controller.UpdateItemTemplate(controllerInput.Id, controllerInput);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateItemTemplate_ThrowsException_WhenMediatorFails()
	{
		UpdateItemTemplateRequest controllerInput = ItemTemplateDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateItemTemplateCommand>(c => c.ItemTemplates.Count == 1),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.UpdateItemTemplate(controllerInput.Id, controllerInput);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── DeleteItemTemplates ─────────────────────────────────

	[Fact]
	public async Task DeleteItemTemplates_ReturnsNoContent_WhenDeleteSucceeds()
	{
		List<Guid> ids = [.. ItemTemplateGenerator.GenerateList(2).Select(t => t.Id)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteItemTemplateCommand>(c => c.Ids.SequenceEqual(ids)),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		Results<NoContent, NotFound> result = await _controller.DeleteItemTemplates(ids);

		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task DeleteItemTemplates_ReturnsNotFound_WhenDeleteFails()
	{
		List<Guid> ids = [ItemTemplateGenerator.Generate().Id];

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteItemTemplateCommand>(c => c.Ids.SequenceEqual(ids)),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		Results<NoContent, NotFound> result = await _controller.DeleteItemTemplates(ids);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task DeleteItemTemplates_ThrowsException_WhenMediatorFails()
	{
		List<Guid> ids = [.. ItemTemplateGenerator.GenerateList(2).Select(t => t.Id)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteItemTemplateCommand>(c => c.Ids.SequenceEqual(ids)),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.DeleteItemTemplates(ids);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── RestoreItemTemplate ─────────────────────────────────

	[Fact]
	public async Task RestoreItemTemplate_ReturnsNoContent_WhenSuccessful()
	{
		Guid id = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<RestoreItemTemplateCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		Results<NoContent, NotFound> result = await _controller.RestoreItemTemplate(id);

		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task RestoreItemTemplate_ReturnsNotFound_WhenEntityDoesNotExist()
	{
		Guid id = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<RestoreItemTemplateCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		Results<NoContent, NotFound> result = await _controller.RestoreItemTemplate(id);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task RestoreItemTemplate_ThrowsException_WhenMediatorFails()
	{
		Guid id = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<RestoreItemTemplateCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.RestoreItemTemplate(id);

		await act.Should().ThrowAsync<Exception>();
	}

	// ── GetItemTemplateHistoryCandidates ────────────────────

	[Fact]
	public async Task GetItemTemplateHistoryCandidates_ReturnsOkResult_WithMappedCandidates()
	{
		List<ItemTemplateHistoryCandidate> candidates =
		[
			new()
			{
				Name = "Whole Milk",
				OccurrenceCount = 7,
				LastPurchasedAt = new DateOnly(2026, 3, 4),
				SuggestedCategory = "Groceries",
				SuggestedSubcategory = "Dairy",
				SuggestedUnitPrice = 3.99m,
				SuggestedItemCode = "MILK-001",
			},
			new()
			{
				Name = "Sourdough Bread",
				OccurrenceCount = 3,
				LastPurchasedAt = new DateOnly(2026, 2, 1),
				SuggestedCategory = null,
				SuggestedSubcategory = null,
				SuggestedUnitPrice = null,
				SuggestedItemCode = null,
			},
		];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetItemTemplateHistoryCandidatesQuery>(q => q.Offset == 0 && q.Limit == 50 && q.MinCount == 2),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ItemTemplateHistoryCandidate>(candidates, candidates.Count, 0, 50));

		Results<Ok<ItemTemplateHistoryCandidateListResponse>, BadRequest<ProblemDetails>> rawResult =
			await _controller.GetItemTemplateHistoryCandidates(0, 50, 2);

		Ok<ItemTemplateHistoryCandidateListResponse> result = Assert.IsType<Ok<ItemTemplateHistoryCandidateListResponse>>(rawResult.Result);
		ItemTemplateHistoryCandidateListResponse actualReturn = result.Value!;

		actualReturn.Total.Should().Be(2);
		actualReturn.Offset.Should().Be(0);
		actualReturn.Limit.Should().Be(50);
		actualReturn.Data.Should().HaveCount(2);

		ItemTemplateHistoryCandidateResponse first = actualReturn.Data.First();
		first.Name.Should().Be("Whole Milk");
		first.OccurrenceCount.Should().Be(7);
		first.LastPurchasedAt.Should().Be(new DateOnly(2026, 3, 4));
		first.SuggestedCategory.Should().Be("Groceries");
		first.SuggestedSubcategory.Should().Be("Dairy");
		first.SuggestedUnitPrice.Should().Be(3.99);
		first.SuggestedItemCode.Should().Be("MILK-001");

		ItemTemplateHistoryCandidateResponse second = actualReturn.Data.Last();
		second.SuggestedCategory.Should().BeNull();
		second.SuggestedSubcategory.Should().BeNull();
		second.SuggestedUnitPrice.Should().BeNull();
		second.SuggestedItemCode.Should().BeNull();
	}

	[Fact]
	public async Task GetItemTemplateHistoryCandidates_UsesDefaultParameters_WhenNoneSupplied()
	{
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetItemTemplateHistoryCandidatesQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ItemTemplateHistoryCandidate>([], 0, 0, 50));

		await _controller.GetItemTemplateHistoryCandidates();

		_mediatorMock.Verify(m => m.Send(
			It.Is<GetItemTemplateHistoryCandidatesQuery>(q => q.Offset == 0 && q.Limit == 50 && q.MinCount == 2),
			It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(-100)]
	public async Task GetItemTemplateHistoryCandidates_ReturnsBadRequest_WhenOffsetIsNegative(int offset)
	{
		Results<Ok<ItemTemplateHistoryCandidateListResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemTemplateHistoryCandidates(offset, 50, 2);

		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("offset must be >= 0");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(501)]
	public async Task GetItemTemplateHistoryCandidates_ReturnsBadRequest_WhenLimitIsOutOfRange(int limit)
	{
		Results<Ok<ItemTemplateHistoryCandidateListResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemTemplateHistoryCandidates(0, limit, 2);

		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-2)]
	public async Task GetItemTemplateHistoryCandidates_ReturnsBadRequest_WhenMinCountIsBelowOne(int minCount)
	{
		Results<Ok<ItemTemplateHistoryCandidateListResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemTemplateHistoryCandidates(0, 50, minCount);

		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("minCount must be >= 1");
	}

	[Fact]
	public async Task GetItemTemplateHistoryCandidates_DoesNotCallMediator_WhenParametersAreInvalid()
	{
		await _controller.GetItemTemplateHistoryCandidates(-1, 50, 2);

		_mediatorMock.Verify(m => m.Send(
			It.IsAny<GetItemTemplateHistoryCandidatesQuery>(),
			It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task GetItemTemplateHistoryCandidates_ThrowsException_WhenMediatorFails()
	{
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetItemTemplateHistoryCandidatesQuery>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		Func<Task> act = () => _controller.GetItemTemplateHistoryCandidates(0, 50, 2);

		await act.Should().ThrowAsync<Exception>();
	}
}
