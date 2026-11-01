using System.Text.Json;
using Application.Classification;
using Application.Normalization;
using Application.Taxonomy;
using Common;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public sealed class CategoryAssistanceSettingsStore(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock)
	: ICategoryAssistanceSettingsStore
{
	public async Task<CategoryAssistanceSettings> GetAsync(CancellationToken ct = default)
	{
		await using var db = await factory.CreateDbContextAsync(ct);
		return Read(await db.CategoryAssistanceSettings.AsNoTracking().SingleAsync(ct));
	}
	public async Task<CategoryAssistanceSettings> UpdateAsync(string actor, Guid expectedRevision,
		CategoryAssistanceSettings proposed, CancellationToken ct = default)
	{
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var transaction = await db.Database.BeginTransactionAsync(ct);
		var result = await UpdateInTransactionAsync(db, actor, expectedRevision, proposed, clock.GetUtcNow(), ct);
		await transaction.CommitAsync(ct);
		return result;
	}
	public static async Task<CategoryAssistanceSettings> UpdateInTransactionAsync(ApplicationDbContext db, string actor,
		Guid expectedRevision, CategoryAssistanceSettings proposed, DateTimeOffset now, CancellationToken ct = default)
	{
		RequireTransaction(db);
		var row = await db.CategoryAssistanceSettings.FromSqlRaw("SELECT * FROM category_assistance.\"Settings\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
		// A caller-owned context may already track an older settings revision.
		// Refresh both current and original values after acquiring the row lock.
		await db.Entry(row).ReloadAsync(ct);
		bool admin = await (from user in db.Users
							join membership in db.UserRoles on user.Id equals membership.UserId
							join role in db.Roles on membership.RoleId equals role.Id
							where user.Id == actor && role.Name == AppRoles.Admin && !(user.LockoutEnabled && user.LockoutEnd > now)
							select user.Id).AnyAsync(ct);
		if (!admin) { throw new TaxonomyException(403, "An active Admin is required to configure category assistance."); }
		var before = Read(row);
		if (!CategoryAssistancePolicy.CanUpdateSettings(before, expectedRevision, actor, admin))
		{ throw new TaxonomyException(409, "Category assistance settings changed. Refresh before saving."); }
		// Only a change that alters what is asked or how answers are decided starts a new revision; limits do not.
		bool decisionChange = before.Enabled != proposed.Enabled || before.Provider != proposed.Provider
			|| before.Model != proposed.Model || before.AutoApply != proposed.AutoApply;
		proposed = proposed with { Revision = decisionChange ? Guid.NewGuid() : before.Revision };
		if (!CategoryAssistancePolicy.IsValid(proposed) || proposed.Provider?.Length > 100 || proposed.Model?.Length > 200
			|| proposed.Provider?.Contains('\0') == true || proposed.Model?.Contains('\0') == true)
		{ throw new TaxonomyException(400, "Valid category assistance settings are required."); }
		// Only the supported, pinned adapter selection can be configured. Readiness is a later dispatch gate.
		if (proposed.Enabled && (proposed.Provider != "jev" || proposed.Model != "jev-1.14.0"))
		{ throw new TaxonomyException(400, "Select the supported category assistance provider and model."); }
		row.Enabled = proposed.Enabled; row.Provider = proposed.Provider; row.Model = proposed.Model;
		row.DailyLimit = proposed.DailyLimit; row.PendingLimit = proposed.PendingLimit; row.Revision = proposed.Revision;
		row.AutoApply = proposed.AutoApply;
		// Results from the old revision can no longer be decided or written automatically. Retire them so they stop
		// holding pending slots; a dispatch already started completes as stale on its own.
		if (decisionChange)
		{
			foreach (var attempt in await db.CategoryAssistanceAttempts
						 .Where(x => x.State == "queued" || x.State == "suggested" || x.State == "abstained")
						 .ToListAsync(ct))
			{
				attempt.State = "stale";
				attempt.ClaimToken = null;
				attempt.LeaseExpiresAt = null;
				attempt.NextEligibleAt = null;
				attempt.CompletedAt = now;
			}
		}
		db.CategoryAssistanceSettingsAudits.Add(new()
		{
			Id = Guid.NewGuid(),
			Actor = actor,
			PreviousRevision = before.Revision,
			Revision = proposed.Revision,
			Before = JsonSerializer.Serialize(before),
			After = JsonSerializer.Serialize(proposed),
			RecordedAt = now
		});
		await db.SaveChangesAsync(ct);
		return proposed;
	}
	internal static CategoryAssistanceSettings Read(CategoryAssistanceSettingsEntity row) =>
		new(row.Enabled, row.Provider, row.Model, row.DailyLimit, row.PendingLimit, row.Revision, row.AutoApply);
	internal static void RequireTransaction(ApplicationDbContext db)
	{
		if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction == null)
		{ throw new InvalidOperationException("Category assistance persistence requires the caller's PostgreSQL transaction."); }
	}
}

