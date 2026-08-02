using FluentValidation;

namespace Application.Queries.Core.ItemTemplate.GetHistoryCandidates;

public class GetItemTemplateHistoryCandidatesQueryValidator : AbstractValidator<GetItemTemplateHistoryCandidatesQuery>
{
	public GetItemTemplateHistoryCandidatesQueryValidator()
	{
		RuleFor(x => x.Offset)
			.GreaterThanOrEqualTo(0).WithMessage("Offset must be >= 0.");

		RuleFor(x => x.Limit)
			.InclusiveBetween(1, 500).WithMessage("Limit must be between 1 and 500.");

		RuleFor(x => x.MinCount)
			.GreaterThanOrEqualTo(1).WithMessage("MinCount must be >= 1.");
	}
}
