using API.Generated.Dtos;
using API.Validators;
using FluentAssertions;
using FluentValidation.Results;

namespace Presentation.API.Tests.Validators;

public class PushYnabTransactionsRequestValidatorTests
{
	private readonly PushYnabTransactionsRequestValidator _validator = new();

	[Fact]
	public void Valid_Request_Passes()
	{
		// Arrange
		PushYnabTransactionsRequest request = new() { ReceiptId = Guid.NewGuid() };

		// Act
		ValidationResult result = _validator.Validate(request);

		// Assert
		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void Empty_ReceiptId_Fails()
	{
		// Arrange
		PushYnabTransactionsRequest request = new() { ReceiptId = Guid.Empty };

		// Act
		ValidationResult result = _validator.Validate(request);

		// Assert
		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e => e.ErrorMessage == PushYnabTransactionsRequestValidator.ReceiptIdMustNotBeEmpty);
	}
}