/// <summary>Called after authoritative receipt/classification save, before commit. Never contacts a provider.</summary>
public static class CategoryAssistanceObservationWriter
{
	internal static async Task RecordAggregateAsync(ApplicationDbContext db, IEnumerable<ReceiptItemEntity> items,
		Guid? clientRequestId, CancellationToken ct)
	{
		if (!db.Database.IsNpgsql() || db.ReceiptWriteActor is not string actor) { return; }
		Guid saveId = clientRequestId ?? Guid.NewGuid();
		foreach (var item in items.OrderBy(x => x.Id))
		{
			await RecordNoChoiceSaveAsync(db, actor, saveId, item.Id, item.Classification != null, false, ct);
		}
	}

	public static async Task<bool> RecordNoChoiceSaveAsync(ApplicationDbContext db, string actor, Guid saveId,
		Guid itemId, bool explicitClassificationIntent, bool excluded, CancellationToken ct = default)
	{
		CategoryAssistanceSettingsStore.RequireTransaction(db);
		if (saveId == Guid.Empty || explicitClassificationIntent || excluded) { return false; }
		// Disabled assistance must not add write-lock contention to ordinary saves.
		// Consent may change after this read, so enabled saves recheck under the locks below.
		if (!await db.CategoryAssistanceSettings.AsNoTracking().AnyAsync(x => x.Id == 1 && x.Enabled, ct)) { return false; }
		// Same first write lock as classification mutations; enrollment captures a coherent assignment fence.
		var taxonomy = await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\"=1 FOR UPDATE").AsNoTracking().SingleAsync(ct);
		var settings = await db.CategoryAssistanceSettings.FromSqlRaw("SELECT * FROM category_assistance.\"Settings\" WHERE \"Id\"=1 FOR UPDATE").AsNoTracking().SingleAsync(ct);
		DateTimeOffset now = TimeProvider.System.GetUtcNow();
		if (!settings.Enabled || !await db.Users.AnyAsync(u => u.Id == actor && !(u.LockoutEnabled && u.LockoutEnd > now), ct)) { return false; }
		if (await db.CategoryAssistanceObservations.AnyAsync(x => x.SaveId == saveId && x.ReceiptItemId == itemId, ct)) { return false; }
		ReceiptItemEntity? item = await db.ReceiptItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == itemId, ct);
		if (item == null || item.DeletedAt != null || !string.IsNullOrWhiteSpace(item.Category) || !string.IsNullOrWhiteSpace(item.Subcategory)) { return false; }
		ReceiptEntity? receipt = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.ReceiptId, ct);
		if (receipt == null || receipt.DeletedAt != null) { return false; }
		var assignment = await (from current in db.CurrentClassifications
								join history in db.Classifications on current.AssignmentId equals history.Id
								where current.ReceiptItemId == itemId
								select history).AsNoTracking().SingleOrDefaultAsync(ct);
		if (assignment != null && !(assignment.CategoryId == null && (assignment.Source == "legacy_import"
			|| assignment.Source == "legacy_write" && assignment.LegacyResolution == "unresolved_path"))) { return false; }
		NormalizationSourceKey source;
		try { source = NormalizationCatalogPolicy.Normalize(new(receipt.Location, item.Description, item.ReceiptItemCode)); }
		catch (NormalizationCatalogException) { return false; }
		db.CategoryAssistanceObservations.Add(new()
		{
			SaveId = saveId,
			ReceiptItemId = itemId,
			ReceiptId = receipt.Id,
			Actor = actor,
			Location = source.Location,
			Description = source.Description,
			ItemCode = source.ItemCode,
			SourceHash = NormalizationCatalogPolicy.Hash(source),
			ItemRevision = item.NormalizationRevision,
			ReceiptRevision = receipt.NormalizationRevision,
			AssignmentId = assignment?.Id,
			SettingsRevision = settings.Revision,
			RestoreGeneration = settings.RestoreGeneration,
			TaxonomyRevision = taxonomy.RevisionId,
			RecordedAt = now
		});
		await db.SaveChangesAsync(ct);
		return true;
	}
}
