using API.Generated.Dtos;
using API.Validators;

namespace Presentation.API.Tests.Validators;

public class UpdateReceiptRequestValidatorTests
{
	private readonly UpdateReceiptRequestValidator _validator = new();

	[Fact]
	public void Should_Pass_When_AllFieldsValid()
	{
		// Arrange
		UpdateReceiptRequest request = new()
		{
			Id = Guid.NewGuid(),
			Location = "Store",
			Date = DateOnly.FromDateTime(DateTime.Today)
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.True(result.IsValid);
	}

	[Fact]
	public void Should_Fail_When_IdIsEmpty()
	{
		// Arrange
		UpdateReceiptRequest request = new()
		{
			Id = Guid.Empty,
			Location = "Store",
			Date = DateOnly.FromDateTime(DateTime.Today)
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateReceiptRequestValidator.IdMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_When_LocationIsEmpty()
	{
		// Arrange
		UpdateReceiptRequest request = new()
		{
			Id = Guid.NewGuid(),
			Location = "",
			Date = DateOnly.FromDateTime(DateTime.Today)
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateReceiptRequestValidator.LocationMustNotBeEmpty);
	}

	[Theory]
	[InlineData(0, false)]
	[InlineData(200, true)]
	[InlineData(201, false)]
	public void GeneratedContract_EnforcesCanonicalLocationLength(int length, bool valid)
	{
		UpdateReceiptRequest request = new()
		{
			Id = Guid.NewGuid(),
			Location = new string('a', length),
			Date = new DateOnly(2025, 1, 1),
		};
		List<System.ComponentModel.DataAnnotations.ValidationResult> errors = [];

		bool actual = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(request,
			new System.ComponentModel.DataAnnotations.ValidationContext(request), errors, validateAllProperties: true);

		Assert.Equal(valid, actual);
		if (!valid)
		{
			Assert.Contains(errors, error => error.MemberNames.Contains("Location"));
		}
	}

	[Fact]
	public void Should_Fail_When_DateIsInTheFuture()
	{
		// Arrange
		UpdateReceiptRequest request = new()
		{
			Id = Guid.NewGuid(),
			Location = "Store",
			Date = DateOnly.FromDateTime(DateTime.Today.AddDays(1))
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateReceiptRequestValidator.DateMustBePriorToCurrentDate);
	}
}
