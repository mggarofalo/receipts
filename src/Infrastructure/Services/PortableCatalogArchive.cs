using System.Text.Json;
using System.Text.Json.Serialization;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>
/// Version 7's catalog state. Imported operation observations remain inspectable but cannot resume
/// against regenerated source revisions. This is business provenance, not imported instance audit logs.
/// </summary>
internal static partial class PortableCatalogArchive
{
	private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
	private static readonly string[] Origins = ["unassigned", "legacy_unknown", "automatic", "manual", "template", "reviewed", "mapped", "excluded", "purchase_excluded"];
	internal sealed record Assignment(Guid Id, string Origin, Guid? MappingId, Guid? AssignmentId);
	internal sealed record ReviewEvidence(Guid Id, NormalizationReviewState State, string? SourceHash, Guid? TargetId, string? Reason,
		string? ExclusionReason, Guid? DecisionId, string? ReviewedBy, DateTimeOffset? ReviewedAt, Guid? MappingRevision);

	internal static async Task ExportAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken ct)
	{
		await using (var schema = sqlite.CreateCommand())
		{
			schema.CommandText = "CREATE TABLE normalization_catalog (kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(kind,id))";
			await schema.ExecuteNonQueryAsync(ct);
		}
		async Task WriteAsync<T>(string kind, Guid id, T value)
		{
			await using var command = sqlite.CreateCommand();
			command.CommandText = "INSERT INTO normalization_catalog(kind,id,payload) VALUES($kind,$id,$payload)";
			command.Parameters.AddWithValue("$kind", kind);
			command.Parameters.AddWithValue("$id", id.ToString());
			command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value, JsonOptions));
			await command.ExecuteNonQueryAsync(ct);
		}
		await foreach (var mapping in source.NormalizationSourceMappings.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
		{ await WriteAsync("mapping", mapping.Id, mapping); }
		await foreach (var operation in source.NormalizationCatalogOperations.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
		{ await WriteAsync("operation", operation.Id, operation); }
		await foreach (var member in source.NormalizationCatalogMembers.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
		{ await WriteAsync("member", member.Id, member); }
		await foreach (var item in source.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
		{
			await WriteAsync("assignment", item.Id, new Assignment(item.Id, item.NormalizationOrigin, item.NormalizationMappingId, item.NormalizationAssignmentId));
			await WriteAsync("review_evidence", item.Id, new ReviewEvidence(item.Id, item.NormalizationReviewState, item.ReviewedSourceHash, item.ReviewedTargetId,
				item.NormalizationReviewReason, item.NormalizationExclusionReason, item.NormalizationReviewDecisionId, item.ReviewedBy, item.ReviewedAt, item.ReviewMappingRevision));
		}
		await foreach (var request in source.ReceiptWriteRequests.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
		{ await WriteAsync("write_request", RequestArchiveId(request), request); }
	}

	internal static async Task ImportAsync(ApplicationDbContext context, SqliteConnection sqlite, int version, CancellationToken ct)
	{
		// Existing operations were fenced by the restore gate before any business upserts.
		if (version < 7)
		{
			await using (var exists = sqlite.CreateCommand())
			{
				exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='receipt_items'";
				if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) == 0) { return; }
			}
			await using var command = sqlite.CreateCommand();
			command.CommandText = "SELECT id FROM receipt_items";
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				Guid id = Guid.Parse(reader.GetString(0));
				var item = await context.ReceiptItems.IgnoreAutoIncludes().SingleAsync(item => item.Id == id, ct);
				item.NormalizationOrigin = item.NormalizedDescriptionId.HasValue ? "legacy_unknown" : "unassigned";
				item.NormalizationMappingId = null;
				item.NormalizationAssignmentId = Guid.NewGuid();
			}
			await context.SaveChangesAsync(ct);
			return;
		}
		ImportState state = await LoadImportStateAsync(context, sqlite, version, ct);
		// Import in dependency order; each payload has an explicit versioned kind and strict shape.
		foreach (string kind in (version >= 8 ? new[] { "mapping", "operation", "member", "assignment", "review_evidence", "write_request" } : new[] { "mapping", "operation", "member", "assignment" }))
		{
			await using var command = sqlite.CreateCommand();
			command.CommandText = "SELECT id,payload FROM normalization_catalog WHERE kind=$kind ORDER BY id";
			command.Parameters.AddWithValue("$kind", kind);
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				Guid id = Guid.Parse(reader.GetString(0));
				string payload = reader.GetString(1);
				if (payload.Length > 128 * 1024) { throw new InvalidOperationException("Catalog backup record exceeds its bounded payload size."); }
				switch (kind)
				{
					case "review_evidence":
						ImportReviewEvidence(context, state, id, Read<ReviewEvidence>(payload));
						break;
					case "write_request":
						var request = Read<ReceiptWriteRequestEntity>(payload);
						if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Actor)
							|| request.RequestHash.Length != 64 || request.Kind is not ("complete" or "item-create" or "item-update"))
						{ throw new InvalidOperationException("Invalid receipt write replay evidence."); }
						var previous = await context.ReceiptWriteRequests.FindAsync([request.Actor, request.RequestId], ct);
						if (previous is null) { context.ReceiptWriteRequests.Add(request); }
						else if (previous.RequestHash != request.RequestHash || previous.Kind != request.Kind || previous.ReceiptId != request.ReceiptId
							|| !previous.ItemIds.SequenceEqual(request.ItemIds))
						{ throw new InvalidOperationException("Receipt replay evidence conflicts with an existing request."); }
						break;
					case "mapping":
						var mapping = ReadMapping(payload, version);
						bool activeMappingTarget = mapping.TargetId is Guid targetId && state.ActiveTargets.Contains(targetId);
						ValidateMapping(mapping, id, activeMappingTarget);
						var existingMapping = state.Mappings.GetValueOrDefault(id);
						if (existingMapping is null) { context.NormalizationSourceMappings.Add(mapping); state.Mappings.Add(id, mapping); }
						else { context.Entry(existingMapping).CurrentValues.SetValues(mapping); }
						break;
					case "operation":
						var operation = Read<NormalizationCatalogOperationEntity>(payload);
						if (operation.Id != id || operation.Members.Count != 0) { throw new InvalidOperationException("Invalid catalog operation in backup."); }
						operation.State = "restored";
						operation.Revision = Guid.NewGuid();
						operation.ErrorCode = "restored_observation";
						operation.ErrorDetail = "Historical evidence restored. Source revisions changed; create a new preview to change restored purchases.";
						var existingOperation = await context.NormalizationCatalogOperations.FindAsync([id], ct);
						if (existingOperation is null) { context.NormalizationCatalogOperations.Add(operation); }
						else { context.Entry(existingOperation).CurrentValues.SetValues(operation); }
						break;
					case "member":
						var member = Read<NormalizationCatalogMemberEntity>(payload);
						if (member.Id != id || !await context.NormalizationCatalogOperations.AnyAsync(operation => operation.Id == member.OperationId, ct))
						{ throw new InvalidOperationException("Invalid catalog operation membership in backup."); }
						var existingMember = await context.NormalizationCatalogMembers.FindAsync([id], ct);
						if (existingMember is null) { context.NormalizationCatalogMembers.Add(member); }
						else { context.Entry(existingMember).CurrentValues.SetValues(member); }
						break;
					case "assignment":
						var assignment = Read<Assignment>(payload);
						var item = state.Items.GetValueOrDefault(id)
							?? throw new InvalidOperationException("Catalog assignment references a missing purchase.");
						string assignmentLocation = state.Locations[item.ReceiptId];
						NormalizationSourceKey assignmentSource = NormalizationCatalogPolicy.Normalize(new(assignmentLocation, item.Description, item.ReceiptItemCode));
						NormalizationSourceMappingEntity? assignmentMapping = assignment.MappingId is Guid assignmentMappingId
							? state.Mappings.GetValueOrDefault(assignmentMappingId) : null;
						ValidateAssignment(assignment, id, item.NormalizedDescriptionId, assignmentSource, assignmentMapping);
						item.NormalizationOrigin = assignment.Origin;
						item.NormalizationMappingId = assignment.MappingId;
						item.NormalizationAssignmentId = assignment.AssignmentId;
						break;
				}
			}
			// All imported rows remain tracked throughout the transaction. Saving each kind once
			// preserves dependency ordering without repeatedly scanning that growing tracker.
			await context.SaveChangesAsync(ct);
		}
		await using var unknown = sqlite.CreateCommand();
		unknown.CommandText = version >= 9
			? "SELECT COUNT(*) FROM normalization_catalog WHERE kind NOT IN ('mapping','operation','member','assignment','review_evidence','write_request','consolidation')"
			: version >= 8
			? "SELECT COUNT(*) FROM normalization_catalog WHERE kind NOT IN ('mapping','operation','member','assignment','review_evidence','write_request')"
			: "SELECT COUNT(*) FROM normalization_catalog WHERE kind NOT IN ('mapping','operation','member','assignment')";
		if (Convert.ToInt64(await unknown.ExecuteScalarAsync(ct)) != 0)
		{ throw new InvalidOperationException("Backup contains an unsupported catalog record kind."); }
		await using var missing = sqlite.CreateCommand();
		missing.CommandText = "SELECT COUNT(*) FROM receipt_items i WHERE NOT EXISTS (SELECT 1 FROM normalization_catalog c WHERE c.kind='assignment' AND c.id=i.id)";
		if (Convert.ToInt64(await missing.ExecuteScalarAsync(ct)) != 0)
		{ throw new InvalidOperationException("Backup is missing purchase normalization provenance."); }
		if (version >= 8)
		{
			missing.CommandText = "SELECT COUNT(*) FROM receipt_items i WHERE NOT EXISTS (SELECT 1 FROM normalization_catalog c WHERE c.kind='review_evidence' AND c.id=i.id)";
			if (Convert.ToInt64(await missing.ExecuteScalarAsync(ct)) != 0) { throw new InvalidOperationException("Backup is missing purchase review evidence."); }
		}
	}
	private static Guid RequestArchiveId(ReceiptWriteRequestEntity request)
		=> new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Actor + ":" + request.RequestId))[..16]);

	private static void ImportReviewEvidence(ApplicationDbContext context, ImportState state, Guid id, ReviewEvidence evidence)
	{
		var item = state.Items.GetValueOrDefault(id)
			?? throw new InvalidOperationException("Review evidence references a missing purchase.");
		if (id != evidence.Id || !Enum.IsDefined(evidence.State)) { throw new InvalidOperationException("Invalid purchase review state."); }
		string location = state.Locations[item.ReceiptId];
		NormalizationSourceMappingEntity? mapping = item.NormalizationMappingId is Guid mappingId
			? state.Mappings.GetValueOrDefault(mappingId) : null;
		bool activeTarget = item.NormalizedDescriptionId is Guid targetId && state.ActiveTargets.Contains(targetId);
		ValidateReview(evidence, new Assignment(id, item.NormalizationOrigin, item.NormalizationMappingId, item.NormalizationAssignmentId),
			NormalizationCatalogPolicy.Hash(new(location, item.Description, item.ReceiptItemCode)), item.NormalizedDescriptionId, activeTarget, mapping);
		item.NormalizationReviewState = evidence.State;
		item.NormalizationReviewReason = evidence.Reason;
		item.NormalizationExclusionReason = evidence.ExclusionReason;
		item.NormalizationReviewDecisionId = evidence.State == NormalizationReviewState.NeedsReview ? null : evidence.DecisionId;
		item.ReviewedTargetId = evidence.TargetId;
		item.ReviewedSourceHash = evidence.State == NormalizationReviewState.NeedsReview ? null : evidence.SourceHash;
		item.ReviewedBy = evidence.ReviewedBy;
		item.ReviewedAt = evidence.ReviewedAt;
		item.ReviewMappingRevision = evidence.State == NormalizationReviewState.NeedsReview ? null : evidence.MappingRevision;
		context.RestoredReviewEvidence.Add(id);
	}

	internal static T Read<T>(string payload)
	{
		try
		{
			using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
			var root = document.RootElement;
			string[] properties = typeof(T).GetProperties().Where(property => property.CanRead && property.CanWrite).Select(property => property.Name).ToArray();
			if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != properties.Length
				|| properties.Any(property => !root.TryGetProperty(property, out _)))
			{ throw new InvalidOperationException("Catalog backup record has missing or duplicate fields."); }
			return JsonSerializer.Deserialize<T>(payload, JsonOptions) ?? throw new InvalidOperationException("Invalid catalog backup payload.");
		}
		catch (JsonException exception)
		{
			throw new InvalidOperationException("Catalog backup contains a malformed record.", exception);
		}
	}
}
