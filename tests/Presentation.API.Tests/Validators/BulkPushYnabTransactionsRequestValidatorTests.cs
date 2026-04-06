using API.Generated.Dtos;
using API.Validators;
using FluentAssertions;
using FluentValidation.Results;

namespace Presentation.API.Tests.Validators;

public class BulkPushYnabTransactionsRequestValidatorTests
{
	private readonly BulkPushYnabTransactionsRequestValidator _validator = new();

	[Fact]
	public void Valid_Request_Passes()
	{
		// Arrange
		BulkPushYnabTransactionsRequest request = new()
		{
			ReceiptIds = [Guid.NewGuid(), Guid.NewGuid()],
		};

		// Act
		ValidationResult result = _validator.Validate(request);

		// Assert
		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void Empty_ReceiptIds_Fails()
	{
		// Arrange
		BulkPushYnabTransactionsRequest request = new()
		{
			ReceiptIds = [],
		};

		// Act
		ValidationResult result = _validator.Validate(request);

		// Assert
		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e => e.ErrorMessage == BulkPushYnabTransactionsRequestValidator.ReceiptIdsMustNotBeEmpty);
	}

	[Fact]
	public void ReceiptIds_WithEmptyGuid_Fails()
	{
		// Arrange
		BulkPushYnabTransactionsRequest request = new()
		{
			ReceiptIds = [Guid.NewGuid(), Guid.Empty],
		};

		// Act
		ValidationResult result = _validator.Validate(request);

		// Assert
		result.IsValid.Should().BeFalse();
	}
}
