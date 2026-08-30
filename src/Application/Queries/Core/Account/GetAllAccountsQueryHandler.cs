using Application.Interfaces.Services;
using Application.Models;
using Mediator;

namespace Application.Queries.Core.Account;

public class GetAllAccountsQueryHandler(IAccountService accountService) : IRequestHandler<GetAllAccountsQuery, PagedResult<Domain.Core.Account>>
{
	public async ValueTask<PagedResult<Domain.Core.Account>> Handle(GetAllAccountsQuery request, CancellationToken cancellationToken)
	{
		return await accountService.GetAllAsync(request.Offset, request.Limit, request.Sort, request.IsActive, request.Q, cancellationToken);
	}
}
