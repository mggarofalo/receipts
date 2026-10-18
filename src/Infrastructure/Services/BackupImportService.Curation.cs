using System.Globalization;
using Application.Exceptions;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public partial class BackupImportService
{
	private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal)
		=> reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

	private static double? ReadNullableScore(SqliteDataReader reader, int ordinal)
	{
		if (reader.IsDBNull(ordinal))
		{
			return null;
		}
		double score = double.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);
		if (!double.IsFinite(score))
		{
			throw new InvalidOperationException("Backup match scores must be finite numbers.");
		}
		return score;
	}

	private static async Task ValidateCurationReferencesAsync(SqliteConnection sqlite, CancellationToken cancellationToken)
	{
		HashSet<Guid> canonicalIds = await ReadBackupIdsAsync(sqlite, "SELECT id FROM normalized_descriptions", cancellationToken);
		HashSet<Guid> receiptIds = await ReadBackupIdsAsync(sqlite, "SELECT id FROM receipts", cancellationToken);
		await ValidateBackupReferencesAsync(sqlite,
			"SELECT normalized_description_id FROM item_templates UNION ALL SELECT normalized_description_id FROM receipt_items UNION ALL SELECT nearest_neighbour_id FROM normalized_descriptions",
			canonicalIds, "canonical description", cancellationToken);
		await ValidateBackupReferencesAsync(sqlite,
			"SELECT receipt_id_a FROM accepted_duplicate_pairs UNION ALL SELECT receipt_id_b FROM accepted_duplicate_pairs",
			receiptIds, "receipt", cancellationToken);
	}

	private static async Task<HashSet<Guid>> ReadBackupIdsAsync(SqliteConnection sqlite, string query, CancellationToken cancellationToken)
	{
		HashSet<Guid> ids = [];
		await using SqliteCommand command = sqlite.CreateCommand();
		command.CommandText = query;
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			ids.Add(Guid.Parse(reader.GetString(0)));
		}
		return ids;
	}

	private static async Task ValidateBackupReferencesAsync(SqliteConnection sqlite, string query,
		HashSet<Guid> includedIds, string resource, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = sqlite.CreateCommand();
		command.CommandText = query;
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			if (!reader.IsDBNull(0) && !includedIds.Contains(Guid.Parse(reader.GetString(0))))
			{
				throw new InvalidOperationException($"Backup curation references a {resource} that is not included in the backup.");
			}
		}
	}

	private static async Task StageCanonicalNamesAsync(ApplicationDbContext context, CancellationToken cancellationToken)
	{
		if (!context.Database.IsNpgsql())
		{
			return;
		}

		// PostgreSQL checks the functional unique indexes immediately, including between
		// individual updates in one save. Free all changing names before restoring swaps.
		// Keep tracked originals intact: the normal save audits only original -> final values.
		var changed = context.ChangeTracker.Entries<NormalizedDescriptionEntity>()
			.Where(entry => entry.State == EntityState.Modified
				&& (entry.Property(entity => entity.CanonicalName).IsModified
					|| entry.Property(entity => entity.DisplayLabel).IsModified))
			.ToList();
		if (changed.Count == 0)
		{
			return;
		}

		Guid[] ids = changed.Select(entry => entry.Entity.Id).ToArray();
		string stagingPrefix = $"__backup_restore_{Guid.NewGuid():N}_";
		string[] names = ids.Select(id => stagingPrefix + id.ToString("N")).ToArray();
		await context.Database.ExecuteSqlInterpolatedAsync($"""
			UPDATE matching."NormalizedDescriptions" AS canonical
			SET "CanonicalName" = staging.name, "DisplayLabel" = NULL
			FROM unnest({ids}, {names}) AS staging(id, name)
			WHERE canonical."Id" = staging.id
			""", cancellationToken);
		foreach (var entry in changed)
		{
			// Both database columns were staged, even when only one final value changed.
			entry.Property(entity => entity.CanonicalName).IsModified = true;
			entry.Property(entity => entity.DisplayLabel).IsModified = true;
		}
	}

	private static async Task<(int Created, int Updated)> UpsertNormalizedDescriptionsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion, CancellationToken cancellationToken)
	{
		if (exportVersion < 4 || !TableExists(sqlite, "normalized_descriptions"))
		{
			return (0, 0);
		}

		bool hasCuration = exportVersion >= 5;
		int created = 0, updated = 0;
		List<(NormalizedDescriptionEntity Entity, Guid? NeighbourId, double? Similarity)> references = [];
		await using (SqliteCommand cmd = sqlite.CreateCommand())
		{
			cmd.CommandText = "SELECT id, canonical_name, status, created_at"
				+ (hasCuration ? ", display_label, nearest_neighbour_id, nearest_neighbour_similarity" : "")
				+ " FROM normalized_descriptions";
			await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
			while (await reader.ReadAsync(cancellationToken))
			{
				Guid id = Guid.Parse(reader.GetString(0));
				string canonicalName = reader.GetString(1);
				if (!Enum.TryParse(reader.GetString(2), out NormalizedDescriptionStatus status) || !Enum.IsDefined(status))
				{
					throw new InvalidOperationException($"Canonical description {id} has an invalid status.");
				}
				DateTimeOffset createdAt = ParseTimestamp(reader.GetString(3));
				string? displayLabel = hasCuration && !reader.IsDBNull(4) ? reader.GetString(4) : null;
				Guid? neighbourId = hasCuration ? ReadNullableGuid(reader, 5) : null;
				double? similarity = hasCuration ? ReadNullableScore(reader, 6) : null;
				NormalizedDescriptionEntity? entity = await context.NormalizedDescriptions.FindAsync([id], cancellationToken);
				if (entity is null)
				{
					entity = new NormalizedDescriptionEntity { Id = id };
					context.NormalizedDescriptions.Add(entity);
					created++;
				}
				else
				{
					if (!string.Equals(entity.CanonicalName, canonicalName, StringComparison.Ordinal))
					{
						// Vectors and comparison evidence describe text, not just the row ID.
						if (!hasCuration)
						{
							entity.NearestNeighbourId = null;
							entity.NearestNeighbourSimilarity = null;
						}
					}

					// Portable backups deliberately do not own rebuildable vectors. Clear both
					// projection columns on every restored row and force EF to send the NULLs even
					// when this transaction originally read NULL. A concurrent rebuild may commit a
					// vector while the restore waits for its row lock; explicit modified flags ensure
					// the restore still wins and the next worker cycle rebuilds the imported text.
					entity.Embedding = null;
					entity.EmbeddingModelVersion = null;
					context.Entry(entity).Property(item => item.Embedding).IsModified = true;
					context.Entry(entity).Property(item => item.EmbeddingModelVersion).IsModified = true;
					updated++;
				}

				entity.CanonicalName = canonicalName;
				entity.Status = status;
				entity.CreatedAt = createdAt;
				if (hasCuration)
				{
					entity.DisplayLabel = displayLabel;
					// Save every canonical parent before applying any self-referencing edge.
					entity.NearestNeighbourId = null;
					entity.NearestNeighbourSimilarity = null;
					references.Add((entity, neighbourId, similarity));
				}
			}
		}

		await StageCanonicalNamesAsync(context, cancellationToken);
		await context.SaveChangesAsync(cancellationToken);
		if (hasCuration)
		{
			foreach ((NormalizedDescriptionEntity entity, Guid? neighbourId, double? similarity) in references)
			{
				entity.NearestNeighbourId = neighbourId;
				// A deleted neighbour may leave a historical score with no remaining ID.
				entity.NearestNeighbourSimilarity = similarity;
			}
			await context.SaveChangesAsync(cancellationToken);
		}
		return (created, updated);
	}

	// Import acknowledges only the pairs explicitly supplied by the archive. Keep the target's
	// semantic identity even when earlier fact writes in this transaction tombstone that pair.
	private static async Task<Dictionary<(Guid First, Guid Second), Guid>> LockReceiptImportAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion, CancellationToken cancellationToken)
	{
		Guid[] receiptIds = await ReadImportIdsAsync(sqlite, "receipts", cancellationToken);
		Guid[] transactionIds = await ReadImportIdsAsync(sqlite, "transactions", cancellationToken);
		Guid[] itemIds = await ReadImportIdsAsync(sqlite, "receipt_items", cancellationToken);
		Guid[] adjustmentIds = await ReadImportIdsAsync(sqlite, "adjustments", cancellationToken);
		Dictionary<(Type Kind, Guid Id), Guid> parents = await CaptureImportedChildParentsAsync(
			context, transactionIds, itemIds, adjustmentIds, cancellationToken);
		HashSet<Guid> lockIds = [.. receiptIds, .. parents.Values];
		lockIds.UnionWith(await ReadImportIdsAsync(sqlite, "transactions", cancellationToken, "receipt_id"));
		lockIds.UnionWith(await ReadImportIdsAsync(sqlite, "receipt_items", cancellationToken, "receipt_id"));
		lockIds.UnionWith(await ReadImportIdsAsync(sqlite, "adjustments", cancellationToken, "receipt_id"));
		await ReceiptFactLock.AcquireAsync(context, lockIds, cancellationToken);
		Dictionary<(Type Kind, Guid Id), Guid> lockedParents = await CaptureImportedChildParentsAsync(
			context, transactionIds, itemIds, adjustmentIds, cancellationToken);
		if (parents.Count != lockedParents.Count || parents.Any(pair => !lockedParents.TryGetValue(pair.Key, out Guid parent) || parent != pair.Value))
		{
			throw new ConcurrencyConflictException("Receipt children changed parents while the backup import was starting. Retry the import.");
		}
		if (exportVersion < 5)
		{
			return [];
		}
		var pairs = await context.AcceptedDuplicatePairs.AsNoTracking()
			.Where(pair => receiptIds.Contains(pair.ReceiptIdA) && receiptIds.Contains(pair.ReceiptIdB))
			.Select(pair => new { pair.Id, pair.ReceiptIdA, pair.ReceiptIdB }).ToListAsync(cancellationToken);
		return pairs.ToDictionary(pair => (pair.ReceiptIdA, pair.ReceiptIdB), pair => pair.Id);
	}

	private static async Task<Dictionary<(Type Kind, Guid Id), Guid>> CaptureImportedChildParentsAsync(
		ApplicationDbContext context, Guid[] transactionIds, Guid[] itemIds, Guid[] adjustmentIds, CancellationToken cancellationToken)
	{
		var payments = await context.Transactions.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
			.Where(row => transactionIds.Contains(row.Id)).Select(row => new { row.Id, row.ReceiptId }).ToListAsync(cancellationToken);
		var items = await context.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
			.Where(row => itemIds.Contains(row.Id)).Select(row => new { row.Id, row.ReceiptId }).ToListAsync(cancellationToken);
		var adjustments = await context.Adjustments.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
			.Where(row => adjustmentIds.Contains(row.Id)).Select(row => new { row.Id, row.ReceiptId }).ToListAsync(cancellationToken);
		Dictionary<(Type Kind, Guid Id), Guid> parents = [];
		foreach (var row in payments)
		{
			parents.Add((typeof(TransactionEntity), row.Id), row.ReceiptId);
		}
		foreach (var row in items)
		{
			parents.Add((typeof(ReceiptItemEntity), row.Id), row.ReceiptId);
		}
		foreach (var row in adjustments)
		{
			parents.Add((typeof(AdjustmentEntity), row.Id), row.ReceiptId);
		}
		return parents;
	}
	private static async Task<Guid[]> ReadImportIdsAsync(SqliteConnection sqlite, string table, CancellationToken cancellationToken, string column = "id")
	{
		if (!TableExists(sqlite, table))
		{
			return [];
		}
		// Table and column names are fixed internal call-site constants, never archive content or user input.
		await using SqliteCommand command = sqlite.CreateCommand();
		command.CommandText = $"SELECT {column} FROM {table}";
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		List<Guid> ids = [];
		while (await reader.ReadAsync(cancellationToken))
		{
			ids.Add(Guid.Parse(reader.GetString(0)));
		}
		return [.. ids];
	}
	private static async Task<(int Created, int Updated)> UpsertAcceptedDuplicatePairsAsync(
		ApplicationDbContext context, SqliteConnection sqlite, int exportVersion,
		IReadOnlyDictionary<(Guid First, Guid Second), Guid> priorAcceptedPairs, CancellationToken cancellationToken)
	{
		if (exportVersion < 5)
		{
			return (0, 0);
		}

		int created = 0, updated = 0;
		await using SqliteCommand cmd = sqlite.CreateCommand();
		cmd.CommandText = "SELECT id, receipt_id_a, receipt_id_b, accepted_at FROM accepted_duplicate_pairs";
		await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			Guid id = Guid.Parse(reader.GetString(0));
			Guid first = Guid.Parse(reader.GetString(1));
			Guid second = Guid.Parse(reader.GetString(2));
			DateTimeOffset acceptedAt = ParseTimestamp(reader.GetString(3));
			if (first.CompareTo(second) >= 0)
			{
				throw new InvalidOperationException($"Accepted duplicate pair {id} must contain two distinct receipt IDs in canonical order.");
			}

			AcceptedDuplicatePairEntity? byId = await context.AcceptedDuplicatePairs.IgnoreQueryFilters()
				.FirstOrDefaultAsync(pair => pair.Id == id, cancellationToken);
			if (byId is not null && (byId.ReceiptIdA != first || byId.ReceiptIdB != second))
			{
				throw new InvalidOperationException($"Accepted duplicate pair ID {id} already belongs to different receipts.");
			}

			// The pair is the user decision; an existing target can have assigned another ID
			// to the same assertion. Keep that target identity rather than duplicating it.
			AcceptedDuplicatePairEntity? active = await context.AcceptedDuplicatePairs
				.FirstOrDefaultAsync(pair => pair.ReceiptIdA == first && pair.ReceiptIdB == second, cancellationToken);
			AcceptedDuplicatePairEntity? prior = null;
			if (active is null && priorAcceptedPairs.TryGetValue((first, second), out Guid priorId))
			{
				prior = await context.AcceptedDuplicatePairs.IgnoreQueryFilters()
					.FirstOrDefaultAsync(pair => pair.Id == priorId, cancellationToken);
			}
			AcceptedDuplicatePairEntity? entity = active ?? prior ?? byId;
			if (entity is null)
			{
				context.AcceptedDuplicatePairs.Add(new AcceptedDuplicatePairEntity
				{
					Id = id,
					ReceiptIdA = first,
					ReceiptIdB = second,
					AcceptedAt = acceptedAt,
				});
				created++;
			}
			else
			{
				entity.AcceptedAt = acceptedAt;
				ClearSoftDelete(entity);
				updated++;
			}
		}

		await context.SaveChangesAsync(cancellationToken);
		return (created, updated);
	}
}
