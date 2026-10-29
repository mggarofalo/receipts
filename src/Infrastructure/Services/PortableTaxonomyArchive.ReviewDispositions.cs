using System.Text.Json;
using Application.Taxonomy;
using Infrastructure.Entities.Taxonomy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	internal static async Task WriteReviewDispositionsAsync(SqliteConnection sqlite, State state, CancellationToken ct)
	{
		ValidateReviewDispositions(state);
		await using var command = sqlite.CreateCommand();
		command.CommandText = "CREATE TABLE review_disposition_archive(kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(kind,id))";
		await command.ExecuteNonQueryAsync(ct);
		async Task Write<T>(string kind, Guid id, T row)
		{
			string payload = JsonSerializer.Serialize(row, JsonOptions);
			command.CommandText = "INSERT INTO review_disposition_archive VALUES($kind,$id,$payload)";
			command.Parameters.Clear();
			command.Parameters.AddWithValue("$kind", kind);
			command.Parameters.AddWithValue("$id", id.ToString("D"));
			command.Parameters.AddWithValue("$payload", payload);
			await command.ExecuteNonQueryAsync(ct);
		}
		foreach (var row in state.ReviewDispositions) { await Write("disposition", row.Id, row); }
		foreach (var row in state.ReviewDispositionOperations) { await Write("operation", row.OperationId, row); }
	}

	private static async Task<State> ReadReviewDispositionsAsync(SqliteConnection sqlite, State state, CancellationToken ct)
	{
		List<HistoricalGroupDispositionEntity> rows = [];
		List<HistoricalDispositionOperationEntity> operations = [];
		await using var command = sqlite.CreateCommand();
		command.CommandText = "SELECT kind,id,payload FROM review_disposition_archive ORDER BY kind,id";
		await using var reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct))
		{
			string kind = reader.GetString(0), id = reader.GetString(1), payload = reader.GetString(2);
			if (kind == "disposition")
			{
				var row = DeserializeStrict<HistoricalGroupDispositionEntity>(payload);
				Require(row != null && id == row.Id.ToString("D"), "Invalid disposition archive key.");
				rows.Add(row!);
			}
			else if (kind == "operation")
			{
				var row = DeserializeStrict<HistoricalDispositionOperationEntity>(payload);
				Require(row != null && id == row.OperationId.ToString("D"), "Invalid disposition operation archive key.");
				operations.Add(row!);
			}
			else { throw new InvalidDataException("Unknown review disposition archive kind."); }
		}
		return state with { IncludesReviewDispositions = true, ReviewDispositions = rows, ReviewDispositionOperations = operations };
	}

	private static void ValidateReviewDispositions(State state)
	{
		if (!state.IncludesReviewDispositions) { return; }
		var rows = state.ReviewDispositions;
		var operations = state.ReviewDispositionOperations;
		Require(rows.Select(x => x.Id).Distinct().Count() == rows.Count &&
			rows.Select(x => (x.Actor, x.SourceHash)).Distinct().Count() == rows.Count &&
			operations.Select(x => x.OperationId).Distinct().Count() == operations.Count, "Duplicate review disposition identity.");
		static bool Source(string actor, string hash, string store, string description, string? code) =>
			Text(actor) && Text(store) && Text(description) && code?.Contains('\0') != true &&
			hash == HistoricalReclassificationService.DispositionSourceHash(new HistoricalGroupKey(store, description, code));
		static bool ValidState(HistoricalGroupDisposition? value) => value is null or HistoricalGroupDisposition.Later or HistoricalGroupDisposition.LeaveAsIs;
		foreach (var row in rows)
		{
			Require(row.Id != Guid.Empty && row.OperationId != Guid.Empty && row.Version > 0 && Utc(row.UpdatedAt) &&
				ValidState(row.Disposition) && Source(row.Actor, row.SourceHash, row.Store, row.RawDescription, row.ItemCode), "Invalid review disposition.");
			var history = operations.Where(x => x.Actor == row.Actor && x.SourceHash == row.SourceHash).OrderBy(x => x.ResultVersion).ToList();
			Require(history.Count == row.Version, "Incomplete disposition operation history.");
			for (int i = 0; i < history.Count; i++)
			{
				var op = history[i];
				Require(op.OperationId != Guid.Empty && op.Store == row.Store && op.RawDescription == row.RawDescription && op.ItemCode == row.ItemCode &&
					op.ExpectedVersion == i && op.ResultVersion == i + 1 && Utc(op.ResultUpdatedAt) && ValidState(op.Disposition), "Invalid disposition operation history.");
			}
			var last = history[^1];
			Require(last.OperationId == row.OperationId && last.Disposition == row.Disposition && last.ResultUpdatedAt == row.UpdatedAt, "Disposition differs from operation history.");
		}
		Require(operations.All(op => rows.Any(row => row.Actor == op.Actor && row.SourceHash == op.SourceHash)), "Orphan disposition operation.");
	}

	private static void ValidateReviewDispositionDestination(State existing, State incoming)
	{
		if (!incoming.IncludesReviewDispositions) { return; }
		foreach (var row in incoming.ReviewDispositions)
		{
			foreach (var local in existing.ReviewDispositions.Where(x => x.Id == row.Id || (x.Actor == row.Actor && x.SourceHash == row.SourceHash)))
			{ Require(Equal(local, row), "Review disposition conflicts with destination."); }
		}
		foreach (var row in incoming.ReviewDispositionOperations)
		{
			foreach (var local in existing.ReviewDispositionOperations.Where(x => x.OperationId == row.OperationId))
			{ Require(Equal(local, row), "Review disposition operation conflicts with destination."); }
		}
	}

	private static async Task RestoreReviewDispositionsAsync(ApplicationDbContext db, State incoming, CancellationToken ct)
	{
		var ids = (await db.HistoricalGroupDispositions.AsNoTracking().Select(x => x.Id).ToListAsync(ct)).ToHashSet();
		var operationIds = (await db.HistoricalDispositionOperations.AsNoTracking().Select(x => x.OperationId).ToListAsync(ct)).ToHashSet();
		db.HistoricalGroupDispositions.AddRange(incoming.ReviewDispositions.Where(x => !ids.Contains(x.Id)));
		db.HistoricalDispositionOperations.AddRange(incoming.ReviewDispositionOperations.Where(x => !operationIds.Contains(x.OperationId)));
		await db.SaveChangesAsync(ct);
	}
}
