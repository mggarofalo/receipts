using Application.Duplicates;
using Application.Interfaces.Services;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Services;

public class DuplicateAcceptanceStore(IDbContextFactory<ApplicationDbContext> contextFactory) : IDuplicateAcceptanceStore
{

	public async Task<DuplicateAcceptanceWriteResult> AcceptAsync(IReadOnlyList<Guid> receiptIds, IReadOnlyList<(Guid A, Guid B)> pairs, DateTimeOffset acceptedAt, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using IDbContextTransaction? transaction = context.Database.IsRelational()
			? await context.Database.BeginTransactionAsync(cancellationToken) : null;
		await ReceiptFactLock.AcquireAsync(context, receiptIds, cancellationToken);
		List<Guid> existing = await context.Receipts.AsNoTracking().Where(r => receiptIds.Contains(r.Id)).Select(r => r.Id).ToListAsync(cancellationToken);
		List<Guid> missing = [.. receiptIds.Except(existing)];
		if (missing.Count > 0)
		{
			return new(0, missing);
		}

		int inserted = context.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
			? await InsertPairsIgnoringConflictsAsync(context, [.. pairs], acceptedAt, cancellationToken)
			: await InsertPairsTrackedAsync(context, [.. pairs], acceptedAt, cancellationToken);
		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken);
		}

		return new(inserted, []);
	}
	/// <summary>
	/// Inserts every pair in one statement, letting the database decide which already exist.
	///
	/// The decision has to happen at write time, not read time. Reading the existing rows first and
	/// skipping the ones that look present loses both ways under concurrency: two accepts racing on
	/// the same pair both insert and one dies on the unique index (23505, rolling back the whole
	/// request including its unrelated pairs), and an unaccept committing between the read and the
	/// write makes the accept report success while leaving the pair un-accepted.
	///
	/// ON CONFLICT DO NOTHING collapses a duplicate INSERT into a no-op, and the affected-row count is
	/// the number genuinely accepted. The conflict target repeats the unique index's predicate so
	/// Postgres can infer the partial index. A tombstone does not conflict — it is outside the partial
	/// index — so re-accepting after an un-accept inserts a fresh row and leaves the un-accept in
	/// history.
	///
	/// ON CONFLICT does NOT, however, collapse a deadlock. Two transactions inserting the same pairs
	/// in opposite orders take the same index-key locks in opposite orders and one dies with 40P01.
	/// Clients may submit the same receipt set in different orders. Sorting the canonical pairs
	/// gives every writer the same global index-lock order, independently of input ordering.
	/// </summary>
	private static async Task<int> InsertPairsIgnoringConflictsAsync(
		ApplicationDbContext context,
		List<(Guid A, Guid B)> pairs,
		DateTimeOffset acceptedAt,
		CancellationToken cancellationToken)
	{
		List<(Guid A, Guid B)> ordered = [.. pairs.OrderBy(p => p.A).ThenBy(p => p.B)];

		Guid[] ids = [.. ordered.Select(_ => Guid.NewGuid())];
		Guid[] aIds = [.. ordered.Select(p => p.A)];
		Guid[] bIds = [.. ordered.Select(p => p.B)];

		return await context.Database.ExecuteSqlRawAsync(
			"""
			INSERT INTO "receipts"."AcceptedDuplicatePairs" ("Id", "ReceiptIdA", "ReceiptIdB", "AcceptedAt")
			SELECT id, a, b, {3}
			FROM unnest({0}::uuid[], {1}::uuid[], {2}::uuid[]) AS t(id, a, b)
			ON CONFLICT ("ReceiptIdA", "ReceiptIdB") WHERE "DeletedAt" IS NULL DO NOTHING;
			""",
			[ids, aIds, bIds, acceptedAt],
			cancellationToken);
	}

	/// <summary>
	/// Change-tracker fallback for providers without ON CONFLICT (the InMemory provider used by unit
	/// tests). Idempotent for sequential callers, which is all a single-threaded test needs; it cannot
	/// be made race-safe without the database constraint, so production never takes this path.
	/// </summary>
	private static async Task<int> InsertPairsTrackedAsync(
		ApplicationDbContext context,
		List<(Guid A, Guid B)> pairs,
		DateTimeOffset acceptedAt,
		CancellationToken cancellationToken)
	{
		HashSet<Guid> endpoints = [.. pairs.SelectMany(p => new[] { p.A, p.B })];

		HashSet<(Guid, Guid)> alreadyActive = [.. (await context.AcceptedDuplicatePairs
			.Where(p => endpoints.Contains(p.ReceiptIdA) && endpoints.Contains(p.ReceiptIdB))
			.Select(p => new { p.ReceiptIdA, p.ReceiptIdB })
			.ToListAsync(cancellationToken))
			.Select(p => (p.ReceiptIdA, p.ReceiptIdB))];

		int accepted = 0;
		foreach ((Guid a, Guid b) in pairs)
		{
			if (alreadyActive.Contains((a, b)))
			{
				continue;
			}

			context.AcceptedDuplicatePairs.Add(new AcceptedDuplicatePairEntity
			{
				Id = Guid.NewGuid(),
				ReceiptIdA = a,
				ReceiptIdB = b,
				AcceptedAt = acceptedAt
			});
			accepted++;
		}

		if (accepted > 0)
		{
			await context.SaveChangesAsync(cancellationToken);
		}

		return accepted;
	}

	public async Task<int> RemovePairsAsync(
		List<Guid> receiptIds,
		CancellationToken cancellationToken)
	{
		HashSet<Guid> idSet = [.. receiptIds];
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		// Remove exactly the pairs among the submitted receipts — nothing wider.
		//
		// An earlier revision expanded to the connected component of the acceptance graph, to fix undo
		// stranding pairs when a group had a soft-deleted member. That over-corrected. The undo button
		// in the accepted-groups list acts on a whole acceptance, but "Report again" in the report acts
		// on a REPORT CLUSTER, and a cluster can be a strict subset of the component: accept {A,B} and
		// {C,D} at tolerance 0, widen tolerance so all four cluster together, accept that, then narrow
		// tolerance again — clicking "Report again" on {A,B} would expand to the component and silently
		// destroy the untouched {C,D} acceptance. Every value in that sequence is a dropdown option.
		//
		// The stranding bug is fixed at its actual source instead: GetAcceptedDuplicatesAsync now
		// reports each group's COMPLETE member set (including soft-deleted receipts) alongside the
		// subset it displays, so the client can submit every member and undo removes every pair. Here,
		// "remove what was asked for and nothing else" is both correct and the only safe rule, because
		// this method cannot tell a full acceptance from a cluster-shaped slice of one.
		List<AcceptedDuplicatePairEntity> toRemove = await context.AcceptedDuplicatePairs
			.Where(p => idSet.Contains(p.ReceiptIdA) && idSet.Contains(p.ReceiptIdB))
			.ToListAsync(cancellationToken);

		if (toRemove.Count == 0)
		{
			return 0;
		}

		// RemoveRange is converted to a soft delete by ApplicationDbContext.HandleSoftDelete.
		context.AcceptedDuplicatePairs.RemoveRange(toRemove);
		await context.SaveChangesAsync(cancellationToken);

		return toRemove.Count;
	}

}
