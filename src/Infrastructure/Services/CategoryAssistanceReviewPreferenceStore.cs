using System.Security.Cryptography;
using System.Text.Json;
using Application.Classification;
using Application.Taxonomy;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public sealed class CategoryAssistanceReviewPreferenceStore(
	IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock)
	: ICategoryAssistanceReviewPreferenceStore
{
	public async Task<CategoryAssistanceSavedReviewPage> ListSavedAsync(string actor, int offset, int limit, CancellationToken ct)
	{
		if (offset < 0 || limit is < 1 or > 100)
		{
			throw new TaxonomyException(400, "Offset must be nonnegative and limit must be 1 through 100.");
		}

		await using var db = await factory.CreateDbContextAsync(ct);
		await RequireActorAsync(db, actor, ct);
		var query = db.CategoryAssistanceReviewPreferences.AsNoTracking().Where(x => x.Actor == actor);
		var total = await query.CountAsync(ct);
		var rows = await query.OrderBy(x => x.Location).ThenBy(x => x.Description)
			.ThenBy(x => x.ItemCodeKey).ThenBy(x => x.AttemptId).Skip(offset).Take(limit).ToListAsync(ct);
		return new(total, offset, limit, rows.Select(x => new CategoryAssistanceSavedReview(x.AttemptId,
			x.Location, x.Description, x.ItemCodeKey, x.State, x.Revision, Excluded(x))).ToArray());
	}

	public async Task<CategoryAssistanceReviewPreference> UpdateSavedAsync(string actor, Guid attemptId,
		CategoryAssistanceReviewPreferenceRequest request, CancellationToken ct)
	{
		if (attemptId == Guid.Empty)
		{
			throw new TaxonomyException(400, "Saved decision ID is required.");
		}

		var (sorted, requestHash) = ValidateRequest(attemptId, request);
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		await RequireActorAsync(db, actor, ct);
		// Acceptance locks the attempt, so take the same lock when it still exists.
		await db.CategoryAssistanceAttempts.FromSqlInterpolated(
			$"SELECT * FROM category_assistance.\"Attempts\" WHERE \"Id\"={attemptId} FOR UPDATE").ToListAsync(ct);
		var previous = await db.CategoryAssistanceReviewOperations.AsNoTracking()
			.SingleOrDefaultAsync(x => x.Actor == actor && x.OperationId == request.OperationId, ct);
		if (previous != null)
		{
			if (previous.AttemptId != attemptId || previous.RequestHash != requestHash)
			{
				throw new TaxonomyException(409, "Operation ID was used with different review preferences.");
			}

			return JsonSerializer.Deserialize<CategoryAssistanceReviewPreference>(previous.ResultJson)!;
		}
		var row = await db.CategoryAssistanceReviewPreferences.SingleOrDefaultAsync(
			x => x.Actor == actor && x.AttemptId == attemptId, ct)
			?? throw new TaxonomyException(404, "Saved decision does not exist.");
		if (row.Revision != request.ExpectedRevision)
		{
			throw new TaxonomyException(409, "Review preferences changed. Refresh before updating.");
		}

		var existing = Excluded(row).ToHashSet();
		if (sorted.Any(x => !existing.Contains(x)))
		{
			throw new TaxonomyException(409, "Saved decisions can only retain or remove existing exclusions.");
		}

		row.State = request.State;
		row.ExcludedItemIdsJson = JsonSerializer.Serialize(sorted);
		row.Revision = Guid.NewGuid();
		var result = Project(attemptId, row);
		db.CategoryAssistanceReviewOperations.Add(new CategoryAssistanceReviewOperationEntity
		{
			Actor = actor,
			OperationId = request.OperationId,
			AttemptId = attemptId,
			RequestHash = requestHash,
			ResultJson = JsonSerializer.Serialize(result),
			RecordedAt = clock.GetUtcNow()
		});
		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);
		return result;
	}

	public async Task<CategoryAssistanceReviewPreference> GetAsync(string actor, Guid attemptId, CancellationToken ct)
	{
		await using var db = await factory.CreateDbContextAsync(ct);
		await RequireActorAndAttemptAsync(db, actor, attemptId, ct);
		var row = await db.CategoryAssistanceReviewPreferences.AsNoTracking()
			.SingleOrDefaultAsync(x => x.Actor == actor && x.AttemptId == attemptId, ct);
		return Project(attemptId, row);
	}

	public async Task<CategoryAssistanceReviewPreference> UpdateAsync(string actor, Guid attemptId,
		CategoryAssistanceReviewPreferenceRequest request, CancellationToken ct)
	{
		var (sorted, requestHash) = ValidateRequest(attemptId, request);
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);

		// Historical writers acquire this gate before reading preferences and hold it
		// through their writes. Discovery manifests are advisory and may remain stale after
		// a preference update. Always acquire this gate before the attempt row lock.
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		await RequireActorAsync(db, actor, ct);
		var previous = await db.CategoryAssistanceReviewOperations.AsNoTracking()
			.SingleOrDefaultAsync(x => x.Actor == actor && x.OperationId == request.OperationId, ct);
		if (previous != null)
		{
			if (previous.AttemptId != attemptId || previous.RequestHash != requestHash)
			{
				throw new TaxonomyException(409, "Operation ID was used with different review preferences.");
			}

			return JsonSerializer.Deserialize<CategoryAssistanceReviewPreference>(previous.ResultJson)!;
		}

		await RequireActorAndAttemptAsync(db, actor, attemptId, ct, true, true);
		var attempt = await db.CategoryAssistanceAttempts.AsNoTracking()
			.SingleAsync(x => x.Id == attemptId, ct);
		if (attempt.State is not ("suggested" or "abstained"))
		{
			throw new TaxonomyException(404, "Suggestion does not exist.");
		}

		var row = await db.CategoryAssistanceReviewPreferences
			.SingleOrDefaultAsync(x => x.Actor == actor && x.AttemptId == attemptId, ct);
		if ((row?.Revision ?? Guid.Empty) != request.ExpectedRevision)
		{
			throw new TaxonomyException(409, "Review preferences changed. Refresh before updating.");
		}

		if (sorted.Length > 0)
		{
			var eligible = await CategoryAssistanceDecisionStore.GetEligibleObservationsAsync(db, attempt, sorted, ct);
			if (eligible.Count != sorted.Length)
			{
				throw new TaxonomyException(409, "Excluded items are no longer eligible for this suggestion. Refresh before updating.");
			}
		}
		if (row == null)
		{
			row = new CategoryAssistanceReviewPreferenceEntity
			{
				Actor = actor,
				AttemptId = attemptId,
				Location = attempt.Location,
				Description = attempt.Description,
				ItemCodeKey = attempt.ItemCodeKey
			};
			db.CategoryAssistanceReviewPreferences.Add(row);
		}
		row.State = request.State;
		row.ExcludedItemIdsJson = JsonSerializer.Serialize(sorted);
		row.Revision = Guid.NewGuid();
		var result = Project(attemptId, row);
		db.CategoryAssistanceReviewOperations.Add(new CategoryAssistanceReviewOperationEntity
		{
			Actor = actor,
			OperationId = request.OperationId,
			AttemptId = attemptId,
			RequestHash = requestHash,
			ResultJson = JsonSerializer.Serialize(result),
			RecordedAt = clock.GetUtcNow()
		});
		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);
		return result;
	}
	private static (Guid[] Sorted, string Hash) ValidateRequest(Guid attemptId, CategoryAssistanceReviewPreferenceRequest request)
	{
		if (request.OperationId == Guid.Empty || request.ExcludedItemIds is null ||
			request.ExcludedItemIds.Count > 1000 || request.ExcludedItemIds.Any(x => x == Guid.Empty) ||
			request.ExcludedItemIds.Distinct().Count() != request.ExcludedItemIds.Count ||
			request.State is not ("active" or "later" or "left_as_is"))
		{
			throw new TaxonomyException(400, "Operation ID, review state and up to 1000 distinct receipt item IDs are required.");
		}

		var sorted = request.ExcludedItemIds.Order().ToArray();
		var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
			new { attemptId, request.ExpectedRevision, request.State, excludedItemIds = sorted })));
		return (sorted, requestHash);
	}

	internal static Guid[] Excluded(CategoryAssistanceReviewPreferenceEntity? row) =>
		row == null ? [] : JsonSerializer.Deserialize<Guid[]>(row.ExcludedItemIdsJson) ?? [];

	private static CategoryAssistanceReviewPreference Project(Guid attemptId,
		CategoryAssistanceReviewPreferenceEntity? row) =>
		new(attemptId, row?.State ?? "active", row?.Revision ?? Guid.Empty, Excluded(row));

	private async Task RequireActorAndAttemptAsync(ApplicationDbContext db, string actor,
		Guid attemptId, CancellationToken ct, bool lockAttempt = false, bool allowClosed = false)
	{
		await RequireActorAsync(db, actor, ct);
		if (attemptId == Guid.Empty)
		{
			throw new TaxonomyException(400, "Suggestion ID is required.");
		}

		var exists = lockAttempt
			? await db.CategoryAssistanceAttempts.FromSqlInterpolated(
				$"SELECT * FROM category_assistance.\"Attempts\" WHERE \"Id\"={attemptId} FOR UPDATE")
				.AnyAsync(x => allowClosed || x.State == "suggested" || x.State == "abstained", ct)
			: await db.CategoryAssistanceAttempts.AnyAsync(x => x.Id == attemptId &&
				(x.State == "suggested" || x.State == "abstained"), ct);
		if (!exists)
		{
			throw new TaxonomyException(404, "Suggestion does not exist.");
		}
	}

	private async Task RequireActorAsync(ApplicationDbContext db, string actor, CancellationToken ct)
	{
		var now = clock.GetUtcNow();
		if (string.IsNullOrWhiteSpace(actor) || !await db.Users.AnyAsync(
			x => x.Id == actor && !(x.LockoutEnabled && x.LockoutEnd > now), ct))
		{
			throw new TaxonomyException(403, "An active receipt writer is required.");
		}
	}
}
