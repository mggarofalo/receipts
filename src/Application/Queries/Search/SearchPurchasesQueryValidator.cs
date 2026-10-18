using FluentValidation;

namespace Application.Queries.Search;

public class SearchPurchasesQueryValidator : AbstractValidator<SearchPurchasesQuery>
{
	public SearchPurchasesQueryValidator()
	{
		RuleFor(x => x.Q).Must(q => q is not null && q.Trim().Length is >= 2 and <= 200)
			.WithMessage("q must contain between 2 and 200 characters after trimming.");
		RuleFor(x => x.Offset).GreaterThanOrEqualTo(0);
		RuleFor(x => x.Limit).InclusiveBetween(1, 100);
	}
}
