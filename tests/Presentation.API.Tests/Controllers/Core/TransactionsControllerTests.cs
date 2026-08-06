using API.Controllers.Core;
using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.Transaction.Create;
using Application.Commands.Transaction.Delete;
using Application.Commands.Transaction.Restore;
using Application.Commands.Transaction.Update;
using Application.Models;
using Application.Queries.Core.Transaction;
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

public class TransactionsControllerTests
{
	private readonly TransactionMapper _mapper;
	private readonly Mock<IMediator> _mediatorMock;
	private readonly Mock<ILogger<TransactionsController>> _loggerMock;
	private readonly Mock<IEntityChangeNotifier> _notifierMock;
	private readonly TransactionsController _controller;

	public TransactionsControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_mapper = new TransactionMapper();
		_loggerMock = ControllerTestHelpers.GetLoggerMock<TransactionsController>();
		_notifierMock = new Mock<IEntityChangeNotifier>();
		_controller = new TransactionsController(_mediatorMock.Object, _mapper, _loggerMock.Object, _notifierMock.Object);
	}

	[Fact]
	public async Task GetTransactionById_ReturnsOkResult_WithTransaction()
	{
		// Arrange
		Transaction mediatorReturn = TransactionGenerator.Generate();
		TransactionResponse expectedControllerReturn = _mapper.ToResponse(mediatorReturn);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetTransactionByIdQuery>(q => q.Id == mediatorReturn.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(mediatorReturn);

		// Act
		Results<Ok<TransactionResponse>, NotFound> result = await _controller.GetTransactionById(mediatorReturn.Id);

		// Assert
		Ok<TransactionResponse> okResult = Assert.IsType<Ok<TransactionResponse>>(result.Result);
		TransactionResponse actualControllerReturn = Assert.IsType<TransactionResponse>(okResult.Value);
		actualControllerReturn.Should().BeEquivalentTo(expectedControllerReturn);
	}

	[Fact]
	public async Task GetTransactionById_ReturnsNotFound_WhenTransactionDoesNotExist()
	{
		// Arrange
		Guid missingTransactionId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetTransactionByIdQuery>(q => q.Id == missingTransactionId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync((Transaction?)null);

		// Act
		Results<Ok<TransactionResponse>, NotFound> result = await _controller.GetTransactionById(missingTransactionId);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task GetTransactionById_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid id = TransactionGenerator.Generate().Id;

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetTransactionByIdQuery>(q => q.Id == id),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetTransactionById(id);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetAllTransactions_ReturnsOkResult_WithListOfTransactions()
	{
		// Arrange
		List<Transaction> mediatorReturn = TransactionGenerator.GenerateList(2);
		List<TransactionResponse> expectedControllerReturn = [.. mediatorReturn.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllTransactionsQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Transaction>(mediatorReturn, mediatorReturn.Count, 0, 50));

		// Act
		Results<Ok<TransactionListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllTransactions(null, 0, 50, null, null);

		// Assert
		Ok<TransactionListResponse> result = Assert.IsType<Ok<TransactionListResponse>>(rawResult.Result);
		TransactionListResponse actualControllerReturn = result.Value!;

		actualControllerReturn.Data.Should().BeEquivalentTo(expectedControllerReturn);
		actualControllerReturn.Total.Should().Be(mediatorReturn.Count);
		actualControllerReturn.Offset.Should().Be(0);
		actualControllerReturn.Limit.Should().Be(50);
	}

	[Theory]
	[InlineData(-1, 50)]
	[InlineData(-100, 50)]
	public async Task GetAllTransactions_ReturnsBadRequest_WhenOffsetIsNegative(int offset, int limit)
	{
		// Act
		Results<Ok<TransactionListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllTransactions(null, offset, limit, null, null);

		// Assert
		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("offset must be >= 0");
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(0, -1)]
	[InlineData(0, 501)]
	public async Task GetAllTransactions_ReturnsBadRequest_WhenLimitIsOutOfRange(int offset, int limit)
	{
		// Act
		Results<Ok<TransactionListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllTransactions(null, offset, limit, null, null);

		// Assert
		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Theory]
	[InlineData(-1, 50)]
	[InlineData(-100, 50)]
	public async Task GetDeletedTransactions_ReturnsBadRequest_WhenOffsetIsNegative(int offset, int limit)
	{
		// Act
		Results<Ok<TransactionListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetDeletedTransactions(offset, limit, null, null);

		// Assert
		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("offset must be >= 0");
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(0, -1)]
	[InlineData(0, 501)]
	public async Task GetDeletedTransactions_ReturnsBadRequest_WhenLimitIsOutOfRange(int offset, int limit)
	{
		// Act
		Results<Ok<TransactionListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetDeletedTransactions(offset, limit, null, null);

		// Assert
		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Fact]
	public async Task GetAllTransactions_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllTransactionsQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetAllTransactions(null, 0, 50, null, null);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetAllTransactions_WithReceiptId_ReturnsOkResult_WithTransactions()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<Transaction> mediatorReturn = TransactionGenerator.GenerateList(2);
		List<TransactionResponse> expectedControllerReturn = [.. mediatorReturn.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetTransactionsByReceiptIdQuery>(q => q.ReceiptId == receiptId && q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Transaction>(mediatorReturn, mediatorReturn.Count, 0, 50));

		// Act
		Results<Ok<TransactionListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllTransactions(receiptId, 0, 50, null, null);

		// Assert
		Ok<TransactionListResponse> result = Assert.IsType<Ok<TransactionListResponse>>(rawResult.Result);
		TransactionListResponse actualControllerReturn = result.Value!;

		actualControllerReturn.Data.Should().BeEquivalentTo(expectedControllerReturn);
		actualControllerReturn.Total.Should().Be(mediatorReturn.Count);
		actualControllerReturn.Offset.Should().Be(0);
		actualControllerReturn.Limit.Should().Be(50);
	}

	[Fact]
	public async Task GetAllTransactions_WithReceiptId_ReturnsOkResult_WithEmptyList_WhenReceiptFoundButNoItems()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetTransactionsByReceiptIdQuery>(q => q.ReceiptId == receiptId && q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Transaction>([], 0, 0, 50));

		// Act
		Results<Ok<TransactionListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllTransactions(receiptId, 0, 50, null, null);

		// Assert
		Ok<TransactionListResponse> result = Assert.IsType<Ok<TransactionListResponse>>(rawResult.Result);
		TransactionListResponse actualControllerReturn = result.Value!;

		actualControllerReturn.Data.Should().BeEmpty();
	}

	[Fact]
	public async Task GetAllTransactions_WithReceiptId_ReturnsEmptyList_WhenReceiptIdNotFound()
	{
		// Arrange
		Guid missingReceiptId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetTransactionsByReceiptIdQuery>(q => q.ReceiptId == missingReceiptId && q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Transaction>([], 0, 0, 50));

		// Act
		Results<Ok<TransactionListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllTransactions(missingReceiptId, 0, 50, null, null);

		// Assert
		Ok<TransactionListResponse> result = Assert.IsType<Ok<TransactionListResponse>>(rawResult.Result);
		TransactionListResponse actualReturn = result.Value!;
		actualReturn.Data.Should().BeEmpty();
	}

	[Fact]
	public async Task GetAllTransactions_WithReceiptId_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.Is<GetTransactionsByReceiptIdQuery>(q => q.ReceiptId == receiptId && q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetAllTransactions(receiptId, 0, 50, null, null);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task CreateTransaction_ReturnsOkResult_WithCreatedTransaction()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Transaction transaction = TransactionGenerator.Generate();
		TransactionResponse expectedReturn = _mapper.ToResponse(transaction);
		CreateTransactionRequest controllerInput = TransactionDtoGenerator.GenerateCreateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateTransactionCommand>(c => c.Transactions.Count == 1 && c.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync([transaction]);

		// Act
		Ok<TransactionResponse> result = await _controller.CreateTransaction(controllerInput, receiptId);

		// Assert
		TransactionResponse actualReturn = result.Value!;

		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task CreateTransaction_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		CreateTransactionRequest controllerInput = TransactionDtoGenerator.GenerateCreateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateTransactionCommand>(c => c.Transactions.Count == 1 && c.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.CreateTransaction(controllerInput, receiptId);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task CreateTransactions_ReturnsOkResult_WithCreatedTransactions()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<CreateTransactionRequest> controllerInput = TransactionDtoGenerator.GenerateCreateRequestList(2);
		controllerInput.ForEach(m => m.AccountId = controllerInput[0].AccountId);
		List<Transaction> mediatorReturn = TransactionGenerator.GenerateList(2);
		List<TransactionResponse> expectedControllerReturn = [.. mediatorReturn.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateTransactionCommand>(c => c.Transactions.Count == controllerInput.Count && c.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(mediatorReturn);

		// Act
		Ok<List<TransactionResponse>> result = await _controller.CreateTransactions(controllerInput, receiptId);

		// Assert
		List<TransactionResponse> actualControllerReturn = result.Value!;

		actualControllerReturn.Should().BeEquivalentTo(expectedControllerReturn);
	}

	[Fact]
	public async Task CreateTransactions_WithMultipleAccountIds_ReturnsOkResult_WithAggregatedResults()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Guid accountId1 = Guid.NewGuid();
		Guid accountId2 = Guid.NewGuid();

		List<CreateTransactionRequest> controllerInput = TransactionDtoGenerator.GenerateCreateRequestList(4);
		controllerInput[0].AccountId = accountId1;
		controllerInput[1].AccountId = accountId1;
		controllerInput[2].AccountId = accountId2;
		controllerInput[3].AccountId = accountId2;

		List<Transaction> mediatorReturn = TransactionGenerator.GenerateList(4);

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateTransactionCommand>(c => c.Transactions.Count == 4 && c.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(mediatorReturn);

		List<TransactionResponse> expectedControllerReturn = [.. mediatorReturn.Select(_mapper.ToResponse)];

		// Act
		Ok<List<TransactionResponse>> result = await _controller.CreateTransactions(controllerInput, receiptId);

		// Assert
		List<TransactionResponse> actualControllerReturn = result.Value!;

		actualControllerReturn.Should().BeEquivalentTo(expectedControllerReturn);
		_mediatorMock.Verify(m => m.Send(It.IsAny<CreateTransactionCommand>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task CreateTransactions_WithMultipleAccountIds_ThrowsException_WhenCommandFails()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Guid accountId1 = Guid.NewGuid();
		Guid accountId2 = Guid.NewGuid();

		List<CreateTransactionRequest> controllerInput = TransactionDtoGenerator.GenerateCreateRequestList(4);
		controllerInput[0].AccountId = accountId1;
		controllerInput[1].AccountId = accountId1;
		controllerInput[2].AccountId = accountId2;
		controllerInput[3].AccountId = accountId2;

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateTransactionCommand>(c => c.Transactions.Count == 4 && c.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception("Batch failed"));

		// Act
		Func<Task> act = () => _controller.CreateTransactions(controllerInput, receiptId);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task CreateTransactions_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<CreateTransactionRequest> controllerInput = TransactionDtoGenerator.GenerateCreateRequestList(2);
		controllerInput.ForEach(m => m.AccountId = controllerInput[0].AccountId);

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateTransactionCommand>(c => c.Transactions.Count == controllerInput.Count && c.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.CreateTransactions(controllerInput, receiptId);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task UpdateTransaction_ReturnsNoContent_WhenUpdateSucceeds()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		UpdateTransactionRequest controllerInput = TransactionDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateTransactionCommand>(c => c.Transactions.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateTransaction(controllerInput, id);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateTransaction_ReturnsNotFound_WhenUpdateFails()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		UpdateTransactionRequest controllerInput = TransactionDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateTransactionCommand>(c => c.Transactions.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateTransaction(controllerInput, id);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateTransaction_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		UpdateTransactionRequest controllerInput = TransactionDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateTransactionCommand>(c => c.Transactions.Count == 1),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.UpdateTransaction(controllerInput, id);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task UpdateTransactions_ReturnsNoContent_WhenUpdateSucceeds()
	{
		// Arrange
		List<UpdateTransactionRequest> controllerInput = TransactionDtoGenerator.GenerateUpdateRequestList(2);
		controllerInput.ForEach(m => m.AccountId = controllerInput[0].AccountId);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateTransactionCommand>(c => c.Transactions.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateTransactions(controllerInput);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateTransactions_WithMultipleAccountIds_ReturnsNoContent_WhenBatchSucceeds()
	{
		// Arrange
		Guid accountId1 = Guid.NewGuid();
		Guid accountId2 = Guid.NewGuid();

		List<UpdateTransactionRequest> controllerInput = TransactionDtoGenerator.GenerateUpdateRequestList(4);
		controllerInput[0].AccountId = accountId1;
		controllerInput[1].AccountId = accountId1;
		controllerInput[2].AccountId = accountId2;
		controllerInput[3].AccountId = accountId2;

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateTransactionCommand>(c => c.Transactions.Count == 4),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateTransactions(controllerInput);

		// Assert
		Assert.IsType<NoContent>(result.Result);
		_mediatorMock.Verify(m => m.Send(It.IsAny<UpdateTransactionCommand>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task UpdateTransactions_WithMultipleAccountIds_ReturnsNotFound_WhenBatchReturnsFalse()
	{
		// Arrange
		Guid accountId1 = Guid.NewGuid();
		Guid accountId2 = Guid.NewGuid();

		List<UpdateTransactionRequest> controllerInput = TransactionDtoGenerator.GenerateUpdateRequestList(4);
		controllerInput[0].AccountId = accountId1;
		controllerInput[1].AccountId = accountId1;
		controllerInput[2].AccountId = accountId2;
		controllerInput[3].AccountId = accountId2;

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateTransactionCommand>(c => c.Transactions.Count == 4),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateTransactions(controllerInput);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateTransactions_ReturnsNotFound_WhenUpdateFails()
	{
		// Arrange
		List<UpdateTransactionRequest> controllerInput = TransactionDtoGenerator.GenerateUpdateRequestList(2);
		controllerInput.ForEach(m => m.AccountId = controllerInput[0].AccountId);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateTransactionCommand>(c => c.Transactions.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateTransactions(controllerInput);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateTransactions_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		List<UpdateTransactionRequest> controllerInput = TransactionDtoGenerator.GenerateUpdateRequestList(2);
		controllerInput.ForEach(m => m.AccountId = controllerInput[0].AccountId);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateTransactionCommand>(c => c.Transactions.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.UpdateTransactions(controllerInput);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task DeleteTransactions_ReturnsNoContent_WhenDeleteSucceeds()
	{
		// Arrange
		List<Guid> controllerInput = [.. TransactionGenerator.GenerateList(2).Select(a => a.Id)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteTransactionCommand>(c => c.Ids.SequenceEqual(controllerInput)),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.DeleteTransactions(controllerInput);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task DeleteTransactions_ReturnsNotFound_WhenDeleteFails()
	{
		// Arrange
		List<Guid> controllerInput = [.. TransactionGenerator.GenerateList(2).Select(t => t.Id)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteTransactionCommand>(c => c.Ids.SequenceEqual(controllerInput)),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.DeleteTransactions(controllerInput);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task DeleteTransactions_ReturnsNotFound_WhenMultipleTransactionsDeleteFails()
	{
		// Arrange
		List<Guid> controllerInput = [.. TransactionGenerator.GenerateList(2).Select(t => t.Id)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteTransactionCommand>(c => c.Ids.SequenceEqual(controllerInput)),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.DeleteTransactions(controllerInput);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task DeleteTransactions_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		List<Guid> controllerInput = [.. TransactionGenerator.GenerateList(2).Select(t => t.Id)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteTransactionCommand>(c => c.Ids.SequenceEqual(controllerInput)),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.DeleteTransactions(controllerInput);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task RestoreTransaction_ReturnsNoContent_WhenSuccessful()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.Is<RestoreTransactionCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.RestoreTransaction(id);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task RestoreTransaction_ReturnsNotFound_WhenEntityDoesNotExist()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.Is<RestoreTransactionCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.RestoreTransaction(id);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task RestoreTransaction_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.Is<RestoreTransactionCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.RestoreTransaction(id);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}
}
