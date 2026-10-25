using API.Services;
using Application.Commands.Trash.Purge;
using Application.Interfaces.Services;
using Asp.Versioning;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers.Core;

[ApiVersion("1.0")]
[ApiController]
[Route("api/trash")]
[Produces("application/json")]
[Authorize]
public class TrashController(IMediator mediator, ILogger<TrashController> logger, IEntityChangeNotifier notifier) : ControllerBase
{
	[HttpPost("purge")]
	[EndpointSummary("Permanently delete eligible soft-deleted items")]
	[Authorize(Policy = "RequireAdmin")]
	public async Task<Results<Ok<PurgeTrashResult>, NoContent>> PurgeTrash(CancellationToken cancellationToken = default)
	{
		logger.LogWarning("PurgeTrash called — permanently deleting eligible soft-deleted items");
		PurgeTrashCommand command = new();
		PurgeTrashResult result = await mediator.Send(command, cancellationToken);

		string[] entityTypes = ["receipt", "receipt-item", "transaction", "adjustment", "account", "category", "subcategory", "item-template"];
		foreach (string entityType in entityTypes)
		{
			await notifier.NotifyAllChanged(entityType, "deleted");
		}

		return result.RetainedRecords > 0 || result.RetainedReceipts > 0 || result.RetainedItems > 0
			? TypedResults.Ok(result) : TypedResults.NoContent();
	}
}
