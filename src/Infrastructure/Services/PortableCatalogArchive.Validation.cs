using Application.Normalization;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Services;

internal static partial class PortableCatalogArchive
{
	// Both restore and offline publication use this authority check. Portable evidence never
	// manufactures instance revisions; the importer regenerates those independently.
	internal static void ValidateReview(ReviewEvidence evidence, Assignment assignment, string sourceHash,
		Guid? targetId, bool activeTarget, NormalizationSourceMappingEntity? mapping)
	{
		Require(evidence.Id == assignment.Id && Enum.IsDefined(evidence.State), "Invalid purchase review state.");
		if (evidence.State == NormalizationReviewState.NeedsReview) { return; }
		Require(evidence.SourceHash == sourceHash && evidence.TargetId == targetId
			&& evidence.DecisionId is Guid decision && decision != Guid.Empty && decision == assignment.AssignmentId
			&& !string.IsNullOrWhiteSpace(evidence.ReviewedBy) && evidence.ReviewedBy.Length <= 450 && evidence.ReviewedAt is not null
			&& (evidence.State != NormalizationReviewState.Excluded || (!targetId.HasValue && !string.IsNullOrWhiteSpace(evidence.ExclusionReason)))
			&& (evidence.State != NormalizationReviewState.Verified || activeTarget), "Purchase review evidence disagrees with source or identity.");
		if (evidence.Reason == "approved_mapping")
		{
			Require(mapping is not null && mapping.Id == assignment.MappingId && mapping.State == "active"
				&& mapping.Revision == evidence.MappingRevision && mapping.SourceHash == sourceHash && mapping.TargetId == targetId
				&& mapping.Exclude == (evidence.State == NormalizationReviewState.Excluded), "Review depends on an unavailable mapping.");
		}
		else
		{
			Require(evidence.Reason == (evidence.State == NormalizationReviewState.Excluded ? "purchase_exclusion" : "purchase_review")
				&& evidence.MappingRevision is null, "Review has no recognized authority.");
		}
	}

