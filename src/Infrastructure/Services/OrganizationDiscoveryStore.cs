using System.Security.Cryptography;
using System.Text.Json;
using Application.Classification;
using Application.Normalization;
using Application.Taxonomy;
using Infrastructure.Entities.Organization;
using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>
/// Bounded deployment-local discovery. The scheduler never dispatches provider work.
/// Observed means captured, never authorized to apply. Every future effect must independently
/// recheck source, assignment, correction, policy, taxonomy and restore fences in its transaction.
/// </summary>
public sealed class OrganizationDiscoveryStore(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock)
	: IOrganizationDiscoveryReader
{
	public const int MaximumChunkSize = 200;
	public async Task<OrganizationDiscoveryProgress> GetProgressAsync(CancellationToken ct = default)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		// A coherent snapshot keeps counts and fences aligned during concurrent saves/settings updates.
		await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
		var settings = await db.CategoryAssistanceSettings.AsNoTracking().SingleAsync(ct);
		bool enabled = CanDiscover(settings);
		if (!enabled) { return new(false, "disabled", 0, 0, 0, 0, 0, null); }
		Guid taxonomyRevision = await db.TaxonomyHeads.AsNoTracking().Select(x => x.RevisionId).SingleAsync(ct);
		var run = await db.OrganizationRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Active &&
			x.PolicyRevision == settings.Revision && x.RestoreGeneration == settings.RestoreGeneration &&
			x.TaxonomyRevision == taxonomyRevision, ct);
		if (run == null) { return new(true, "awaiting_run", 0, 0, 0, 0, 0, null); }
		var counts = await db.OrganizationItems.AsNoTracking().Where(x => x.RunId == run.Id)
			.GroupBy(x => x.State).Select(x => new { State = x.Key, Count = x.LongCount() })
			.ToDictionaryAsync(x => x.State, x => x.Count, ct);
		long sources = await db.OrganizationItems.AsNoTracking()
			.Where(x => x.RunId == run.Id && x.State == "observed" && x.SourceId != null)
			.Select(x => x.SourceId).Distinct().LongCountAsync(ct);
		return new(true, run.State, sources, counts.GetValueOrDefault("observed"),
			counts.GetValueOrDefault("protected"), counts.GetValueOrDefault("unavailable"),
			counts.GetValueOrDefault("invalid_source"), run.UpdatedAt);
	}

	private static bool CanDiscover(Entities.CategoryAssistance.CategoryAssistanceSettingsEntity settings) =>
		settings.Enabled && CategoryAssistancePolicy.IsValid(CategoryAssistanceSettingsStore.Read(settings));

	private static bool EligibleAssignment(PurchaseClassificationEntity? current) =>
			OrganizationEffectPolicy.Eligibility(current == null ? null : new PurchaseClassification(
					current.Id, current.ReceiptItemId, current.Sequence, current.PreviousAssignmentId,
					current.CategoryId, current.TaxonomyRevisionId, current.RecordedAt, current.Actor,
					current.Source, current.Reason, current.OperationId, [], current.LegacyCategory,
					current.LegacySubcategory, current.LegacyResolution), available: true,
					leaveAsIs: false, suppressed: false) == OrganizationEligibility.Eligible;
	private sealed class SourceCandidate
	{
		public Guid Id { get; set; }
		public Guid ReceiptId { get; set; }
		public string Description { get; set; } = "";
		public string? ReceiptItemCode { get; set; }
	}

	public async Task<Guid> StartAsync(Guid expectedSettingsRevision, CancellationToken ct)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		var head = await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
		var settings = await db.CategoryAssistanceSettings.FromSqlRaw("SELECT * FROM category_assistance.\"Settings\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
		if (!CanDiscover(settings)) { return Guid.Empty; }
		if (expectedSettingsRevision == Guid.Empty || settings.Revision != expectedSettingsRevision)
		{
			throw new InvalidOperationException("organization_context_changed");
		}

		OrganizationRunEntity? active = await db.OrganizationRuns.SingleOrDefaultAsync(x => x.Active, ct);
		if (active != null && active.PolicyRevision == settings.Revision &&
			active.RestoreGeneration == settings.RestoreGeneration && active.TaxonomyRevision == head.RevisionId)
		{
			return active.Id;
		}

		if (active != null)
		{
			active.Active = false;
			active.State = "stale";
			active.UpdatedAt = clock.GetUtcNow();
			await db.SaveChangesAsync(ct);
		}
		Guid? highWater = await db.ReceiptItems.IgnoreQueryFilters().AsNoTracking()
			.OrderByDescending(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
		OrganizationRunEntity run = new()
		{
			Id = Guid.NewGuid(),
			PolicyRevision = settings.Revision,
			RestoreGeneration = settings.RestoreGeneration,
			TaxonomyRevision = head.RevisionId,
			HighWater = highWater,
			State = highWater == null ? "watching" : "scanning",
			CreatedAt = clock.GetUtcNow(),
			UpdatedAt = clock.GetUtcNow()
		};
		db.OrganizationRuns.Add(run);
		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);
		return run.Id;
	}

	/// <summary>One sweep page, one item-inbox page and one source-expansion page at most; cursor, captures and inbox acknowledgments commit together. Returns work units including source candidates or an empty-source acknowledgment, not a distinct purchase count.</summary>
	public async Task<int> DiscoverAsync(Guid runId, int chunkSize, CancellationToken ct)
	{
		if (chunkSize is < 1 or > MaximumChunkSize)
		{
			throw new ArgumentOutOfRangeException(nameof(chunkSize));
		}

		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		// Shared ordering with existing receipt/classification/restore machinery.
		await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({0x52435054}, {0x4E4F524D})", ct);
		var head = await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
		var settings = await db.CategoryAssistanceSettings.FromSqlRaw("SELECT * FROM category_assistance.\"Settings\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
		OrganizationRunEntity run = await db.OrganizationRuns.FromSqlInterpolated($"SELECT * FROM organization.\"Runs\" WHERE \"Id\"={runId} FOR UPDATE").SingleAsync(ct);
		if (!run.Active)
		{
			return 0;
		}
		if (run.PolicyRevision != settings.Revision || run.RestoreGeneration != settings.RestoreGeneration || run.TaxonomyRevision != head.RevisionId)
		{
			run.Active = false;
			run.State = "stale";
			run.UpdatedAt = clock.GetUtcNow();
			await db.SaveChangesAsync(ct);
			await tx.CommitAsync(ct);
			return 0;
		}
		// Disabling settings is the durable stop switch, checked under the same lock as updates/restore.
		if (!CanDiscover(settings)) { return 0; }
		// Lock inbox rows before reading committed facts. A concurrent producer's UPSERT waits;
		// after our DELETE commits it inserts a new row, rather than losing the newer change.
		List<OrganizationDirtyItemEntity> dirty = await db.OrganizationDirtyItems.FromSqlInterpolated(
			$"SELECT * FROM organization.\"DirtyItems\" ORDER BY \"ItemId\" LIMIT {chunkSize} FOR UPDATE SKIP LOCKED").ToListAsync(ct);
		List<Guid> page = [];
		if (run.State == "scanning")
		{
			page = await db.ReceiptItems.IgnoreQueryFilters().AsNoTracking()
				.Where(x => (!run.Cursor.HasValue || x.Id.CompareTo(run.Cursor.Value) > 0) &&
					run.HighWater.HasValue && x.Id.CompareTo(run.HighWater.Value) <= 0)
				.OrderBy(x => x.Id).Select(x => x.Id).Take(chunkSize).ToListAsync(ct);
			if (page.Count > 0)
			{
				run.Cursor = page[^1];
			}

			if (page.Count < chunkSize)
			{
				run.State = "watching";
			}
		}
		List<Guid> sourcePage = [];
		int sourceExamined = 0;
		OrganizationDirtySourceEntity? dirtySource = await db.OrganizationDirtySources.FromSqlRaw(
			"SELECT * FROM organization.\"DirtySources\" ORDER BY \"Id\" LIMIT 1 FOR UPDATE SKIP LOCKED").SingleOrDefaultAsync(ct);
		if (dirtySource != null)
		{
			// Expression indexes narrow the keyset to this description/code across stores.
			// Exact equality rechecks every hash hit; MD5 never establishes source identity.
			Guid cursor = dirtySource.Cursor ?? Guid.Empty;
			List<SourceCandidate> candidates = dirtySource.Kind == "raw"
				? await db.Database.SqlQuery<SourceCandidate>($"""
                    SELECT "Id", "ReceiptId", "Description", "ReceiptItemCode"
                    FROM receipts."ReceiptItems"
                    WHERE "Id" >= {cursor} AND ({dirtySource.Cursor == null} OR "Id" > {cursor})
                      AND md5("Description" || chr(31) || coalesce("ReceiptItemCode", '')) =
                          md5({dirtySource.Description} || chr(31) || coalesce(CAST({dirtySource.ItemCode} AS text), ''))
                      AND "Description" COLLATE "C" = {dirtySource.Description}
                      AND "ReceiptItemCode" COLLATE "C" IS NOT DISTINCT FROM CAST({dirtySource.ItemCode} AS text)
                    ORDER BY "Id" LIMIT {chunkSize}
                    """).ToListAsync(ct)
				: await db.Database.SqlQuery<SourceCandidate>($"""
                    SELECT "Id", "ReceiptId", "Description", "ReceiptItemCode"
                    FROM receipts."ReceiptItems"
                    WHERE "Id" >= {cursor} AND ({dirtySource.Cursor == null} OR "Id" > {cursor})
                      AND md5(btrim("Description") || chr(31) || coalesce(nullif(btrim("ReceiptItemCode"), ''), '')) =
                          md5({dirtySource.Description} || chr(31) || coalesce(CAST({dirtySource.ItemCode} AS text), ''))
                      AND btrim("Description") COLLATE "C" = {dirtySource.Description}
                      AND nullif(btrim("ReceiptItemCode"), '') COLLATE "C" IS NOT DISTINCT FROM CAST({dirtySource.ItemCode} AS text)
                    ORDER BY "Id" LIMIT {chunkSize}
                    """).ToListAsync(ct);
			// Acknowledging an empty final page is work even if other sources remain.
			sourceExamined = Math.Max(1, candidates.Count);
			Guid[] receiptIds = candidates.Select(x => x.ReceiptId).Distinct().ToArray();
			var locations = await db.Receipts.IgnoreQueryFilters().AsNoTracking().Where(x => receiptIds.Contains(x.Id))
				.Select(x => new { x.Id, x.Location }).ToDictionaryAsync(x => x.Id, x => x.Location, ct);
			foreach (var candidate in candidates)
			{
				if (!locations.TryGetValue(candidate.ReceiptId, out string? location))
				{
					continue;
				}

				bool match = dirtySource.Kind == "raw"
					? location == dirtySource.Store && candidate.Description == dirtySource.Description && candidate.ReceiptItemCode == dirtySource.ItemCode
					: location.Trim(' ') == dirtySource.Store && candidate.Description.Trim(' ') == dirtySource.Description &&
						(string.IsNullOrEmpty(candidate.ReceiptItemCode?.Trim(' ')) ? null : candidate.ReceiptItemCode.Trim(' ')) == dirtySource.ItemCode;
				if (match)
				{
					sourcePage.Add(candidate.Id);
				}
			}
			if (candidates.Count < chunkSize)
			{
				db.OrganizationDirtySources.Remove(dirtySource);
			}
			else
			{
				dirtySource.Cursor = candidates[^1].Id;
			}
		}
		Guid[] ids = page.Concat(dirty.Select(x => x.ItemId)).Concat(sourcePage).Distinct().ToArray();
		var facts = await (from item in db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
						   join receipt in db.Receipts.IgnoreQueryFilters().AsNoTracking() on item.ReceiptId equals receipt.Id
						   where ids.Contains(item.Id)
						   select new
						   {
							   item.Id,
							   item.ReceiptId,
							   ItemRevision = item.NormalizationRevision,
							   ReceiptRevision = receipt.NormalizationRevision,
							   receipt.Location,
							   item.Description,
							   Code = item.ReceiptItemCode,
							   item.Category,
							   item.Subcategory,
							   Live = item.DeletedAt == null && receipt.DeletedAt == null
						   }).ToListAsync(ct);
		var assignments = await (from current in db.CurrentClassifications.AsNoTracking()
								 join assignment in db.Classifications.AsNoTracking() on current.AssignmentId equals assignment.Id
								 where ids.Contains(current.ReceiptItemId)
								 select assignment).ToDictionaryAsync(x => x.ReceiptItemId, ct);
		Dictionary<Guid, OrganizationItemEntity> existing = await db.OrganizationItems
			.Where(x => x.RunId == run.Id && ids.Contains(x.ItemId)).ToDictionaryAsync(x => x.ItemId, ct);
		var factsById = facts.ToDictionary(x => x.Id);
		// A null legacy observation is not a deliberate manual clear. Preserve every other
		// assignment and raw label; discovery still grants no authority to apply an effect.
		var candidatesById = facts.Where(x => x.Live && EligibleAssignment(assignments.GetValueOrDefault(x.Id)) &&
	string.IsNullOrWhiteSpace(x.Category) && string.IsNullOrWhiteSpace(x.Subcategory)).ToDictionary(x => x.Id);
		Guid[] candidateIds = candidatesById.Keys.ToArray();
		// Two correlated existence checks produce a bounded set of IDs in one round trip.
		HashSet<Guid> suppressed = (await (from item in db.ReceiptItems.IgnoreQueryFilters().AsNoTracking()
										   join receipt in db.Receipts.IgnoreQueryFilters().AsNoTracking() on item.ReceiptId equals receipt.Id
										   where candidateIds.Contains(item.Id) &&
											   (db.HistoricalGroupDispositions.Any(d => d.Store == receipt.Location && d.RawDescription == item.Description &&
												   d.ItemCode == item.ReceiptItemCode && d.Disposition != null) ||
												(from preference in db.CategoryAssistanceReviewPreferences
												 join attempt in db.CategoryAssistanceAttempts on preference.AttemptId equals attempt.Id
												 where attempt.Location == receipt.Location.Trim() && attempt.Description == item.Description.Trim() &&
												   attempt.ItemCodeKey == (item.ReceiptItemCode == null ? "" : item.ReceiptItemCode.Trim()) &&
												   (preference.State != "active" || preference.ExcludedItemIdsJson != "[]")
												 select preference.AttemptId).Any())
										   select item.Id).ToListAsync(ct)).ToHashSet();
		Dictionary<Guid, (NormalizationSourceKey Key, string Hash)> sourceKeys = [];
		foreach (var fact in candidatesById.Values.Where(x => !suppressed.Contains(x.Id)))
		{
			try
			{
				NormalizationSourceKey key = NormalizationCatalogPolicy.Normalize(new(fact.Location, fact.Description, fact.Code));
				string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { fact.Location, fact.Description, fact.Code })));
				sourceKeys.Add(fact.Id, (key, hash));
			}
			catch (NormalizationCatalogException) { }
		}
		string[] rawHashes = sourceKeys.Values.Select(x => x.Hash).Distinct().ToArray();
		Dictionary<string, OrganizationSourceEntity> sources = await db.OrganizationSources
			.Where(x => x.RunId == run.Id && rawHashes.Contains(x.RawHash)).ToDictionaryAsync(x => x.RawHash, ct);
		foreach (Guid id in ids)
		{
			if (!existing.TryGetValue(id, out OrganizationItemEntity? manifest))
			{
				manifest = new() { RunId = run.Id, ItemId = id };
				db.OrganizationItems.Add(manifest);
			}
			manifest.ObservedAt = clock.GetUtcNow();
			factsById.TryGetValue(id, out var fact);
			if (fact == null)
			{
				manifest.State = "unavailable";
				continue;
			}
			manifest.ReceiptId = fact.ReceiptId;
			manifest.ItemRevision = fact.ItemRevision;
			manifest.ReceiptRevision = fact.ReceiptRevision;
			assignments.TryGetValue(id, out var assignment);
			manifest.ExpectedAssignmentId = assignment?.Id;
			manifest.PreviousCategoryId = assignment?.CategoryId;
			manifest.State = !fact.Live ? "unavailable" : candidatesById.ContainsKey(id) ? "observed" : "protected";
			if (manifest.State != "observed" || suppressed.Contains(id))
			{
				manifest.SourceId = null;
				if (suppressed.Contains(id))
				{
					manifest.State = "protected";
				}

				continue;
			}
			if (!sourceKeys.TryGetValue(id, out var sourceKey))
			{
				manifest.SourceId = null;
				manifest.State = "invalid_source";
				continue;
			}
			NormalizationSourceKey key = sourceKey.Key;
			string rawHash = sourceKey.Hash;
			sources.TryGetValue(rawHash, out OrganizationSourceEntity? source);
			if (source != null && (source.Store != fact.Location || source.Description != fact.Description || source.ItemCode != fact.Code))
			{
				throw new InvalidOperationException("organization_source_hash_collision");
			}

			if (source == null)
			{
				source = new()
				{
					Id = Guid.NewGuid(),
					RunId = run.Id,
					RawHash = rawHash,
					Store = fact.Location,
					Description = fact.Description,
					ItemCode = fact.Code,
					NormalizedHash = NormalizationCatalogPolicy.Hash(key),
					NormalizedStore = key.Location,
					NormalizedDescription = key.Description,
					NormalizedItemCode = key.ItemCode
				};
				db.OrganizationSources.Add(source);
				sources.Add(rawHash, source);
			}
			manifest.SourceId = source.Id;

		}
		db.OrganizationDirtyItems.RemoveRange(dirty);
		run.UpdatedAt = clock.GetUtcNow();
		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);
		// Include source acknowledgment so exact-multiple/empty final pages cannot stop a drain early.
		return ids.Length + sourceExamined;
	}
}
