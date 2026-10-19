using Application.Models.Search;
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
		RuleFor(x => x.Options!).SetValidator(new PurchaseSearchOptionsValidator());
	}
}

internal class PurchaseSearchOptionsValidator : AbstractValidator<PurchaseSearchOptions>
{
	public PurchaseSearchOptionsValidator()
	{
		RuleFor(x => x.SortBy).Must(value => value is "date" or "description" or "location" or "unitPrice" or "quantity" or "totalAmount")
			.WithMessage("sortBy must be date, description, location, unitPrice, quantity, or totalAmount.");
		RuleFor(x => x.SortDirection).Must(value => value is "asc" or "desc")
			.WithMessage("sortDirection must be asc or desc.");
		RuleFor(x => x.Location).Must(value => value is null || value.Trim().Length <= 200)
			.WithMessage("location must contain at most 200 characters after trimming.");
		RuleFor(x => x.EndDate).Must((options, end) => !end.HasValue || !options.StartDate.HasValue || end >= options.StartDate)
			.WithMessage("endDate must not be earlier than startDate.");
		RuleFor(x => x.MinUnitPrice).GreaterThanOrEqualTo(0);
		RuleFor(x => x.MaxUnitPrice).GreaterThanOrEqualTo(0)
			.Must((options, max) => !max.HasValue || !options.MinUnitPrice.HasValue || max >= options.MinUnitPrice)
			.WithMessage("maxUnitPrice must not be less than minUnitPrice.");
	}
}
