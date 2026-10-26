using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>Revokes deployment-local assistance before portable business rows are restored.</summary>
internal static class CategoryAssistanceRestoreFence
{
	internal static async Task FenceAsync(ApplicationDbContext db, CancellationToken ct)
	{
		CategoryAssistanceSettingsStore.RequireTransaction(db);
		// Dispatch and connection rotation take these locks in the same order.
		await db.TaxonomyHeads.FromSqlRaw(
			"SELECT * FROM taxonomy.\"Head\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
		var settings = await db.CategoryAssistanceSettings.FromSqlRaw(
			"SELECT * FROM category_assistance.\"Settings\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
		var connection = await db.CategoryAssistanceConnection.FromSqlRaw(
			"SELECT * FROM category_assistance.\"Connection\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);

		var before = new
		{
			settings.Enabled,
			settings.Provider,
			settings.Model,
			settings.DailyLimit,
			settings.PendingLimit,
			settings.Revision,
			settings.RestoreGeneration
		};
		Guid previousRevision = settings.Revision;
		settings.Enabled = false;
		settings.Revision = Guid.NewGuid();
		settings.RestoreGeneration = Guid.NewGuid();
		connection.Generation = Guid.NewGuid();
		connection.Changing = true;
		connection.ConfigurationFingerprint = "";

		DateTimeOffset now = DateTimeOffset.UtcNow;
		// Started calls remain charged. Unsent queued work has no debit.
		foreach (var attempt in await db.CategoryAssistanceAttempts
					 .Where(x => x.State == "queued" || x.State == "dispatching" || x.State == "suggested")
					 .ToListAsync(ct))
		{
			attempt.State = attempt.State == "dispatching" ? "indeterminate" : "stale";
			attempt.ResultJson = null;
			attempt.ClaimToken = null;
			attempt.LeaseExpiresAt = null;
			attempt.NextEligibleAt = null;
			attempt.CompletedAt = now;
		}
		db.CategoryAssistanceSettingsAudits.Add(new()
		{
			Id = Guid.NewGuid(),
			Actor = "backup_restore",
			PreviousRevision = previousRevision,
			Revision = settings.Revision,
			Before = JsonSerializer.Serialize(before),
			After = JsonSerializer.Serialize(new
			{
				settings.Enabled,
				settings.Provider,
				settings.Model,
				settings.DailyLimit,
				settings.PendingLimit,
				settings.Revision,
				settings.RestoreGeneration
			}),
			RecordedAt = now
		});
		// Save within the import transaction so rollback restores every local fence and audit row.
		await db.SaveChangesAsync(ct);
	}
}
