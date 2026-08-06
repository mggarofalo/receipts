using API.Generated.Dtos;
using API.Mapping.Aggregates;
using Application.Queries.Aggregates.TransactionAccounts;
using Asp.Versioning;
using Domain.Aggregates;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers.Aggregates;

[ApiVersion("1.0")]
[ApiController]
[Route("api/transaction-accounts")]
[Produces("application/json")]
[Authorize]
public class TransactionAccountController(IMediator mediator, TransactionAccountMapper mapper) : ControllerBase
{
	[HttpGet("")]
	[EndpointSummary("Get transaction accounts")]
	[EndpointDescription("Returns transaction accounts filtered by transaction ID or receipt ID.")]
	public async Task<Results<Ok<List<TransactionAccountResponse>>, BadRequest<ProblemDetails>>> GetTransactionAccounts([FromQuery] Guid? transactionId = null, [FromQuery] Guid? receiptId = null, CancellationToken cancellationToken = default)
	{
		if (transactionId.HasValue && receiptId.HasValue)
		{
			return ApiProblem.BadRequest("Provide either transactionId or receiptId, not both.");
		}

		if (transactionId.HasValue)
		{
			GetTransactionAccountByTransactionIdQuery query = new(transactionId.Value);
			TransactionAccount? result = await mediator.Send(query, cancellationToken);

			List<TransactionAccountResponse> model = result != null
				? [mapper.ToResponse(result)]
				: [];
			return TypedResults.Ok(model);
		}

		if (receiptId.HasValue)
		{
			GetTransactionAccountsByReceiptIdQuery query = new(receiptId.Value);
			List<TransactionAccount>? result = await mediator.Send(query, cancellationToken);

			List<TransactionAccountResponse> model = [.. (result ?? []).Select(mapper.ToResponse)];
			return TypedResults.Ok(model);
		}

		return ApiProblem.BadRequest("Either transactionId or receiptId query parameter is required.");
	}
}
