using API.Controllers.Aggregates;
using API.Generated.Dtos;
using API.Mapping.Aggregates;
using API.Mapping.Core;
using Application.Queries.Aggregates.TransactionAccounts;
using Domain.Aggregates;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using SampleData.Domain.Aggregates;
using SampleData.Domain.Core;

namespace Presentation.API.Tests.Controllers.Aggregates;

public class TransactionAccountControllerTests
{
	private readonly TransactionAccountMapper _mapper;
	private readonly Mock<IMediator> _mediatorMock;
	private readonly TransactionAccountController _controller;

	public TransactionAccountControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_mapper = new TransactionAccountMapper();
		_controller = new TransactionAccountController(_mediatorMock.Object, _mapper);
	}

	[Fact]
	public async Task GetTransactionAccounts_ByTransactionId_ReturnsOkResult_WhenTransactionExists()
	{
		// Arrange
		TransactionAccount transactionAccount = TransactionAccountGenerator.Generate();
		TransactionAccountResponse expectedReturn = _mapper.ToResponse(transactionAccount);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetTransactionAccountByTransactionIdQuery>(q => q.TransactionId == transactionAccount.Transaction.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(transactionAccount);

		// Act
		Results<Ok<List<TransactionAccountResponse>>, BadRequest<ProblemDetails>> result = await _controller.GetTransactionAccounts(transactionId: transactionAccount.Transaction.Id, receiptId: null);

		// Assert
		Ok<List<TransactionAccountResponse>> okResult = Assert.IsType<Ok<List<TransactionAccountResponse>>>(result.Result);
		List<TransactionAccountResponse> actualReturn = okResult.Value!;

		actualReturn[0].Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task GetTransactionAccounts_ByTransactionId_ReturnsEmptyList_WhenTransactionDoesNotExist()
	{
		// Arrange
		Guid missingTransactionId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetTransactionAccountByTransactionIdQuery>(q => q.TransactionId == missingTransactionId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync((TransactionAccount?)null);

		// Act
		Results<Ok<List<TransactionAccountResponse>>, BadRequest<ProblemDetails>> result = await _controller.GetTransactionAccounts(transactionId: missingTransactionId, receiptId: null);

		// Assert
		Ok<List<TransactionAccountResponse>> okResult = Assert.IsType<Ok<List<TransactionAccountResponse>>>(result.Result);
		List<TransactionAccountResponse> actualReturn = okResult.Value!;
		actualReturn.Should().BeEmpty();
	}

	[Fact]
	public async Task GetTransactionAccounts_ByTransactionId_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid transactionId = TransactionAccountGenerator.Generate().Transaction.Id;

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetTransactionAccountByTransactionIdQuery>(q => q.TransactionId == transactionId),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetTransactionAccounts(transactionId: transactionId, receiptId: null);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetTransactionAccounts_ByReceiptId_ReturnsOkResult_WhenReceiptExists()
	{
		// Arrange
		Guid receiptId = ReceiptGenerator.Generate().Id;
		TransactionAccount transactionAccount = TransactionAccountGenerator.Generate();
		TransactionAccountResponse expectedReturn = _mapper.ToResponse(transactionAccount);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetTransactionAccountsByReceiptIdQuery>(q => q.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync([transactionAccount]);

		// Act
		Results<Ok<List<TransactionAccountResponse>>, BadRequest<ProblemDetails>> result = await _controller.GetTransactionAccounts(transactionId: null, receiptId: receiptId);

		// Assert
		Ok<List<TransactionAccountResponse>> okResult = Assert.IsType<Ok<List<TransactionAccountResponse>>>(result.Result);
		List<TransactionAccountResponse> actualReturn = okResult.Value!;

		actualReturn[0].Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task GetTransactionAccounts_ByReceiptId_ReturnsEmptyList_WhenReceiptDoesNotExist()
	{
		// Arrange
		Guid missingReceiptId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetTransactionAccountsByReceiptIdQuery>(q => q.ReceiptId == missingReceiptId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync((List<TransactionAccount>?)null);

		// Act
		Results<Ok<List<TransactionAccountResponse>>, BadRequest<ProblemDetails>> result = await _controller.GetTransactionAccounts(transactionId: null, receiptId: missingReceiptId);

		// Assert
		Ok<List<TransactionAccountResponse>> okResult = Assert.IsType<Ok<List<TransactionAccountResponse>>>(result.Result);
		List<TransactionAccountResponse> actualReturn = okResult.Value!;
		actualReturn.Should().BeEmpty();
	}

	[Fact]
	public async Task GetTransactionAccounts_ByReceiptId_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid receiptId = ReceiptGenerator.Generate().Id;

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetTransactionAccountsByReceiptIdQuery>(q => q.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetTransactionAccounts(transactionId: null, receiptId: receiptId);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}
}
