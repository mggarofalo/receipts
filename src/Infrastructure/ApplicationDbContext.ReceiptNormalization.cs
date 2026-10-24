using Application.Normalization;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	internal bool RestoringNormalizationEvidence { get; set; }
	internal HashSet<Guid> RestoredReviewEvidence { get; } = [];
	internal string? ReceiptWriteActor => _currentUserAccessor?.UserId;
	public DbSet<ReceiptWriteRequestEntity> ReceiptWriteRequests { get; set; } = null!;
	public DbSet<NormalizationReviewWorkEntity> NormalizationReviewWork { get; set; } = null!;

	// Called only by explicitly approved legacy review use cases after their source/target guards.
	internal async Task RecordReviewedAssignmentsAsync(IReadOnlyCollection<ReceiptItemEntity> items, string actor, CancellationToken ct)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		if (!await Users.AnyAsync(user => user.Id == actor && !(user.LockoutEnabled && user.LockoutEnd > now), ct))
		{ throw new NormalizationCatalogException(403, "review_actor_required", "An enabled signed-in user is required to confirm a normalization choice."); }
		Guid[] parentIds = items.Select(item => item.ReceiptId).Distinct().ToArray();
		var parents = await Receipts.IgnoreAutoIncludes().Where(parent => parentIds.Contains(parent.Id)).ToDictionaryAsync(parent => parent.Id, ct);
		foreach (var item in items)
		{
			if (item.DeletedAt is not null || !parents.TryGetValue(item.ReceiptId, out var parent))
			{ throw ReviewConflict("normalization_item_changed", "A selected purchase is no longer available."); }
			string hash = NormalizationCatalogPolicy.Hash(new(parent.Location, item.Description, item.ReceiptItemCode));
			item.NormalizationOrigin = "reviewed";
			item.NormalizationMappingId = null;
			item.NormalizationAssignmentId = Guid.NewGuid();
			item.NormalizationReviewDecisionId = item.NormalizationAssignmentId;
			item.NormalizationReviewState = NormalizationReviewState.Verified;
			item.NormalizationReviewReason = "purchase_review";
			item.NormalizationSourceHash = hash;
			item.ReviewedSourceHash = hash;
			item.ReviewedTargetId = item.NormalizedDescriptionId;
			item.ReviewedBy = actor;
			item.ReviewedAt = now;
			item.ReviewMappingRevision = null;
		}
	}

	private bool HasNormalizationWrites() => ChangeTracker.Entries().Any(entry =>
		entry.Entity is ReceiptItemEntity item && (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted || item.NormalizationIntent is not null)
		|| entry.Entity is ReceiptEntity && (entry.State is EntityState.Added or EntityState.Deleted
			|| entry.State == EntityState.Modified && (entry.Property(nameof(ReceiptEntity.Location)).IsModified || entry.Property(nameof(ReceiptEntity.DeletedAt)).IsModified))
		|| entry.Entity is NormalizationSourceMappingEntity && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
		|| entry.Entity is NormalizedDescriptionEntity && (entry.State is EntityState.Added or EntityState.Deleted
			|| entry.State == EntityState.Modified && entry.Property(nameof(NormalizedDescriptionEntity.Status)).IsModified));

	private async Task PrepareReceiptNormalizationAsync(CancellationToken ct)
	{
		// All application writers share this short local transaction. Never perform retrieval or inference here.
		Guid[] changedParents = ChangeTracker.Entries<ReceiptEntity>().Where(entry => entry.State == EntityState.Modified
			&& (entry.Property(value => value.Location).IsModified || entry.Property(value => value.DeletedAt).IsModified))
			.Select(entry => entry.Entity.Id).ToArray();
		if (changedParents.Length > 0)
		{
			await ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().Where(item => changedParents.Contains(item.ReceiptId)).LoadAsync(ct).ConfigureAwait(false);
		}
		Guid[] withdrawnMappings = ChangeTracker.Entries<NormalizationSourceMappingEntity>()
			.Where(entry => entry.State == EntityState.Modified || entry.State == EntityState.Deleted).Select(entry => entry.Entity.Id).ToArray();
		Guid[] changedTargets = ChangeTracker.Entries<NormalizedDescriptionEntity>().Where(entry => entry.State == EntityState.Deleted
			|| entry.State == EntityState.Modified && entry.Property(value => value.Status).IsModified).Select(entry => entry.Entity.Id).ToArray();
		if (withdrawnMappings.Length > 0 || changedTargets.Length > 0)
		{
			await ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().Where(item =>
				(item.NormalizationMappingId != null && withdrawnMappings.Contains(item.NormalizationMappingId.Value))
				|| (item.NormalizedDescriptionId != null && changedTargets.Contains(item.NormalizedDescriptionId.Value))).LoadAsync(ct).ConfigureAwait(false);
		}
		EntityEntry<ReceiptItemEntity>[] entries = ChangeTracker.Entries<ReceiptItemEntity>().Where(entry =>
			entry.State is EntityState.Added or EntityState.Modified || entry.Entity.NormalizationIntent is not null || changedParents.Contains(entry.Entity.ReceiptId)
			|| entry.Entity.NormalizationMappingId is Guid mappingId && withdrawnMappings.Contains(mappingId)
			|| entry.Entity.NormalizedDescriptionId is Guid targetId && changedTargets.Contains(targetId)).ToArray();
		if (entries.Length == 0) { return; }
		Guid[] parentIds = entries.Select(entry => entry.Entity.ReceiptId).Distinct().ToArray();
		var parents = await Receipts.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking().Where(parent => parentIds.Contains(parent.Id))
			.Select(parent => new { parent.Id, parent.Location, parent.DeletedAt }).ToDictionaryAsync(parent => parent.Id, ct).ConfigureAwait(false);
		foreach (var entry in ChangeTracker.Entries<ReceiptEntity>().Where(entry => parentIds.Contains(entry.Entity.Id)))
		{
			parents[entry.Entity.Id] = new { entry.Entity.Id, entry.Entity.Location, entry.Entity.DeletedAt };
		}
		var sources = entries.Where(entry => parents.ContainsKey(entry.Entity.ReceiptId)).ToDictionary(entry => entry.Entity.Id,
			entry => NormalizationCatalogPolicy.Normalize(new NormalizationSourceKey(parents[entry.Entity.ReceiptId].Location, entry.Entity.Description, entry.Entity.ReceiptItemCode)));
		var hashes = sources.ToDictionary(pair => pair.Key, pair => NormalizationCatalogPolicy.Hash(pair.Value));
		string[] sourceHashes = hashes.Values.Distinct().ToArray();
		var mappings = await NormalizationSourceMappings.Where(mapping => sourceHashes.Contains(mapping.SourceHash) && mapping.State == "active")
			.ToDictionaryAsync(mapping => mapping.SourceHash, ct).ConfigureAwait(false);
		foreach (var entry in ChangeTracker.Entries<NormalizationSourceMappingEntity>())
		{
			if (entry.Entity.State == "active" && sourceHashes.Contains(entry.Entity.SourceHash)) { mappings[entry.Entity.SourceHash] = entry.Entity; }
			else { mappings.Remove(entry.Entity.SourceHash); }
		}
		Guid[] targetIds = entries.Select(entry => entry.Entity.NormalizationIntent?.TargetId ?? entry.Entity.NormalizedDescriptionId)
			.Concat(mappings.Values.Select(mapping => mapping.TargetId)).Where(id => id.HasValue).Select(id => id!.Value).Distinct().Order().ToArray();
		await NormalizationWriteGuard.LockTargetsAsync(this, targetIds, ct).ConfigureAwait(false);
		var targets = await NormalizedDescriptions.IgnoreAutoIncludes().Where(target => targetIds.Contains(target.Id)).ToDictionaryAsync(target => target.Id, ct).ConfigureAwait(false);
		foreach (var entry in ChangeTracker.Entries<NormalizedDescriptionEntity>())
		{
			if (entry.State != EntityState.Deleted) { targets[entry.Entity.Id] = entry.Entity; }
			else { targets.Remove(entry.Entity.Id); }
		}
		bool actorChecked = false;
		// Repeated explicit new-product choices within this save share the identity created here.
		// This cache never admits a pre-existing catalog collision as a reviewed choice.
		Dictionary<string, Guid> createdTargets = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, bool> sourceAmbiguity = new(StringComparer.Ordinal);
		HashSet<string> stagedDivergentSources = entries.Where(entry => hashes.ContainsKey(entry.Entity.Id)
			&& mappings.TryGetValue(hashes[entry.Entity.Id], out var remembered)
			&& (entry.Entity.NormalizationIntent?.Kind == ReceiptNormalizationIntentKind.ConfirmNew
				|| entry.Entity.NormalizationIntent is { Kind: ReceiptNormalizationIntentKind.ConfirmExisting } choice && choice.TargetId != remembered.TargetId))
			.Select(entry => hashes[entry.Entity.Id]).ToHashSet(StringComparer.Ordinal);
		foreach (var entry in entries)
		{
			ReceiptItemEntity item = entry.Entity;
			if (!hashes.TryGetValue(item.Id, out string? hash)) { continue; }
			bool sourceChanged = item.NormalizationSourceHash != hash;
			item.NormalizationSourceHash = hash;
			if (RestoringNormalizationEvidence)
			{
				if (!RestoredReviewEvidence.Contains(item.Id))
				{
					item.NormalizationReviewState = NormalizationReviewState.NeedsReview;
					item.NormalizationReviewReason = item.NormalizedDescriptionId.HasValue ? "legacy_unknown" : "unknown_source";
					item.NormalizationReviewDecisionId = null;
					item.ReviewedSourceHash = null;
					item.ReviewMappingRevision = null;
					item.ReviewedBy = null;
					item.ReviewedAt = null;
				}
				continue;
			}
			ReceiptNormalizationIntent? intent = item.NormalizationIntent;
			if (intent is not null)
			{
				ReceiptNormalizationPolicy.Validate(intent, hash);
				if (entry.State != EntityState.Added && intent.ExpectedItemRevision != item.NormalizationRevision)
				{
					throw ReviewConflict("normalization_item_changed", "This purchase changed. Reload it before confirming a normalization choice.");
				}
				if (intent.Kind is ReceiptNormalizationIntentKind.ConfirmExisting or ReceiptNormalizationIntentKind.ConfirmNew or ReceiptNormalizationIntentKind.Exclude)
				{
					if (!actorChecked)
					{
						string? actor = _currentUserAccessor?.UserId;
						DateTimeOffset now = DateTimeOffset.UtcNow;
						if (actor is null || !await Users.AnyAsync(user => user.Id == actor && !(user.LockoutEnabled && user.LockoutEnd > now), ct).ConfigureAwait(false))
						{ throw new NormalizationCatalogException(403, "review_actor_required", "An enabled signed-in user is required to confirm a normalization choice."); }
						actorChecked = true;
					}
				}
			}
			if (item.DeletedAt is not null || parents[item.ReceiptId].DeletedAt is not null)
			{
				item.NormalizationReviewState = NormalizationReviewState.NeedsReview;
				item.NormalizationReviewReason = "unavailable";
				continue;
			}
			NormalizationSourceMappingEntity? mapping = mappings.GetValueOrDefault(hash);
			bool mappingValid = mapping is not null && (mapping.Exclude || (mapping.TargetId is Guid mappedTarget
				&& targets.TryGetValue(mappedTarget, out var mappedIdentity) && mappedIdentity.Status == NormalizedDescriptionStatus.Active));
			if (mappingValid)
			{
				if (!sourceAmbiguity.TryGetValue(hash, out bool ambiguous))
				{
					ambiguous = stagedDivergentSources.Contains(hash) || await NormalizationCatalogService.SourceItems(this, sources[item.Id])
						.Where(value => value.NormalizedDescriptionId != null).Select(value => value.NormalizedDescriptionId).Distinct().Take(2).CountAsync(ct) > 1;
					sourceAmbiguity.Add(hash, ambiguous);
				}
				mappingValid = !ambiguous;
			}
			if (intent?.ExpectedMappingRevision is Guid expectedMapping && (!mappingValid || mapping!.Revision != expectedMapping))
			{ throw ReviewConflict("normalization_mapping_changed", "The remembered source decision changed. Refresh or explicitly defer review."); }
			bool targetActive = item.NormalizedDescriptionId is Guid currentTarget && targets.TryGetValue(currentTarget, out var currentIdentity)
				&& currentIdentity.Status == NormalizedDescriptionStatus.Active;
			bool independentReview = item.NormalizationReviewReason is "purchase_review" or "purchase_exclusion";
			bool evidenceCurrent = ReceiptNormalizationPolicy.EvidenceIsCurrent(new(
				!sourceChanged && item.ReviewedSourceHash == hash,
				item.NormalizationReviewDecisionId.HasValue && item.NormalizationReviewDecisionId == item.NormalizationAssignmentId,
				item.ReviewedTargetId == item.NormalizedDescriptionId, targetActive, independentReview,
				mappingValid && item.NormalizationMappingId == mapping!.Id && item.ReviewMappingRevision == mapping.Revision,
				item.NormalizationReviewState));
			if (intent is null && evidenceCurrent) { continue; }
			// Price/quantity/date writes do not manufacture verification from a legacy link or origin string.
			if (intent is null && !sourceChanged && entry.State != EntityState.Added && item.NormalizationReviewState == NormalizationReviewState.NeedsReview
				&& !entry.Property(value => value.NormalizationAssignmentId).IsModified) { continue; }
			Guid? targetId = null;
			if (intent?.Kind == ReceiptNormalizationIntentKind.ConfirmExisting)
			{
				if (!targets.TryGetValue(intent.TargetId!.Value, out var target) || target.Status != NormalizedDescriptionStatus.Active || target.NormalizationRevision != intent.ExpectedTargetRevision)
				{ throw ReviewConflict("normalization_target_changed", "The selected product changed or is not active. Refresh the choice or explicitly defer review."); }
				targetId = target.Id;
			}
			else if (intent?.Kind == ReceiptNormalizationIntentKind.ConfirmNew
				&& createdTargets.TryGetValue(intent.NewName!.Trim(), out Guid createdTarget))
			{
				targetId = createdTarget;
			}
			else if (intent?.Kind == ReceiptNormalizationIntentKind.ConfirmNew)
			{
				string name = intent.NewName!.Trim();
				string lower = name.ToLowerInvariant();
				NormalizedDescriptionEntity? collision = targets.Values.FirstOrDefault(target => target.CanonicalName.Equals(name, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(target.DisplayLabel, name, StringComparison.OrdinalIgnoreCase))
					?? await NormalizedDescriptions.IgnoreAutoIncludes().FirstOrDefaultAsync(target => target.CanonicalName.ToLower() == lower || (target.DisplayLabel != null && target.DisplayLabel.ToLower() == lower), ct).ConfigureAwait(false);
				if (collision is not null)
				{
					throw new NormalizationCatalogException(409, "normalization_name_exists", "A catalog identity already uses that name. Review the existing identity instead of creating a duplicate.",
						new Dictionary<string, object?> { ["normalizedDescriptionId"] = collision.Id, ["normalizedDescriptionStatus"] = collision.Status.ToString() });
				}
				NormalizedDescriptionEntity created = new() { Id = Guid.NewGuid(), CanonicalName = name, Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow };
				NormalizedDescriptions.Add(created);
				targets.Add(created.Id, created);
				createdTargets.Add(name, created.Id);
				targetId = created.Id;
			}
			if (intent?.RememberSource == true)
			{
				bool exclude = intent.Kind == ReceiptNormalizationIntentKind.Exclude;
				if (mapping is not null && (mapping.TargetId != targetId || mapping.Exclude != exclude))
				{ throw ReviewConflict("normalization_mapping_conflict", "This source already has a different remembered decision. Use the scoped correction preview; this receipt cannot change history."); }
				NormalizationSourceKey key = sources[item.Id];
				bool stagedConflict = entries.Any(other => other.Entity.Id != item.Id && hashes.GetValueOrDefault(other.Entity.Id) == hash
					&& other.Entity.NormalizationIntent is { } otherIntent
					&& (otherIntent.Kind == ReceiptNormalizationIntentKind.ConfirmExisting && (exclude || otherIntent.TargetId != targetId)
						|| otherIntent.Kind == ReceiptNormalizationIntentKind.Exclude && !exclude
						|| otherIntent.Kind == ReceiptNormalizationIntentKind.ConfirmNew && (exclude || !string.Equals(otherIntent.NewName?.Trim(), targets.GetValueOrDefault(targetId ?? Guid.Empty)?.CanonicalName, StringComparison.OrdinalIgnoreCase))));
				if (stagedConflict) { throw ReviewConflict("normalization_source_ambiguous", "These draft rows assign different products to the same source. Confirm each purchase without remembering an unconditional mapping."); }
				bool mixed = await NormalizationCatalogService.SourceItems(this, key).AnyAsync(other => other.Id != item.Id && other.NormalizedDescriptionId != null
					&& other.NormalizedDescriptionId != targetId, ct).ConfigureAwait(false);
				if (mixed) { throw ReviewConflict("normalization_source_ambiguous", "This exact source has different recorded identities. Confirm this purchase only, or review the source history before remembering it."); }
				if (mapping is null)
				{
					mapping = new()
					{
						Id = Guid.NewGuid(),
						SourceHash = hash,
						Location = key.Location,
						Description = key.Description,
						ItemCode = key.ItemCode,
						TargetId = targetId,
						Exclude = exclude,
						ExclusionReason = intent.Reason,
						ApprovedBy = _currentUserAccessor!.UserId!,
						ApprovedAt = DateTimeOffset.UtcNow
					};
					NormalizationSourceMappings.Add(mapping);
					mappings.Add(hash, mapping);
				}
			}
			ReceiptNormalizationDecision decision = ReceiptNormalizationPolicy.Decide(intent?.Kind, mappingValid, mapping?.Exclude == true);
			item.NormalizationReviewState = decision.State;
			item.NormalizationReviewReason = decision.Reason;
			item.NormalizationExclusionReason = decision.State == NormalizationReviewState.Excluded ? intent?.Reason ?? mapping?.ExclusionReason ?? "Approved source exclusion" : null;
			if (decision.State == NormalizationReviewState.NeedsReview)
			{
				if (item.NormalizedDescriptionId.HasValue && !sourceChanged) { item.NormalizationReviewReason = intent is null ? "legacy_unknown" : decision.Reason; }
				item.NormalizationReviewDecisionId = null;
				item.ReviewMappingRevision = null;
				item.ReviewedSourceHash = null;
				continue;
			}
			bool explicitChoice = intent?.Kind is ReceiptNormalizationIntentKind.ConfirmExisting or ReceiptNormalizationIntentKind.ConfirmNew or ReceiptNormalizationIntentKind.Exclude;
			item.NormalizedDescriptionId = explicitChoice ? targetId : mapping!.TargetId;
			item.NormalizedDescriptionMatchScore = null;
			item.NormalizationOrigin = explicitChoice ? decision.State == NormalizationReviewState.Excluded ? "purchase_excluded" : "reviewed" : mapping!.Exclude ? "excluded" : "mapped";
			item.NormalizationMappingId = explicitChoice ? null : mapping!.Id;
			item.NormalizationAssignmentId = explicitChoice ? Guid.NewGuid() : item.NormalizationAssignmentId ?? Guid.NewGuid();
			item.NormalizationReviewDecisionId = item.NormalizationAssignmentId;
			item.ReviewedSourceHash = hash;
			item.ReviewedTargetId = item.NormalizedDescriptionId;
			item.ReviewedBy = explicitChoice ? _currentUserAccessor!.UserId : mapping!.ApprovedBy;
			item.ReviewedAt = DateTimeOffset.UtcNow;
			item.ReviewMappingRevision = explicitChoice ? null : mapping!.Revision;
		}
		if (Database.IsNpgsql())
		{
			// The downgrade-only SQL guards recognize exactly these locally validated rows.
			// Transaction-local scope expires on commit/rollback and is replaced at each save.
			string prepared = "," + string.Join(',', entries.Select(entry => entry.Entity.Id)) + ",";
			await Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('receipts.review_prepared', {prepared}, true)", ct).ConfigureAwait(false);
		}
	}

	private async Task ReconcileNormalizationReviewWorkAsync(CancellationToken ct)
	{
		if (!Database.IsNpgsql()) { return; }
		Guid[] ids = ChangeTracker.Entries<ReceiptItemEntity>().Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
			.Select(entry => entry.Entity.Id).Distinct().ToArray();
		if (ids.Length == 0) { return; }
		await Database.ExecuteSqlInterpolatedAsync($"""
			INSERT INTO matching."NormalizationReviewWork" ("ItemId", "ReceiptId", "SourceHash", "SourceRevision", "CreatedAt", "UpdatedAt")
			SELECT i."Id", i."ReceiptId", i."NormalizationSourceHash", i."NormalizationRevision", now(), now()
			FROM receipts."ReceiptItems" i JOIN receipts."Receipts" r ON r."Id" = i."ReceiptId"
			WHERE i."Id" = ANY ({ids}) AND i."DeletedAt" IS NULL AND r."DeletedAt" IS NULL
			AND i."NormalizationReviewState" = 'NeedsReview' AND i."NormalizationSourceHash" IS NOT NULL
			ON CONFLICT ("ItemId") DO UPDATE SET "ReceiptId" = EXCLUDED."ReceiptId", "SourceHash" = EXCLUDED."SourceHash",
			"SourceRevision" = EXCLUDED."SourceRevision", "UpdatedAt" = now(),
			"CreatedAt" = CASE WHEN matching."NormalizationReviewWork"."SourceHash" = EXCLUDED."SourceHash"
			THEN matching."NormalizationReviewWork"."CreatedAt" ELSE now() END;
			""", ct).ConfigureAwait(false);
		await Database.ExecuteSqlInterpolatedAsync($"""
			DELETE FROM matching."NormalizationReviewWork" w WHERE w."ItemId" = ANY ({ids}) AND NOT EXISTS (
			SELECT 1 FROM receipts."ReceiptItems" i JOIN receipts."Receipts" r ON r."Id" = i."ReceiptId"
			WHERE i."Id" = w."ItemId" AND i."DeletedAt" IS NULL AND r."DeletedAt" IS NULL AND i."NormalizationReviewState" = 'NeedsReview');
			""", ct).ConfigureAwait(false);
	}

	private static NormalizationCatalogException ReviewConflict(string code, string message) => new(409, code, message);
}
