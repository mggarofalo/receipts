using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Normalization;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

internal static class ReceiptWriteIdempotency
{
	internal static async Task<ReceiptWriteRequestEntity> FindOrCreateAsync(ApplicationDbContext context,
		Guid requestId, string kind, object payload, CancellationToken ct)
	{
		if (requestId == Guid.Empty) { throw new NormalizationCatalogException(400, "invalid_request_id", "Supply a nonempty client request ID."); }
		string actor = context.ReceiptWriteActor ?? throw new NormalizationCatalogException(403, "request_actor_required", "A signed-in user is required for replayable receipt writes.");
		string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
		ReceiptWriteRequestEntity? previous = await context.ReceiptWriteRequests.SingleOrDefaultAsync(value => value.Actor == actor && value.RequestId == requestId, ct);
		if (previous is not null)
		{
			if (previous.Kind != kind || previous.RequestHash != hash)
			{ throw new NormalizationCatalogException(409, "receipt_request_conflict", "This request ID was already used for different receipt facts or normalization choices."); }
			return previous;
		}
		ReceiptWriteRequestEntity created = new() { Actor = actor, RequestId = requestId, Kind = kind, RequestHash = hash };
		context.ReceiptWriteRequests.Add(created);
		return created;
	}
}
