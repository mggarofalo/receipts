using API.Generated.Dtos;
using API.Validators;
using FluentAssertions;
using SampleData.Dtos.Core;

namespace Presentation.API.Tests.Validators;

public class CreateCompleteReceiptRequestValidatorTests
{
	private readonly CreateCompleteReceiptRequestValidator _validator = new();

	[Fact]
	public void Validate_WithValidAdjustment_Passes()
	{
		CreateCompleteReceiptRequest request = ValidRequest();
		request.Adjustments = [new CreateAdjustmentRequest { Type = "Discount", Amount = -2 }];

		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void Validate_WithZeroAdjustmentAmount_ReturnsIndexedAdjustmentError()
	{
		CreateCompleteReceiptRequest request = ValidRequest();
		request.Adjustments = [new CreateAdjustmentRequest { Type = "Tip", Amount = 0 }];

		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(error =>
			error.PropertyName == "Adjustments[0].Amount" &&
			error.ErrorMessage == CreateAdjustmentRequestValidator.AmountMustBeNonZero);
	}

	[Fact]
	public void Validate_WithAdjustmentsOmitted_Passes()
	{
		CreateCompleteReceiptRequest request = ValidRequest();
		request.Adjustments = null!;

		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		result.IsValid.Should().BeTrue();
	}

	private static CreateCompleteReceiptRequest ValidRequest() => new()
	{
		Receipt = ReceiptDtoGenerator.GenerateCreateRequest(),
		Transactions = TransactionDtoGenerator.GenerateCreateRequestList(1),
		Items = ReceiptItemDtoGenerator.GenerateCreateRequestList(1),
		Adjustments = [],
	};
}
