using System.Text.Json;
using Infrastructure.Entities.Taxonomy;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	private const int HistoricalCandidatePayloadLimit = 128 * 1024;

	// v14 codec; manual payloads retain the unbounded text accepted by v13.
	internal static async Task WriteHistoricalCandidateAsync(SqliteConnection sqlite, State state, CancellationToken ct)
	{
		ValidateHistoricalCandidate(state);
		var headers = state.HistoricalOperations.ToDictionary(h => h.Id);
		async Task Write(string kind, string id, string payload)
		{
			Require(!kind.StartsWith("automatic_", StringComparison.Ordinal) || payload.Length <= HistoricalCandidatePayloadLimit, "Historical candidate record exceeds its bounded payload size.");
			await using var command = sqlite.CreateCommand();
			command.CommandText = "INSERT INTO historical_operation_archive(kind,id,payload) VALUES($kind,$id,$payload)";
			command.Parameters.AddWithValue("$kind", kind);
			command.Parameters.AddWithValue("$id", id);
			command.Parameters.AddWithValue("$payload", payload);
			await command.ExecuteNonQueryAsync(ct);
		}
		foreach (var header in state.HistoricalOperations)
		{
			bool automatic = header.Kind == HistoricalOperationKind.ApprovedMappingV1;
			string payload = automatic
					? JsonSerializer.Serialize(new PortableAutomaticHistoricalV14.HeaderWire(header, header.Kind, header.ApprovedMappingRevisionId), JsonOptions)
					: HistoricalManualPayload(header);
			await Write(automatic ? "automatic_operation" : "operation", ArchiveKey(header), payload);
		}
		foreach (var item in state.HistoricalItems)
		{
			bool automatic = headers[item.OperationId].Kind == HistoricalOperationKind.ApprovedMappingV1;
			string payload = automatic
					? JsonSerializer.Serialize(new PortableAutomaticHistoricalV14.ItemWire(item, item.ApprovedMappingRevisionId), JsonOptions)
					: HistoricalManualPayload(item);
			await Write(automatic ? "automatic_item" : "item", ArchiveKey(item), payload);
		}
	}

	// Validate the captured business snapshot before any destination writes.
	// Completed operations may precede later source edits; compare receipt and
	// item evidence independently when the corresponding revision still matches.
	private static async Task ValidateCandidateBusinessReferencesAsync(SqliteConnection sqlite, State state, CancellationToken ct)
	{
		var receipts = new Dictionary<Guid, string>();
		await using (var command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT id, location FROM receipts";
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct)) { receipts.Add(Guid.Parse(reader.GetString(0)), reader.GetString(1)); }
		}
		var items = new Dictionary<Guid, (Guid ReceiptId, string Description, string? Code)>();
		await using (var command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT id, receipt_id, description, receipt_item_code FROM receipt_items";
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				var receiptId = Guid.Parse(reader.GetString(1));
				Require(receipts.ContainsKey(receiptId), "Business item references a missing receipt.");
				items.Add(Guid.Parse(reader.GetString(0)), (receiptId, reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
			}
		}
		Require(state.Assignments.All(a => items.ContainsKey(a.ReceiptItemId)), "Classification references a missing business item.");
		var revisions = state.SourceRevisions.ToDictionary(r => (r.Table, r.Id), r => r.Revision);
		foreach (var row in state.HistoricalItems)
		{
			Require(receipts.ContainsKey(row.ReceiptId) && items.TryGetValue(row.ItemId, out _),
				"Historical item references a missing business receipt/item.");
			var item = items[row.ItemId];
			if (revisions[("ReceiptItems", row.ItemId)] == row.ItemRevision)
			{
				Require(item.ReceiptId == row.ReceiptId, "Historical item references a different business receipt with an unchanged item revision.");
				Require(item.Description == row.RawDescription && item.Code == row.ItemCode,
					"Historical source differs from unchanged business revisions.");
			}
			if (revisions[("Receipts", row.ReceiptId)] == row.ReceiptRevision)
			{
				Require(receipts[row.ReceiptId] == row.Store, "Historical source differs from unchanged business revisions.");
			}
		}
	}

	internal static async Task<State> ReadHistoricalCandidateAsync(SqliteConnection sqlite, State state, CancellationToken ct)
	{
		List<HistoricalOperationEntity> headers = [];
		List<HistoricalOperationItemEntity> items = [];
		HashSet<Guid> automaticHeaders = [];
		HashSet<(Guid OperationId, Guid ItemId)> automaticItems = [];
		Dictionary<Guid, int> automaticItemCounts = [];
		await using var command = sqlite.CreateCommand();
		// A UTF-16 code unit occupies at most three UTF-8 bytes. Casting to BLOB
		// makes length NUL-safe; CASE prevents an oversized value crossing into .NET.
		command.CommandText = """
                        SELECT CASE WHEN length(CAST(kind AS BLOB)) <= $bytes THEN kind END,
                               CASE WHEN length(CAST(id AS BLOB)) <= $bytes THEN id END,
                               CASE WHEN kind IN ('operation','item') OR length(CAST(payload AS BLOB)) <= $bytes THEN payload END
                        FROM historical_operation_archive ORDER BY kind,id
                        """;
		command.Parameters.AddWithValue("$bytes", HistoricalCandidatePayloadLimit * 3);
		await using var reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct))
		{
			Require(!reader.IsDBNull(0) && !reader.IsDBNull(1) && !reader.IsDBNull(2),
					"Historical candidate record exceeds its bounded payload size or has null fields.");
			string kind = reader.GetString(0), key = reader.GetString(1), payload = reader.GetString(2);
			Require(kind.Length <= HistoricalCandidatePayloadLimit && key.Length <= HistoricalCandidatePayloadLimit
					&& !payload.Contains('\0') && (!kind.StartsWith("automatic_", StringComparison.Ordinal) || payload.Length <= HistoricalCandidatePayloadLimit), "Historical candidate record exceeds its bounded payload size.");
			if (kind is "operation" or "automatic_operation")
			{
				var header = kind == "operation" ? DeserializeStrict<HistoricalOperationEntity>(payload)
						: PortableAutomaticHistoricalV14.ReadHeader(payload).Header;
				Require(header != null && key == ArchiveKey(header), "Historical archive key differs from payload.");
				headers.Add(header!);
				if (kind == "automatic_operation") { automaticHeaders.Add(header!.Id); }
			}
			else if (kind is "item" or "automatic_item")
			{
				var item = kind == "item" ? DeserializeStrict<HistoricalOperationItemEntity>(payload)
						: PortableAutomaticHistoricalV14.ReadItem(payload).Item;
				Require(item != null && key == ArchiveKey(item), "Historical archive key differs from payload.");
				items.Add(item!);
				if (kind == "automatic_item")
				{
					int count = automaticItemCounts.GetValueOrDefault(item!.OperationId) + 1;
					Require(count <= 100, "Automatic historical manifest exceeds its bounded size.");
					automaticItemCounts[item.OperationId] = count;
					automaticItems.Add((item.OperationId, item.ItemId));
				}
			}
			else { throw new InvalidDataException("Unknown historical archive kind."); }
		}
		Require(headers.All(h => automaticHeaders.Contains(h.Id) == (h.Kind == HistoricalOperationKind.ApprovedMappingV1)), "Historical header wire kind differs from provenance.");
		Require(items.All(i => automaticItems.Contains((i.OperationId, i.ItemId)) == automaticHeaders.Contains(i.OperationId)), "Historical item wire kind differs from header.");
		var result = state with { IncludesHistorical = true, HistoricalOperations = headers, HistoricalItems = items };
		ValidateHistoricalCandidate(result);
		return result;
	}
}
