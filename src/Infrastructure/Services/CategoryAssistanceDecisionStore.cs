using System.Security.Cryptography;
using System.Text.Json;
using Application.Classification;
using Application.Normalization;
using Application.Taxonomy;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Entities.Core;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public sealed class CategoryAssistanceDecisionStore(IDbContextFactory<ApplicationDbContext> factory,
	ICategoryAssistanceCapabilitySource capabilitySource, IOptions<ClassificationOptions> classificationOptions,
	TimeProvider clock) : ICategoryAssistanceDecisionStore
{
	private sealed record SavedDecision(string RequestHash, CategoryAssistanceDecisionResult Result);

	public async Task<CategoryAssistanceEligibleItemPage> ListEligibleItemsAsync(Guid attemptId, int offset,
		int limit, CancellationToken ct)
		=> await ListEligibleItemsAsync("", attemptId, offset, limit, ct);

	public async Task<CategoryAssistanceEligibleItemPage> ListEligibleItemsAsync(string actor, Guid attemptId,
		int offset, int limit, CancellationToken ct)
	{
		if (offset < 0 || limit is < 1 or > 100)
		{
			throw new TaxonomyException(400, "Offset must be nonnegative and limit must be 1 through 100.");
		}

		await using var db = await factory.CreateDbContextAsync(ct);
		var attempt = await db.CategoryAssistanceAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attemptId, ct)
			?? throw new TaxonomyException(404, "Suggestion does not exist.");
		var eligible = await GetEligibleObservationsAsync(db, attempt, null, ct);
		if (!string.IsNullOrEmpty(actor))
		{
			var preference = await db.CategoryAssistanceReviewPreferences.AsNoTracking()
				.SingleOrDefaultAsync(x => x.Actor == actor && x.AttemptId == attemptId, ct);
			var excluded = CategoryAssistanceReviewPreferenceStore.Excluded(preference).ToHashSet();
			eligible.RemoveAll(x => excluded.Contains(x.ReceiptItemId));
		}
		var markers = eligible.OrderBy(x => x.ReceiptItemId).Skip(offset).Take(limit).ToList();
		var ids = markers.Select(x => x.ReceiptItemId).ToList();
		var details = await (from item in db.ReceiptItems.AsNoTracking()
							 join receipt in db.Receipts.AsNoTracking() on item.ReceiptId equals receipt.Id
							 where ids.Contains(item.Id)
							 select new
							 {
								 item.Id,
								 item.ReceiptId,
								 receipt.Date,
								 item.Description,
								 receipt.Location,
								 item.Quantity,
								 item.TotalAmount,
								 item.TotalAmountCurrency,
								 item.NormalizationRevision,
								 ReceiptRevision = receipt.NormalizationRevision
							 })
				.ToListAsync(ct);
		var byId = details.ToDictionary(x => x.Id);
		var items = markers.Where(x => byId.TryGetValue(x.ReceiptItemId, out var detail) &&
									  detail.ReceiptId == x.ReceiptId &&
									  detail.NormalizationRevision == x.ItemRevision &&
									  detail.ReceiptRevision == x.ReceiptRevision)
				.Select(x => byId[x.ReceiptItemId])
				.Select(x => new CategoryAssistanceEligibleItem(x.Id, x.ReceiptId, x.Date,
						x.Description, x.Location, x.Quantity, x.TotalAmount,
						x.TotalAmountCurrency.ToString()))
				.ToList();
		return new(eligible.Count, offset, limit, items.Select(x => x.Id).ToList(), items);
	}

	public Task<CategoryAssistanceDecisionResult> AcceptAsync(string actor, Guid attemptId,
		CategoryAssistanceDecisionRequest request, CancellationToken ct)
	{
		if (request.OperationId == Guid.Empty || request.ExpectedTaxonomyRevision == Guid.Empty ||
			request.ItemIds is null || request.ItemIds.Count is < 1 or > 100 ||
			request.ItemIds.Any(x => x == Guid.Empty) || request.ItemIds.Distinct().Count() != request.ItemIds.Count)
		{
			throw new TaxonomyException(400, "A nonempty operation, taxonomy revision and 1–100 distinct item IDs are required.");
		}

		return DecideAsync(actor, attemptId, "accepted", request.OperationId,
			request.ExpectedTaxonomyRevision, request.ItemIds.Order().ToArray(), request.CategoryId, ct);
	}

	public Task<CategoryAssistanceDecisionResult> DismissAsync(string actor, Guid attemptId,
		Guid operationId, CancellationToken ct)
	{
		if (operationId == Guid.Empty)
		{
			throw new TaxonomyException(400, "Operation ID is required.");
		}

		return DecideAsync(actor, attemptId, "dismissed", operationId, null, [], null, ct);
	}

	private async Task<CategoryAssistanceDecisionResult> DecideAsync(string actor, Guid attemptId, string decision,
		Guid operationId, Guid? expectedTaxonomy, Guid[] itemIds, Guid? target, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(actor) || attemptId == Guid.Empty)
		{
			throw new TaxonomyException(400, "Actor and suggestion ID are required.");
		}

		var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
			new { attemptId, decision, operationId, expectedTaxonomy, itemIds, target })));
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		var head = await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
		var settings = await db.CategoryAssistanceSettings.FromSqlRaw(
			"SELECT * FROM category_assistance.\"Settings\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
		var connection = await db.CategoryAssistanceConnection.FromSqlRaw(
			"SELECT * FROM category_assistance.\"Connection\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
		var attempt = await db.CategoryAssistanceAttempts.FromSqlInterpolated(
			$"SELECT * FROM category_assistance.\"Attempts\" WHERE \"Id\"={attemptId} FOR UPDATE")
			.SingleOrDefaultAsync(ct) ?? throw new TaxonomyException(404, "Suggestion does not exist.");
		if (attempt.DecisionOperationId.HasValue)
		{
			var saved = JsonSerializer.Deserialize<SavedDecision>(attempt.DecisionJson ?? "")
				?? throw new TaxonomyException(409, "Suggestion decision cannot be replayed.");
			if (attempt.DecisionActor != actor || attempt.DecisionOperationId != operationId || saved.RequestHash != requestHash)
			{
				throw new TaxonomyException(409, "Suggestion has a different terminal decision.");
			}

			await tx.CommitAsync(ct);
			return saved.Result;
		}
		if (await db.CategoryAssistanceAttempts.AnyAsync(x => x.DecisionActor == actor &&
			x.DecisionOperationId == operationId && x.Id != attemptId, ct))
		{
			throw new TaxonomyException(409, "Operation ID was used for another suggestion.");
		}

		var now = clock.GetUtcNow();
		if (!await db.Users.AnyAsync(x => x.Id == actor && !(x.LockoutEnabled && x.LockoutEnd > now), ct))
		{
			throw new TaxonomyException(403, "An active receipt writer is required.");
		}

		var capability = await capabilitySource.GetAsync(ct);
		if (!CategoryAssistancePolicy.CanSuggest(CategoryAssistanceSettingsStore.Read(settings), capability) ||
			!settings.Enabled || settings.Revision != attempt.SettingsRevision ||
			settings.RestoreGeneration != attempt.RestoreGeneration ||
			settings.Provider != attempt.Provider || settings.Model != attempt.Model ||
			connection.Changing || connection.Generation != attempt.ConnectionGeneration ||
			connection.Provider != attempt.Provider || connection.Model != attempt.Model ||
			connection.ConfigurationFingerprint.Length != 64 ||
			attempt.QuestionPolicyVersion != CategorySuggestionPolicy.Version ||
			attempt.SourceRevision != attempt.ItemRevision ||
			head.RevisionId != attempt.TaxonomyRevision ||
			(decision == "accepted" && head.RevisionId != expectedTaxonomy))
		{
			throw new TaxonomyException(409, "Suggestion context changed. Refresh before deciding.");
		}

		if (attempt.State is not ("suggested" or "abstained"))
		{
			throw new TaxonomyException(409, "Suggestion is no longer available for review.");
		}

		Guid? category = null;
		if (decision == "accepted")
		{
			var preference = await db.CategoryAssistanceReviewPreferences.AsNoTracking()
				.SingleOrDefaultAsync(x => x.Actor == actor && x.AttemptId == attemptId, ct);
			var excluded = CategoryAssistanceReviewPreferenceStore.Excluded(preference).ToHashSet();
			if (itemIds.Any(excluded.Contains))
			{
				throw new TaxonomyException(409, "Selected items are excluded from your category assistance review.");
			}

			CategoryAssistanceCompletion completion;
			try { completion = JsonSerializer.Deserialize<CategoryAssistanceCompletion>(attempt.ResultJson ?? "")!; }
			catch (JsonException) { throw new TaxonomyException(409, "Suggestion result is unavailable."); }
			var suggestion = completion?.Suggestion;
			if (suggestion == null)
			{
				throw new TaxonomyException(409, "Suggestion result is unavailable.");
			}

			if (target.HasValue)
			{
				category = target;
			}
			else if (attempt.State == "suggested" && Guid.TryParse(suggestion.CategoryId, out var suggested))
			{
				category = suggested;
			}

			if (category is null)
			{
				throw new TaxonomyException(400, "Choose a category to accept an abstention.");
			}

			if (!await db.TaxonomyNodes.AnyAsync(x => x.RevisionId == head.RevisionId &&
				x.CategoryId == category && !x.Archived, ct))
			{
				throw new TaxonomyException(409, "Selected category is no longer available.");
			}

			if (attempt.State == "suggested" && target is null)
			{
				await ValidateSuggestedPathAsync(db, head.RevisionId, suggestion.Path, category.Value, ct);
			}

			var selected = await GetEligibleObservationsAsync(db, attempt, itemIds, ct);
			if (selected.Count != itemIds.Length)
			{
				throw new TaxonomyException(409, "Selected items are no longer eligible. Refresh before accepting.");
			}

			await ReceiptFactLock.AcquireAsync(db, selected.Select(x => x.ReceiptId), ct);
			foreach (var marker in selected)
			{
				if (!await IsEligibleAsync(db, attempt, marker, ct))
				{
					throw new TaxonomyException(409, "Selected items are no longer eligible. Refresh before accepting.");
				}
			}

			foreach (var marker in selected)
			{
				var itemId = marker.ReceiptItemId;
				var perItemOperation = new Guid(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
					new { attemptId, operationId, itemId })).AsSpan(0, 16));
				await ClassificationService.AssignInTransactionAsync(db, actor, itemId,
					new ClassificationCommand(category, head.RevisionId, marker.AssignmentId, perItemOperation,
						$"Accepted category assistance suggestion {attemptId:D}."),
					classificationOptions.Value.WritesEnabled, clock, ct);
			}
		}
		var result = new CategoryAssistanceDecisionResult(attemptId, decision, itemIds, category);
		attempt.State = decision;
		attempt.DecisionActor = actor;
		attempt.DecisionOperationId = operationId;
		attempt.DecisionJson = JsonSerializer.Serialize(new SavedDecision(requestHash, result));
		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);
		return result;
	}

	internal static async Task<List<CategoryAssistanceObservationEntity>> GetEligibleObservationsAsync(
		ApplicationDbContext db, CategoryAssistanceAttemptEntity attempt, Guid[]? itemIds, CancellationToken ct)
	{
		var query = db.CategoryAssistanceObservations.AsNoTracking().Where(x => x.AttemptId == attempt.Id &&
			(attempt.RequestScope == Guid.Empty || x.SaveId == attempt.RequestScope));
		if (itemIds != null)
		{
			query = query.Where(x => itemIds.Contains(x.ReceiptItemId));
		}
		// Select the newest observation before testing eligibility. Filtering first would
		// resurrect an older eligible observation after a newer ineligible save.
		var observations = await query.OrderByDescending(x => x.RecordedAt)
			.ThenByDescending(x => x.SaveId).ToListAsync(ct);
		var latest = observations.GroupBy(x => x.ReceiptItemId).Select(x => x.First());
		var eligible = new List<CategoryAssistanceObservationEntity>();
		foreach (var marker in latest)
		{
			if (await IsEligibleAsync(db, attempt, marker, ct))
			{
				eligible.Add(marker);
			}
		}

		return eligible;
	}

	private static async Task ValidateSuggestedPathAsync(ApplicationDbContext db, Guid revision,
		IReadOnlyList<string>? proposedPath, Guid category, CancellationToken ct)
	{
		if (proposedPath is null || proposedPath.Count == 0 ||
			!Guid.TryParse(proposedPath[^1], out var leaf) || leaf != category)
		{
			throw new TaxonomyException(409, "Suggestion path is invalid.");
		}

		var path = new List<Guid>(proposedPath.Count);
		foreach (var segment in proposedPath)
		{
			if (!Guid.TryParse(segment, out var id) || path.Contains(id))
			{
				throw new TaxonomyException(409, "Suggestion path is invalid.");
			}

			path.Add(id);
		}
		var nodes = await db.TaxonomyNodes.AsNoTracking()
			.Where(x => x.RevisionId == revision && path.Contains(x.CategoryId))
			.Select(x => new { x.CategoryId, x.ParentId, x.Archived }).ToListAsync(ct);
		if (nodes.Count != path.Count || nodes.Any(x => x.Archived) ||
			path.Select((id, index) => new { id, parent = index == 0 ? (Guid?)null : path[index - 1] })
				.Any(x => !nodes.Any(n => n.CategoryId == x.id && n.ParentId == x.parent)))
		{
			throw new TaxonomyException(409, "Suggestion path is invalid.");
		}
	}

	internal static async Task<bool> IsEligibleAsync(ApplicationDbContext db,
		CategoryAssistanceAttemptEntity attempt, CategoryAssistanceObservationEntity marker, CancellationToken ct)
	{
		if ((attempt.RequestScope == Guid.Empty && marker.Evidence != "post_consent_no_choice_save") ||
			(attempt.RequestScope != Guid.Empty &&
				(marker.Evidence != "explicit_history_request" || marker.SaveId != attempt.RequestScope)) ||
	marker.SettingsRevision != attempt.SettingsRevision || marker.RestoreGeneration != attempt.RestoreGeneration ||
	marker.TaxonomyRevision != attempt.TaxonomyRevision || marker.SourceHash != attempt.SourceHash ||
	marker.Location != attempt.Location || marker.Description != attempt.Description ||
	(marker.ItemCode ?? "") != attempt.ItemCodeKey)
		{
			return false;
		}

		var item = await db.ReceiptItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == marker.ReceiptItemId, ct);
		var receipt = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == marker.ReceiptId, ct);
		if (item == null || receipt == null || item.ReceiptId != receipt.Id || item.DeletedAt != null ||
			receipt.DeletedAt != null || item.NormalizationRevision != marker.ItemRevision ||
						receipt.NormalizationRevision != marker.ReceiptRevision ||
						marker.Evidence == "post_consent_no_choice_save" && (!string.IsNullOrEmpty(item.Category) ||
						!string.IsNullOrEmpty(item.Subcategory)))
		{
			return false;
		}

		var mapping = await (from h in db.CategoryMappingHeads.AsNoTracking()
							 join r in db.CategoryMappingRevisions.AsNoTracking() on h.RevisionId equals r.Id
							 where h.SourceFingerprint == marker.SourceHash && r.Location == marker.Location
								 && r.Description == marker.Description && r.ItemCode == marker.ItemCode
							 select r).SingleOrDefaultAsync(ct);
		if (mapping?.CategoryId != null)
		{
			return false;
		}

		var assignment = await (from current in db.CurrentClassifications.AsNoTracking()
								join history in db.Classifications.AsNoTracking() on current.AssignmentId equals history.Id
								where current.ReceiptItemId == item.Id
								select history).SingleOrDefaultAsync(ct);
		if (assignment?.Id != marker.AssignmentId || assignment != null && marker.Evidence != "explicit_history_request" && !(assignment.CategoryId == null &&
				(assignment.Source == "legacy_import" || assignment.Source == "legacy_write" &&
				 assignment.LegacyResolution == "unresolved_path")))
		{
			return false;
		}

		try
		{
			var source = NormalizationCatalogPolicy.Normalize(new(receipt.Location, item.Description, item.ReceiptItemCode));
			return source.Location == marker.Location && source.Description == marker.Description &&
				(source.ItemCode ?? "") == (marker.ItemCode ?? "") &&
				NormalizationCatalogPolicy.Hash(source) == marker.SourceHash;
		}
		catch (NormalizationCatalogException) { return false; }
	}
}
