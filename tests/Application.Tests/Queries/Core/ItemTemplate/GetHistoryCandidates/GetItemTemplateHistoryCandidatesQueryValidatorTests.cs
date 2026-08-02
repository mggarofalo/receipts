using Application.Queries.Core.ItemTemplate.GetHistoryCandidates;
using FluentAssertions;
using FluentValidation.Results;

namespace Application.Tests.Queries.Core.ItemTemplate.GetHistoryCandidates;

public class GetItemTemplateHistoryCandidatesQueryValidatorTests
{
	private readonly GetItemTemplateHistoryCandidatesQueryValidator _validator = new();

	[Fact]
	public void Validate_ShouldPass_ForDefaultParameters()
	{
		ValidationResult result = _validator.Validate(new GetItemTemplateHistoryCandidatesQuery(0, 50, 2));

		result.IsValid.Should().BeTrue();
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(-100)]
	public void Validate_ShouldFail_WhenOffsetIsNegative(int offset)
	{
		ValidationResult result = _validator.Validate(new GetItemTemplateHistoryCandidatesQuery(offset, 50, 2));

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e => e.PropertyName == nameof(GetItemTemplateHistoryCandidatesQuery.Offset));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(501)]
	public void Validate_ShouldFail_WhenLimitIsOutOfRange(int limit)
	{
		ValidationResult result = _validator.Validate(new GetItemTemplateHistoryCandidatesQuery(0, limit, 2));

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e => e.PropertyName == nameof(GetItemTemplateHistoryCandidatesQuery.Limit));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-3)]
	public void Validate_ShouldFail_WhenMinCountIsBelowOne(int minCount)
	{
		ValidationResult result = _validator.Validate(new GetItemTemplateHistoryCandidatesQuery(0, 50, minCount));

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e => e.PropertyName == nameof(GetItemTemplateHistoryCandidatesQuery.MinCount));
	}
}
