using System.Text.Json;
using Application.Classification;
using Application.Normalization;
using Infrastructure.Entities.CategoryAssistance;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	private static string ReviewKey(string actor, Guid id) => JsonSerializer.Serialize(new { Actor = actor, Id = id }, JsonOptions);

	internal static async Task WriteReviewPreferencesAsync(SqliteConnection sqlite, State state, CancellationToken ct)
	{
		ValidateReviewPreferences(state);
		await using var command = sqlite.CreateCommand();
		command.CommandText = "CREATE TABLE review_preference_archive(kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(kind,id))";
		await command.ExecuteNonQueryAsync(ct);
		async Task Write<T>(string kind, string key, T row)
		{
			command.CommandText = "INSERT INTO review_preference_archive VALUES($kind,$id,$payload)";
			command.Parameters.Clear();
			command.Parameters.AddWithValue("$kind", kind);
			command.Parameters.AddWithValue("$id", key);
			command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(row, JsonOptions));
			await command.ExecuteNonQueryAsync(ct);
		}
		foreach (var row in state.ReviewPreferences) { await Write("preference", ReviewKey(row.Actor, row.AttemptId), row); }
		foreach (var row in state.ReviewOperations) { await Write("operation", ReviewKey(row.Actor, row.OperationId), row); }
	}

	internal static async Task<State> ReadReviewPreferencesAsync(SqliteConnection sqlite, State state, CancellationToken ct)
	{
		List<CategoryAssistanceReviewPreferenceEntity> preferences = [];
		List<CategoryAssistanceReviewOperationEntity> operations = [];
		await using var command = sqlite.CreateCommand();
		command.CommandText = "SELECT kind,id,payload FROM review_preference_archive ORDER BY kind,id";
		await using var reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct))
		{
			var kind = reader.GetString(0); var key = reader.GetString(1); var payload = reader.GetString(2);
			if (kind == "preference")
			{
				var row = DeserializeStrict<CategoryAssistanceReviewPreferenceEntity>(payload);
				Require(row != null && key == ReviewKey(row.Actor, row.AttemptId), "Invalid review preference archive key.");
				preferences.Add(row!);
			}
			else if (kind == "operation")
			{
				var row = DeserializeStrict<CategoryAssistanceReviewOperationEntity>(payload);
				Require(row != null && key == ReviewKey(row.Actor, row.OperationId), "Invalid review operation archive key.");
				operations.Add(row!);
			}
			else { throw new InvalidDataException("Unknown review preference archive kind."); }
		}
		return state with { IncludesReviewPreferences = true, ReviewPreferences = preferences, ReviewOperations = operations };
	}

	private static Guid[] ReviewExcluded(CategoryAssistanceReviewPreferenceEntity row)
	{
		var ids = JsonSerializer.Deserialize<Guid[]>(row.ExcludedItemIdsJson) ?? throw new InvalidDataException("Invalid review exclusions.");
		Require(ids.Length <= 1000 && ids.All(x => x != Guid.Empty) && ids.SequenceEqual(ids.Order()) && ids.Distinct().Count() == ids.Length,
			"Invalid review exclusions.");
		return ids;
	}

	private static void ValidateReviewPreferences(State state)
	{
		if (!state.IncludesReviewPreferences) { return; }
		Require(state.ReviewPreferences.Select(x => (x.Actor, x.AttemptId)).Distinct().Count() == state.ReviewPreferences.Count &&
			state.ReviewOperations.Select(x => (x.Actor, x.OperationId)).Distinct().Count() == state.ReviewOperations.Count,
			"Duplicate review preference identity.");
		foreach (var row in state.ReviewPreferences)
		{
			Require(Text(row.Actor) && row.Actor.Length <= 450 && row.AttemptId != Guid.Empty && row.Revision != Guid.Empty &&
				row.State is "active" or "later" or "left_as_is" && row.Location != null && row.Description != null && row.ItemCodeKey != null &&
				!row.Location.Contains('\0') && !row.Description.Contains('\0') && !row.ItemCodeKey.Contains('\0'), "Invalid review preference.");
			NormalizationSourceKey source;
			try { source = NormalizationCatalogPolicy.Normalize(new(row.Location!, row.Description!, row.ItemCodeKey)); }
			catch (NormalizationCatalogException) { throw new InvalidDataException("Invalid review preference source."); }
			Require(source.Location == row.Location && source.Description == row.Description && (source.ItemCode ?? "") == row.ItemCodeKey,
				"Noncanonical review preference source.");
			_ = ReviewExcluded(row);
		}
		foreach (var group in state.ReviewPreferences.GroupBy(x => x.AttemptId))
		{
			Require(group.Select(x => (x.Location, x.Description, x.ItemCodeKey)).Distinct().Count() == 1,
				"Review preferences disagree on attempt source.");
		}

		var preferences = state.ReviewPreferences.ToDictionary(p => (p.Actor, p.AttemptId));
		var results = new Dictionary<(string Actor, Guid AttemptId, Guid Revision), CategoryAssistanceReviewPreference>();
		foreach (var op in state.ReviewOperations)
		{
			Require(Text(op.Actor) && op.OperationId != Guid.Empty && op.AttemptId != Guid.Empty && Utc(op.RecordedAt) &&
				op.RequestHash != null && op.RequestHash.Length == 64 && op.RequestHash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F') &&
				preferences.ContainsKey((op.Actor, op.AttemptId)), "Invalid review operation.");
			var result = DeserializeStrict<CategoryAssistanceReviewPreference>(op.ResultJson);
			Require(result != null && result.AttemptId == op.AttemptId && result.Revision != Guid.Empty &&
				result.State is "active" or "later" or "left_as_is" && result.ExcludedItemIds != null &&
				result.ExcludedItemIds.Count <= 1000 && result.ExcludedItemIds.All(x => x != Guid.Empty) &&
				result.ExcludedItemIds.SequenceEqual(result.ExcludedItemIds.Order()) && result.ExcludedItemIds.Distinct().Count() == result.ExcludedItemIds.Count,
				"Invalid review operation result.");
			Require(results.TryAdd((op.Actor, op.AttemptId, result!.Revision), result), "Duplicate review operation revision.");
		}
		foreach (var preference in state.ReviewPreferences)
		{
			if (results.TryGetValue((preference.Actor, preference.AttemptId, preference.Revision), out var current))
			{
				Require(current.State == preference.State && current.ExcludedItemIds.SequenceEqual(ReviewExcluded(preference)),
					"Review preference differs from operation result.");
			}
		}
	}

	private static void ValidateReviewPreferenceDestination(State existing, State incoming)
	{
		if (!incoming.IncludesReviewPreferences) { return; }
		var preferences = existing.ReviewPreferences.ToDictionary(x => (x.Actor, x.AttemptId));
		var sources = existing.ReviewPreferences.GroupBy(x => x.AttemptId)
			.ToDictionary(g => g.Key, g => (g.First().Location, g.First().Description, g.First().ItemCodeKey));
		var operations = existing.ReviewOperations.ToDictionary(x => (x.Actor, x.OperationId));
		foreach (var row in incoming.ReviewPreferences)
		{
			if (sources.TryGetValue(row.AttemptId, out var source))
			{
				Require(source == (row.Location, row.Description, row.ItemCodeKey), "Review preference source conflicts with destination.");
			}

			if (preferences.TryGetValue((row.Actor, row.AttemptId), out var local))
			{
				Require(Equal(local, row), "Review preference conflicts with destination.");
			}
		}
		foreach (var row in incoming.ReviewOperations)
		{
			if (operations.TryGetValue((row.Actor, row.OperationId), out var local))
			{
				Require(Equal(local, row), "Review operation conflicts with destination.");
			}
		}
	}

	private static async Task RestoreReviewPreferencesAsync(ApplicationDbContext db, State incoming, CancellationToken ct)
	{
		var preferences = (await db.CategoryAssistanceReviewPreferences.AsNoTracking().ToListAsync(ct)).Select(x => (x.Actor, x.AttemptId)).ToHashSet();
		var operations = (await db.CategoryAssistanceReviewOperations.AsNoTracking().ToListAsync(ct)).Select(x => (x.Actor, x.OperationId)).ToHashSet();
		db.CategoryAssistanceReviewPreferences.AddRange(incoming.ReviewPreferences.Where(x => !preferences.Contains((x.Actor, x.AttemptId))));
		db.CategoryAssistanceReviewOperations.AddRange(incoming.ReviewOperations.Where(x => !operations.Contains((x.Actor, x.OperationId))));
		await db.SaveChangesAsync(ct);
	}
}
