using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	// Inactive candidate. The caller owns the restore transaction and coordination locks,
	// validates ordinary history, and restores assignment/taxonomy dependencies first.
	internal static async Task RestoreEffectsCandidateAsync(ApplicationDbContext db, State existingHistory,
		State incomingHistory, EffectEvidence? incoming, CancellationToken ct)
	{
		Require(db.Database.IsNpgsql() && db.Database.CurrentTransaction != null,
			"Effect restoration requires a PostgreSQL restore transaction.");
		Require(!db.ChangeTracker.HasChanges(), "Effect restoration requires saved dependency changes.");
		var existing = await LoadEffectsCandidateAsync(db, ct);
		var manifests = existing.Manifests.Select(m => m.Manifest).ToList();
		var outcomes = existing.Outcomes;
		var retained = ValidateEffectDestination(existingHistory, existing, incomingHistory, incoming);
		var storedManifests = manifests.Select(m => m.Id).ToHashSet();
		var storedOutcomes = outcomes.Select(o => (o.ManifestId, o.ItemId, o.Phase)).ToHashSet();
		var tracked = db.ChangeTracker.Entries().Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
		var transaction = db.Database.CurrentTransaction!;
		string savepoint = "effect_restore_" + Guid.NewGuid().ToString("N");
		await transaction.CreateSavepointAsync(savepoint, ct);
		try
		{
			foreach (var manifest in retained.Manifests.Where(m => !storedManifests.Contains(m.Manifest.Id)))
			{
				// Copy portable properties only: never attach a source-context shadow stamp.
				db.OrganizationEffectManifests.Add(Copy(manifest.Manifest));
				await db.SaveChangesAsync(ct);
				db.OrganizationEffectItems.AddRange(retained.Items.Where(i => i.ManifestId == manifest.Manifest.Id).Select(Copy));
				await db.SaveChangesAsync(ct);
				// PostgreSQL stamps/seals this complete member set and derives both phase
				// reservations via its item trigger. Reservation rows are never imported.
			}
			// Immediate undo lineage triggers require apply results to exist already.
			foreach (string phase in new[] { "apply", "undo" })
			{
				db.OrganizationEffectOutcomes.AddRange(retained.Outcomes.Where(o => o.Phase == phase
					&& !storedOutcomes.Contains((o.ManifestId, o.ItemId, o.Phase))).Select(Copy));
				await db.SaveChangesAsync(ct);
			}
			await transaction.ReleaseSavepointAsync(savepoint, ct);
		}
		catch
		{
			// Even a caller which catches the failure cannot commit a partial manifest.
			await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
			foreach (var entry in db.ChangeTracker.Entries().Where(e => !tracked.Contains(e.Entity)).ToList())
			{ entry.State = EntityState.Detached; }
			throw;
		}

		static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
	}
	internal static async Task<EffectEvidence> LoadEffectsCandidateAsync(ApplicationDbContext db, CancellationToken ct)
	{
		var manifests = await db.OrganizationEffectManifests.AsNoTracking().ToListAsync(ct);
		var items = await db.OrganizationEffectItems.AsNoTracking().ToListAsync(ct);
		var outcomes = await db.OrganizationEffectOutcomes.AsNoTracking().ToListAsync(ct);
		var members = items.ToLookup(i => i.ManifestId);
		return new(manifests.Select(m => SealEffectManifest(m, members[m.Id])).ToList(), items, outcomes);
	}

}