	internal static async Task ValidateArchiveAsync(SqliteConnection sqlite, CancellationToken ct)
	{
		await ValidateConsolidationsAsync(sqlite, ct);
		await ValidateNamesAsync(sqlite, ct);
		Dictionary<Guid, (Guid? Target, string Hash, NormalizationSourceKey Source)> items = [];
		HashSet<Guid> active = [];
		HashSet<Guid> targets = [];
		await using (SqliteCommand command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT id,status FROM normalized_descriptions";
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				Guid id = Guid.Parse(reader.GetString(0));
				Require(Enum.TryParse(reader.GetString(1), out NormalizedDescriptionStatus status) && Enum.IsDefined(status), "Unknown canonical status.");
				targets.Add(id);
				if (status == NormalizedDescriptionStatus.Active) { active.Add(id); }
			}
		}
		await using (SqliteCommand command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT i.id,i.normalized_description_id,r.location,i.description,i.receipt_item_code FROM receipt_items i JOIN receipts r ON r.id=i.receipt_id";
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				NormalizationSourceKey source = NormalizationCatalogPolicy.Normalize(new(reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
				items.Add(Guid.Parse(reader.GetString(0)), (reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)), NormalizationCatalogPolicy.Hash(source), source));
			}
		}
		Dictionary<Guid, NormalizationSourceMappingEntity> mappings = [];
		Dictionary<Guid, Assignment> assignments = [];
		Dictionary<Guid, ReviewEvidence> reviews = [];
		HashSet<Guid> operations = [];
		List<NormalizationCatalogMemberEntity> members = [];
		await using (SqliteCommand command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT kind,id,payload FROM normalization_catalog";
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				Guid id = Guid.Parse(reader.GetString(1));
				string payload = reader.GetString(2);
				Require(id != Guid.Empty && payload.Length <= 128 * 1024, "Invalid archive record.");
				switch (reader.GetString(0))
				{
					case "consolidation": break;
					case "mapping":
						NormalizationSourceMappingEntity mapping = Read<NormalizationSourceMappingEntity>(payload);
						ValidateMapping(mapping, id, mapping.TargetId is Guid target && active.Contains(target));
						mappings.Add(id, mapping);
						break;
					case "assignment":
						Assignment assignment = Read<Assignment>(payload);
						Require(assignment.Id == id, "Assignment ID mismatch.");
						assignments.Add(id, assignment);
						break;
					case "review_evidence":
						ReviewEvidence evidence = Read<ReviewEvidence>(payload);
						Require(evidence.Id == id, "Review ID mismatch.");
						reviews.Add(id, evidence);
						break;
					case "operation":
						NormalizationCatalogOperationEntity operation = Read<NormalizationCatalogOperationEntity>(payload);
						Require(operation.Id == id && operation.Members.Count == 0, "Invalid portable operation.");
						operations.Add(id);
						break;
					case "member":
						NormalizationCatalogMemberEntity member = Read<NormalizationCatalogMemberEntity>(payload);
						Require(member.Id == id, "Member ID mismatch.");
						members.Add(member);
						break;
					case "write_request":
						ReceiptWriteRequestEntity request = Read<ReceiptWriteRequestEntity>(payload);
						Require(request.RequestId != Guid.Empty && !string.IsNullOrWhiteSpace(request.Actor) && request.RequestHash.Length == 64
							&& request.Kind is "complete" or "item-create" or "item-update", "Invalid receipt replay evidence.");
						break;
					default: throw new InvalidDataException("Unknown portable evidence kind.");
				}
			}
		}
		Require(items.Keys.ToHashSet().SetEquals(assignments.Keys) && items.Keys.ToHashSet().SetEquals(reviews.Keys)
			&& members.All(member => operations.Contains(member.OperationId)), "Missing or dangling portable evidence.");
		foreach ((Guid id, Assignment assignment) in assignments)
		{
			var item = items[id];
			NormalizationSourceMappingEntity? mapping = assignment.MappingId is Guid mappingId ? mappings.GetValueOrDefault(mappingId) : null;
			ValidateAssignment(assignment, id, item.Target, item.Source, mapping);
			Require(item.Target is not Guid target || targets.Contains(target), "Dangling canonical link.");
			ValidateReview(reviews[id], assignment, item.Hash, item.Target, item.Target is Guid linked && active.Contains(linked), mapping);
		}
	}

	internal static void ValidateMapping(NormalizationSourceMappingEntity mapping, Guid id, bool activeTarget)
	{
		Require(mapping.Id == id && mapping.State is "active" or "revoked"
			&& mapping.SourceHash == NormalizationCatalogPolicy.Hash(new(mapping.Location, mapping.Description, mapping.ItemCode))
			&& mapping.Exclude != mapping.TargetId.HasValue
			&& (mapping.State != "active" || !mapping.TargetId.HasValue || activeTarget), "Invalid catalog mapping.");
	}

	internal static void ValidateAssignment(Assignment assignment, Guid id, Guid? target, NormalizationSourceKey source, NormalizationSourceMappingEntity? mapping)
	{
		Require(assignment.Id == id && Origins.Contains(assignment.Origin, StringComparer.Ordinal)
			&& (assignment.Origin is "unassigned" or "excluded" or "purchase_excluded" ? !target.HasValue : target.HasValue)
			&& (assignment.Origin is "mapped" or "excluded" || !assignment.MappingId.HasValue), "Invalid assignment origin.");
		if (assignment.Origin is "mapped" or "excluded")
		{
			Require(mapping is not null && mapping.Id == assignment.MappingId && mapping.TargetId == target && assignment.AssignmentId is not null
				&& mapping.Exclude == (assignment.Origin == "excluded") && mapping.Location == source.Location
				&& mapping.Description == source.Description && mapping.ItemCode == source.ItemCode, "Assignment disagrees with mapping.");
		}
	}

	private static void Require(bool condition, string message)
	{
		if (!condition) { throw new InvalidDataException(message); }
	}
}
