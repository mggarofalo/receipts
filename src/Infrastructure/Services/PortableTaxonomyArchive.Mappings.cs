using System.Security.Cryptography;
using System.Text.Json;
using Application.Normalization;
using Application.Taxonomy;
using Domain.Taxonomy;
using Infrastructure.Entities.Taxonomy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	private static async Task ExportMappingsAsync(ApplicationDbContext db, SqliteConnection sqlite, CancellationToken ct)
	{
		async Task Write<T>(string kind, T row)
		{
			await using var command = sqlite.CreateCommand();
			command.CommandText = "INSERT INTO category_mapping_archive(kind,id,payload) VALUES($kind,$id,$payload)";
			command.Parameters.AddWithValue("$kind", kind);
			command.Parameters.AddWithValue("$id", ArchiveKey(row));
			command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(row, JsonOptions));
			await command.ExecuteNonQueryAsync(ct);
		}
		foreach (var row in await db.CategoryMappingRevisions.AsNoTracking().ToListAsync(ct)) { await Write("revision", row); }
		foreach (var row in await db.CategoryMappingHeads.AsNoTracking().ToListAsync(ct)) { await Write("head", row); }
	}

	private static async Task<State> ReadMappingsAsync(SqliteConnection sqlite, State state, CancellationToken ct)
	{
		List<CategoryMappingRevisionEntity> revisions = [];
		List<CategoryMappingHeadEntity> heads = [];
		await using var command = sqlite.CreateCommand();
		command.CommandText = "SELECT kind,id,payload FROM category_mapping_archive ORDER BY kind,id";
		await using var reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct))
		{
			string kind = reader.GetString(0), id = reader.GetString(1), payload = reader.GetString(2);
			if (kind == "revision")
			{
				var row = DeserializeStrict<CategoryMappingRevisionEntity>(payload) ?? throw new InvalidDataException("Null mapping revision.");
				Require(id == ArchiveKey(row), "Mapping archive key differs from payload.");
				revisions.Add(row);
			}
			else if (kind == "head")
			{
				var row = DeserializeStrict<CategoryMappingHeadEntity>(payload) ?? throw new InvalidDataException("Null mapping head.");
				Require(id == ArchiveKey(row), "Mapping archive key differs from payload.");
				heads.Add(row);
			}
			else { throw new InvalidDataException("Unknown mapping archive kind."); }
		}
		return state with { IncludesMappings = true, MappingRevisions = revisions, MappingHeads = heads };
	}

	private static void ValidateMappings(State state)
	{
		Unique(state.MappingRevisions); Unique(state.MappingHeads);
		Require(state.MappingRevisions.Select(r => (r.Actor, r.OperationId)).Distinct().Count() == state.MappingRevisions.Count,
			"Duplicate mapping actor operation.");
		var revisions = state.MappingRevisions.ToDictionary(r => r.Id);
		foreach (var row in state.MappingRevisions)
		{
			NormalizationSourceKey source = new(row.Location, row.Description, row.ItemCode);
			Require(Text(row.Location) && Text(row.Description) && row.ItemCode?.Contains('\0') != true
				&& source == NormalizationCatalogPolicy.Normalize(source)
				&& row.SourceFingerprint == NormalizationCatalogPolicy.Hash(source), "Invalid mapping source fingerprint.");
			Require(row.Id != Guid.Empty && row.OperationId != Guid.Empty && Text(row.Actor) && Text(row.Reason)
				&& Utc(row.RecordedAt) && Hash(row.RequestHash)
				&& state.Revisions.Any(r => r.Id == row.TaxonomyRevisionId && r.RecordedAt <= row.RecordedAt), "Invalid mapping metadata or taxonomy reference.");
			var command = new CategoryMappingCommand(source, row.SourceFingerprint, row.PreviousRevisionId,
				row.TaxonomyRevisionId, row.CategoryId, row.OperationId, row.Reason);
			Require(row.RequestHash == Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command))), "Invalid mapping request hash.");
			string? semantics = row.CategoryId is Guid category
				? CategoryMappingPolicy.SemanticFingerprint(state.Nodes.Where(n => n.RevisionId == row.TaxonomyRevisionId)
					.Select(n => new TaxonomyNode(n.CategoryId, n.ParentId, n.Name, n.Definition, n.Archived)).ToList(), category) : null;
			Require(row.CategoryId == null ? row.SemanticFingerprint == null && row.PreviousRevisionId != null
				: semantics != null && semantics == row.SemanticFingerprint, "Invalid mapping semantic fingerprint.");
			if (row.PreviousRevisionId is Guid prior)
			{
				Require(revisions.TryGetValue(prior, out var predecessor) && predecessor.SourceFingerprint == row.SourceFingerprint
					&& predecessor.Location == row.Location && predecessor.Description == row.Description && predecessor.ItemCode == row.ItemCode
					&& predecessor.RecordedAt <= row.RecordedAt, "Invalid mapping predecessor.");
			}
			HashSet<Guid> visited = [];
			CategoryMappingRevisionEntity? cursor = row;
			while (cursor != null)
			{
				Require(visited.Add(cursor.Id), "Cyclic mapping history.");
				cursor = cursor.PreviousRevisionId is Guid previous && revisions.TryGetValue(previous, out var parent) ? parent : null;
			}
		}
		foreach (var group in state.MappingRevisions.GroupBy(r => r.SourceFingerprint))
		{
			var roots = group.Where(r => r.PreviousRevisionId == null).ToList();
			var tips = group.Where(r => !group.Any(n => n.PreviousRevisionId == r.Id)).ToList();
			Require(roots.Count == 1 && tips.Count == 1 && group.Where(r => r.PreviousRevisionId != null)
				.Select(r => r.PreviousRevisionId).Distinct().Count() == group.Count() - 1, "Branched mapping history.");
			Require(state.MappingHeads.Count(h => h.SourceFingerprint == group.Key && h.RevisionId == tips[0].Id) == 1,
				"Mapping head differs from history.");
		}
		Require(state.MappingHeads.Count == state.MappingRevisions.Select(r => r.SourceFingerprint).Distinct().Count(), "Extra mapping head.");
	}

	private static void ValidateMappingDestination(State existing, State incoming)
	{
		if (!incoming.IncludesMappings) { return; } // v10 and earlier never assert absence of approvals.
		Prefix(existing.MappingRevisions, incoming.MappingRevisions);
	}

	private static async Task RestoreMappingsAsync(ApplicationDbContext db, State state, CancellationToken ct)
	{
		if (!state.IncludesMappings) { return; }
		var stored = (await db.CategoryMappingRevisions.AsNoTracking().Select(r => r.Id).ToListAsync(ct)).ToHashSet();
		var pending = state.MappingRevisions.Where(r => !stored.Contains(r.Id)).ToList();
		while (pending.Count > 0)
		{
			var level = pending.Where(r => r.PreviousRevisionId == null || stored.Contains(r.PreviousRevisionId.Value)).ToList();
			Require(level.Count > 0, "Invalid mapping restore order.");
			db.CategoryMappingRevisions.AddRange(level);
			await db.SaveChangesAsync(ct);
			foreach (var row in level) { stored.Add(row.Id); pending.Remove(row); }
		}
		var heads = await db.CategoryMappingHeads.ToDictionaryAsync(h => h.SourceFingerprint, ct);
		foreach (var head in state.MappingHeads)
		{
			if (heads.TryGetValue(head.SourceFingerprint, out var existing)) { existing.RevisionId = head.RevisionId; }
			else { db.CategoryMappingHeads.Add(head); }
		}
		await db.SaveChangesAsync(ct);
	}
}
