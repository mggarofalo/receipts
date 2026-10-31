using System.Security.Cryptography;
using System.Text.Json;
using Application.Classification;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Taxonomy;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Applies strong Jev matches. Each pass considers one completed suggestion (or an already applied one) with
/// observations it has not yet checked,
/// decides with <see cref="CategoryAutoWritePolicy"/> and, for each saving actor, writes the still-eligible purchases
/// through the shared classification path as one automatic operation that the activity list can undo. It only fills
/// purchases with no category (the same eligibility as accepting a suggestion) and never approves a mapping.
/// </summary>
public sealed class CategoryAssistanceAutoWriter(IDbContextFactory<ApplicationDbContext> factory,
	ICategoryAssistanceCapabilitySource capabilitySource, IOptions<ClassificationOptions> classificationOptions,
	IOptionsMonitor<CategoryAutoWriteOptions> autoWriteOptions, TimeProvider clock, CategoryAssistanceAutoWriteCursor cursor,
	ICommittedChangePublisher? publisher = null)
{
	internal const int MaximumItemsPerPass = 100;
	internal const string Reason = "Applied a strong Jev category match.";

	/// <returns>True when a suggestion was considered, whether or not anything was written.</returns>
	public async Task<bool> ApplyNextAsync(CancellationToken ct = default)
	{
		CategoryAutoWriteOptions options = autoWriteOptions.CurrentValue;
		// Invalid thresholds fail closed: suggestions remain, nothing is written.
		if (!classificationOptions.Value.WritesEnabled || !options.IsValid()) { return false; }

		int written;
		await using (ApplicationDbContext db = await factory.CreateDbContextAsync(ct))
		{
			await using var tx = await db.Database.BeginTransactionAsync(ct);
			// The lock order of explicit acceptance: write gate, taxonomy head, settings, connection, attempt, receipts.
			await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
			var head = await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
			var settings = await db.CategoryAssistanceSettings.FromSqlRaw(
				"SELECT * FROM category_assistance.\"Settings\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
			var connection = await db.CategoryAssistanceConnection.FromSqlRaw(
				"SELECT * FROM category_assistance.\"Connection\" WHERE \"Id\"=1 FOR UPDATE").SingleOrDefaultAsync(ct);
			var capability = await capabilitySource.GetAsync(ct);
			if (connection is null || connection.Changing || connection.ConfigurationFingerprint.Length != 64 ||
				!CategoryAssistancePolicy.CanSuggest(CategoryAssistanceSettingsStore.Read(settings), capability))
			{
				await tx.CommitAsync(ct);
				return false;
			}

			var candidates = db.CategoryAssistanceAttempts.Where(a => (a.State == "suggested" || a.State == "applied")
					&& a.QuestionPolicyVersion == CategorySuggestionPolicy.Version && a.SourceRevision == a.ItemRevision
					&& a.RestoreGeneration == settings.RestoreGeneration && a.SettingsRevision == settings.Revision
					&& a.TaxonomyRevision == head.RevisionId && a.Provider == settings.Provider && a.Model == settings.Model
					&& a.ConnectionGeneration == connection.Generation && connection.Provider == a.Provider && connection.Model == a.Model
					&& db.CategoryAssistanceObservations.Any(o => o.AttemptId == a.Id
						&& (a.AutoWriteCheckedAt == null || o.RecordedAt > a.AutoWriteCheckedAt)))
				.Select(a => a.Id);
			// Rotate before writing: a pass that fails (and so saves no checkpoint) yields its turn to the next attempt.
			Guid? after = cursor.Last;
			Guid? attemptId = await candidates.Where(id => after == null || id.CompareTo(after.Value) > 0)
				.OrderBy(id => id).Select(id => (Guid?)id).FirstOrDefaultAsync(ct)
				?? await candidates.OrderBy(id => id).Select(id => (Guid?)id).FirstOrDefaultAsync(ct);
			cursor.Last = attemptId;
			if (attemptId is null)
			{
				await tx.CommitAsync(ct);
				return false;
			}

			var attempt = await db.CategoryAssistanceAttempts.FromSqlInterpolated(
				$"SELECT * FROM category_assistance.\"Attempts\" WHERE \"Id\"={attemptId} FOR UPDATE").SingleAsync(ct);
			// Observations are recorded under the taxonomy head lock held here, so any later one has a later timestamp.
			DateTimeOffset checkedAt = clock.GetUtcNow();
			written = 0;
			if (settings.AutoApply && Decide(attempt, options) is { } decision &&
				Guid.TryParse(decision.Decision.CategoryId, out Guid category) &&
				await db.TaxonomyNodes.AnyAsync(x => x.RevisionId == head.RevisionId && x.CategoryId == category && !x.Archived, ct))
			{
				// A strong match leaves the review queue (and the pending limit); later saves of the same source attach
				// to it and are written by a subsequent pass.
				attempt.State = "applied";
				var eligible = await EligibleAsync(db, attempt, ct);
				if (eligible.Count > MaximumItemsPerPass)
				{
					// Leave the checkpoint so the next pass continues; written purchases are no longer eligible.
					eligible = [.. eligible.OrderBy(x => x.ReceiptItemId).Take(MaximumItemsPerPass)];
					checkedAt = attempt.AutoWriteCheckedAt ?? DateTimeOffset.MinValue;
				}

				await ReceiptFactLock.AcquireAsync(db, eligible.Select(x => x.ReceiptId), ct);
				foreach (var actorItems in eligible.GroupBy(x => x.Actor, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal))
				{
					written += await WriteAsync(db, attempt, actorItems.Key, [.. actorItems.OrderBy(x => x.ReceiptItemId)],
						category, head.RevisionId, decision.Evidence, ct);
				}
			}
			else if (attempt.State == "applied")
			{
				// Automatic writes were switched off, or the thresholds no longer call this strong: later saves of
				// the source need review like any other suggestion.
				attempt.State = "suggested";
			}

			attempt.AutoWriteCheckedAt = checkedAt == DateTimeOffset.MinValue ? null : checkedAt;
			await db.SaveChangesAsync(ct);
			await tx.CommitAsync(ct);
		}

		if (written > 0 && publisher != null)
		{
			await publisher.PublishAsync(new(CommittedEntityType.ReceiptItem, CommittedChangeType.Updated, SuppressToast: true));
		}
		return true;
	}

	private (CategoryAutoWriteDecision Decision, JevAutoWriteEvidence Evidence)? Decide(
		Entities.CategoryAssistance.CategoryAssistanceAttemptEntity attempt, CategoryAutoWriteOptions options)
	{
		CategorySuggestionRequest? request;
		CategorySuggestion? suggestion;
		try
		{
			request = JsonSerializer.Deserialize<CategorySuggestionRequest>(attempt.RequestJson);
			suggestion = JsonSerializer.Deserialize<CategoryAssistanceCompletion>(attempt.ResultJson ?? "")?.Suggestion;
		}
		catch (JsonException) { return null; }

		if (request is null || request.TaxonomyRevision != attempt.TaxonomyRevision.ToString("D") ||
			CategoryAutoWritePolicy.Decide(request, suggestion, options) is not { } decision)
		{
			return null;
		}

		return (decision, new(attempt.Id, attempt.RequestId, suggestion!.Provenance.QuestionPolicy, suggestion.Provenance.Model,
			request.TaxonomyRevision, decision.Level, decision.FitProbability, decision.RootProbability, decision.LeafProbability,
			options.RootAutoWriteThreshold, options.LeafAutoWriteThreshold, options.LeafAutoWriteEnabled));
	}

	// Acceptance eligibility limited to uncategorized purchases (a history request's categorized members stay for a person
	// to decide), minus purchases the saving actor set aside or left as is in their own review.
	private static async Task<List<Entities.CategoryAssistance.CategoryAssistanceObservationEntity>> EligibleAsync(
		ApplicationDbContext db, Entities.CategoryAssistance.CategoryAssistanceAttemptEntity attempt, CancellationToken ct)
	{
		var eligible = await CategoryAssistanceDecisionStore.GetEligibleObservationsAsync(db, attempt, null, ct, uncategorizedOnly: true);
		var actors = eligible.Select(x => x.Actor).Distinct().ToList();
		var preferences = await db.CategoryAssistanceReviewPreferences.AsNoTracking()
			.Where(x => x.AttemptId == attempt.Id && actors.Contains(x.Actor)).ToListAsync(ct);
		return [.. eligible.Where(marker => preferences.SingleOrDefault(p => p.Actor == marker.Actor) is not { } preference ||
			(preference.State == "active" && !CategoryAssistanceReviewPreferenceStore.Excluded(preference).Contains(marker.ReceiptItemId)))];
	}

	private async Task<int> WriteAsync(ApplicationDbContext db, Entities.CategoryAssistance.CategoryAssistanceAttemptEntity attempt,
		string actor, IReadOnlyList<Entities.CategoryAssistance.CategoryAssistanceObservationEntity> markers, Guid category,
		Guid revision, JevAutoWriteEvidence evidence, CancellationToken ct)
	{
		List<HistoricalOperationItemEntity> rows = [];
		foreach (var marker in markers)
		{
			// Recheck under the receipt locks; a save between capture and lock makes the purchase ineligible.
			if (!await CategoryAssistanceDecisionStore.IsEligibleAsync(db, attempt, marker, ct, uncategorizedOnly: true)) { continue; }
			var source = await (from item in db.ReceiptItems.AsNoTracking()
								join receipt in db.Receipts.AsNoTracking() on item.ReceiptId equals receipt.Id
								where item.Id == marker.ReceiptItemId
								select new
								{
									item.Description,
									item.ReceiptItemCode,
									item.NormalizationRevision,
									receipt.Location,
									ReceiptRevision = receipt.NormalizationRevision
								}).SingleAsync(ct);
			Guid? previous = marker.AssignmentId is Guid assignment
				? await db.Classifications.Where(x => x.Id == assignment).Select(x => x.CategoryId).SingleAsync(ct) : null;
			rows.Add(new()
			{
				ItemId = marker.ReceiptItemId,
				ReceiptId = marker.ReceiptId,
				Store = source.Location,
				RawDescription = source.Description,
				ItemCode = source.ReceiptItemCode,
				ItemRevision = source.NormalizationRevision,
				ReceiptRevision = source.ReceiptRevision,
				ExpectedAssignmentId = marker.AssignmentId,
				PreviousCategoryId = previous,
				TargetCategoryId = category,
				// Stable per attempt, purchase and replaced assignment, so a retried pass cannot write twice.
				ApplyOperationId = new Guid(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
					new { Kind = "jev_auto_write", attempt.Id, marker.ReceiptItemId, marker.AssignmentId })).AsSpan(0, 16)),
				UndoOperationId = Guid.NewGuid(),
				State = HistoricalItemState.Pending,
				UndoState = HistoricalItemState.Pending
			});
		}
		if (rows.Count == 0) { return 0; }

		HistoricalGroupSelection[] groups = [.. rows.Select(x => new HistoricalGroupKey(x.Store, x.RawDescription, x.ItemCode)).Distinct()
			.Select(group => new HistoricalGroupSelection(group, category))];
		var operation = new HistoricalOperationEntity
		{
			Id = Guid.NewGuid(),
			Kind = HistoricalOperationKind.JevSuggestionV1,
			Actor = actor,
			TaxonomyRevisionId = revision,
			Reason = Reason,
			RequestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
				new { attempt.Id, actor, revision, category, items = rows.Select(x => x.ItemId), evidence }))),
			SelectionJson = JsonSerializer.Serialize(groups),
			CapturedAt = clock.GetUtcNow(),
			State = HistoricalOperationState.Applying
		};
		db.HistoricalOperations.Add(operation);
		await db.SaveChangesAsync(ct);
		foreach (var row in rows)
		{
			row.OperationId = operation.Id;
			db.HistoricalOperationItems.Add(row);
		}
		await db.SaveChangesAsync(ct);
		foreach (var row in rows)
		{
			var result = await ClassificationService.AssignJevInTransactionAsync(db, actor, row.ItemId, category, revision,
				row.ExpectedAssignmentId, row.ApplyOperationId, evidence, classificationOptions.Value.WritesEnabled, clock, ct);
			row.State = HistoricalItemState.Applied;
			row.AppliedAssignmentId = result.Id;
		}
		operation.State = HistoricalOperationState.Completed;
		await db.SaveChangesAsync(ct);
		return rows.Count;
	}
}

/// <summary>Process-wide rotation over attempts awaiting an automatic-write check.</summary>
public sealed class CategoryAssistanceAutoWriteCursor
{
	private readonly Lock gate = new();
	private Guid? last;
	internal Guid? Last
	{
		get { lock (gate) { return last; } }
		set { lock (gate) { last = value; } }
	}
}
