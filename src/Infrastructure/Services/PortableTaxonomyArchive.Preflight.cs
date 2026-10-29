using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	// Caller must hold the restore coordination lock and taxonomy head lock until commit.
	// This method is read-only: no archived row is attached to the destination context.
	internal static async Task PreflightDestinationAsync(ApplicationDbContext db, State incoming, CancellationToken ct)
				=> await PreflightDestinationCoreAsync(db, incoming, ct, candidate: false);

	// Candidate validation is not wired to public import or restore.
	internal static Task PreflightCandidateDestinationAsync(ApplicationDbContext db, State incoming, CancellationToken ct)
			=> PreflightDestinationCoreAsync(db, incoming, ct, candidate: true);

	private static async Task PreflightDestinationCoreAsync(ApplicationDbContext db, State incoming, CancellationToken ct, bool candidate)
	{
		var existing = new State(await db.TaxonomyIdentities.AsNoTracking().ToListAsync(ct),
			await db.TaxonomyHeads.AsNoTracking().ToListAsync(ct), await db.TaxonomyRevisions.AsNoTracking().ToListAsync(ct),
			await db.TaxonomyNodes.AsNoTracking().ToListAsync(ct), await db.TaxonomyOperations.AsNoTracking().ToListAsync(ct),
			await db.Classifications.AsNoTracking().ToListAsync(ct), await db.CurrentClassifications.AsNoTracking().ToListAsync(ct),
			await db.ClassificationOperations.AsNoTracking().ToListAsync(ct), await db.LegacyMigrations.AsNoTracking().ToListAsync(ct),
			await db.LegacyMigrationCrosswalks.AsNoTracking().ToListAsync(ct), await db.LegacyMigrationItems.AsNoTracking().ToListAsync(ct))
		{
			IncludesHistorical = true,
			HistoricalOperations = await db.HistoricalOperations.AsNoTracking().ToListAsync(ct),
			HistoricalItems = await db.HistoricalOperationItems.AsNoTracking().ToListAsync(ct),
			IncludesMappings = true,
			MappingRevisions = await db.CategoryMappingRevisions.AsNoTracking().ToListAsync(ct),
			MappingHeads = await db.CategoryMappingHeads.AsNoTracking().ToListAsync(ct)
		};
		ValidateDestination(existing, incoming, candidate);
		await PreflightSourceRevisionsAsync(db, incoming, ct);
	}

	internal static void ValidateDestination(State existing, State incoming) => ValidateDestination(existing, incoming, candidate: false);

	internal static void ValidateCandidateDestination(State existing, State incoming) => ValidateDestination(existing, incoming, candidate: true);

	private static void ValidateDestination(State existing, State incoming, bool candidate)
	{
		if (candidate) { ValidateCandidate(existing); ValidateCandidate(incoming); }
		else { Validate(existing); Validate(incoming); }
		ValidateMappingDestination(existing, incoming);
		if (candidate) { ValidateHistoricalCandidateDestination(existing, incoming); }
		else { ValidateHistoricalDestination(existing, incoming); }
		// A destination is a strict prefix of the archive, including all immutable rows.
		// Reject stale backups even when their head happens to share a revision ID.
		Require(existing.Revisions.Count <= incoming.Revisions.Count, "Restore would rewind taxonomy head.");
		Prefix(existing.Identities, incoming.Identities);
		Prefix(existing.Revisions, incoming.Revisions);
		Prefix(existing.Nodes, incoming.Nodes);
		Prefix(existing.Operations, incoming.Operations);
		Prefix(existing.Assignments, incoming.Assignments);
		Prefix(existing.ClassificationOperations, incoming.ClassificationOperations);
		Prefix(existing.Migrations, incoming.Migrations);
		Prefix(existing.Crosswalks, incoming.Crosswalks);
		Prefix(existing.MigrationItems, incoming.MigrationItems);
		foreach (var revision in existing.Revisions)
		{
			var stored = existing.Nodes.Where(n => n.RevisionId == revision.Id).Select(ArchiveKey).ToHashSet(StringComparer.Ordinal);
			var restored = incoming.Nodes.Where(n => n.RevisionId == revision.Id).Select(ArchiveKey).ToHashSet(StringComparer.Ordinal);
			Require(stored.SetEquals(restored), "Existing sealed revision has a different complete snapshot.");
		}
		// Existing per-item history must also be a prefix. The archive's rebuilt projection
		// can advance it, but no later destination assignment may be overwritten by old labels.
		foreach (var current in existing.Current)
		{
			var old = existing.Assignments.Single(a => a.Id == current.AssignmentId);
			var latest = incoming.Assignments.Where(a => a.ReceiptItemId == current.ReceiptItemId).MaxBy(a => a.Sequence);
			Require(latest != null && latest.Sequence >= old.Sequence, "Restore would overwrite a later classification.");
		}
	}

	private static void Prefix<T>(IEnumerable<T> existing, IEnumerable<T> incoming)
	{
		var index = incoming.ToDictionary(ArchiveKey, StringComparer.Ordinal);
		foreach (var row in existing)
		{
			Require(index.TryGetValue(ArchiveKey(row), out var archived) && Equal(row, archived),
				"Restore conflicts with immutable destination history.");
		}
	}
}
