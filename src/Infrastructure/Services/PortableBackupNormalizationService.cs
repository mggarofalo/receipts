using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Services;

/// <summary>Transforms a closed portable artifact only. It never connects to an application database.</summary>
public sealed class PortableBackupNormalizationService(IOfflinePortableBackupInspection? inspection = null) : IOfflinePortableBackupNormalization
{
	private readonly IOfflinePortableBackupInspection inspection = inspection ?? new OfflinePortableBackupInspection();
	private sealed record Lineage(string SourceSha256, string ManifestSha256, string ToolRevision, string OutputSha256);

	public async Task<OfflineBackupNormalizationResult> ApplyAsync(string sourcePath, string manifestPath, string outputPath, CancellationToken cancellationToken = default)
	{
		string source = Resolve(sourcePath), manifestFile = Resolve(manifestPath), output = Resolve(outputPath);
		EnsurePaths(source, manifestFile, output);
		await using FileStream sourceLease = new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
		byte[] bytes = await File.ReadAllBytesAsync(manifestFile, cancellationToken);
		string digest = Convert.ToHexString(SHA256.HashData(bytes));
		OfflineNormalizationManifest manifest = OfflineNormalizationManifestPolicy.Read(new UTF8Encoding(false, true).GetString(bytes), true, DateTimeOffset.UtcNow);
		OfflineBackupInspection baseline = await inspection.InspectAsync(source, cancellationToken);
		inspection.ValidateManifest(manifest, baseline, true, DateTimeOffset.UtcNow);
		await ValidateFileAsync(source, baseline.FormatVersion, cancellationToken);
		await ValidateSourceAsync(source, baseline.FormatVersion, cancellationToken);
		if (File.Exists(output)) { return await VerifyCoreAsync(source, output, bytes, digest, manifest, baseline, cancellationToken); }
		string sidecar = output + ".manifest.json", lineageFile = output + ".lineage.json";
		if (new[] { "-wal", "-shm", "-journal" }.Any(suffix => File.Exists(output + suffix)) || Directory.Exists(output) || File.Exists(sidecar) || Directory.Exists(sidecar) || File.Exists(lineageFile) || Directory.Exists(lineageFile))
		{ throw new InvalidDataException("Output artifacts already exist."); }
		string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
		bool wroteManifest = false, wroteLineage = false;
		try
		{
			await using (FileStream copy = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				sourceLease.Position = 0;
				await sourceLease.CopyToAsync(copy, cancellationToken);
				await copy.FlushAsync(cancellationToken);
			}
			await using (SqliteConnection sqlite = Open(temporary, false))
			{
				await sqlite.OpenAsync(cancellationToken);
				await TransformAsync(sqlite, manifest, baseline, digest, cancellationToken);
			}
			await ValidateFileAsync(temporary, OutputVersion(baseline.FormatVersion), cancellationToken);
			OfflineBackupInspection candidate = await inspection.InspectAsync(temporary, cancellationToken);
			inspection.VerifyBaseline(baseline, candidate);
			sourceLease.Position = 0;
			if (Convert.ToHexString(await SHA256.HashDataAsync(sourceLease, cancellationToken)) != baseline.SourceSha256)
			{ throw new InvalidDataException("Source changed."); }
			await WriteNewAsync(sidecar, bytes, cancellationToken); wroteManifest = true;
			await WriteNewAsync(lineageFile, JsonSerializer.SerializeToUtf8Bytes(new Lineage(baseline.SourceSha256, digest, manifest.ToolRevision, candidate.SourceSha256)), cancellationToken); wroteLineage = true;
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporary, output, false);
			return new(output, candidate.SourceSha256, baseline.SourceSha256, digest, false, candidate.ReviewStateCounts);
		}
		catch
		{
			if (wroteManifest) { File.Delete(sidecar); }
			if (wroteLineage) { File.Delete(lineageFile); }
			throw;
		}
		finally
		{
			foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" }) { File.Delete(temporary + suffix); }
		}
	}

	public async Task<OfflineBackupNormalizationResult> VerifyAsync(string sourcePath, string manifestPath, string outputPath, CancellationToken cancellationToken = default)
	{
		string source = Resolve(sourcePath), manifestFile = Resolve(manifestPath), output = Resolve(outputPath);
		EnsurePaths(source, manifestFile, output);
		await using FileStream sourceLease = new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
		byte[] bytes = await File.ReadAllBytesAsync(manifestFile, cancellationToken);
		string digest = Convert.ToHexString(SHA256.HashData(bytes));
		OfflineNormalizationManifest manifest = OfflineNormalizationManifestPolicy.Read(new UTF8Encoding(false, true).GetString(bytes), true, DateTimeOffset.UtcNow);
		OfflineBackupInspection baseline = await inspection.InspectAsync(source, cancellationToken);
		inspection.ValidateManifest(manifest, baseline, true, DateTimeOffset.UtcNow);
		await ValidateFileAsync(source, baseline.FormatVersion, cancellationToken);
		await ValidateSourceAsync(source, baseline.FormatVersion, cancellationToken);
		return await VerifyCoreAsync(source, output, bytes, digest, manifest, baseline, cancellationToken);
	}

	private async Task<OfflineBackupNormalizationResult> VerifyCoreAsync(string source, string output, byte[] bytes, string digest,
		OfflineNormalizationManifest manifest, OfflineBackupInspection baseline, CancellationToken ct)
	{
		if (!(await File.ReadAllBytesAsync(output + ".manifest.json", ct)).SequenceEqual(bytes)) { throw new InvalidDataException("Manifest sidecar mismatch."); }
		Lineage lineage = JsonSerializer.Deserialize<Lineage>(await File.ReadAllBytesAsync(output + ".lineage.json", ct), OfflineNormalizationManifestPolicy.JsonOptions)
			?? throw new InvalidDataException("Missing output lineage.");
		OfflineBackupInspection candidate = await inspection.InspectAsync(output, ct);
		if (lineage != new Lineage(baseline.SourceSha256, digest, manifest.ToolRevision, candidate.SourceSha256) || candidate.FormatVersion != OutputVersion(baseline.FormatVersion))
		{ throw new InvalidDataException("Output lineage or checksum mismatch."); }
		inspection.VerifyBaseline(baseline, candidate);
		await ValidateFileAsync(output, OutputVersion(baseline.FormatVersion), ct);
		await using SqliteConnection sqlite = Open(output, true);
		await sqlite.OpenAsync(ct);
		foreach ((string key, string expected) in new[] { ("offline_source_sha256", baseline.SourceSha256), ("offline_manifest_sha256", digest), ("offline_tool_revision", manifest.ToolRevision) })
		{
			await using SqliteCommand command = sqlite.CreateCommand();
			command.CommandText = "SELECT value FROM backup_metadata WHERE key=$key";
			command.Parameters.AddWithValue("$key", key);
			if (Convert.ToString(await command.ExecuteScalarAsync(ct)) != expected) { throw new InvalidDataException("Output metadata mismatch."); }
		}
		// Reuse the apply transformation as the sole authority, beginning with the exact original A.
		await using SqliteConnection original = Open(source, true);
		await original.OpenAsync(ct);
		await using SqliteConnection replay = new("Data Source=:memory:");
		await replay.OpenAsync(ct);
		original.BackupDatabase(replay);
		await TransformAsync(replay, manifest, baseline, digest, ct);
		if (await NormalizationStateAsync(replay, ct) != await NormalizationStateAsync(sqlite, ct))
		{ throw new InvalidDataException("Output normalization differs from the reviewed manifest."); }
		return new(output, candidate.SourceSha256, baseline.SourceSha256, digest, true, candidate.ReviewStateCounts);
	}

	private static async Task<string> NormalizationStateAsync(SqliteConnection sqlite, CancellationToken ct)
	{
		List<string> tables = [];
		foreach (string query in new[] {
			"SELECT * FROM normalized_descriptions",
			"SELECT * FROM normalization_catalog",
			"SELECT id,normalized_description_id,normalized_description_match_score FROM receipt_items",
			"SELECT id,normalized_description_id FROM item_templates" })
		{
			await using SqliteCommand command = sqlite.CreateCommand(); command.CommandText = query;
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
			List<string> rows = [];
			while (await reader.ReadAsync(ct))
			{
				List<object?> cells = [];
				for (int i = 0; i < reader.FieldCount; i++)
				{
					object value = reader.GetValue(i);
					cells.Add(value is DBNull ? null : new { Type = value.GetType().FullName, Value = value });
				}
				rows.Add(JsonSerializer.Serialize(cells));
			}
			rows.Sort(StringComparer.Ordinal); tables.Add(JsonSerializer.Serialize(rows));
		}
		return JsonSerializer.Serialize(tables);
	}

	private static async Task TransformAsync(SqliteConnection sqlite, OfflineNormalizationManifest manifest, OfflineBackupInspection baseline, string digest, CancellationToken ct)
	{
		string actor = OfflineNormalizationManifestPolicy.Actor(manifest, digest);
		DateTimeOffset reviewedAt = manifest.Review!.ReviewedAt;
		Guid Id(string purpose) => new(SHA256.HashData(Encoding.UTF8.GetBytes(digest + ":" + purpose))[..16]);
		await using SqliteTransaction transaction = sqlite.BeginTransaction();
		async Task Execute(string sql, params (string Name, object? Value)[] values)
		{
			await using SqliteCommand command = sqlite.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = sql;
			foreach ((string name, object? value) in values) { command.Parameters.AddWithValue(name, value ?? DBNull.Value); }
			await command.ExecuteNonQueryAsync(ct);
		}
		Task Write<T>(string kind, Guid id, T value) => Execute("INSERT INTO normalization_catalog(kind,id,payload) VALUES($kind,$id,$payload) ON CONFLICT(kind,id) DO UPDATE SET payload=excluded.payload",
			("$kind", kind), ("$id", id.ToString()), ("$payload", JsonSerializer.Serialize(value)));
		Dictionary<Guid, PortableCatalogArchive.Assignment> assignments = [];
		Dictionary<Guid, PortableCatalogArchive.ReviewEvidence> reviews = [];
		Dictionary<Guid, NormalizationSourceMappingEntity> mappings = [];
		if (baseline.FormatVersion == 6)
		{
			await Execute("CREATE TABLE normalization_catalog(kind TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(kind,id))");
			foreach (OfflineSourceObservation item in baseline.Observations.Where(o => o.EntityKind == "item"))
			{
				assignments.Add(item.EntityId, new(item.EntityId, item.LinkedCanonicalId.HasValue ? "legacy_unknown" : "unassigned", null, Id("legacy-assignment:" + item.EntityId)));
				reviews.Add(item.EntityId, new(item.EntityId, NormalizationReviewState.NeedsReview, null, null, null, null, null, null, null, null));
			}
		}
		else
		{
			await using SqliteCommand command = sqlite.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "SELECT kind,id,payload FROM normalization_catalog WHERE kind IN ('assignment','review_evidence','mapping')";
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				Guid id = Guid.Parse(reader.GetString(1)); string payload = reader.GetString(2);
				switch (reader.GetString(0))
				{
					case "assignment": assignments.Add(id, PortableCatalogArchive.Read<PortableCatalogArchive.Assignment>(payload)); break;
					case "review_evidence": reviews.Add(id, PortableCatalogArchive.Read<PortableCatalogArchive.ReviewEvidence>(payload)); break;
					case "mapping": mappings.Add(id, PortableCatalogArchive.Read<NormalizationSourceMappingEntity>(payload)); break;
				}
			}
		}
		foreach (OfflineCanonicalDecision decision in manifest.CanonicalDecisions)
		{
			await Execute("INSERT INTO normalized_descriptions(id,canonical_name,status,created_at,display_label) VALUES($id,$name,'Active',$at,$label) ON CONFLICT(id) DO UPDATE SET canonical_name=excluded.canonical_name,display_label=excluded.display_label,status='Active'",
				("$id", decision.CanonicalId.ToString()), ("$name", decision.CanonicalName), ("$label", decision.DisplayLabel), ("$at", reviewedAt.ToString("O")));
		}
		foreach (OfflineConsolidationDecision decision in manifest.Consolidations)
		{
			OfflineReviewedDecision reviewed = manifest.Review.Decisions.Single(value => value.DecisionId == decision.DecisionId);
			await Write("consolidation", decision.OldCanonicalId, new PortableCatalogArchive.Consolidation(
				decision.OldCanonicalId, decision.SurvivorCanonicalId, decision.DecisionId, actor, reviewedAt, reviewed.Rationale, digest, baseline.SourceSha256, manifest.ToolRevision));
			foreach (NormalizationSourceMappingEntity mapping in mappings.Values.Where(m => m.TargetId == decision.OldCanonicalId))
			{
				mapping.TargetId = decision.SurvivorCanonicalId; mapping.Revision = Id("consolidation-mapping:" + decision.DecisionId + ":" + mapping.Id);
				mapping.ApprovedBy = actor; mapping.ApprovedAt = reviewedAt;
			}
			foreach (OfflineSourceObservation item in baseline.Observations.Where(o => o.EntityKind == "item" && o.LinkedCanonicalId == decision.OldCanonicalId))
			{
				PortableCatalogArchive.Assignment assignment = assignments[item.EntityId];
				PortableCatalogArchive.ReviewEvidence evidence = reviews[item.EntityId];
				Guid assignmentId = Id("consolidation-assignment:" + decision.DecisionId + ":" + item.EntityId);
				assignments[item.EntityId] = assignment with { AssignmentId = assignmentId };
				if (evidence.State == NormalizationReviewState.NeedsReview && evidence.Reason == "unavailable")
				{
					assignments[item.EntityId] = new(item.EntityId, "legacy_unknown", null, assignmentId);
					reviews[item.EntityId] = new(item.EntityId, NormalizationReviewState.NeedsReview, null, null, "legacy_unknown", null, null, null, null, null);
				}
				if (evidence.State != NormalizationReviewState.NeedsReview)
				{
					reviews[item.EntityId] = evidence with
					{
						TargetId = decision.SurvivorCanonicalId,
						DecisionId = assignmentId,
						ReviewedBy = actor,
						ReviewedAt = reviewedAt,
						MappingRevision = assignment.MappingId is Guid mappingId ? mappings[mappingId].Revision : null
					};
				}
			}
			await Execute("UPDATE receipt_items SET normalized_description_id=$survivor,normalized_description_match_score=NULL WHERE normalized_description_id=$old", ("$survivor", decision.SurvivorCanonicalId.ToString()), ("$old", decision.OldCanonicalId.ToString()));
			await Execute("UPDATE item_templates SET normalized_description_id=$survivor WHERE normalized_description_id=$old", ("$survivor", decision.SurvivorCanonicalId.ToString()), ("$old", decision.OldCanonicalId.ToString()));
			await Execute("UPDATE normalized_descriptions SET nearest_neighbour_id=NULL,nearest_neighbour_similarity=NULL WHERE nearest_neighbour_id=$old", ("$old", decision.OldCanonicalId.ToString()));
			await Execute("DELETE FROM normalized_descriptions WHERE id=$old", ("$old", decision.OldCanonicalId.ToString()));
		}
		foreach (IGrouping<string, OfflineAssignmentDecision> group in manifest.Assignments.Where(d => d.RememberSource).GroupBy(d => d.SourceHash))
		{
			OfflineAssignmentDecision decision = group.First();
			foreach (NormalizationSourceMappingEntity previous in mappings.Values.Where(m => m.SourceHash == group.Key && m.State == "active")) { previous.State = "revoked"; }
			Guid mappingId = Id("mapping:" + group.Key);
			mappings.Add(mappingId, new()
			{
				Id = mappingId,
				Revision = Id("mapping-revision:" + group.Key),
				SourceHash = group.Key,
				Location = decision.Source.Location,
				Description = decision.Source.Description,
				ItemCode = decision.Source.ItemCode,
				TargetId = decision.CanonicalId,
				Exclude = decision.Exclude,
				ExclusionReason = decision.ExclusionReason,
				ApprovedBy = actor,
				ApprovedAt = reviewedAt
			});
		}
		foreach (OfflineAssignmentDecision decision in manifest.Assignments)
		{
			string table = decision.EntityKind == "item" ? "receipt_items" : "item_templates";
			await Execute($"UPDATE {table} SET normalized_description_id=$target" + (decision.EntityKind == "item" ? ",normalized_description_match_score=NULL" : "") + " WHERE id=$id",
				("$target", decision.CanonicalId?.ToString()), ("$id", decision.EntityId.ToString()));
			if (decision.EntityKind != "item") { continue; }
			Guid assignmentId = Id("assignment:" + decision.DecisionId);
			Guid? mappingId = decision.RememberSource ? Id("mapping:" + decision.SourceHash) : null;
			assignments[decision.EntityId] = new(decision.EntityId, decision.RememberSource ? (decision.Exclude ? "excluded" : "mapped") : (decision.Exclude ? "purchase_excluded" : "reviewed"), mappingId, assignmentId);
			reviews[decision.EntityId] = new(decision.EntityId, decision.Exclude ? NormalizationReviewState.Excluded : NormalizationReviewState.Verified, decision.SourceHash, decision.CanonicalId,
				decision.RememberSource ? "approved_mapping" : (decision.Exclude ? "purchase_exclusion" : "purchase_review"), decision.ExclusionReason, assignmentId, actor, reviewedAt,
				mappingId is Guid mapped ? mappings[mapped].Revision : null);
		}
		foreach (NormalizationSourceMappingEntity mapping in mappings.Values) { await Write("mapping", mapping.Id, mapping); }
		foreach (PortableCatalogArchive.Assignment assignment in assignments.Values) { await Write("assignment", assignment.Id, assignment); }
		foreach (PortableCatalogArchive.ReviewEvidence review in reviews.Values) { await Write("review_evidence", review.Id, review); }
		foreach ((string key, string value) in new[] { ("export_version", OutputVersion(baseline.FormatVersion).ToString(System.Globalization.CultureInfo.InvariantCulture)), ("offline_source_sha256", baseline.SourceSha256), ("offline_manifest_sha256", digest), ("offline_tool_revision", manifest.ToolRevision) })
		{
			await Execute("INSERT INTO backup_metadata(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", value));
		}
		await transaction.CommitAsync(ct);
		await PortableCatalogArchive.ValidateNamesAsync(sqlite, ct);
	}

	private static async Task ValidateSourceAsync(string path, int version, CancellationToken ct)
	{
		await using SqliteConnection sqlite = Open(path, true);
		await sqlite.OpenAsync(ct);
		await using SqliteCommand command = sqlite.CreateCommand();
		command.CommandText = "SELECT EXISTS(SELECT 1 FROM backup_metadata WHERE key GLOB 'offline_*')";
		bool offlineOutput = Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0;
		if (version >= 8)
		{
			command.CommandText = "SELECT EXISTS(SELECT 1 FROM normalization_catalog WHERE kind='consolidation')";
			offlineOutput |= Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0;
		}
		if (offlineOutput)
		{
			throw new InvalidDataException("A prior offline output cannot be used as a new source. Revise the reviewed manifest against the preserved original source A and use a new output path.");
		}
	}

	private static int OutputVersion(int sourceVersion) => sourceVersion == 10 ? 10 : 9;

	private static async Task ValidateFileAsync(string path, int version, CancellationToken ct)
	{
		await using SqliteConnection sqlite = Open(path, true);
		await sqlite.OpenAsync(ct);
		await using SqliteConnection expected = new("Data Source=:memory:");
		await expected.OpenAsync(ct);
		await BackupService.CreateSchemaAsync(expected, ct, version);
		if (version >= 8)
		{
			await using SqliteCommand catalogSchema = expected.CreateCommand();
			catalogSchema.CommandText = "CREATE TABLE normalization_catalog(kind TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(kind,id))";
			await catalogSchema.ExecuteNonQueryAsync(ct);
		}
		if (version == 10)
		{
			await using SqliteCommand taxonomySchema = expected.CreateCommand();
			taxonomySchema.CommandText = "CREATE TABLE taxonomy_archive(kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(kind,id)); CREATE TABLE portable_deletion_state(kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(kind,id));";
			await taxonomySchema.ExecuteNonQueryAsync(ct);
			var taxonomy = await PortableTaxonomyArchive.ReadAsync(sqlite, ct);
			await PortableTaxonomyArchive.ReadDeletionStateAsync(sqlite, taxonomy, ct);
		}
		await using (SqliteCommand objects = sqlite.CreateCommand())
		{
			objects.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('trigger','view')";
			if (Convert.ToInt64(await objects.ExecuteScalarAsync(ct)) != 0) { throw new InvalidDataException("Unsupported schema objects."); }
		}
		// Compare the versioned schema, including constraints, against the application exporter.
		async Task<Dictionary<string, string>> Schema(SqliteConnection connection)
		{
			Dictionary<string, string> result = [];
			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				string table = reader.GetString(0);
				result.Add(table, string.Concat(reader.GetString(1).Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant());
			}
			return result;
		}
		Dictionary<string, string> actualSchema = await Schema(sqlite), expectedSchema = await Schema(expected);
		if (!actualSchema.OrderBy(p => p.Key).SequenceEqual(expectedSchema.OrderBy(p => p.Key))) { throw new InvalidDataException("Unsupported portable schema."); }
		foreach ((string table, string column) in new[] { ("receipt_items", "unit_price_currency"), ("receipt_items", "total_amount_currency"), ("receipts", "tax_amount_currency"), ("transactions", "amount_currency"), ("adjustments", "amount_currency"), ("item_templates", "default_unit_price_currency") })
		{
			await using SqliteCommand command = sqlite.CreateCommand();
			command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} IS NOT NULL AND {column}!='USD'";
			if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0) { throw new InvalidDataException("Unsupported currency."); }
		}
		if (version >= 8) { await PortableCatalogArchive.ValidateArchiveAsync(sqlite, ct); }
	}

	private static SqliteConnection Open(string path, bool readOnly) => new(new SqliteConnectionStringBuilder { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true }.ToString());
	private static string Resolve(string path)
	{
		string full = Path.GetFullPath(path);
		return File.Exists(full) ? new FileInfo(full).ResolveLinkTarget(true)?.FullName ?? full : full;
	}
	private static void EnsurePaths(string source, string manifest, string output)
	{
		if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase) || string.Equals(manifest, output, StringComparison.OrdinalIgnoreCase)
			|| new[] { "-wal", "-shm", "-journal", ".manifest.json", ".lineage.json" }.Any(s => string.Equals(source + s, output, StringComparison.OrdinalIgnoreCase))
			|| string.Equals(source, output + ".manifest.json", StringComparison.OrdinalIgnoreCase) || string.Equals(manifest, output + ".manifest.json", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(source, output + ".lineage.json", StringComparison.OrdinalIgnoreCase) || string.Equals(manifest, output + ".lineage.json", StringComparison.OrdinalIgnoreCase))
		{ throw new InvalidDataException("Source, manifest and output must be separate."); }
	}
	private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken ct)
	{
		bool created = false;
		try
		{
			await using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
			created = true;
			await file.WriteAsync(bytes, ct);
			await file.FlushAsync(ct);
		}
		catch { if (created) { File.Delete(path); } throw; }
	}
}
