using System.Security.Cryptography;
using System.Text.Json;
using Application.Classification;
using Application.Normalization;
using Application.Taxonomy;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public sealed class CategoryAssistanceHistoricalRequestStore(
	IDbContextFactory<ApplicationDbContext> factory,
	ICategoryAssistanceCapabilitySource capabilitySource,
	TimeProvider clock) : ICategoryAssistanceHistoricalRequestStore
{
	private const int MaximumGroups = 25;
	private const int MaximumItems = 100;
	private sealed record StoredGroup(HistoricalGroupKey key, Guid[] ItemIds);
	private sealed record Candidate(ReceiptItemEntity Item, ReceiptEntity Receipt,
		Guid? AssignmentId, string? AssignmentSource, Guid? CategoryId, string? LegacyResolution);

	public async Task<CategoryAssistanceHistoricalProgress> EnrollAsync(string actor,
		CategoryAssistanceHistoricalRequest request, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(actor) || request.OperationId == Guid.Empty ||
			request.TaxonomyRevisionId == Guid.Empty || request.Groups is not { Count: >= 1 and <= MaximumGroups } ||
			request.Groups.Any(g => g == null || string.IsNullOrWhiteSpace(g.Store) ||
				string.IsNullOrWhiteSpace(g.RawDescription) || g.Store.Length > 1000 ||
				g.RawDescription.Length > 2000 || g.ItemCode?.Length > 200 ||
				g.Store.Contains('\0') || g.RawDescription.Contains('\0') || g.ItemCode?.Contains('\0') == true) ||
			request.Groups.Distinct().Count() != request.Groups.Count)
		{
			throw new TaxonomyException(400, "Provide an operation ID, taxonomy revision and 1 to 25 distinct exact source groups.");
		}

		var ordered = request.Groups.OrderBy(g => g.Store, StringComparer.Ordinal)
			.ThenBy(g => g.RawDescription, StringComparer.Ordinal)
			.ThenBy(g => g.ItemCode, StringComparer.Ordinal).ToArray();
		string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
			new { request.TaxonomyRevisionId, Groups = ordered })));
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		// The worker and receipt/classification writes take these locks in the same order.
		var head = await db.TaxonomyHeads.FromSqlRaw(
			"SELECT * FROM taxonomy.\"Head\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
		var settings = await db.CategoryAssistanceSettings.FromSqlRaw(
			"SELECT * FROM category_assistance.\"Settings\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
		var previous = await db.CategoryAssistanceHistoricalRequests.AsNoTracking()
			.SingleOrDefaultAsync(x => x.OperationId == request.OperationId, ct);
		if (previous != null)
		{
			if (previous.Actor != actor || previous.RequestHash != hash)
			{
				throw new TaxonomyException(409, "Operation ID was used for a different historical assistance request.");
			}

			await tx.CommitAsync(ct);
			return await GetAsync(actor, request.OperationId, ct);
		}
		DateTimeOffset now = clock.GetUtcNow();
		if (!await db.Users.AnyAsync(u => u.Id == actor && !(u.LockoutEnabled && u.LockoutEnd > now), ct))
		{
			throw new TaxonomyException(403, "An active receipt writer is required.");
		}

		var capability = await capabilitySource.GetAsync(ct);
		if (!CategoryAssistancePolicy.CanSuggest(CategoryAssistanceSettingsStore.Read(settings), capability))
		{
			throw new TaxonomyException(409, "Category assistance is unavailable. Check consent and provider readiness.");
		}

		var connection = await db.CategoryAssistanceConnection.AsNoTracking().SingleOrDefaultAsync(ct);
		if (connection == null || connection.Changing || connection.Generation == Guid.Empty ||
			connection.Provider != settings.Provider || connection.Model != settings.Model ||
			connection.ConfigurationFingerprint.Length != 64 || head.RevisionId != request.TaxonomyRevisionId)
		{
			throw new TaxonomyException(409, "Category assistance context changed. Refresh before requesting.");
		}

		var stored = new List<StoredGroup>();
		var markers = new List<CategoryAssistanceObservationEntity>();
		var sources = new HashSet<string>(StringComparer.Ordinal);
		foreach (var key in ordered)
		{
			// Raw bytewise equality is essential: normalization may merge two visibly distinct groups.
			var raw = await (from item in db.ReceiptItems.AsNoTracking()
							 join receipt in db.Receipts.AsNoTracking() on item.ReceiptId equals receipt.Id
							 where item.DeletedAt == null && receipt.DeletedAt == null &&
								   EF.Functions.Collate(receipt.Location, "C") == key.Store &&
								   EF.Functions.Collate(item.Description, "C") == key.RawDescription &&
								   ((key.ItemCode == null && item.ReceiptItemCode == null) ||
									(key.ItemCode != null && item.ReceiptItemCode != null &&
									 EF.Functions.Collate(item.ReceiptItemCode, "C") == key.ItemCode))
							 orderby item.Id
							 select new { Item = item, Receipt = receipt }).Take(MaximumItems + 1).ToListAsync(ct);
			if (raw.Count > MaximumItems)
			{
				throw new TaxonomyException(400, "A selected exact group contains more than 100 purchases. Choose a smaller group.");
			}

			var ids = new List<Guid>();
			foreach (var pair in raw)
			{
				var item = pair.Item;
				var receipt = pair.Receipt;
				var assignment = await (from current in db.CurrentClassifications.AsNoTracking()
										join history in db.Classifications.AsNoTracking() on current.AssignmentId equals history.Id
										where current.ReceiptItemId == item.Id
										select history).SingleOrDefaultAsync(ct);
				NormalizationSourceKey source;
				try { source = NormalizationCatalogPolicy.Normalize(new(receipt.Location, item.Description, item.ReceiptItemCode)); }
				catch (NormalizationCatalogException) { continue; }
				string sourceHash = NormalizationCatalogPolicy.Hash(source);
				var mapping = await (from h in db.CategoryMappingHeads.AsNoTracking()
									 join r in db.CategoryMappingRevisions.AsNoTracking() on h.RevisionId equals r.Id
									 where h.SourceFingerprint == sourceHash && r.Location == source.Location &&
										   r.Description == source.Description && r.ItemCode == source.ItemCode
									 select r.CategoryId).SingleOrDefaultAsync(ct);
				if (mapping != null)
				{
					continue;
				}

				ids.Add(item.Id);
				sources.Add(sourceHash + "\0" + source.Location + "\0" + source.Description + "\0" + source.ItemCode);
				markers.Add(new CategoryAssistanceObservationEntity
				{
					SaveId = request.OperationId,
					ReceiptItemId = item.Id,
					ReceiptId = receipt.Id,
					Actor = actor,
					Location = source.Location,
					Description = source.Description,
					ItemCode = source.ItemCode,
					SourceHash = sourceHash,
					ItemRevision = item.NormalizationRevision,
					ReceiptRevision = receipt.NormalizationRevision,
					AssignmentId = assignment?.Id,
					SettingsRevision = settings.Revision,
					RestoreGeneration = settings.RestoreGeneration,
					TaxonomyRevision = head.RevisionId,
					Evidence = "explicit_history_request",
					RecordedAt = now
				});
			}
			if (ids.Count == 0)
			{
				throw new TaxonomyException(409, "A selected exact key has no eligible purchases. Refresh the key list.");
			}

			stored.Add(new StoredGroup(key, ids.ToArray()));
		}
		int unreviewed = await db.CategoryAssistanceAttempts.CountAsync(x =>
			x.QuestionPolicyVersion == CategorySuggestionPolicy.Version &&
			(x.State == "queued" || x.State == "dispatching" || x.State == "suggested" || x.State == "abstained"), ct);
		// Each operation reserves its own normalized sources until its attempts exist.
		// Distinct raw groups within one operation may share a reservation.
		var reservations = new HashSet<string>(sources.Select(source =>
			request.OperationId.ToString("D") + "\0" + source), StringComparer.Ordinal);
		var reserved = await (from marker in db.CategoryAssistanceObservations.AsNoTracking()
							  join history in db.CategoryAssistanceHistoricalRequests.AsNoTracking()
								  on marker.SaveId equals history.OperationId
							  where marker.AttemptId == null && !marker.DispatchSkipped &&
									marker.Evidence == "explicit_history_request" &&
									history.SettingsRevision == settings.Revision &&
									history.RestoreGeneration == settings.RestoreGeneration &&
									history.TaxonomyRevision == head.RevisionId &&
									history.ConnectionGeneration == connection.Generation
							  select new
							  {
								  marker.SaveId,
								  marker.SourceHash,
								  marker.Location,
								  marker.Description,
								  marker.ItemCode
							  })
			.ToListAsync(ct);
		foreach (var source in reserved)
		{
			reservations.Add(source.SaveId.ToString("D") + "\0" + source.SourceHash + "\0" +
				source.Location + "\0" + source.Description + "\0" + source.ItemCode);
		}

		var scopes = reservations.Select(x => Guid.Parse(x[..x.IndexOf('\0')])).Distinct().ToArray();
		var hashes = reservations.Select(x => x[(x.IndexOf('\0') + 1)..].Split('\0')[0]).Distinct().ToArray();
		// Dispatch attaches matching markers to an existing attempt, including an obsolete one.
		var existing = await db.CategoryAssistanceAttempts.AsNoTracking().Where(x =>
			scopes.Contains(x.RequestScope) && x.RestoreGeneration == settings.RestoreGeneration &&
			x.TaxonomyRevision == head.RevisionId && x.Provider == settings.Provider &&
			x.Model == settings.Model && x.ConnectionGeneration == connection.Generation &&
			hashes.Contains(x.SourceHash)).ToListAsync(ct);
		foreach (var attempt in existing)
		{
			reservations.Remove(attempt.RequestScope.ToString("D") + "\0" + attempt.SourceHash + "\0" +
				attempt.Location + "\0" + attempt.Description + "\0" + attempt.ItemCodeKey);
		}

		if (unreviewed + reservations.Count > settings.PendingLimit)
		{
			throw new TaxonomyException(409, "Too many unreviewed or pending suggestions. Review existing suggestions or select fewer groups.");
		}

		db.CategoryAssistanceHistoricalRequests.Add(new()
		{
			OperationId = request.OperationId,
			Actor = actor,
			RequestHash = hash,
			GroupsJson = JsonSerializer.Serialize(stored),
			ItemCount = markers.Count,
			SettingsRevision = settings.Revision,
			RestoreGeneration = settings.RestoreGeneration,
			TaxonomyRevision = head.RevisionId,
			ConnectionGeneration = connection.Generation,
			CreatedAt = now
		});
		db.CategoryAssistanceObservations.AddRange(markers);
		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);
		return await GetAsync(actor, request.OperationId, ct);
	}

	public async Task<CategoryAssistanceHistoricalProgress> GetAsync(string actor, Guid operationId, CancellationToken ct)
	{
		await using var db = await factory.CreateDbContextAsync(ct);
		var request = await db.CategoryAssistanceHistoricalRequests.AsNoTracking()
			.SingleOrDefaultAsync(x => x.OperationId == operationId && x.Actor == actor, ct)
			?? throw new TaxonomyException(404, "Historical assistance request does not exist.");
		var groups = JsonSerializer.Deserialize<StoredGroup[]>(request.GroupsJson)!;
		var markers = await db.CategoryAssistanceObservations.AsNoTracking()
			.Where(x => x.SaveId == operationId).ToListAsync(ct);
		var attemptIds = markers.Where(x => x.AttemptId != null).Select(x => x.AttemptId!.Value).Distinct().ToArray();
		var attempts = await db.CategoryAssistanceAttempts.AsNoTracking()
			.Where(x => attemptIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
		var settings = await db.CategoryAssistanceSettings.AsNoTracking().SingleAsync(ct);
		var head = await db.TaxonomyHeads.AsNoTracking().SingleAsync(ct);
		var connection = await db.CategoryAssistanceConnection.AsNoTracking().SingleOrDefaultAsync(ct);
		bool current = settings.Enabled && settings.Revision == request.SettingsRevision &&
			settings.RestoreGeneration == request.RestoreGeneration && head.RevisionId == request.TaxonomyRevision &&
			connection?.Generation == request.ConnectionGeneration && connection.Changing == false;
		var byId = markers.ToDictionary(x => x.ReceiptItemId);
		var progress = groups.Select(g =>
		{
			var groupMarkers = g.ItemIds.Select(id => byId[id]).ToArray();
			var states = groupMarkers.Select(m => m.AttemptId is Guid id && attempts.TryGetValue(id, out var a)
				? a.QuestionPolicyVersion == CategorySuggestionPolicy.Version ? a.State : "stale"
				: m.DispatchSkipped || !current ? "stale" : "waiting").ToArray();
			return new CategoryAssistanceHistoricalGroupProgress(g.key, g.ItemIds.Length,
								groupMarkers.Where(x => x.AttemptId != null).Select(x => x.AttemptId!.Value).Distinct().ToArray(), states)
			{
				FailureReasons = groupMarkers.Select(m => m.AttemptId is Guid id &&
						attempts.TryGetValue(id, out var a) && a.State == "failed"
						? CategoryAssistanceFailureCode.Read(a.ResultJson) ?? "Unknown" : null).ToArray()
			};
		}).ToArray();
		var allStates = progress.SelectMany(x => x.States).ToArray();
		string state = allStates.All(x => x is "suggested" or "abstained" or "accepted" or "dismissed") ? "ready" :
			allStates.All(x => x is "stale" or "failed" or "indeterminate") ? "failed" :
			allStates.Any(x => x is "waiting" or "queued" or "dispatching") ? "pending" : "partial";
		return new(request.OperationId, request.CreatedAt, groups.Length, request.ItemCount, state, progress);
	}

	public async Task<CategoryAssistanceHistoricalPage> ListAsync(string actor, int offset, int limit, CancellationToken ct)
	{
		if (offset < 0 || limit is < 1 or > 25)
		{
			throw new TaxonomyException(400, "Use a nonnegative offset and limit of 1 to 25.");
		}

		await using var db = await factory.CreateDbContextAsync(ct);
		var query = db.CategoryAssistanceHistoricalRequests.AsNoTracking().Where(x => x.Actor == actor);
		int total = await query.CountAsync(ct);
		var ids = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.OperationId)
			.Skip(offset).Take(limit).Select(x => x.OperationId).ToArrayAsync(ct);
		var items = new List<CategoryAssistanceHistoricalProgress>(ids.Length);
		foreach (var id in ids)
		{
			items.Add(await GetAsync(actor, id, ct));
		}

		return new(total, offset, limit, items);
	}
}

