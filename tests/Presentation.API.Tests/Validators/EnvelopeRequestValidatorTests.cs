using API.Generated.Dtos;
using API.Validators;
using FluentValidation.Results;

namespace Presentation.API.Tests.Validators;

public class EnvelopeRequestValidatorTests
{
	public static TheoryData<string?> BlankNames => new() { null, "", "   ", "\t" };

	[Theory]
	[MemberData(nameof(BlankNames))]
	public void CreateEnvelope_BlankName_Fails(string? name)
	{
		// Act
		ValidationResult actual = new CreateEnvelopeRequestValidator().Validate(new CreateEnvelopeRequest { Name = name! });

		// Assert
		Assert.Contains(actual.Errors, e => e.ErrorMessage == EnvelopeRequestRules.NameMustNotBeBlank);
	}

	[Theory]
	[MemberData(nameof(BlankNames))]
	public void UpdateEnvelope_BlankName_Fails(string? name)
	{
		// Act
		ValidationResult actual = new UpdateEnvelopeRequestValidator().Validate(new UpdateEnvelopeRequest { Name = name! });

		// Assert
		Assert.Contains(actual.Errors, e => e.ErrorMessage == EnvelopeRequestRules.NameMustNotBeBlank);
	}

	[Theory]
	[MemberData(nameof(BlankNames))]
	public void CreateOccasion_BlankName_Fails(string? name)
	{
		// Act
		ValidationResult actual = new CreateOccasionRequestValidator().Validate(new CreateOccasionRequest { Name = name! });

		// Assert
		Assert.Contains(actual.Errors, e => e.ErrorMessage == EnvelopeRequestRules.NameMustNotBeBlank);
	}

	[Theory]
	[MemberData(nameof(BlankNames))]
	public void UpdateOccasion_BlankName_Fails(string? name)
	{
		// Act
		ValidationResult actual = new UpdateOccasionRequestValidator().Validate(new UpdateOccasionRequest { Name = name! });

		// Assert
		Assert.Contains(actual.Errors, e => e.ErrorMessage == EnvelopeRequestRules.NameMustNotBeBlank);
	}

	[Fact]
	public void NamedRequests_Pass()
	{
		// Act / Assert
		Assert.True(new CreateEnvelopeRequestValidator().Validate(new CreateEnvelopeRequest { Name = "Groceries" }).IsValid);
		Assert.True(new UpdateEnvelopeRequestValidator().Validate(new UpdateEnvelopeRequest { Name = "Groceries", IsActive = false }).IsValid);
		Assert.True(new CreateOccasionRequestValidator().Validate(new CreateOccasionRequest { Name = "Birthday" }).IsValid);
		Assert.True(new UpdateOccasionRequestValidator().Validate(new UpdateOccasionRequest { Name = "Birthday" }).IsValid);
	}

	[Theory]
	[MemberData(nameof(BlankNames))]
	public void SetYnabEnvelopeMapping_BlankCategoryId_Fails(string? categoryId)
	{
		// Arrange
		SetYnabEnvelopeMappingRequest request = new()
		{
			YnabCategoryId = categoryId!,
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "Needs",
			YnabBudgetId = Guid.NewGuid(),
		};

		// Act
		ValidationResult actual = new SetYnabEnvelopeMappingRequestValidator().Validate(request);

		// Assert
		Assert.Contains(actual.Errors, e => e.ErrorMessage == SetYnabEnvelopeMappingRequestValidator.YnabCategoryIdMustNotBeEmpty);
	}

	[Fact]
	public void SetYnabEnvelopeMapping_WithCategoryId_Passes()
	{
		// Arrange
		SetYnabEnvelopeMappingRequest request = new()
		{
			YnabCategoryId = "cat-1",
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "Needs",
			YnabBudgetId = Guid.NewGuid(),
		};

		// Act
		ValidationResult actual = new SetYnabEnvelopeMappingRequestValidator().Validate(request);

		// Assert
		Assert.True(actual.IsValid);
	}
}
