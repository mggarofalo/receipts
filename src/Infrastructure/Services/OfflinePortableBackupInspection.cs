using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Normalization;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Services;

/// <summary>Inspects a closed portable SQLite artifact without importing or changing it.</summary>
public sealed class OfflinePortableBackupInspection : IOfflinePortableBackupInspection
{
	public async Task<OfflineBackupInspection> InspectAsync(string sourcePath, CancellationToken cancellationToken = default)
	{
		string path = Path.GetFullPath(sourcePath);
		foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
		{
			if (File.Exists(path + suffix)) { throw new InvalidDataException("Inspection requires a closed standalone backup without SQLite sidecars."); }
		}
		// Deny concurrent writers for the complete hash/read/hash interval, including SQLite reads.
		await using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		string originalHash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken));
		SqliteConnectionStringBuilder connectionString = new() { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, ForeignKeys = true };
		await using SqliteConnection sqlite = new(connectionString.ToString());
		await sqlite.OpenAsync(cancellationToken);
		await using (SqliteCommand queryOnly = sqlite.CreateCommand())
		{
			queryOnly.CommandText = "PRAGMA query_only=ON";
			await queryOnly.ExecuteNonQueryAsync(cancellationToken);
		}
		await using SqliteTransaction snapshot = sqlite.BeginTransaction(deferred: true);
		await CheckAsync(sqlite, "PRAGMA integrity_check", true, cancellationToken);
		await CheckAsync(sqlite, "PRAGMA foreign_key_check", false, cancellationToken);
		List<string> tables = [];
		await using (SqliteCommand command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name COLLATE BINARY";
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			while (await reader.ReadAsync(cancellationToken)) { tables.Add(reader.GetString(0)); }
		}
		if (!tables.Contains("backup_metadata") || PortableBackupFormat.RequiredTables.Except(tables).Any())
		{ throw new InvalidDataException("The portable backup is missing required tables."); }
		int version;
		await using (SqliteCommand command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT value FROM backup_metadata WHERE key='export_version'";
			if (!int.TryParse(Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture), out version) || version is not (6 or 8 or 9 or 10 or 11 or 12))
			{ throw new InvalidDataException("Inspection supports portable backup formats 6, 8, 9, 10, 11 and 12."); }
		}
		if (version >= 8 && !tables.Contains("normalization_catalog")) { throw new InvalidDataException("Format 8 requires normalization evidence."); }
		if (version >= 10)
		{
			var taxonomy = await PortableTaxonomyArchive.ReadAsync(sqlite, cancellationToken, version);
			await PortableTaxonomyArchive.ReadDeletionStateAsync(sqlite, taxonomy, cancellationToken);
		}
		SortedDictionary<string, string> digests = new(StringComparer.Ordinal);
		SortedDictionary<string, long> counts = new(StringComparer.Ordinal);
		foreach (string table in tables)
		{
			List<string> columns = [];
			await using (SqliteCommand schema = sqlite.CreateCommand())
			{
				schema.CommandText = $"PRAGMA table_info({Quote(table)})";
				await using SqliteDataReader reader = await schema.ExecuteReaderAsync(cancellationToken);
				while (await reader.ReadAsync(cancellationToken)) { columns.Add(reader.GetString(1)); }
			}
			columns.Sort(StringComparer.Ordinal);
			List<string> facts = table switch
			{
				"receipt_items" => columns.Where(c => c is not ("normalized_description_id" or "normalized_description_match_score")).ToList(),
				"item_templates" => columns.Where(c => c != "normalized_description_id").ToList(),
				_ => columns
			};
			List<string> rows = [];
			await using (SqliteCommand command = sqlite.CreateCommand())
			{
				command.CommandText = $"SELECT {string.Join(',', facts.Select(Quote))} FROM {Quote(table)}";
				await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
				while (await reader.ReadAsync(cancellationToken))
				{
					object?[] values = new object?[reader.FieldCount];
					for (int i = 0; i < values.Length; i++) { values[i] = EncodeCell(reader.GetValue(i)); }
					rows.Add(JsonSerializer.Serialize(values));
				}
			}
			counts.Add(table, rows.Count);
			if (table is "normalized_descriptions" or "normalization_catalog" or "backup_metadata") { continue; }
			rows.Sort(StringComparer.Ordinal);
			// Explicit SQLite storage classes prevent JSON text/blob and integer/real collisions.
			// Sorted rows remove rowid dependence without losing duplicate rows.
			digests.Add(table, Hash(JsonSerializer.Serialize(new { Columns = facts, Rows = rows })));
		}
		Dictionary<Guid, string> states = [];
		if (version >= 8)
		{
			await using SqliteCommand command = sqlite.CreateCommand();
			command.CommandText = "SELECT id,payload FROM normalization_catalog WHERE kind='review_evidence'";
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			while (await reader.ReadAsync(cancellationToken))
			{
				using JsonDocument document = JsonDocument.Parse(reader.GetString(1));
				JsonElement state = document.RootElement.GetProperty("State");
				string name = state.ValueKind == JsonValueKind.Number ? state.GetInt32() switch { 0 => "NeedsReview", 1 => "Verified", 2 => "Excluded", _ => throw new InvalidDataException("Unknown review state.") } : state.GetString()!;
				if (name is not ("NeedsReview" or "Verified" or "Excluded")) { throw new InvalidDataException("Unknown review state."); }
				states.Add(Guid.Parse(reader.GetString(0)), name);
			}
		}
		List<OfflineSourceObservation> observations = [];
		await using (SqliteCommand command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT i.id,r.location,i.description,i.receipt_item_code,i.normalized_description_id FROM receipt_items i JOIN receipts r ON r.id=i.receipt_id ORDER BY i.id";
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			while (await reader.ReadAsync(cancellationToken))
			{
				Guid id = Guid.Parse(reader.GetString(0));
				NormalizationSourceKey key = NormalizationCatalogPolicy.Normalize(new(reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
				observations.Add(new("item", id, key, NormalizationCatalogPolicy.Hash(key), reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)), states.GetValueOrDefault(id, "NeedsReview")));
			}
		}
		if (observations.Count != counts["receipt_items"] || states.Keys.Except(observations.Select(o => o.EntityId)).Any())
		{ throw new InvalidDataException("Backup contains dangling purchase relationships or review evidence."); }
		await using (SqliteCommand command = sqlite.CreateCommand())
		{
			// Templates have no store scope; the empty location is explicit and never a remembered purchase rule.
			command.CommandText = "SELECT id,name,default_item_code,normalized_description_id FROM item_templates ORDER BY id";
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			while (await reader.ReadAsync(cancellationToken))
			{
				NormalizationSourceKey key = NormalizationCatalogPolicy.Normalize(new("", reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
				observations.Add(new("template", Guid.Parse(reader.GetString(0)), key, NormalizationCatalogPolicy.Hash(key), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), "UnverifiedTemplate"));
			}
		}
		List<OfflineCanonicalObservation> targets = [];
		await using (SqliteCommand command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT id,canonical_name,display_label,status FROM normalized_descriptions ORDER BY id";
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			while (await reader.ReadAsync(cancellationToken)) { targets.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3))); }
		}
		List<OfflineCurrencyTotal> totals = [];
		foreach ((string table, string amount, string currency) in new[] { ("receipt_items", "total_amount", "total_amount_currency"), ("receipts", "tax_amount", "tax_amount_currency"), ("transactions", "amount", "amount_currency"), ("adjustments", "amount", "amount_currency") })
		{
			SortedDictionary<string, decimal> sums = new(StringComparer.Ordinal);
			await using SqliteCommand command = sqlite.CreateCommand();
			command.CommandText = $"SELECT {Quote(amount)},{Quote(currency)} FROM {Quote(table)}";
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			while (await reader.ReadAsync(cancellationToken))
			{
				string code = reader.GetString(1);
				decimal value = decimal.Parse(reader.GetString(0), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
				sums[code] = checked(sums.GetValueOrDefault(code) + value);
			}
			totals.AddRange(sums.Select(s => new OfflineCurrencyTotal(table, s.Key, s.Value)));
		}
		source.Position = 0;
		if (originalHash != Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken))) { throw new InvalidDataException("Source backup changed during inspection."); }
		Dictionary<string, long> stateCounts = observations.Where(o => o.EntityKind == "item").GroupBy(o => o.ReviewState).ToDictionary(g => g.Key, g => g.LongCount());
		int scopedSourceCount = observations.Where(o => o.EntityKind == "item").Select(o => o.Source).Distinct().Count();
		return new(1, originalHash, version, Hash(JsonSerializer.Serialize(digests)), digests, counts, totals, stateCounts, scopedSourceCount, observations, targets);
	}

	public void VerifyBaseline(OfflineBackupInspection baseline, OfflineBackupInspection candidate)
	{
		if (baseline.InspectionVersion != 1 || candidate.InspectionVersion != 1 || baseline.FactualBaselineSha256 != candidate.FactualBaselineSha256
			|| !baseline.FactualTableSha256.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(candidate.FactualTableSha256.OrderBy(p => p.Key, StringComparer.Ordinal))
			|| !baseline.CurrencyTotals.SequenceEqual(candidate.CurrencyTotals))
		{ throw new InvalidDataException("Factual, financial or unrelated relationship baseline mismatch."); }
	}

	public void ValidateManifest(OfflineNormalizationManifest manifest, OfflineBackupInspection source, bool requireReviewed, DateTimeOffset now)
	{
		OfflineNormalizationManifestPolicy.Validate(manifest, requireReviewed, now);
		if (manifest.SourceSha256 != source.SourceSha256 || manifest.SourceFormatVersion != source.FormatVersion || manifest.FactualBaselineSha256 != source.FactualBaselineSha256)
		{ throw new InvalidDataException("Manifest does not describe this exact source and baseline."); }
		Dictionary<(string, Guid), OfflineSourceObservation> observations = source.Observations.ToDictionary(o => (o.EntityKind, o.EntityId));
		HashSet<Guid> existing = source.CanonicalTargets.Select(t => t.Id).ToHashSet();
		HashSet<Guid> approved = manifest.CanonicalDecisions.Select(d => d.CanonicalId).ToHashSet();
		foreach (OfflineAssignmentDecision decision in manifest.Assignments)
		{
			if (!observations.TryGetValue((decision.EntityKind, decision.EntityId), out OfflineSourceObservation? observation) || observation.Source != decision.Source || observation.SourceHash != decision.SourceHash
				|| decision.CanonicalId is Guid target && !approved.Contains(target))
			{ throw new InvalidDataException("Assignment must match an existing observation and explicitly approved canonical name."); }
		}
		foreach (OfflineUnresolvedDecision decision in manifest.Unresolved)
		{
			if (!observations.ContainsKey((decision.EntityKind, decision.EntityId))) { throw new InvalidDataException("Unresolved observation is absent from the source."); }
		}
		foreach (OfflineConsolidationDecision decision in manifest.Consolidations)
		{
			if (!existing.Contains(decision.OldCanonicalId) || !approved.Contains(decision.SurvivorCanonicalId)) { throw new InvalidDataException("Consolidations require existing old IDs and approved survivor names."); }
		}
		// A remembered scope cannot silently collapse heterogeneous reviewed choices.
		foreach (IGrouping<string, OfflineAssignmentDecision> group in manifest.Assignments.Where(d => d.EntityKind == "item").GroupBy(d => d.SourceHash))
		{
			if (group.Any(d => d.RememberSource) && (group.Any(d => !d.RememberSource) || group.Select(d => (d.CanonicalId, d.Exclude, d.ExclusionReason)).Distinct().Count() != 1
				|| source.Observations.Any(o => o.EntityKind == "item" && o.SourceHash == group.Key && !group.Any(d => d.EntityId == o.EntityId))))
			{ throw new InvalidDataException("Remembered mappings require explicit consistent review of the entire scoped source."); }
		}
	}

	private static async Task CheckAsync(SqliteConnection sqlite, string sql, bool expectOk, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = sqlite.CreateCommand();
		command.CommandText = sql;
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		bool row = await reader.ReadAsync(cancellationToken);
		if (expectOk ? !row || reader.GetString(0) != "ok" || await reader.ReadAsync(cancellationToken) : row)
		{ throw new InvalidDataException("SQLite integrity or foreign-key validation failed."); }
	}
	private static object?[] EncodeCell(object value) => value switch
	{
		DBNull => ["null", null],
		long integer => ["integer", integer.ToString(CultureInfo.InvariantCulture)],
		double real => ["real", BitConverter.DoubleToInt64Bits(real).ToString("X16", CultureInfo.InvariantCulture)],
		string text => ["text", text],
		byte[] blob => ["blob", Convert.ToHexString(blob)],
		_ => throw new InvalidDataException("Unsupported SQLite storage type.")
	};
	private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
	private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
