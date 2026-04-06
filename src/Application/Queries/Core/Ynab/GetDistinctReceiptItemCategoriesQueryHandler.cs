using Application.Interfaces.Services;
using MediatR;

namespace Application.Queries.Core.Ynab;

public class GetDistinctReceiptItemCategoriesQueryHandler(IYnabCategoryMappingService service) : IRequestHandler<GetDistinctReceiptItemCategoriesQuery, List<string>>
{
	public async Task<List<string>> Handle(GetDistinctReceiptItemCategoriesQuery request, CancellationToken cancellationToken)
	{
		return await service.GetDistinctReceiptItemCategoriesAsync(cancellationToken);
	}
}
