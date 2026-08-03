using API.Generated.Dtos;
using API.Validators;
using FluentAssertions;
using FluentValidation.Results;

namespace Presentation.API.Tests.Validators;

public class AcceptDuplicateGroupRequestValidatorTests
{
	private readonly AcceptDuplicateGroupRequestValidator _validator = new();

	[Fact]
	public void Valid_Request_Passes()
	{
		// Arrange
		AcceptDuplicateGroupRequest request = new()
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
		AcceptDuplicateGroupRequest request = new() { ReceiptIds = [] };

		// Act
		ValidationResult result = _validator.Validate(request);

		// Assert
		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e => e.ErrorMessage == AcceptDuplicateGroupRequestValidator.ReceiptIdsTooFew);
	}

	[Fact]
	public void Single_ReceiptId_Fails()
	{
		// Arrange — a group needs two receipts to be a group.
		AcceptDuplicateGroupRequest request = new() { ReceiptIds = [Guid.NewGuid()] };

		// Act
		ValidationResult result = _validator.Validate(request);

		// Assert
		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e => e.ErrorMessage == AcceptDuplicateGroupRequestValidator.ReceiptIdsTooFew);
	}

	[Fact]
	public void SameReceiptIdRepeated_Fails()
	{
		// Arrange — two entries but one distinct receipt is still not a pair.
		Guid receiptId = Guid.NewGuid();
		AcceptDuplicateGroupRequest request = new() { ReceiptIds = [receiptId, receiptId] };

		// Act
		ValidationResult result = _validator.Validate(request);

		// Assert
		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e => e.ErrorMessage == AcceptDuplicateGroupRequestValidator.ReceiptIdsTooFew);
	}

	[Fact]
	public void EmptyGuid_Fails()
	{
		// Arrange
		AcceptDuplicateGroupRequest request = new() { ReceiptIds = [Guid.NewGuid(), Guid.Empty] };

		// Act
		ValidationResult result = _validator.Validate(request);

		// Assert
		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e => e.ErrorMessage == AcceptDuplicateGroupRequestValidator.ReceiptIdMustNotBeEmpty);
	}

	[Fact]
	public void MaxReceiptIds_Passes()
	{
		// Arrange
		AcceptDuplicateGroupRequest request = new()
		{
			ReceiptIds = [.. Enumerable.Range(0, AcceptDuplicateGroupRequestValidator.MaxReceiptIds).Select(_ => Guid.NewGuid())],
		};

		// Act
		ValidationResult result = _validator.Validate(request);

		// Assert
		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void MoreThanMaxReceiptIds_Fails()
	{
		// Arrange — accepting expands to C(n,2) rows, so the list must be bounded. 101 IDs would be
		// 5,050 pairs; the 300 IDs a reviewer measured were 44,850 rows in a single transaction.
		AcceptDuplicateGroupRequest request = new()
		{
			ReceiptIds = [.. Enumerable.Range(0, AcceptDuplicateGroupRequestValidator.MaxReceiptIds + 1).Select(_ => Guid.NewGuid())],
		};

		// Act
		ValidationResult result = _validator.Validate(request);

		// Assert
		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e => e.ErrorMessage == AcceptDuplicateGroupRequestValidator.ReceiptIdsTooMany);
	}

	[Fact]
	public void MaxReceiptIds_MatchesTheBulkYnabCap()
	{
		// The two endpoints take the same receiptIds shape; keeping one number stops them drifting.
		AcceptDuplicateGroupRequestValidator.MaxReceiptIds
			.Should().Be(BulkPushYnabTransactionsRequestValidator.MaxReceiptIds);
	}
}
