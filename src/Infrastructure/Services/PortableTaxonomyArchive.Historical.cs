using System.Security.Cryptography;
using System.Text.Json;
using Application.Taxonomy;
using Infrastructure.Entities.Taxonomy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	private static async Task ExportHistoricalAsync(ApplicationDbContext db, SqliteConnection sqlite, CancellationToken ct)
	{
		if (await db.HistoricalOperations.AnyAsync(x => x.Kind != HistoricalOperationKind.ManualV1, ct))
		{ throw new InvalidDataException("Automatic historical operations cannot be exported yet."); }
		async Task Rows<T>(string kind, IQueryable<T> rows) where T : class
		{
			await foreach (T row in rows.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
			{
				await using var command = sqlite.CreateCommand();
				command.CommandText = "INSERT INTO historical_operation_archive(kind,id,payload) VALUES($kind,$id,$payload)";
				command.Parameters.AddWithValue("$kind", kind);
				command.Parameters.AddWithValue("$id", ArchiveKey(row));
				command.Parameters.AddWithValue("$payload", HistoricalManualPayload(row));
				await command.ExecuteNonQueryAsync(ct);
			}
		}
		await Rows("operation", db.HistoricalOperations);
		await Rows("item", db.HistoricalOperationItems);
	}


	// The public archive format predates automatic provenance. Keep its exact manual
	// shape until automatic evidence has a separately validated portable format.
	private static string HistoricalManualPayload<T>(T row) where T : class
	{
		object payload = row switch
		{
			HistoricalOperationEntity h => new
			{
				h.Id,
				h.Actor,
				h.RequestHash,
				h.SelectionJson,
				h.TaxonomyRevisionId,
				h.CapturedAt,
				h.Reason,
				h.State,
				h.UndoTaxonomyRevisionId,
				h.UndoReason
			},
			HistoricalOperationItemEntity i => new
			{
				i.OperationId,
				i.ItemId,
				i.ReceiptId,
				i.Store,
				i.RawDescription,
				i.ItemCode,
				i.ItemRevision,
				i.ReceiptRevision,
				i.ExpectedAssignmentId,
				i.PreviousCategoryId,
				i.TargetCategoryId,
				i.ApplyOperationId,
				i.UndoOperationId,
				i.State,
				i.AppliedAssignmentId,
				i.Reason,
				i.UndoState,
				i.UndoAssignmentId,
				i.UndoReason
			},
			_ => throw new InvalidOperationException("Unknown historical archive row.")
		};
		return JsonSerializer.Serialize(payload, JsonOptions);
	}

	private static async Task<State> ReadHistoricalAsync(SqliteConnection sqlite, State state, CancellationToken ct)
	{
		List<HistoricalOperationEntity> operations = [];
		List<HistoricalOperationItemEntity> items = [];
		await using var command = sqlite.CreateCommand();
		command.CommandText = "SELECT kind,id,payload FROM historical_operation_archive ORDER BY kind,id";
		await using var reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct))
		{
			string kind = reader.GetString(0), key = reader.GetString(1), payload = reader.GetString(2);
			if (kind == "operation")
			{
				var row = DeserializeStrict<HistoricalOperationEntity>(payload) ?? throw new InvalidDataException("Null historical operation.");
				Require(key == ArchiveKey(row), "Historical archive key differs from payload.");
				operations.Add(row);
			}
			else if (kind == "item")
			{
				var row = DeserializeStrict<HistoricalOperationItemEntity>(payload) ?? throw new InvalidDataException("Null historical item.");
				Require(key == ArchiveKey(row), "Historical archive key differs from payload.");
				items.Add(row);
			}
			else { throw new InvalidDataException("Unknown historical archive kind."); }
		}
		return state with { IncludesHistorical = true, HistoricalOperations = operations, HistoricalItems = items };
	}

	private static void ValidateHistorical(State s) => ValidateHistorical(s, allowAutomatic: false);

	internal static void ValidateHistoricalCandidate(State s) => ValidateHistorical(s, allowAutomatic: true);

	private static void ValidateHistorical(State s, bool allowAutomatic)
	{
		Unique(s.HistoricalOperations); Unique(s.HistoricalItems);
		var headers = s.HistoricalOperations.ToDictionary(x => x.Id);
		var assignments = s.Assignments.ToDictionary(x => x.Id);
		HashSet<Guid> operationIds = [];
		foreach (var row in s.HistoricalItems)
		{
			Require(row.ApplyOperationId != Guid.Empty && row.UndoOperationId != Guid.Empty
				&& operationIds.Add(row.ApplyOperationId) && operationIds.Add(row.UndoOperationId),
				"Invalid historical manifest evidence: duplicate operation ID.");
		}
		foreach (var h in s.HistoricalOperations)
		{
			var rows = s.HistoricalItems.Where(i => i.OperationId == h.Id).ToList();
			if (allowAutomatic && h.Kind == HistoricalOperationKind.ApprovedMappingV1)
			{
				PortableAutomaticHistoricalV14.Validate(s, h, rows);
				continue;
			}
			Require(h.Kind == HistoricalOperationKind.ManualV1, "Automatic historical operations cannot be restored yet.");
			Require(h.ApprovedMappingRevisionId == null, "Manual historical headers cannot carry automatic provenance.");
			Require(h.Id != Guid.Empty && Text(h.Actor) && Text(h.Reason) && Hash(h.RequestHash)
				&& Utc(h.CapturedAt) && Enum.IsDefined(h.State)
				&& s.Revisions.Any(r => r.Id == h.TaxonomyRevisionId && r.RecordedAt <= h.CapturedAt), "Invalid historical header.");
			var groups = DeserializeStrict<HistoricalGroupSelection[]>(h.SelectionJson)
				?? throw new InvalidDataException("Null historical selection.");
			Require(groups.Length is >= 1 and <= HistoricalReclassificationPolicy.MaximumSelectedGroups
				&& groups.All(g => g != null && g.Group != null && g.Group.Store != null && g.Group.RawDescription != null
					&& !g.Group.Store.Contains('\0') && !g.Group.RawDescription.Contains('\0') && g.Group.ItemCode?.Contains('\0') != true)
				&& groups.Select(g => g.Group).Distinct().Count() == groups.Length, "Invalid historical selection.");
			var canonical = groups.OrderBy(g => g.Group.Store, StringComparer.Ordinal)
				.ThenBy(g => g.Group.RawDescription, StringComparer.Ordinal).ThenBy(g => g.Group.ItemCode, StringComparer.Ordinal).ToArray();
			Require(groups.SequenceEqual(canonical), "Historical selection is not canonical.");
			Require(h.RequestHash == Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
			{ h.Actor, h.TaxonomyRevisionId, h.Reason, Groups = canonical }))), "Historical request hash differs from intent.");
			Require(groups.All(g => g.TargetCategoryId == null || s.Nodes.Any(n => n.RevisionId == h.TaxonomyRevisionId
				&& n.CategoryId == g.TargetCategoryId && !n.Archived)), "Historical target is unavailable in captured taxonomy.");
			bool undo = h.UndoTaxonomyRevisionId != null;
			Require(undo ? Text(h.UndoReason) && s.Revisions.Any(r => r.Id == h.UndoTaxonomyRevisionId)
				: h.UndoReason == null, "Invalid frozen historical undo intent.");
			Require(undo == (h.State is HistoricalOperationState.Undoing or HistoricalOperationState.Undone), "Historical state differs from undo intent.");
			Require(h.State != HistoricalOperationState.Preview || rows.All(i => i.State == HistoricalItemState.Pending), "Preview has applied results.");
			Require(h.State != HistoricalOperationState.Completed || rows.All(i => i.State != HistoricalItemState.Pending), "Completed operation has pending items.");
			Require(h.State != HistoricalOperationState.Undone || rows.All(i => i.State != HistoricalItemState.Applied || i.UndoState != HistoricalItemState.Pending), "Undone operation has pending undo items.");
			foreach (var row in rows)
			{
				Require(row.ApprovedMappingRevisionId == null, "Manual historical items cannot carry automatic provenance.");
				Require(row.ItemId != Guid.Empty && row.ReceiptId != Guid.Empty && row.ItemRevision != Guid.Empty && row.ReceiptRevision != Guid.Empty
					&& groups.Any(g => g.Group == new HistoricalGroupKey(row.Store, row.RawDescription, row.ItemCode) && g.TargetCategoryId == row.TargetCategoryId)
					&& row.ApplyOperationId != Guid.Empty && row.UndoOperationId != Guid.Empty, "Invalid historical manifest evidence.");
				Require(row.ExpectedAssignmentId is Guid previous
					? assignments.TryGetValue(previous, out var a) && a.ReceiptItemId == row.ItemId && a.CategoryId == row.PreviousCategoryId && a.RecordedAt <= h.CapturedAt
					: row.PreviousCategoryId == null, "Historical previous assignment differs from evidence.");
				ValidateHistoricalOutcome(s, h, row, false);
				ValidateHistoricalOutcome(s, h, row, true);
			}
		}
		Require(s.HistoricalItems.All(i => headers.ContainsKey(i.OperationId)), "Orphan historical manifest item.");
	}

	private static void ValidateHistoricalOutcome(State s, HistoricalOperationEntity h, HistoricalOperationItemEntity row, bool undo)
	{
		var state = undo ? row.UndoState : row.State;
		var assignmentId = undo ? row.UndoAssignmentId : row.AppliedAssignmentId;
		var reason = undo ? row.UndoReason : row.Reason;
		Guid operationId = undo ? row.UndoOperationId : row.ApplyOperationId;
		Require(Enum.IsDefined(state), "Unknown historical outcome.");
		Require(!undo || state == HistoricalItemState.Pending || (row.State == HistoricalItemState.Applied && h.UndoTaxonomyRevisionId != null), "Undo outcome lacks applied evidence or frozen intent.");
		if (state != HistoricalItemState.Applied)
		{
			Require(assignmentId == null && (state == HistoricalItemState.Pending ? reason == null : Text(reason)), "Invalid historical non-applied outcome.");
			Require(!s.ClassificationOperations.Any(o => o.Actor == h.Actor && o.OperationId == operationId), "Unapplied historical operation has a classification result.");
			return;
		}
		Require(assignmentId != null && (!undo || (row.AppliedAssignmentId != null && h.UndoTaxonomyRevisionId != null)), "Historical applied outcome lacks assignment or undo intent.");
		var a = s.Assignments.SingleOrDefault(a => a.Id == assignmentId);
		var command = undo
			? HistoricalReclassificationPolicy.UndoCommand(row.Evidence(), row.AppliedAssignmentId!.Value, h.UndoTaxonomyRevisionId!.Value, h.UndoReason!)
			: HistoricalReclassificationPolicy.Command(row.Evidence(), h.TaxonomyRevisionId, h.Reason);
		Guid itemId = row.ItemId;
		string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { itemId, command })));
		Require(reason == null && a != null && a.ReceiptItemId == row.ItemId && a.Actor == h.Actor && a.OperationId == operationId
			&& a.PreviousAssignmentId == command.ExpectedAssignmentId && a.CategoryId == command.CategoryId
			&& a.TaxonomyRevisionId == command.TaxonomyRevisionId && a.Reason == command.Reason && a.Source == "manual"
			&& a.RecordedAt >= h.CapturedAt && s.ClassificationOperations.Any(o => o.Actor == h.Actor && o.OperationId == operationId
				&& o.AssignmentId == a.Id && o.RequestHash == hash), "Historical applied result differs from classification history.");
	}

	private static void ValidateHistoricalDestination(State existing, State incoming) => ValidateHistoricalDestination(existing, incoming, allowAutomatic: false);

	internal static void ValidateHistoricalCandidateDestination(State existing, State incoming) => ValidateHistoricalDestination(existing, incoming, allowAutomatic: true);

	private static void ValidateHistoricalDestination(State existing, State incoming, bool allowAutomatic)
	{
		// Older formats contain no historical operations. Preserve the destination's retained
		// evidence; never interpret omission as deletion. For v12, a shared operation must be
		// byte-equivalent (including progress). Rejecting changed progress prevents replay of
		// older outcomes and avoids mutating sealed first-undo intent through UPDATE.
		// Validate the retained union as well, including cross-operation ID collisions.
		var incomingIds = incoming.HistoricalOperations.Select(h => h.Id).ToHashSet();
		ValidateHistorical(incoming with
		{
			HistoricalOperations = [.. incoming.HistoricalOperations, .. existing.HistoricalOperations.Where(h => !incomingIds.Contains(h.Id))],
			HistoricalItems = [.. incoming.HistoricalItems, .. existing.HistoricalItems.Where(i => !incomingIds.Contains(i.OperationId))]
		}, allowAutomatic);
		if (!incoming.IncludesHistorical) { return; }
		foreach (var header in existing.HistoricalOperations)
		{
			var archived = incoming.HistoricalOperations.SingleOrDefault(h => h.Id == header.Id);
			if (archived == null) { continue; }
			Require(Equal(header, archived), "Restore conflicts with historical destination progress or intent.");
			Prefix(existing.HistoricalItems.Where(i => i.OperationId == header.Id), incoming.HistoricalItems.Where(i => i.OperationId == header.Id));
			Require(existing.HistoricalItems.Count(i => i.OperationId == header.Id) == incoming.HistoricalItems.Count(i => i.OperationId == header.Id), "Restore changes frozen historical manifest.");
		}
	}
}
