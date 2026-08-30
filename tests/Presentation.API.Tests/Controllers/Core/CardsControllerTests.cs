using API.Controllers.Core;
using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.Card.Create;
using Application.Commands.Card.Delete;
using Application.Commands.Card.Merge;
using Application.Commands.Card.Update;
using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Merge;
using Application.Queries.Core.Card;
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

public class CardsControllerTests
{
	private readonly CardMapper _mapper;
	private readonly Mock<IMediator> _mediatorMock;
	private readonly Mock<ILogger<CardsController>> _loggerMock;
	private readonly Mock<IEntityChangeNotifier> _notifierMock;
	private readonly Mock<ICardService> _cardServiceMock;
	private readonly Mock<IAccountService> _accountServiceMock;
	private readonly CardsController _controller;

	public CardsControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_mapper = new CardMapper();
		_loggerMock = ControllerTestHelpers.GetLoggerMock<CardsController>();
		_notifierMock = new Mock<IEntityChangeNotifier>();
		_cardServiceMock = new Mock<ICardService>();
		_accountServiceMock = new Mock<IAccountService>();
		_accountServiceMock.Setup(s => s.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);
		_controller = new CardsController(_mediatorMock.Object, _mapper, _loggerMock.Object, _notifierMock.Object, _cardServiceMock.Object, _accountServiceMock.Object);
		_controller.ControllerContext = new ControllerContext
		{
			HttpContext = new DefaultHttpContext()
		};
	}

	[Fact]
	public async Task GetAccountById_ReturnsOkResult_WhenAccountExists()
	{
		// Arrange
		Card account = CardGenerator.Generate();
		CardResponse expectedReturn = _mapper.ToResponse(account);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetCardByIdQuery>(q => q.Id == account.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(account);

		// Act
		Results<Ok<CardResponse>, NotFound> result = await _controller.GetCardById(account.Id);

		// Assert
		Ok<CardResponse> okResult = Assert.IsType<Ok<CardResponse>>(result.Result);
		CardResponse actualReturn = Assert.IsType<CardResponse>(okResult.Value);
		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task GetAccountById_ReturnsNotFound_WhenAccountDoesNotExist()
	{
		// Arrange
		Guid missingAccountId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetCardByIdQuery>(q => q.Id == missingAccountId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync((Card?)null);

		// Act
		Results<Ok<CardResponse>, NotFound> result = await _controller.GetCardById(missingAccountId);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task GetAccountById_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid id = CardGenerator.Generate().Id;

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetCardByIdQuery>(q => q.Id == id),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetCardById(id);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetAllAccounts_ReturnsOkResult_WithListOfAccounts()
	{
		// Arrange
		List<Card> accounts = CardGenerator.GenerateList(2);
		List<CardResponse> expectedReturn = [.. accounts.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllCardsQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Card>(accounts, accounts.Count, 0, 50));

		// Act
		Results<Ok<CardListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllCards(null, null, 0, 50, null, null);

		// Assert
		Ok<CardListResponse> result = Assert.IsType<Ok<CardListResponse>>(rawResult.Result);
		CardListResponse actualReturn = result.Value!;

		actualReturn.Data.Should().BeEquivalentTo(expectedReturn);
		actualReturn.Total.Should().Be(accounts.Count);
		actualReturn.Offset.Should().Be(0);
		actualReturn.Limit.Should().Be(50);
	}

	[Fact]
	public async Task GetAllAccounts_PassesIsActiveToQuery()
	{
		// Arrange
		List<Card> accounts = CardGenerator.GenerateList(1);
		List<CardResponse> expectedReturn = [.. accounts.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllCardsQuery>(q => q.Offset == 0 && q.Limit == 50 && q.IsActive == true && q.Q == "4242"),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Card>(accounts, accounts.Count, 0, 50));

		// Act
		Results<Ok<CardListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllCards(true, "4242", 0, 50, null, null);

		// Assert
		Ok<CardListResponse> result = Assert.IsType<Ok<CardListResponse>>(rawResult.Result);
		CardListResponse actualReturn = result.Value!;

		actualReturn.Data.Should().BeEquivalentTo(expectedReturn);
		actualReturn.Total.Should().Be(accounts.Count);
	}

	[Theory]
	[InlineData(-1, 50)]
	[InlineData(-100, 50)]
	public async Task GetAllAccounts_ReturnsBadRequest_WhenOffsetIsNegative(int offset, int limit)
	{
		// Act
		Results<Ok<CardListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllCards(null, null, offset, limit, null, null);

		// Assert
		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("offset must be >= 0");
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(0, -1)]
	[InlineData(0, 501)]
	public async Task GetAllAccounts_ReturnsBadRequest_WhenLimitIsOutOfRange(int offset, int limit)
	{
		// Act
		Results<Ok<CardListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllCards(null, null, offset, limit, null, null);

		// Assert
		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Fact]
	public async Task GetAllAccounts_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllCardsQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetAllCards(null, null, 0, 50, null, null);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task CreateAccount_ReturnsOkResult_WithCreatedAccount()
	{
		// Arrange
		Card account = CardGenerator.Generate();
		CardResponse expectedReturn = _mapper.ToResponse(account);

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateCardCommand>(c => c.Cards.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync([account]);

		CreateCardRequest controllerInput = CardDtoGenerator.GenerateCreateRequest();

		// Act
		Results<Ok<CardResponse>, BadRequest<ProblemDetails>> result = await _controller.CreateCard(controllerInput);

		// Assert
		Ok<CardResponse> okResult = Assert.IsType<Ok<CardResponse>>(result.Result);
		CardResponse actualReturn = okResult.Value!;

		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task CreateCard_ForwardsAccountId_WhenAccountExists()
	{
		// Arrange
		Guid parentAccountId = Guid.NewGuid();
		CreateCardRequest controllerInput = CardDtoGenerator.GenerateCreateRequest();
		controllerInput.AccountId = parentAccountId;

		_accountServiceMock.Setup(s => s.ExistsAsync(parentAccountId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		Card createdCard = CardGenerator.Generate();
		createdCard.AccountId = parentAccountId;

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateCardCommand>(c => c.Cards.Count == 1 && c.Cards[0].AccountId == parentAccountId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync([createdCard]);

		// Act
		Results<Ok<CardResponse>, BadRequest<ProblemDetails>> result = await _controller.CreateCard(controllerInput);

		// Assert
		Ok<CardResponse> okResult = Assert.IsType<Ok<CardResponse>>(result.Result);
		okResult.Value!.AccountId.Should().Be(parentAccountId);
	}

	[Fact]
	public async Task CreateCard_ReturnsBadRequest_WhenAccountIdDoesNotExist()
	{
		// Arrange
		Guid missingAccountId = Guid.NewGuid();
		CreateCardRequest controllerInput = CardDtoGenerator.GenerateCreateRequest();
		controllerInput.AccountId = missingAccountId;

		_accountServiceMock.Setup(s => s.ExistsAsync(missingAccountId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<Ok<CardResponse>, BadRequest<ProblemDetails>> result = await _controller.CreateCard(controllerInput);

		// Assert
		Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		_mediatorMock.Verify(m => m.Send(It.IsAny<CreateCardCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task CreateCards_ReturnsBadRequest_WhenAnyAccountIdDoesNotExist()
	{
		// Arrange
		Guid missingAccountId = Guid.NewGuid();
		List<CreateCardRequest> controllerInput = CardDtoGenerator.GenerateCreateRequestList(2);
		controllerInput[1].AccountId = missingAccountId;

		_accountServiceMock.Setup(s => s.ExistsAsync(missingAccountId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<Ok<List<CardResponse>>, BadRequest<ProblemDetails>> result = await _controller.CreateCards(controllerInput);

		// Assert
		Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		_mediatorMock.Verify(m => m.Send(It.IsAny<CreateCardCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task CreateAccount_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		CreateCardRequest controllerInput = CardDtoGenerator.GenerateCreateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateCardCommand>(c => c.Cards.Count == 1),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = async () => await _controller.CreateCard(controllerInput);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task CreateAccounts_ReturnsOkResult_WithCreatedAccounts()
	{
		// Arrange
		List<Card> accounts = CardGenerator.GenerateList(2);
		List<CardResponse> expectedReturn = [.. accounts.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateCardCommand>(c => c.Cards.Count == accounts.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(accounts);

		List<CreateCardRequest> controllerInput = CardDtoGenerator.GenerateCreateRequestList(2);

		// Act
		Results<Ok<List<CardResponse>>, BadRequest<ProblemDetails>> result = await _controller.CreateCards(controllerInput);

		// Assert
		Ok<List<CardResponse>> okResult = Assert.IsType<Ok<List<CardResponse>>>(result.Result);
		List<CardResponse> actualReturn = okResult.Value!;

		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task CreateAccounts_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		List<CreateCardRequest> controllerInput = CardDtoGenerator.GenerateCreateRequestList(2);
		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateCardCommand>(c => c.Cards.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = async () => await _controller.CreateCards(controllerInput);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task UpdateAccount_ReturnsNoContent_WhenUpdateSucceeds()
	{
		// Arrange
		UpdateCardRequest controllerInput = CardDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateCardCommand>(c => c.Cards.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result = await _controller.UpdateCard(controllerInput.Id, controllerInput);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateAccount_ReturnsNotFound_WhenUpdateFails()
	{
		// Arrange
		UpdateCardRequest controllerInput = CardDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateCardCommand>(c => c.Cards.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result = await _controller.UpdateCard(controllerInput.Id, controllerInput);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateAccount_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		UpdateCardRequest controllerInput = CardDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateCardCommand>(c => c.Cards.Count == 1),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.UpdateCard(controllerInput.Id, controllerInput);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task UpdateCard_ForwardsAccountId_WhenAccountExists()
	{
		// Arrange
		Guid parentAccountId = Guid.NewGuid();
		UpdateCardRequest controllerInput = CardDtoGenerator.GenerateUpdateRequest();
		controllerInput.AccountId = parentAccountId;

		_accountServiceMock.Setup(s => s.ExistsAsync(parentAccountId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateCardCommand>(c => c.Cards.Count == 1 && c.Cards[0].AccountId == parentAccountId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result = await _controller.UpdateCard(controllerInput.Id, controllerInput);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateCard_ReturnsBadRequest_WhenAccountIdDoesNotExist()
	{
		// Arrange
		Guid missingAccountId = Guid.NewGuid();
		UpdateCardRequest controllerInput = CardDtoGenerator.GenerateUpdateRequest();
		controllerInput.AccountId = missingAccountId;

		_accountServiceMock.Setup(s => s.ExistsAsync(missingAccountId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result = await _controller.UpdateCard(controllerInput.Id, controllerInput);

		// Assert
		Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		_mediatorMock.Verify(m => m.Send(It.IsAny<UpdateCardCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task UpdateCards_ReturnsBadRequest_WhenAnyAccountIdDoesNotExist()
	{
		// Arrange
		Guid missingAccountId = Guid.NewGuid();
		List<UpdateCardRequest> controllerInput = CardDtoGenerator.GenerateUpdateRequestList(2);
		controllerInput[0].AccountId = missingAccountId;

		_accountServiceMock.Setup(s => s.ExistsAsync(missingAccountId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result = await _controller.UpdateCards(controllerInput);

		// Assert
		Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		_mediatorMock.Verify(m => m.Send(It.IsAny<UpdateCardCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task UpdateAccounts_ReturnsNoContent_WhenUpdateSucceeds()
	{
		// Arrange
		List<UpdateCardRequest> controllerInput = CardDtoGenerator.GenerateUpdateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateCardCommand>(c => c.Cards.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result = await _controller.UpdateCards(controllerInput);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateAccounts_ReturnsNotFound_WhenUpdateFails()
	{
		// Arrange
		List<UpdateCardRequest> controllerInput = CardDtoGenerator.GenerateUpdateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateCardCommand>(c => c.Cards.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result = await _controller.UpdateCards(controllerInput);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateAccounts_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		List<UpdateCardRequest> controllerInput = CardDtoGenerator.GenerateUpdateRequestList(2);
		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateCardCommand>(c => c.Cards.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.UpdateCards(controllerInput);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task DeleteAccount_ReturnsNoContent_WhenDeleteSucceeds()
	{
		// Arrange
		Guid id = Guid.NewGuid();

		_cardServiceMock.Setup(s => s.GetTransactionCountByCardIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteCardCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await _controller.DeleteCard(id);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task DeleteAccount_ReturnsNotFound_WhenAccountDoesNotExist()
	{
		// Arrange
		Guid id = Guid.NewGuid();

		_cardServiceMock.Setup(s => s.GetTransactionCountByCardIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteCardCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await _controller.DeleteCard(id);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task DeleteAccount_ReturnsConflict_WhenTransactionsExist()
	{
		// Arrange
		Guid id = Guid.NewGuid();

		_cardServiceMock.Setup(s => s.GetTransactionCountByCardIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(5);

		// Act
		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await _controller.DeleteCard(id);

		// Assert
		Assert.IsType<Conflict<ProblemDetails>>(result.Result);
	}

	[Fact]
	public async Task DeleteAccount_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid id = Guid.NewGuid();

		_cardServiceMock.Setup(s => s.GetTransactionCountByCardIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteCardCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.DeleteCard(id);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task PreviewMergeCards_ReturnsTheImpactWithoutNotifyingAnyone()
	{
		// RECEIPTS-889: a preview writes nothing, so broadcasting a change would be a lie.
		MergeCardsPreviewRequest request = new()
		{
			TargetAccountId = Guid.NewGuid(),
			SourceCardIds = [Guid.NewGuid(), Guid.NewGuid()],
		};

		Guid removedAccountId = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<PreviewMergeCardsQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new MergeCardsPreview(
				[new Application.Models.Merge.MergeCardsPreviewAccount(removedAccountId, "Source Account")],
				CardsToMove: 2,
				TransactionsToRepoint: 37,
				TrashedTransactionsToRepoint: 4,
				SurvivingYnabMapping: null,
				Conflicts: null));

		Results<Ok<MergeCardsPreviewResponse>, BadRequest<ProblemDetails>, NotFound> result =
			await _controller.PreviewMergeCards(request);

		Ok<MergeCardsPreviewResponse> ok = Assert.IsType<Ok<MergeCardsPreviewResponse>>(result.Result);
		(ok.Value!.CardsToMove, ok.Value.TransactionsToRepoint, ok.Value.TrashedTransactionsToRepoint)
			.Should().Be((2, 37, 4));
		ok.Value.AccountsToRemove.Should().ContainSingle()
			.Which.Name.Should().Be("Source Account");
		_notifierMock.Verify(
			n => n.NotifyBulkChanged(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Guid>>()),
			Times.Never);
	}

	[Fact]
	public async Task PreviewMergeCards_WhenTheMergeWouldBeRejected_RejectsToo()
	{
		// The preview must not answer where the merge would throw; promising an impact
		// that cannot be delivered is worse than showing none.
		MergeCardsPreviewRequest request = new()
		{
			TargetAccountId = Guid.NewGuid(),
			SourceCardIds = [Guid.NewGuid()],
		};

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<PreviewMergeCardsQuery>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new ArgumentException(
				"Source account would be partially merged: all of its cards must be included in the merge, or none."));

		Results<Ok<MergeCardsPreviewResponse>, BadRequest<ProblemDetails>, NotFound> result =
			await _controller.PreviewMergeCards(request);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Contain("all of its cards must be included");
	}

	[Fact]
	public async Task PreviewMergeCards_WithNoCards_ReturnsBadRequest()
	{
		MergeCardsPreviewRequest request = new()
		{
			TargetAccountId = Guid.NewGuid(),
			SourceCardIds = [],
		};

		Results<Ok<MergeCardsPreviewResponse>, BadRequest<ProblemDetails>, NotFound> result =
			await _controller.PreviewMergeCards(request);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(CardsController.SourceCardIdsRequired);
		_mediatorMock.Verify(m => m.Send(It.IsAny<PreviewMergeCardsQuery>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task MergeCards_WithNoCards_ReturnsBadRequest()
	{
		MergeCardsRequest request = new()
		{
			TargetAccountId = Guid.NewGuid(),
			SourceCardIds = [],
		};

		Results<Ok<MergeCardsResponse>, BadRequest<ProblemDetails>, NotFound, Conflict<MergeCardsConflictResponse>> result =
			await _controller.MergeCards(request);

		BadRequest<ProblemDetails> bad = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		bad.Value!.Detail.Should().Be(CardsController.SourceCardIdsRequired);
	}

	[Fact]
	public async Task MergeCards_WithASingleCard_IsAccepted()
	{
		// RECEIPTS-887: one card used to be rejected here before the request ever reached
		// the service, which is what made folding a single-card account impossible.
		MergeCardsRequest request = new()
		{
			TargetAccountId = Guid.NewGuid(),
			SourceCardIds = [Guid.NewGuid()],
		};

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<MergeCardsIntoAccountCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new MergeCardsResult(1, 1, 4, null));

		Results<Ok<MergeCardsResponse>, BadRequest<ProblemDetails>, NotFound, Conflict<MergeCardsConflictResponse>> result =
			await _controller.MergeCards(request);

		Ok<MergeCardsResponse> ok = Assert.IsType<Ok<MergeCardsResponse>>(result.Result);
		(ok.Value!.AccountsRemoved, ok.Value.CardsMoved, ok.Value.TransactionsRepointed)
			.Should().Be((1, 1, 4));
	}

	[Fact]
	public async Task MergeCards_WhenServiceSucceeds_ReturnsOk()
	{
		MergeCardsRequest request = new()
		{
			TargetAccountId = Guid.NewGuid(),
			SourceCardIds = [Guid.NewGuid(), Guid.NewGuid()],
		};

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<MergeCardsIntoAccountCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new MergeCardsResult(1, 2, 7, null));

		Results<Ok<MergeCardsResponse>, BadRequest<ProblemDetails>, NotFound, Conflict<MergeCardsConflictResponse>> result =
			await _controller.MergeCards(request);

		Ok<MergeCardsResponse> ok = Assert.IsType<Ok<MergeCardsResponse>>(result.Result);
		(ok.Value!.AccountsRemoved, ok.Value.CardsMoved, ok.Value.TransactionsRepointed)
			.Should().Be((1, 2, 7));
		_notifierMock.Verify(n => n.NotifyBulkChanged("card", "updated", It.IsAny<IEnumerable<Guid>>()), Times.Once);
	}

	[Fact]
	public async Task MergeCards_WhenNothingChanged_ReturnsZeroedCountsAndDoesNotNotify()
	{
		// RECEIPTS-893. The response used to be `{ success: true }` whether or not anything
		// moved, so the client had no way to say so. It must now carry zeroes — and there is
		// nothing for connected clients to refetch, so no broadcast either.
		MergeCardsRequest request = new()
		{
			TargetAccountId = Guid.NewGuid(),
			SourceCardIds = [Guid.NewGuid(), Guid.NewGuid()],
		};

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<MergeCardsIntoAccountCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(MergeCardsResult.NoOp());

		Results<Ok<MergeCardsResponse>, BadRequest<ProblemDetails>, NotFound, Conflict<MergeCardsConflictResponse>> result =
			await _controller.MergeCards(request);

		Ok<MergeCardsResponse> ok = Assert.IsType<Ok<MergeCardsResponse>>(result.Result);
		(ok.Value!.AccountsRemoved, ok.Value.CardsMoved, ok.Value.TransactionsRepointed)
			.Should().Be((0, 0, 0));
		_notifierMock.Verify(
			n => n.NotifyBulkChanged(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Guid>>()),
			Times.Never);
	}

	[Fact]
	public async Task MergeCards_WhenServiceReturnsConflicts_ReturnsConflict()
	{
		MergeCardsRequest request = new()
		{
			TargetAccountId = Guid.NewGuid(),
			SourceCardIds = [Guid.NewGuid(), Guid.NewGuid()],
		};

		List<Application.Models.Merge.YnabMappingConflict> conflicts =
		[
			new(Guid.NewGuid(), "A", "b", "y1", "Y1"),
			new(Guid.NewGuid(), "B", "b", "y2", "Y2"),
		];

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<MergeCardsIntoAccountCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(MergeCardsResult.Conflicted(conflicts));

		Results<Ok<MergeCardsResponse>, BadRequest<ProblemDetails>, NotFound, Conflict<MergeCardsConflictResponse>> result =
			await _controller.MergeCards(request);

		Conflict<MergeCardsConflictResponse> conflict = Assert.IsType<Conflict<MergeCardsConflictResponse>>(result.Result);
		conflict.Value!.Conflicts.Should().HaveCount(2);
	}

	[Fact]
	public async Task MergeCards_WhenTargetNotFound_ReturnsNotFound()
	{
		MergeCardsRequest request = new()
		{
			TargetAccountId = Guid.NewGuid(),
			SourceCardIds = [Guid.NewGuid(), Guid.NewGuid()],
		};

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<MergeCardsIntoAccountCommand>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException("not found"));

		Results<Ok<MergeCardsResponse>, BadRequest<ProblemDetails>, NotFound, Conflict<MergeCardsConflictResponse>> result =
			await _controller.MergeCards(request);

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task MergeCards_WithInvalidCommand_ReturnsBadRequest()
	{
		MergeCardsRequest request = new()
		{
			TargetAccountId = Guid.Empty,
			SourceCardIds = [Guid.NewGuid(), Guid.NewGuid()],
		};

		Results<Ok<MergeCardsResponse>, BadRequest<ProblemDetails>, NotFound, Conflict<MergeCardsConflictResponse>> result =
			await _controller.MergeCards(request);

		Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
	}
}
