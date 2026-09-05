using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Utilities;

// PostgreSQL revisions are short-lived worker snapshots, not a public concurrency contract.
// Keep target locks before item locks, and never hold either across model/embedding work.
internal static class NormalizationWriteGuard
{
	internal sealed class ItemSnapshot
	{
		public Guid Id { get; set; }
		public string Description { get; set; } = string.Empty;
		public Guid? NormalizedDescriptionId { get; set; }
		public double? NormalizedDescriptionMatchScore { get; set; }
		public DateTimeOffset? DeletedAt { get; set; }
		public string? Revision { get; set; }

		public bool Matches(ItemSnapshot current) => Id == current.Id
			&& string.Equals(Revision, current.Revision, StringComparison.Ordinal)
			&& string.Equals(Description, current.Description, StringComparison.Ordinal)
			&& NormalizedDescriptionId == current.NormalizedDescriptionId
			&& NormalizedDescriptionMatchScore == current.NormalizedDescriptionMatchScore
			&& DeletedAt == current.DeletedAt;
	}

	internal static async Task<List<ItemSnapshot>> ReadItemSnapshotsAsync(
		ApplicationDbContext context, Guid[] ids, CancellationToken cancellationToken)
	{
		if (ids.Length == 0)
		{
			return [];
		}
		if (context.Database.IsNpgsql())
		{
			return await context.Database.SqlQuery<ItemSnapshot>($"""
				SELECT "Id", "Description", "NormalizedDescriptionId", "NormalizedDescriptionMatchScore",
				       "DeletedAt", xmin::text AS "Revision"
				FROM receipts."ReceiptItems" WHERE "Id" = ANY ({ids})
				""").ToListAsync(cancellationToken);
		}

		// Nonrelational unit fixtures can exercise value guards; PostgreSQL tests establish
		// row-version and locking guarantees, including edit/revert and replica races.
		return await context.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
			.Where(item => ids.Contains(item.Id))
			.Select(item => new ItemSnapshot
			{
				Id = item.Id,
				Description = item.Description,
				NormalizedDescriptionId = item.NormalizedDescriptionId,
				NormalizedDescriptionMatchScore = item.NormalizedDescriptionMatchScore,
				DeletedAt = item.DeletedAt,
			})
			.ToListAsync(cancellationToken);
	}

	internal static async Task AcquireWriteGateAsync(ApplicationDbContext context, CancellationToken cancellationToken)
	{
		if (!context.Database.IsNpgsql())
		{
			return;
		}
		RequireTransaction(context);
		// RCPT / NORM: one transaction-scoped gate for short canonical write phases.
		// It coordinates child-first EF deletes with guarded attachment and rejection,
		// and protects exact-text tombstones even when a fuzzy target has another ID.
		await context.Database.ExecuteSqlInterpolatedAsync(
			$"SELECT pg_advisory_xact_lock({0x52435054}, {0x4E4F524D})", cancellationToken);
	}

	internal static async Task LockTargetsAsync(ApplicationDbContext context, Guid[] ids, CancellationToken cancellationToken)
	{
		if (!context.Database.IsNpgsql() || ids.Length == 0)
		{
			return;
		}
		await AcquireWriteGateAsync(context, cancellationToken);
		// NO KEY UPDATE excludes status changes/rejection but is compatible with FK KEY SHARE.
		await context.Database.SqlQuery<Guid>($"""
			SELECT "Id" AS "Value" FROM matching."NormalizedDescriptions"
			WHERE "Id" = ANY ({ids}) ORDER BY "Id" FOR NO KEY UPDATE
			""").ToListAsync(cancellationToken);
	}

	internal static async Task LockItemsAsync(ApplicationDbContext context, Guid[] ids, CancellationToken cancellationToken)
	{
		if (!context.Database.IsNpgsql() || ids.Length == 0)
		{
			return;
		}
		RequireTransaction(context);
		await context.Database.SqlQuery<Guid>($"""
			SELECT "Id" AS "Value" FROM receipts."ReceiptItems"
			WHERE "Id" = ANY ({ids}) ORDER BY "Id" FOR UPDATE
			""").ToListAsync(cancellationToken);
	}

	private static void RequireTransaction(ApplicationDbContext context)
	{
		if (context.Database.CurrentTransaction is null)
		{
			throw new InvalidOperationException("Normalization write locks require an active transaction.");
		}
	}
}
