using System.Text.Json;
using System.Text.Json.Serialization;
using Application.Normalization;
using Infrastructure.Entities.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>
/// Version 7's catalog state. Imported operation observations remain inspectable but cannot resume
/// against regenerated source revisions. This is business provenance, not imported instance audit logs.
/// </summary>
internal static class PortableCatalogArchive
{
	private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
	private static readonly string[] Origins = ["unassigned", "legacy_unknown", "automatic", "manual", "template", "reviewed", "mapped", "excluded"];
	private sealed record Assignment(Guid Id, string Origin, Guid? MappingId, Guid? AssignmentId);

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
		await foreach (var item in source.ReceiptItems.IgnoreAutoIncludes().AsNoTracking().Where(item => item.DeletedAt == null).AsAsyncEnumerable().WithCancellation(ct))
		{ await WriteAsync("assignment", item.Id, new Assignment(item.Id, item.NormalizationOrigin, item.NormalizationMappingId, item.NormalizationAssignmentId)); }
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
		// Import in dependency order; each payload has an explicit versioned kind and strict shape.
		foreach (string kind in new[] { "mapping", "operation", "member", "assignment" })
		{
			await using var command = sqlite.CreateCommand();
			command.CommandText = "SELECT id,payload FROM normalization_catalog WHERE kind=$kind ORDER BY id";
			command.Parameters.AddWithValue("$kind", kind);
			await using var reader = await command.ExecuteReaderAsync(ct);
			int pending = 0;
			while (await reader.ReadAsync(ct))
			{
				Guid id = Guid.Parse(reader.GetString(0));
				string payload = reader.GetString(1);
				if (payload.Length > 128 * 1024) { throw new InvalidOperationException("Catalog backup record exceeds its bounded payload size."); }
				switch (kind)
				{
					case "mapping":
						var mapping = Read<NormalizationSourceMappingEntity>(payload);
						if (mapping.Id != id || mapping.State is not ("active" or "revoked")
							|| mapping.SourceHash != NormalizationCatalogPolicy.Hash(new(mapping.Location, mapping.Description, mapping.ItemCode))
							|| mapping.Exclude == mapping.TargetId.HasValue)
						{ throw new InvalidOperationException("Invalid catalog mapping in backup."); }
						if (mapping.State == "active" && mapping.TargetId is Guid targetId
							&& !await context.NormalizedDescriptions.AnyAsync(target => target.Id == targetId && target.Status == Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active, ct))
						{ throw new InvalidOperationException("An active catalog mapping references a missing or inactive normalized description."); }
						var existingMapping = await context.NormalizationSourceMappings.FindAsync([id], ct);
						if (existingMapping is null) { context.NormalizationSourceMappings.Add(mapping); }
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
						if (assignment.Id != id || !Origins.Contains(assignment.Origin, StringComparer.Ordinal))
						{ throw new InvalidOperationException("Invalid catalog assignment in backup."); }
						var item = await context.ReceiptItems.IgnoreAutoIncludes().SingleOrDefaultAsync(item => item.Id == id, ct)
							?? throw new InvalidOperationException("Catalog assignment references a missing purchase.");
						if (assignment.Origin is "unassigned" or "excluded" ? item.NormalizedDescriptionId.HasValue : !item.NormalizedDescriptionId.HasValue)
						{ throw new InvalidOperationException("Catalog assignment origin disagrees with its normalized link."); }
						if (assignment.Origin is not ("mapped" or "excluded") && assignment.MappingId.HasValue)
						{ throw new InvalidOperationException("Only mapped or excluded assignments may reference a source mapping."); }
						if (assignment.Origin is "mapped" or "excluded")
						{
							var approved = await context.NormalizationSourceMappings.SingleOrDefaultAsync(mapping => mapping.Id == assignment.MappingId, ct);
							string location = await context.Receipts.Where(receipt => receipt.Id == item.ReceiptId).Select(receipt => receipt.Location).SingleAsync(ct);
							NormalizationSourceKey source = NormalizationCatalogPolicy.Normalize(new(location, item.Description, item.ReceiptItemCode));
							if (approved is null || approved.TargetId != item.NormalizedDescriptionId || assignment.AssignmentId is null
								|| approved.Exclude != (assignment.Origin == "excluded")
								|| approved.Location != source.Location || approved.Description != source.Description || approved.ItemCode != source.ItemCode)
							{ throw new InvalidOperationException("Catalog assignment does not agree with its mapping."); }
						}
						item.NormalizationOrigin = assignment.Origin;
						item.NormalizationMappingId = assignment.MappingId;
						item.NormalizationAssignmentId = assignment.AssignmentId;
						break;
				}
				if (++pending == 100) { await context.SaveChangesAsync(ct); pending = 0; }
			}
			await context.SaveChangesAsync(ct);
		}
		await using var unknown = sqlite.CreateCommand();
		unknown.CommandText = "SELECT COUNT(*) FROM normalization_catalog WHERE kind NOT IN ('mapping','operation','member','assignment')";
		if (Convert.ToInt64(await unknown.ExecuteScalarAsync(ct)) != 0)
		{ throw new InvalidOperationException("Backup contains an unsupported catalog record kind."); }
		await using var missing = sqlite.CreateCommand();
		missing.CommandText = "SELECT COUNT(*) FROM receipt_items i WHERE NOT EXISTS (SELECT 1 FROM normalization_catalog c WHERE c.kind='assignment' AND c.id=i.id)";
		if (Convert.ToInt64(await missing.ExecuteScalarAsync(ct)) != 0)
		{ throw new InvalidOperationException("Backup is missing purchase normalization provenance."); }
	}
	private static T Read<T>(string payload)
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
