using System.Text.Json;
using Application.Classification;
using Application.Normalization;
using Infrastructure.Entities.CategoryAssistance;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>Durable local queue. Every state transition takes the taxonomy writer lock first.</summary>
public sealed class CategoryAssistanceDispatchStore(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock)
	: ICategoryAssistanceDispatchStore
{
	private static readonly TimeSpan DispatchLease = TimeSpan.FromMinutes(2);

	public async Task<CategoryAssistanceQueuedWork?> ClaimQueuedAsync(CancellationToken ct)
	{
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		var head = await LockHeadAsync(db, ct);
		var settings = await LockSettingsAsync(db, ct);
		DateTimeOffset now = clock.GetUtcNow();
		var connection = await db.CategoryAssistanceConnection.AsNoTracking().SingleOrDefaultAsync(ct);
		bool bound = connection != null && !connection.Changing && connection.Generation != Guid.Empty
			&& connection.Provider == settings.Provider && connection.Model == settings.Model
			&& connection.ConfigurationFingerprint.Length == 64;
		await ExpireAsync(db, now, ct);
		var staleQueued = await db.CategoryAssistanceAttempts.Where(x => x.State == "queued"
			&& (x.RestoreGeneration != settings.RestoreGeneration || x.SettingsRevision != settings.Revision
				|| x.TaxonomyRevision != head.RevisionId || !bound
				|| x.ConnectionGeneration != connection!.Generation)).ToListAsync(ct);
		foreach (var stale in staleQueued)
		{ stale.State = "stale"; stale.CompletedAt = now; stale.ClaimToken = null; stale.LeaseExpiresAt = null; }
		await db.SaveChangesAsync(ct);
		if (!settings.Enabled || !bound || string.IsNullOrWhiteSpace(settings.Provider) || string.IsNullOrWhiteSpace(settings.Model))
		{ await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null; }

		// Attach new save markers to an already known source identity even when its result is terminal.
		var markers = await db.CategoryAssistanceObservations.Where(x => x.AttemptId == null && !x.DispatchSkipped
						&& (x.Evidence == "post_consent_no_choice_save" || x.Evidence == "explicit_history_request")
			&& x.RestoreGeneration == settings.RestoreGeneration && x.TaxonomyRevision == head.RevisionId
			&& x.SettingsRevision == settings.Revision)
			.OrderBy(x => x.RecordedAt).Take(64).ToListAsync(ct);
		foreach (var marker in markers)
		{
			Guid requestScope = marker.Evidence == "explicit_history_request" ? marker.SaveId : Guid.Empty;
			var existing = db.CategoryAssistanceAttempts.Local.FirstOrDefault(x =>
								x.RequestScope == requestScope && x.RestoreGeneration == marker.RestoreGeneration && x.SourceHash == marker.SourceHash
				&& x.Location == marker.Location && x.Description == marker.Description
				&& x.ItemCodeKey == (marker.ItemCode ?? "") && x.TaxonomyRevision == marker.TaxonomyRevision
				&& x.Provider == settings.Provider && x.Model == settings.Model
				&& x.ConnectionGeneration == connection!.Generation
				&& x.QuestionPolicyVersion == CategorySuggestionPolicy.Version)
				?? await db.CategoryAssistanceAttempts.SingleOrDefaultAsync(x =>
								x.RequestScope == requestScope && x.RestoreGeneration == marker.RestoreGeneration && x.SourceHash == marker.SourceHash
				&& x.Location == marker.Location && x.Description == marker.Description
				&& x.ItemCodeKey == (marker.ItemCode ?? "") && x.TaxonomyRevision == marker.TaxonomyRevision
				&& x.Provider == settings.Provider && x.Model == settings.Model
				&& x.ConnectionGeneration == connection!.Generation
				&& x.QuestionPolicyVersion == CategorySuggestionPolicy.Version, ct);
			if (existing != null) { marker.AttemptId = existing.Id; continue; }
			bool collision = db.CategoryAssistanceAttempts.Local.Any(x => x.RequestScope == requestScope && x.RestoreGeneration == marker.RestoreGeneration
	&& x.SourceHash == marker.SourceHash && x.TaxonomyRevision == marker.TaxonomyRevision
	&& x.Provider == settings.Provider && x.Model == settings.Model
	&& x.ConnectionGeneration == connection!.Generation
	&& x.QuestionPolicyVersion == CategorySuggestionPolicy.Version)
					|| await db.CategoryAssistanceAttempts.AnyAsync(x => x.RequestScope == requestScope && x.RestoreGeneration == marker.RestoreGeneration
		&& x.SourceHash == marker.SourceHash && x.TaxonomyRevision == marker.TaxonomyRevision
		&& x.Provider == settings.Provider && x.Model == settings.Model
		&& x.ConnectionGeneration == connection!.Generation
		&& x.QuestionPolicyVersion == CategorySuggestionPolicy.Version, ct);
			if (collision) { marker.DispatchSkipped = true; continue; }
			if (await PendingCountAsync(db, ct) + db.CategoryAssistanceAttempts.Local.Count(x => db.Entry(x).State == EntityState.Added) >= settings.PendingLimit)
			{
				break;
			}

			if (!await EligibleAsync(db, marker, head.RevisionId, ct)) { marker.DispatchSkipped = true; continue; }
			var request = await BuildRequestAsync(db, marker.Description, head.RevisionId, ct);
			bool invalidRequest = request == null || !CategorySuggestionPolicy.IsValid(request);
			var attempt = new CategoryAssistanceAttemptEntity
			{
				Id = Guid.NewGuid(),
				RequestId = Guid.NewGuid(),
				RequestScope = requestScope,
				RestoreGeneration = marker.RestoreGeneration,
				SourceHash = marker.SourceHash,
				Location = marker.Location,
				Description = marker.Description,
				ItemCodeKey = marker.ItemCode ?? "",
				SourceRevision = marker.ItemRevision,
				TaxonomyRevision = marker.TaxonomyRevision,
				Provider = settings.Provider!,
				Model = settings.Model!,
				QuestionPolicyVersion = CategorySuggestionPolicy.Version,
				SettingsRevision = settings.Revision,
				ConnectionGeneration = connection!.Generation,
				ReceiptId = marker.ReceiptId,
				ReceiptRevision = marker.ReceiptRevision,
				ItemId = marker.ReceiptItemId,
				ItemRevision = marker.ItemRevision,
				AssignmentId = marker.AssignmentId,
				RequestJson = invalidRequest ? "" : JsonSerializer.Serialize(request),
				State = invalidRequest ? "failed" : "queued",
				ResultJson = invalidRequest ? JsonSerializer.Serialize(
										new CategoryAssistanceCompletion(CategoryAssistanceAttemptOutcome.Failed,
												Failure: SuggestionFailure.InvalidInput)) : null,
				CompletedAt = invalidRequest ? now : null,
				CreatedAt = now
			};
			db.CategoryAssistanceAttempts.Add(attempt);
			marker.AttemptId = attempt.Id;
		}
		await db.SaveChangesAsync(ct);
		var queued = await db.CategoryAssistanceAttempts.Where(x => x.State == "queued"
			&& x.RestoreGeneration == settings.RestoreGeneration && x.SettingsRevision == settings.Revision
			&& x.ConnectionGeneration == connection!.Generation
			&& (x.NextEligibleAt == null || x.NextEligibleAt <= now)
			&& (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now))
			.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
		if (queued != null)
		{
			queued.ClaimToken = Guid.NewGuid();
			queued.LeaseExpiresAt = now + CategoryAssistanceQueuePolicy.QueuedClaimLease;
			await db.SaveChangesAsync(ct);
		}
		await tx.CommitAsync(ct);
		return queued == null ? null : ToWork(queued);
	}

	public async Task DeferQueuedAsync(CategoryAssistanceQueuedWork work, CategoryAssistanceQueuedDeferral reason,
		DateTimeOffset now, CancellationToken ct)
	{
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		var head = await LockHeadAsync(db, ct);
		var settings = await LockSettingsAsync(db, ct);
		var row = await db.CategoryAssistanceAttempts.SingleOrDefaultAsync(x => x.Id == work.AttemptId
			&& x.State == "queued" && x.ClaimToken == work.QueueClaimToken, ct);
		if (row != null)
		{
			row.Deferrals++;
			bool current = await CurrentAsync(db, row, head.RevisionId, settings, ct);
			if (!current || row.Deferrals >= CategoryAssistanceQueuePolicy.MaximumDeferrals)
			{ row.State = "stale"; row.CompletedAt = now; row.NextEligibleAt = null; }
			else { row.NextEligibleAt = now + CategoryAssistanceQueuePolicy.Backoff(row.Deferrals); }
			row.ClaimToken = null; row.LeaseExpiresAt = null;
			await db.SaveChangesAsync(ct);
		}
		await tx.CommitAsync(ct);
	}

	public async Task<CategoryAssistanceDispatchClaim?> TryStartDispatchAsync(CategoryAssistanceQueuedWork work,
		CategoryAssistanceCapability capability, DateTimeOffset now, CancellationToken ct)
	{
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		var head = await LockHeadAsync(db, ct);
		var settings = await LockSettingsAsync(db, ct);
		await ExpireAsync(db, now, ct);
		var row = await db.CategoryAssistanceAttempts.SingleOrDefaultAsync(x => x.Id == work.AttemptId
			&& x.State == "queued" && x.ClaimToken == work.QueueClaimToken && x.LeaseExpiresAt > now, ct);
		if (row == null || !CategoryAssistancePolicy.CanSuggest(CategoryAssistanceSettingsStore.Read(settings), capability)
			|| !await CurrentAsync(db, row, head.RevisionId, settings, ct)
			|| await PendingCountAsync(db, ct) > settings.PendingLimit)
		{ await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null; }
		DateOnly day = DateOnly.FromDateTime(now.UtcDateTime);
		var usage = await db.CategoryAssistanceDailyUsage.SingleOrDefaultAsync(x => x.UtcDay == day, ct);
		if (usage == null) { usage = new() { UtcDay = day }; db.CategoryAssistanceDailyUsage.Add(usage); }
		if (usage.DispatchAttempts >= settings.DailyLimit)
		{ await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null; }
		usage.DispatchAttempts++;
		row.State = "dispatching"; row.ClaimToken = Guid.NewGuid();
		row.LeaseExpiresAt = now + DispatchLease; row.DebitUtcDay = day;
		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);
		return new(ToWork(row), row.ClaimToken.Value);
	}

	public async Task CompleteAsync(CategoryAssistanceDispatchClaim claim, CategoryAssistanceCompletion completion,
		CategoryAssistanceCapability capability, CancellationToken ct)
	{
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		var head = await LockHeadAsync(db, ct);
		var settings = await LockSettingsAsync(db, ct);
		DateTimeOffset now = clock.GetUtcNow();
		var row = await db.CategoryAssistanceAttempts.SingleOrDefaultAsync(x => x.Id == claim.Work.AttemptId
			&& x.State == "dispatching" && x.ClaimToken == claim.ClaimToken, ct);
		if (row != null)
		{
			bool current = row.LeaseExpiresAt > now
				&& CategoryAssistancePolicy.CanSuggest(CategoryAssistanceSettingsStore.Read(settings), capability)
				&& await CurrentAsync(db, row, head.RevisionId, settings, ct);
			row.State = current ? completion.Outcome.ToString().ToLowerInvariant() : "stale";
			row.ResultJson = current ? JsonSerializer.Serialize(completion) : null;
			row.CompletedAt = now; row.ClaimToken = null; row.LeaseExpiresAt = null;
			await db.SaveChangesAsync(ct);
		}
		await tx.CommitAsync(ct);
	}

	public async Task RejectQueuedAsync(CategoryAssistanceQueuedWork work, SuggestionFailure failure, CancellationToken ct)
	{
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		await LockHeadAsync(db, ct);
		var row = await db.CategoryAssistanceAttempts.SingleOrDefaultAsync(x => x.Id == work.AttemptId
			&& x.State == "queued" && x.ClaimToken == work.QueueClaimToken, ct);
		if (row != null)
		{
			row.State = "failed"; row.ResultJson = JsonSerializer.Serialize(failure);
			row.CompletedAt = clock.GetUtcNow(); row.ClaimToken = null; row.LeaseExpiresAt = null;
			await db.SaveChangesAsync(ct);
		}
		await tx.CommitAsync(ct);
	}

	private static async Task<Infrastructure.Entities.Taxonomy.TaxonomyHeadEntity> LockHeadAsync(ApplicationDbContext db, CancellationToken ct)
		=> await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
	private static async Task<CategoryAssistanceSettingsEntity> LockSettingsAsync(ApplicationDbContext db, CancellationToken ct)
		=> await db.CategoryAssistanceSettings.FromSqlRaw("SELECT * FROM category_assistance.\"Settings\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
	private static Task<int> PendingCountAsync(ApplicationDbContext db, CancellationToken ct)
				=> db.CategoryAssistanceAttempts.CountAsync(x => x.State == "queued" || x.State == "dispatching"
						|| x.State == "suggested" || x.State == "abstained", ct);
	private static async Task ExpireAsync(ApplicationDbContext db, DateTimeOffset now, CancellationToken ct)
	{
		var expired = await db.CategoryAssistanceAttempts.Where(x => x.State == "dispatching" && x.LeaseExpiresAt <= now).ToListAsync(ct);
		foreach (var row in expired)
		{ row.State = "indeterminate"; row.CompletedAt = now; row.ClaimToken = null; row.LeaseExpiresAt = null; }
		// Queued leases are unbilled; clear the token so the same attempt can be reclaimed.
		var queued = await db.CategoryAssistanceAttempts.Where(x => x.State == "queued" && x.LeaseExpiresAt <= now).ToListAsync(ct);
		foreach (var row in queued)
		{
			row.Deferrals++;
			if (row.Deferrals >= CategoryAssistanceQueuePolicy.MaximumDeferrals)
			{ row.State = "stale"; row.CompletedAt = now; }
			else { row.NextEligibleAt = now + CategoryAssistanceQueuePolicy.Backoff(row.Deferrals); }
			row.ClaimToken = null; row.LeaseExpiresAt = null;
		}
		await db.SaveChangesAsync(ct);
	}

	private static async Task<bool> EligibleAsync(ApplicationDbContext db, CategoryAssistanceObservationEntity marker,
		Guid taxonomyRevision, CancellationToken ct)
	{
		var item = await db.ReceiptItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == marker.ReceiptItemId, ct);
		var receipt = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == marker.ReceiptId, ct);
		if (marker.Evidence is not ("post_consent_no_choice_save" or "explicit_history_request") || item == null || receipt == null || item.ReceiptId != receipt.Id || item.DeletedAt != null || receipt.DeletedAt != null
	|| item.NormalizationRevision != marker.ItemRevision || receipt.NormalizationRevision != marker.ReceiptRevision
				|| marker.Evidence == "post_consent_no_choice_save" && (!string.IsNullOrWhiteSpace(item.Category) || !string.IsNullOrWhiteSpace(item.Subcategory)))
		{
			return false;
		}

		NormalizationSourceKey source;
		try { source = NormalizationCatalogPolicy.Normalize(new(receipt.Location, item.Description, item.ReceiptItemCode)); }
		catch (NormalizationCatalogException) { return false; }
		if (source.Location != marker.Location || source.Description != marker.Description || source.ItemCode != marker.ItemCode
			|| NormalizationCatalogPolicy.Hash(source) != marker.SourceHash)
		{
			return false;
		}

		var assignment = await (from current in db.CurrentClassifications.AsNoTracking()
								join history in db.Classifications.AsNoTracking() on current.AssignmentId equals history.Id
								where current.ReceiptItemId == item.Id
								select history).SingleOrDefaultAsync(ct);
		if (assignment?.Id != marker.AssignmentId || assignment != null && marker.Evidence != "explicit_history_request" && !(assignment.CategoryId == null
				&& (assignment.Source == "legacy_import" || assignment.Source == "legacy_write" && assignment.LegacyResolution == "unresolved_path")
				))
		{
			return false;
		}

		var mapping = await (from h in db.CategoryMappingHeads.AsNoTracking()
							 join r in db.CategoryMappingRevisions.AsNoTracking() on h.RevisionId equals r.Id
							 where h.SourceFingerprint == marker.SourceHash && r.Location == marker.Location
								 && r.Description == marker.Description && r.ItemCode == marker.ItemCode
							 select r).SingleOrDefaultAsync(ct);
		return mapping?.CategoryId == null;
	}

	private static async Task<bool> CurrentAsync(ApplicationDbContext db, CategoryAssistanceAttemptEntity row,
		Guid taxonomyRevision, CategoryAssistanceSettingsEntity settings, CancellationToken ct)
	{
		if (!settings.Enabled || settings.Revision != row.SettingsRevision || settings.RestoreGeneration != row.RestoreGeneration
			|| settings.Provider != row.Provider || settings.Model != row.Model || taxonomyRevision != row.TaxonomyRevision)
		{
			return false;
		}

		var connection = await db.CategoryAssistanceConnection.AsNoTracking().SingleOrDefaultAsync(ct);
		if (connection == null || connection.Changing || connection.Generation == Guid.Empty
			|| connection.Generation != row.ConnectionGeneration || connection.Provider != row.Provider
			|| connection.Model != row.Model || connection.ConfigurationFingerprint.Length != 64)
		{
			return false;
		}

		var marker = await db.CategoryAssistanceObservations.AsNoTracking().Where(x => x.AttemptId == row.Id
				&& (row.RequestScope == Guid.Empty || x.SaveId == row.RequestScope)
	&& x.ReceiptItemId == row.ItemId && x.ItemRevision == row.ItemRevision)
	.OrderByDescending(x => x.RecordedAt).FirstOrDefaultAsync(ct);
		if (marker == null ||
			(row.RequestScope == Guid.Empty && marker.Evidence != "post_consent_no_choice_save") ||
			(row.RequestScope != Guid.Empty && marker.Evidence != "explicit_history_request") ||
			marker.ReceiptRevision != row.ReceiptRevision || marker.AssignmentId != row.AssignmentId
	|| marker.SourceHash != row.SourceHash || marker.ItemRevision != row.SourceRevision
	|| !await EligibleAsync(db, marker, taxonomyRevision, ct))
		{
			return false;
		}

		var request = await BuildRequestAsync(db, marker.Description, taxonomyRevision, ct);
		return request != null && JsonSerializer.Serialize(request) == row.RequestJson;
	}

	private static async Task<CategorySuggestionRequest?> BuildRequestAsync(ApplicationDbContext db, string description,
		Guid revision, CancellationToken ct)
	{
		var nodes = await db.TaxonomyNodes.AsNoTracking().Where(x => x.RevisionId == revision && !x.Archived)
			.OrderBy(x => x.CategoryId).ToListAsync(ct);
		if (nodes.Count == 0)
		{
			return null;
		}

		var ids = nodes.Select(x => x.CategoryId).ToHashSet();
		if (nodes.Any(x => x.ParentId != null && !ids.Contains(x.ParentId.Value)))
		{
			return null;
		}

		return new(description, revision.ToString("D"), nodes.Select(x => new SuggestionCategory(
						x.CategoryId.ToString("D"), x.Name, string.IsNullOrWhiteSpace(x.Definition) ? x.Name : x.Definition, x.ParentId?.ToString("D"))).ToArray());
	}

	private static CategoryAssistanceQueuedWork ToWork(CategoryAssistanceAttemptEntity row)
	{
		var key = new NormalizationSourceKey(row.Location, row.Description, row.ItemCodeKey == "" ? null : row.ItemCodeKey);
		var source = new CategoryAssistanceSource(key, row.SourceHash, row.SourceRevision);
		var fence = new CategoryAssistanceFence(row.SettingsRevision, row.ConnectionGeneration,
			row.RestoreGeneration, row.SourceHash, row.SourceRevision, row.TaxonomyRevision, row.AssignmentId);
		return new(row.Id, row.ClaimToken ?? Guid.Empty, row.RequestId, row.ReceiptId, row.ReceiptRevision,
			row.ItemId, row.ItemRevision, source, fence, row.Provider, row.Model, row.QuestionPolicyVersion,
			JsonSerializer.Deserialize<CategorySuggestionRequest>(row.RequestJson)!);
	}
}
