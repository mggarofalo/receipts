using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	// Candidate only. The eventual caller must hold the same source snapshot as the ordinary
	// archive export and validate retained history. The inactive v14 candidate exporter composes
	// these with ordinary history; no current-format path calls them.
	internal static async Task ExportEffectsCandidateAsync(ApplicationDbContext db, SqliteConnection sqlite,
		State retained, CancellationToken ct, SqliteTransaction? enclosingTransaction = null)
	{
		var manifests = await db.OrganizationEffectManifests.AsNoTracking().ToListAsync(ct);
		var items = await db.OrganizationEffectItems.AsNoTracking().ToListAsync(ct);
		var outcomes = await db.OrganizationEffectOutcomes.AsNoTracking().ToListAsync(ct);
		var members = items.ToLookup(i => i.ManifestId);
		EffectEvidence evidence = new(manifests.Select(m => SealEffectManifest(m, members[m.Id])).ToList(), items, outcomes);
		ValidateEffectEvidence(retained, evidence);
		var records = WriteEffectEvidence(evidence);
		// Apply the reader's envelope limits to exports too, so every candidate export can be read.
		ReadEffectEvidence(records);
		using var ownedTransaction = enclosingTransaction is null ? sqlite.BeginTransaction() : null;
		var transaction = enclosingTransaction ?? ownedTransaction!;
		await using var command = sqlite.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "CREATE TABLE organization_effect_archive (kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(kind,id))";
		await command.ExecuteNonQueryAsync(ct);
		foreach (var row in records)
		{
			command.CommandText = "INSERT INTO organization_effect_archive(kind,id,payload) VALUES($kind,$id,$payload)";
			command.Parameters.Clear();
			command.Parameters.AddWithValue("$kind", row.Kind);
			command.Parameters.AddWithValue("$id", row.Id);
			command.Parameters.AddWithValue("$payload", row.Payload);
			await command.ExecuteNonQueryAsync(ct);
		}
		if (ownedTransaction is not null) { await ownedTransaction.CommitAsync(ct); }
	}

	internal static async Task<EffectEvidence> ReadEffectsCandidateAsync(SqliteConnection sqlite, State retained, CancellationToken ct)
	{
		List<EffectRecord> records = [];
		await using var command = sqlite.CreateCommand();
		// Check SQLite byte lengths before materializing potentially oversized payload strings.
		command.CommandText = "SELECT kind,id,payload,length(CAST(payload AS BLOB)),length(CAST(kind AS BLOB)),length(CAST(id AS BLOB)) FROM organization_effect_archive ORDER BY kind,id";
		await using var reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct))
		{
			Require(!reader.IsDBNull(0) && !reader.IsDBNull(1) && !reader.IsDBNull(2)
				&& reader.GetInt64(3) <= MaximumEffectPayloadBytes && reader.GetInt64(4) <= 8 && reader.GetInt64(5) <= 256,
				"Invalid or oversized effect archive row.");
			records.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
		}
		var evidence = ReadEffectEvidence(records);
		ValidateEffectEvidence(retained, evidence);
		return evidence;
	}
}
