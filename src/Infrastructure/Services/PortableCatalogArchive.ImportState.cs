using System.Text.Json.Nodes;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

internal static partial class PortableCatalogArchive
{
	internal static async Task<Dictionary<Guid, Guid>> LoadLegacyAssignmentIdsAsync(SqliteConnection sqlite, int version, CancellationToken ct)
	{
		Dictionary<Guid, Guid> assignments = [];
		if (version < 10) { return assignments; }
		await using SqliteCommand command = sqlite.CreateCommand();
		command.CommandText = "SELECT payload FROM normalization_catalog WHERE kind='assignment'";
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct))
		{
			Assignment assignment = Read<Assignment>(reader.GetString(0));
			if (assignment.Origin == "legacy_unknown" && assignment.MappingId is null && assignment.AssignmentId is Guid id)
			{ assignments.Add(assignment.Id, id); }
		}
		return assignments;
	}

	private sealed record ImportState(
		Dictionary<Guid, ReceiptItemEntity> Items,
		Dictionary<Guid, string> Locations,
		Dictionary<Guid, NormalizationSourceMappingEntity> Mappings,
		HashSet<Guid> ActiveTargets);

	private static NormalizationSourceMappingEntity ReadMapping(string payload, int version)
	{
		if (version < 8)
		{
			JsonObject legacyMapping = JsonNode.Parse(payload)!.AsObject();
			if (legacyMapping.ContainsKey("ExclusionReason")) { throw new InvalidOperationException("Legacy mapping contains unsupported evidence."); }
			legacyMapping.Add("ExclusionReason", null);
			payload = legacyMapping.ToJsonString();
		}
		return Read<NormalizationSourceMappingEntity>(payload);
	}

	private static async Task<ImportState> LoadImportStateAsync(ApplicationDbContext context, SqliteConnection sqlite, int version, CancellationToken ct)
	{
		await NormalizationWriteGuard.AcquireWriteGateAsync(context, ct);
		HashSet<Guid> itemIds = [], mappingIds = [], targetIds = [];
		await using (SqliteCommand command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT kind,id,payload FROM normalization_catalog WHERE kind IN ('mapping','assignment','review_evidence')";
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				Guid id = Guid.Parse(reader.GetString(1));
				string payload = reader.GetString(2);
				if (payload.Length > 128 * 1024) { throw new InvalidOperationException("Catalog backup record exceeds its bounded payload size."); }
				switch (reader.GetString(0))
				{
					case "mapping":
						mappingIds.Add(id);
						if (ReadMapping(payload, version).TargetId is Guid targetId) { targetIds.Add(targetId); }
						break;
					case "assignment":
						itemIds.Add(id);
						Assignment assignment = Read<Assignment>(payload);
						if (assignment.MappingId is Guid mappingId) { mappingIds.Add(mappingId); }
						break;
					case "review_evidence":
						itemIds.Add(id);
						break;
				}
			}
		}

		// The normalization write gate keeps these snapshots current until commit.
		// Key them by archive references, never by the whole target database.
		Dictionary<Guid, ReceiptItemEntity> items = await (version >= 10 ? context.ReceiptItems.IgnoreQueryFilters() : context.ReceiptItems).IgnoreAutoIncludes()
			.Where(item => itemIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, ct);
		Guid[] receiptIds = items.Values.Select(item => item.ReceiptId).Distinct().ToArray();
		Dictionary<Guid, string> locations = await (version >= 10 ? context.Receipts.IgnoreQueryFilters() : context.Receipts).IgnoreAutoIncludes().AsNoTracking()
			.Where(receipt => receiptIds.Contains(receipt.Id)).Select(receipt => new { receipt.Id, receipt.Location })
			.ToDictionaryAsync(receipt => receipt.Id, receipt => receipt.Location, ct);
		mappingIds.UnionWith(items.Values.Where(item => item.NormalizationMappingId.HasValue)
			.Select(item => item.NormalizationMappingId!.Value));
		Dictionary<Guid, NormalizationSourceMappingEntity> mappings = await context.NormalizationSourceMappings
			.Where(mapping => mappingIds.Contains(mapping.Id)).ToDictionaryAsync(mapping => mapping.Id, ct);
		targetIds.UnionWith(items.Values.Where(item => item.NormalizedDescriptionId.HasValue)
			.Select(item => item.NormalizedDescriptionId!.Value));
		HashSet<Guid> activeTargets = (await context.NormalizedDescriptions.IgnoreAutoIncludes().AsNoTracking()
			.Where(target => targetIds.Contains(target.Id) && target.Status == NormalizedDescriptionStatus.Active)
			.Select(target => target.Id).ToListAsync(ct)).ToHashSet();
		return new(items, locations, mappings, activeTargets);
	}
}
