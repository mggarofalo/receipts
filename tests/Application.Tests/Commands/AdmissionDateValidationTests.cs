using Application.Commands.Receipt.Create;
using Application.Commands.Receipt.CreateComplete;
using Application.Commands.Receipt.Update;
using Application.Commands.Transaction.Create;
using Application.Commands.Transaction.Update;
using Application.Validation;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;

namespace Application.Tests.Commands;

public class AdmissionDateValidationTests
{
	private static readonly DateOnly Today = new(2040, 5, 6);
	private static readonly AdmissionDatePolicy DatePolicy = new(
		new FixedTimeProvider(new DateTimeOffset(2040, 5, 6, 12, 0, 0, TimeSpan.Zero)));

	[Fact]
	public void CreateReceipt_UsesControlledAdmissionDate()
	{
		CreateReceiptCommandValidator validator = new(DatePolicy);

		AssertDateBoundary(
			date => new CreateReceiptCommand([CreateReceipt(date)]),
			validator,
			CreateReceiptCommandValidator.DateCannotBeInTheFuture);
	}

	[Fact]
	public void UpdateReceipt_UsesControlledAdmissionDate()
	{
		UpdateReceiptCommandValidator validator = new(DatePolicy);

		AssertDateBoundary(
			date => new UpdateReceiptCommand([CreateReceipt(date)]),
			validator,
			UpdateReceiptCommandValidator.DateCannotBeInTheFuture);
	}

	[Fact]
	public void CreateTransaction_UsesControlledAdmissionDate()
	{
		CreateTransactionCommandValidator validator = new(DatePolicy);

		AssertDateBoundary(
			date => new CreateTransactionCommand([CreateTransaction(date)], Guid.NewGuid()),
			validator,
			CreateTransactionCommandValidator.DateCannotBeInTheFuture);
	}

	[Fact]
	public void UpdateTransaction_UsesControlledAdmissionDate()
	{
		UpdateTransactionCommandValidator validator = new(DatePolicy);

		AssertDateBoundary(
			date => new UpdateTransactionCommand([CreateTransaction(date)]),
			validator,
			UpdateTransactionCommandValidator.DateCannotBeInTheFuture);
	}

	[Fact]
	public void CreateCompleteReceipt_UsesControlledAdmissionDateForReceiptAndTransactions()
	{
		CreateCompleteReceiptCommandValidator validator = new(DatePolicy);
		CreateCompleteReceiptCommand valid = new(
			CreateReceipt(Today),
			[CreateTransaction(Today)],
			[]);
		CreateCompleteReceiptCommand future = new(
			CreateReceipt(Today.AddDays(1)),
			[CreateTransaction(Today.AddDays(1))],
			[]);

		ValidationResult validResult = validator.Validate(valid);
		ValidationResult futureResult = validator.Validate(future);

		validResult.IsValid.Should().BeTrue();
		futureResult.Errors.Should().HaveCount(2);
		futureResult.Errors.Should().OnlyContain(error =>
			error.ErrorMessage == CreateCompleteReceiptCommandValidator.DateCannotBeInTheFuture);
		futureResult.Errors.Should().Contain(error => error.PropertyName == "Receipt.Date");
		futureResult.Errors.Should().Contain(error => error.PropertyName == "Transactions[0]");
	}

	private static void AssertDateBoundary<TCommand>(
		Func<DateOnly, TCommand> commandFactory,
		IValidator<TCommand> validator,
		string expectedMessage)
	{
		ValidationResult todayResult = validator.Validate(commandFactory(Today));
		ValidationResult futureResult = validator.Validate(commandFactory(Today.AddDays(1)));

		todayResult.IsValid.Should().BeTrue();
		futureResult.IsValid.Should().BeFalse();
		futureResult.Errors.Should().ContainSingle(error => error.ErrorMessage == expectedMessage);
	}

	private static Domain.Core.Receipt CreateReceipt(DateOnly date) =>
		new(Guid.NewGuid(), "Test Store", date, new Domain.Money(1m));

	private static Domain.Core.Transaction CreateTransaction(DateOnly date) =>
		new(Guid.NewGuid(), Guid.NewGuid(), new Domain.Money(1m), date);

	private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => utcNow;
	}
}
