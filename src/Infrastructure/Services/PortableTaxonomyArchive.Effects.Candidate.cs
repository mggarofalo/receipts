using Microsoft.Data.Sqlite;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	// Testable staging format only. Public import/inspection still reject this version.
	internal const int EffectsCandidateVersion = 14;
	internal sealed record EffectsCandidateArchive(State History, EffectEvidence Effects);

	internal static async Task<EffectsCandidateArchive> ReadV14CandidateAsync(SqliteConnection sqlite, CancellationToken ct)
	{
		await using (var version = sqlite.CreateCommand())
		{
			version.CommandText = "SELECT value FROM backup_metadata WHERE key='export_version'";
			await using var rows = await version.ExecuteReaderAsync(ct);
			Require(await rows.ReadAsync(ct) && rows.GetString(0) == "14", "Expected candidate archive version 14.");
			Require(!await rows.ReadAsync(ct), "Duplicate candidate archive version.");
		}
		HashSet<string> tables = [];
		await using (var schema = sqlite.CreateCommand())
		{
			schema.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
			await using var rows = await schema.ExecuteReaderAsync(ct);
			while (await rows.ReadAsync(ct)) { tables.Add(rows.GetString(0)); }
		}
		Require(PortableBackupFormat.RequiredTablesFor(13).Append("organization_effect_archive").All(tables.Contains),
			"Missing required candidate archive table.");
		await using (var schema = sqlite.CreateCommand())
		{
			schema.CommandText = "PRAGMA table_info(organization_effect_archive)";
			await using var rows = await schema.ExecuteReaderAsync(ct);
			List<string> columns = [];
			while (await rows.ReadAsync(ct))
			{
				Require(rows.GetString(2).Equals("TEXT", StringComparison.OrdinalIgnoreCase), "Invalid candidate effect column type.");
				columns.Add(rows.GetString(1));
			}
			Require(columns.SequenceEqual(new[] { "kind", "id", "payload" }), "Invalid candidate effect columns.");
		}
		// Validate ordinary history first; effect validation is never a substitute for it.
		var history = await ReadAsync(sqlite, ct, 13);
		var effects = await ReadEffectsCandidateAsync(sqlite, history, ct);
		return new(history, effects);
	}
}
