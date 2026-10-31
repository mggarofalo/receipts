using System.Text.Json;
using Application.Classification;
using Application.Normalization;
using Infrastructure.Entities.CategoryAssistance;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public sealed class CategoryAssistanceReadStore(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock)
	: ICategoryAssistanceReadStore
{
	public async Task<CategoryAssistanceSuggestionPage> ListSuggestionsAsync(int offset, int limit, CancellationToken ct)
		=> await ListSuggestionsAsync("", "all", offset, limit, ct);

	public async Task<CategoryAssistanceSuggestionPage> ListSuggestionsAsync(string actor, string reviewFilter,
		int offset, int limit, CancellationToken ct)
	{
		if (offset < 0 || limit is < 1 or > 100)
		{
			throw new ArgumentOutOfRangeException(nameof(limit), "Offset must be nonnegative and limit must be 1 through 100.");
		}

		await using var db = await factory.CreateDbContextAsync(ct);
		var query = db.CategoryAssistanceAttempts.AsNoTracking()
			.Where(x => x.State == "suggested" || x.State == "abstained");
		if (!string.IsNullOrEmpty(actor) && reviewFilter != "all")
		{
			query = reviewFilter switch
			{
				"active" => query.Where(x => !db.CategoryAssistanceReviewPreferences.Any(p =>
					p.Actor == actor && p.AttemptId == x.Id && p.State != "active")),
				"later" or "left_as_is" => query.Where(x => db.CategoryAssistanceReviewPreferences.Any(p =>
					p.Actor == actor && p.AttemptId == x.Id && p.State == reviewFilter)),
				_ => throw new ArgumentException("Invalid review filter", nameof(reviewFilter))
			};
		}
		int total = await query.CountAsync(ct);
		var rows = await query.OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.Id)
			.Skip(offset).Take(limit).ToListAsync(ct);
		return new(total, offset, limit, await ProjectAsync(db, rows, actor, ct));
	}

	public async Task<CategoryAssistanceSuggestionRead?> GetSuggestionAsync(Guid attemptId, CancellationToken ct)
		=> await GetSuggestionAsync("", attemptId, ct);

	public async Task<CategoryAssistanceSuggestionRead?> GetSuggestionAsync(string actor, Guid attemptId,
		CancellationToken ct)
	{
		await using var db = await factory.CreateDbContextAsync(ct);
		var row = await db.CategoryAssistanceAttempts.AsNoTracking()
				.SingleOrDefaultAsync(x => x.Id == attemptId &&
						(x.State == "suggested" || x.State == "abstained"), ct);
		if (row is null)
		{
			return null;
		}

		return (await ProjectAsync(db, [row], actor, ct)).Single();
	}

	private static async Task<List<CategoryAssistanceSuggestionRead>> ProjectAsync(
		ApplicationDbContext db, IReadOnlyList<CategoryAssistanceAttemptEntity> rows,
		string actor, CancellationToken ct)
	{
		var settings = await db.CategoryAssistanceSettings.AsNoTracking().SingleAsync(ct);
		var head = await db.TaxonomyHeads.AsNoTracking().SingleAsync(ct);
		var connection = await db.CategoryAssistanceConnection.AsNoTracking().SingleOrDefaultAsync(ct);
		var items = new List<CategoryAssistanceSuggestionRead>(rows.Count);
		var attemptIds = rows.Select(x => x.Id).ToArray();
		var preferences = string.IsNullOrEmpty(actor) ? [] : await db.CategoryAssistanceReviewPreferences
			.AsNoTracking().Where(x => x.Actor == actor && attemptIds.Contains(x.AttemptId)).ToListAsync(ct);
		foreach (var row in rows)
		{
			var preference = preferences.FirstOrDefault(x => x.AttemptId == row.Id);
			var excluded = CategoryAssistanceReviewPreferenceStore.Excluded(preference);
			var suggestion = StoredSuggestion(row);
			var path = SuggestedPath(row, suggestion);
			bool current = suggestion != null && await IsCurrentAsync(db, row, settings, head.RevisionId,
				connection, path, ct);
			int affected = (await CategoryAssistanceDecisionStore.GetEligibleObservationsAsync(
				db, row, null, ct)).Count(x => !excluded.Contains(x.ReceiptItemId));
			var source = new NormalizationSourceKey(row.Location, row.Description,
				row.ItemCodeKey == "" ? null : row.ItemCodeKey);
			// Attempts retain the normalized source, not the raw item text. Only expose
			// current raw text when the captured item and receipt revisions still match.
			var original = await (from item in db.ReceiptItems.AsNoTracking()
								  join receipt in db.Receipts.AsNoTracking() on item.ReceiptId equals receipt.Id
								  where item.Id == row.ItemId && receipt.Id == row.ReceiptId &&
									  item.DeletedAt == null && receipt.DeletedAt == null &&
									  item.NormalizationRevision == row.ItemRevision &&
									  receipt.NormalizationRevision == row.ReceiptRevision
								  select new { item.Description, item.ReceiptItemCode, receipt.Location })
				.SingleOrDefaultAsync(ct);
			string? originalDescription = null;
			if (original != null)
			{
				try
				{
					var liveSource = NormalizationCatalogPolicy.Normalize(new(
						original.Location, original.Description, original.ReceiptItemCode));
					if (liveSource == source && NormalizationCatalogPolicy.Hash(liveSource) == row.SourceHash)
					{
						originalDescription = original.Description;
					}
				}
				catch (NormalizationCatalogException) { /* No reliable original remains. */ }
			}
			items.Add(new(row.Id, source, originalDescription, affected, path,
				suggestion?.AbstentionReason, suggestion?.Confidence, row.State, current,
								row.CreatedAt, row.CompletedAt,
				suggestion?.FitProbability, suggestion?.CategoryProbability)
			{
				SelectedPathSupport = suggestion?.SelectedPathSupport,
				ReviewState = preference?.State ?? "active",
				ReviewRevision = preference?.Revision ?? Guid.Empty,
				ExcludedItemCount = excluded.Length,
				ProbabilityScope = suggestion?.ProbabilityScope ?? "global_choice",
				RootCandidates = Candidates(row, suggestion?.RootCandidates),
				LeafCandidates = Candidates(row, suggestion?.LeafCandidates)
			});
		}
		return items;
	}

	/// <summary>An attempt's stored suggestion; null when it has none or the result does not parse.</summary>
	internal static CategorySuggestion? StoredSuggestion(CategoryAssistanceAttemptEntity row)
	{
		try { return JsonSerializer.Deserialize<CategoryAssistanceCompletion>(row.ResultJson ?? "")?.Suggestion; }
		catch (JsonException) { return null; }
	}

	/// <summary>The suggested root-to-leaf path; empty when a segment does not parse or another question policy produced it.</summary>
	internal static List<Guid> SuggestedPath(CategoryAssistanceAttemptEntity row, CategorySuggestion? suggestion)
	{
		var path = new List<Guid>();
		if (suggestion?.Path == null || row.QuestionPolicyVersion != CategorySuggestionPolicy.Version)
		{
			return path;
		}
		foreach (var segment in suggestion.Path)
		{
			if (!Guid.TryParse(segment, out var id)) { return []; }
			path.Add(id);
		}
		return path;
	}

	internal static IReadOnlyList<CategoryAssistanceCandidateRead> Candidates(
		Entities.CategoryAssistance.CategoryAssistanceAttemptEntity row, IReadOnlyList<SuggestionCandidate>? candidates) =>
		row.QuestionPolicyVersion != CategorySuggestionPolicy.Version || candidates is null ||
			candidates.Any(c => c is null || !Guid.TryParse(c.CategoryId, out _))
			? [] : [.. candidates.Select(c => new CategoryAssistanceCandidateRead(Guid.Parse(c.CategoryId), c.Probability))];

	public async Task<CategoryAssistanceActivity> GetActivityAsync(CancellationToken ct)
	{
		await using var db = await factory.CreateDbContextAsync(ct);
		var counts = await db.CategoryAssistanceAttempts.AsNoTracking()
			.GroupBy(x => new { x.State, x.QuestionPolicyVersion })
			.Select(g => new { g.Key.State, g.Key.QuestionPolicyVersion, Count = g.Count() }).ToListAsync(ct);
		int Count(string state) => counts.Where(x =>
			(x.QuestionPolicyVersion != CategorySuggestionPolicy.Version &&
				x.State is "queued" or "dispatching" or "suggested" or "abstained" ? "stale" : x.State) == state)
			.Sum(x => x.Count);
		var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
		int attempts = await db.CategoryAssistanceDailyUsage.AsNoTracking()
			.Where(x => x.UtcDay == day).Select(x => x.DispatchAttempts).SingleOrDefaultAsync(ct);
		var recent = await db.CategoryAssistanceAttempts.AsNoTracking()
				.Where(x => x.State == "failed" && x.CompletedAt != null)
				.OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.Id)
				.Take(10).Select(x => new { x.Id, x.ResultJson, x.CompletedAt }).ToListAsync(ct);
		return new(Count("queued"), Count("dispatching"), Count("suggested"), Count("abstained"),
				Count("failed"), Count("stale"), Count("indeterminate"), attempts)
		{
			Applied = Count("applied"),
			RecentFailures = recent.Select(x => new CategoryAssistanceFailureRead(x.Id,
					CategoryAssistanceFailureCode.Read(x.ResultJson) ?? "Unknown",
					x.CompletedAt!.Value)).ToArray()
		};
	}

	private static async Task<bool> IsCurrentAsync(ApplicationDbContext db, CategoryAssistanceAttemptEntity row,
		CategoryAssistanceSettingsEntity settings, Guid taxonomyRevision,
		CategoryAssistanceConnectionEntity? connection, IReadOnlyList<Guid> path, CancellationToken ct)
	{
		if (!settings.Enabled || settings.Revision != row.SettingsRevision ||
			settings.RestoreGeneration != row.RestoreGeneration || settings.Provider != row.Provider ||
			settings.Model != row.Model || taxonomyRevision != row.TaxonomyRevision ||
			row.SourceRevision != row.ItemRevision ||
			row.QuestionPolicyVersion != CategorySuggestionPolicy.Version ||
			connection == null || connection.Changing || connection.Generation != row.ConnectionGeneration ||
			connection.Provider != row.Provider || connection.Model != row.Model ||
			connection.ConfigurationFingerprint.Length != 64)
		{
			return false;
		}

		if (row.State == "suggested" && path.Count == 0)
		{
			return false;
		}

		if (path.Count > 0)
		{
			var nodes = await db.TaxonomyNodes.AsNoTracking()
				.Where(x => x.RevisionId == taxonomyRevision && path.Contains(x.CategoryId))
				.Select(x => new { x.CategoryId, x.ParentId, x.Archived }).ToListAsync(ct);
			if (nodes.Count != path.Count || nodes.Any(x => x.Archived) ||
				path.Select((id, index) => new { id, parent = index == 0 ? (Guid?)null : path[index - 1] })
					.Any(x => !nodes.Any(n => n.CategoryId == x.id && n.ParentId == x.parent)))
			{
				return false;
			}
		}
		var marker = await db.CategoryAssistanceObservations.AsNoTracking()
				.Where(x => x.AttemptId == row.Id && x.ReceiptItemId == row.ItemId &&
					(row.RequestScope == Guid.Empty || x.SaveId == row.RequestScope))
			.OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.SaveId)
			.FirstOrDefaultAsync(ct);
		if (marker == null ||
			(row.RequestScope == Guid.Empty && marker.Evidence != "post_consent_no_choice_save") ||
			(row.RequestScope != Guid.Empty && marker.Evidence != "explicit_history_request") ||
	marker.ItemRevision != row.ItemRevision || marker.ReceiptRevision != row.ReceiptRevision ||
	marker.AssignmentId != row.AssignmentId || marker.SourceHash != row.SourceHash ||
	marker.Location != row.Location || marker.Description != row.Description ||
	(marker.ItemCode ?? "") != row.ItemCodeKey)
		{
			return false;
		}

		var item = await db.ReceiptItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.ItemId, ct);
		var receipt = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.ReceiptId, ct);
		if (item == null || receipt == null || item.ReceiptId != receipt.Id || item.DeletedAt != null ||
			receipt.DeletedAt != null || item.NormalizationRevision != row.ItemRevision ||
			receipt.NormalizationRevision != row.ReceiptRevision ||
						marker.Evidence == "post_consent_no_choice_save" && (!string.IsNullOrWhiteSpace(item.Category) || !string.IsNullOrWhiteSpace(item.Subcategory)))
		{
			return false;
		}

		NormalizationSourceKey source;
		try { source = NormalizationCatalogPolicy.Normalize(new(receipt.Location, item.Description, item.ReceiptItemCode)); }
		catch (NormalizationCatalogException) { return false; }
		if (source.Location != row.Location || source.Description != row.Description ||
			(source.ItemCode ?? "") != row.ItemCodeKey || NormalizationCatalogPolicy.Hash(source) != row.SourceHash)
		{
			return false;
		}

		var assignment = await (from current in db.CurrentClassifications.AsNoTracking()
								join history in db.Classifications.AsNoTracking() on current.AssignmentId equals history.Id
								where current.ReceiptItemId == row.ItemId
								select history).SingleOrDefaultAsync(ct);
		if (assignment?.Id != row.AssignmentId || assignment != null && marker.Evidence != "explicit_history_request" && !(assignment.CategoryId == null &&
				(assignment.Source == "legacy_import" || assignment.Source == "legacy_write" &&
				 assignment.LegacyResolution == "unresolved_path")))
		{
			return false;
		}

		var mapping = await (from h in db.CategoryMappingHeads.AsNoTracking()
							 join r in db.CategoryMappingRevisions.AsNoTracking() on h.RevisionId equals r.Id
							 where h.SourceFingerprint == row.SourceHash && r.Location == row.Location &&
								   r.Description == row.Description && r.ItemCode == (row.ItemCodeKey == "" ? null : row.ItemCodeKey)
							 select r.CategoryId).SingleOrDefaultAsync(ct);
		return mapping == null;
	}
}
