using System.Text.Json;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

internal static partial class PortableCatalogArchive
{
	// Artifact authority only. It is deliberately not a replayable application operation.
	internal sealed record Consolidation(Guid RetiredId, Guid SurvivorId, Guid DecisionId,
		string ReviewedBy, DateTimeOffset ReviewedAt, string Rationale,
		string ManifestSha256, string SourceSha256, string ToolRevision);

	internal static async Task<List<Consolidation>> ValidateConsolidationsAsync(SqliteConnection sqlite, CancellationToken ct)
	{
		List<Consolidation> records = [];
		await using (SqliteCommand command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT id,payload FROM normalization_catalog WHERE kind='consolidation'";
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				Consolidation record = Read<Consolidation>(reader.GetString(1));
				Require(Guid.Parse(reader.GetString(0)) == record.RetiredId, "Consolidation ID mismatch.");
				records.Add(record);
			}
		}
		if (records.Count == 0) { return records; }
		await using (SqliteCommand version = sqlite.CreateCommand())
		{
			version.CommandText = "SELECT value FROM backup_metadata WHERE key='export_version'";
			Require(Convert.ToString(await version.ExecuteScalarAsync(ct)) is "9" or "10" or "11" or "12" or "13" or "14" or "15", "Consolidation requires format 9 through 15.");
		}
		Dictionary<Guid, string> targets = [];
		await using (SqliteCommand command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT id,status FROM normalized_descriptions";
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct)) { targets.Add(Guid.Parse(reader.GetString(0)), reader.GetString(1)); }
		}
		HashSet<Guid> retired = records.Select(r => r.RetiredId).ToHashSet();
		Require(retired.Count == records.Count && records.Select(r => r.DecisionId).Distinct().Count() == records.Count,
			"Duplicate consolidation authority.");
		static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
		foreach (Consolidation record in records)
		{
			Require(record.RetiredId != Guid.Empty && record.SurvivorId != Guid.Empty && record.DecisionId != Guid.Empty
				&& record.RetiredId != record.SurvivorId && !targets.ContainsKey(record.RetiredId)
				&& targets.GetValueOrDefault(record.SurvivorId) == "Active" && !retired.Contains(record.SurvivorId),
				"Consolidation requires an absent loser and active survivor without chains or cycles.");
			Require(Hash(record.ManifestSha256) && Hash(record.SourceSha256)
				&& !string.IsNullOrWhiteSpace(record.ToolRevision) && record.ToolRevision.Length <= 200
				&& !string.IsNullOrWhiteSpace(record.Rationale) && record.Rationale.Length <= 2000
				&& record.ReviewedAt != default && record.ReviewedAt <= DateTimeOffset.UtcNow
				&& !string.IsNullOrWhiteSpace(record.ReviewedBy) && record.ReviewedBy.Length <= 450
				&& record.ReviewedBy.Length > "offline-agent:;manifest-sha256:".Length + 64
				&& record.ReviewedBy.StartsWith("offline-agent:", StringComparison.Ordinal)
				&& record.ReviewedBy.EndsWith(";manifest-sha256:" + record.ManifestSha256, StringComparison.Ordinal),
				"Consolidation requires actual reviewed manifest and source provenance.");
		}
		return records;
	}

	internal static async Task ValidateNamesAsync(SqliteConnection sqlite, CancellationToken ct)
	{
		// Match the application's invariant lowercase policy and the two PostgreSQL functional indexes.
		Dictionary<string, Guid> canonical = new(StringComparer.Ordinal), displayed = new(StringComparer.Ordinal);
		await using SqliteCommand command = sqlite.CreateCommand();
		command.CommandText = "SELECT id,canonical_name,display_label FROM normalized_descriptions";
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			string name = reader.GetString(1).ToLowerInvariant();
			string label = (reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2)).ToLowerInvariant();
			Require(canonical.TryAdd(name, id) && displayed.TryAdd(label, id), "Canonical or effective display names collide.");
		}
	}

	internal static async Task ConsumeConsolidationsAsync(ApplicationDbContext context, SqliteConnection sqlite, int version, CancellationToken ct)
	{
		if (version < 9) { return; }
		await ValidateArchiveAsync(sqlite, ct);
		List<Consolidation> records = await ValidateConsolidationsAsync(sqlite, ct);
		Dictionary<Guid, Guid> archiveAssignments = await LoadLegacyAssignmentIdsAsync(sqlite, version, ct);
		foreach (Consolidation record in records)
		{
			NormalizedDescriptionEntity? loser = await context.NormalizedDescriptions.FindAsync([record.RetiredId], ct);
			if (loser is null) { continue; } // Fresh restore or repeated import: absence alone never authorizes deletion.
			NormalizedDescriptionEntity? survivor = await context.NormalizedDescriptions.FindAsync([record.SurvivorId], ct);
			if (survivor is null)
			{
				// Establish the FK parent under a private temporary name before releasing the loser's names.
				survivor = new()
				{
					Id = record.SurvivorId,
					CanonicalName = "__backup_consolidation_" + Guid.NewGuid().ToString("N"),
					Status = Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active,
					CreatedAt = record.ReviewedAt
				};
				context.NormalizedDescriptions.Add(survivor);
				await context.SaveChangesAsync(ct);
			}
			// Like MergeAsync, preserve trashed purchases/templates as well as live rows. Artifact upserts
			// subsequently install the exact reviewed assignments, mapping revisions and review evidence.
			// Rows absent from the artifact (including trash) retain only a descriptive link.
			// Stamp an explicit legacy assignment so persistence cannot infer a manual choice.
			foreach (ReceiptItemEntity item in await context.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes()
				.Where(item => item.NormalizedDescriptionId == record.RetiredId).ToListAsync(ct))
			{
				item.NormalizedDescriptionId = record.SurvivorId;
				item.NormalizationOrigin = "legacy_unknown";
				item.NormalizationMappingId = null;
				// Install the archive's final legacy identity with the relink and proof invalidation.
				item.NormalizationAssignmentId = archiveAssignments.TryGetValue(item.Id, out Guid assignmentId)
					? assignmentId : Guid.NewGuid();
				item.NormalizationReviewState = Domain.NormalizedDescriptions.NormalizationReviewState.NeedsReview;
				item.NormalizationReviewReason = "legacy_unknown";
				item.NormalizationExclusionReason = null;
				item.NormalizationReviewDecisionId = null;
				item.ReviewedTargetId = null;
				item.ReviewedSourceHash = null;
				item.ReviewedBy = null;
				item.ReviewedAt = null;
				item.ReviewMappingRevision = null;
			}
			foreach (ItemTemplateEntity template in await context.ItemTemplates.IgnoreQueryFilters()
				.Where(item => item.NormalizedDescriptionId == record.RetiredId).ToListAsync(ct))
			{ template.NormalizedDescriptionId = record.SurvivorId; }
			foreach (NormalizationSourceMappingEntity mapping in await context.NormalizationSourceMappings
				.Where(mapping => mapping.TargetId == record.RetiredId).ToListAsync(ct))
			{ mapping.TargetId = record.SurvivorId; }
			foreach (NormalizedDescriptionEntity neighbour in await context.NormalizedDescriptions
				.Where(target => target.NearestNeighbourId == record.RetiredId).ToListAsync(ct))
			{ neighbour.NearestNeighbourId = null; neighbour.NearestNeighbourSimilarity = null; }
			foreach (Guid auditId in new[] { record.RetiredId, record.SurvivorId })
			{
				context.AddSemanticAuditEntry(NormalizedDescriptionService.NormalizedDescriptionEntityType, auditId.ToString(), AuditAction.Merge,
					[
						new FieldChange { FieldName = "retiredId", OldValue = record.RetiredId.ToString(), NewValue = null },
						new FieldChange { FieldName = "survivorId", OldValue = null, NewValue = record.SurvivorId.ToString() },
						new FieldChange { FieldName = "offlineAuthority", OldValue = null, NewValue = JsonSerializer.Serialize(record) }
					], DateTimeOffset.UtcNow);
			}
			// Use the same entity removal and audited SaveChanges mechanism as the application merge.
			context.NormalizedDescriptions.Remove(loser);
			await context.SaveChangesAsync(ct);
		}
	}
}
