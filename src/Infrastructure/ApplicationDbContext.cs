using System.Text.Json;
using Application.Interfaces.Services;
using Common;
using Infrastructure.Entities;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
	private const string PostgreSQL = "Npgsql.EntityFrameworkCore.PostgreSQL";
	private const string InMemory = "Microsoft.EntityFrameworkCore.InMemory";
	private const string DatabaseProviderNotSupported = "Database provider {0} not supported";

	private readonly ICurrentUserAccessor? _currentUserAccessor;
	private readonly IDescriptionChangeSignal? _descriptionChangeSignal;

	// [ActivatorUtilitiesConstructor] so the (singleton) IDbContextFactory injects the current-user
	// accessor and the description-change signal when it builds a context via ActivatorUtilities.
	// Both dependencies are singletons, so resolving them from the factory's root provider is legal
	// (no captive dependency). Before RECEIPTS-753 this attribute sat on the options-only ctor below,
	// which left both fields null on every factory-created context (the primary write path).
	// [ActivatorUtilitiesConstructor] so the (singleton) IDbContextFactory injects the current-user
	// accessor and the description-change signal when it builds a context via ActivatorUtilities.
	// Both dependencies are singletons, so resolving them from the factory's root provider is legal
	// (no captive dependency). Before RECEIPTS-753 this attribute sat on the options-only ctor below,
	// which left both fields null on every factory-created context (the primary write path).
	[ActivatorUtilitiesConstructor]
	public ApplicationDbContext(
		DbContextOptions<ApplicationDbContext> options,
		ICurrentUserAccessor currentUserAccessor,
		IDescriptionChangeSignal? descriptionChangeSignal = null)
		: base(options)
	{
		_currentUserAccessor = currentUserAccessor;
		_descriptionChangeSignal = descriptionChangeSignal;
	}

	// Options-only ctor retained for design-time tooling (migrations) and tests that intentionally
	// exercise the null-accessor path. No longer the ActivatorUtilities default.
	public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
		: base(options)
	{
	}

	/// <summary>
	/// When <see langword="false"/>, <see cref="SaveChangesAsync"/> skips audit-log generation.
	/// Defaults to <see langword="true"/>. Bulk-seeding paths set this to <see langword="false"/>
	/// so a one-time sample-data import does not emit thousands of null-attributed audit rows.
	/// </summary>
	public bool AuditingEnabled { get; set; } = true;

	public virtual DbSet<AccountEntity> Accounts { get; set; } = null!;
	public virtual DbSet<CardEntity> Cards { get; set; } = null!;
	public virtual DbSet<CategoryEntity> Categories { get; set; } = null!;
	public virtual DbSet<SubcategoryEntity> Subcategories { get; set; } = null!;
	public virtual DbSet<ReceiptEntity> Receipts { get; set; } = null!;
	public virtual DbSet<TransactionEntity> Transactions { get; set; } = null!;
	public virtual DbSet<ReceiptItemEntity> ReceiptItems { get; set; } = null!;
	public virtual DbSet<AdjustmentEntity> Adjustments { get; set; } = null!;
	public virtual DbSet<ApiKeyEntity> ApiKeys { get; set; } = null!;
	public virtual DbSet<ItemTemplateEntity> ItemTemplates { get; set; } = null!;
	public virtual DbSet<ItemEmbeddingEntity> ItemEmbeddings { get; set; } = null!;
	public virtual DbSet<NormalizedDescriptionEntity> NormalizedDescriptions { get; set; } = null!;
	public virtual DbSet<NormalizedDescriptionSettingsEntity> NormalizedDescriptionSettings { get; set; } = null!;
	public virtual DbSet<DistinctDescriptionEntity> DistinctDescriptions { get; set; } = null!;
	public virtual DbSet<ItemSimilarityEdgeEntity> ItemSimilarityEdges { get; set; } = null!;
	public virtual DbSet<AuditLogEntity> AuditLogs { get; set; } = null!;
	public virtual DbSet<AuthAuditLogEntity> AuthAuditLogs { get; set; } = null!;
	public virtual DbSet<SeedHistoryEntry> SeedHistory { get; set; } = null!;
	public virtual DbSet<YnabSyncRecordEntity> YnabSyncRecords { get; set; } = null!;
	public virtual DbSet<YnabSelectedBudgetEntity> YnabSelectedBudgets { get; set; } = null!;
	public virtual DbSet<YnabAccountMappingEntity> YnabAccountMappings { get; set; } = null!;
	public virtual DbSet<YnabCategoryMappingEntity> YnabCategoryMappings { get; set; } = null!;
	public virtual DbSet<YnabServerKnowledgeEntity> YnabServerKnowledge { get; set; } = null!;
	public virtual DbSet<YnabSyncEventEntity> YnabSyncEvents { get; set; } = null!;

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		// RECEIPTS-746: place the seven ASP.NET Identity tables in the `identity` schema.
		// These have no IEntityTypeConfiguration class — base.OnModelCreating maps them — so the
		// schema override lives here. Table names are unchanged; only the schema moves.
		modelBuilder.Entity<ApplicationUser>().ToTable("AspNetUsers", "identity");
		modelBuilder.Entity<IdentityRole>().ToTable("AspNetRoles", "identity");
		modelBuilder.Entity<IdentityUserRole<string>>().ToTable("AspNetUserRoles", "identity");
		modelBuilder.Entity<IdentityUserClaim<string>>().ToTable("AspNetUserClaims", "identity");
		modelBuilder.Entity<IdentityUserLogin<string>>().ToTable("AspNetUserLogins", "identity");
		modelBuilder.Entity<IdentityUserToken<string>>().ToTable("AspNetUserTokens", "identity");
		modelBuilder.Entity<IdentityRoleClaim<string>>().ToTable("AspNetRoleClaims", "identity");

		PrepareEntityTypesInModelBuilder(modelBuilder, Database.ProviderName);
		modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

		// The InMemory provider cannot map pgvector's Vector type directly.
		// Convert Vector <-> string so InMemory tests can work with embeddings.
		if (Database.ProviderName == InMemory)
		{
			modelBuilder.Entity<ItemEmbeddingEntity>()
				.Property(e => e.Embedding)
				.HasColumnType(null)
				.HasConversion(
					v => string.Join(';', v.ToArray()),
					v => new Pgvector.Vector(v.Split(';').Select(float.Parse).ToArray()));

			modelBuilder.Entity<NormalizedDescriptionEntity>()
				.Property(e => e.Embedding)
				.HasColumnType(null)
				.HasConversion(
					v => v == null ? null : string.Join(';', v.ToArray()),
					v => string.IsNullOrEmpty(v) ? null : new Pgvector.Vector(v.Split(';').Select(float.Parse).ToArray()));
		}
	}

	public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
	{
		HandleSoftDelete();

		List<AuditEntry> auditEntries = AuditingEnabled ? CollectAuditEntries() : [];
		HashSet<string> touchedDescriptions = CollectTouchedReceiptItemDescriptions();

		int result = await base.SaveChangesAsync(cancellationToken);

		if (auditEntries.Count > 0)
		{
			foreach (AuditEntry entry in auditEntries)
			{
				// For Created entities, fill in the generated ID after save
				if (entry.AuditLog.Action == AuditAction.Create && entry.TrackedEntry is not null)
				{
					object? idValue = entry.TrackedEntry.Property("Id").CurrentValue;
					if (idValue is not null)
					{
						entry.AuditLog.EntityId = idValue.ToString()!;
					}
				}
			}

			AuditLogs.AddRange(auditEntries.Select(e => e.AuditLog));
			await base.SaveChangesAsync(cancellationToken);
		}

		if (touchedDescriptions.Count > 0)
		{
			await ReconcileDistinctDescriptionsAsync(touchedDescriptions, cancellationToken);
		}

		return result;
	}

	private HashSet<string> CollectTouchedReceiptItemDescriptions()
	{
		HashSet<string> touched = [];
		foreach (EntityEntry<ReceiptItemEntity> entry in ChangeTracker.Entries<ReceiptItemEntity>())
		{
			switch (entry.State)
			{
				case EntityState.Added:
				case EntityState.Deleted:
					// A brand-new active item, or an item leaving the tracker outright, changes
					// the active-item membership for its current description.
					AddIfPresent(touched, entry.Entity.Description);
					break;

				case EntityState.Modified:
					// Only reconcile a Modified item when it can actually affect the active-item
					// set for some description: either the Description text changed, or the item's
					// active state flipped (soft-delete / restore — DeletedAt gained or lost a
					// value). A metadata-only edit (e.g. Quantity/UnitPrice) leaves membership
					// unchanged, so reconciling it would be a pair of wasted round trips.
					string? originalDescription = entry.OriginalValues[nameof(ReceiptItemEntity.Description)] as string;
					string? currentDescription = entry.Entity.Description;
					bool descriptionChanged = !string.Equals(originalDescription, currentDescription, StringComparison.Ordinal);

					DateTimeOffset? originalDeletedAt = entry.OriginalValues[nameof(ReceiptItemEntity.DeletedAt)] as DateTimeOffset?;
					bool activeStateChanged = (originalDeletedAt is null) != (entry.Entity.DeletedAt is null);

					if (descriptionChanged || activeStateChanged)
					{
						AddIfPresent(touched, currentDescription);
						AddIfPresent(touched, originalDescription);
					}
					break;

				default:
					break;
			}
		}
		return touched;

		static void AddIfPresent(HashSet<string> set, string? value)
		{
			if (!string.IsNullOrEmpty(value))
			{
				set.Add(value);
			}
		}
	}

	private async Task ReconcileDistinctDescriptionsAsync(HashSet<string> descriptions, CancellationToken cancellationToken)
	{
		// Skip providers that don't support the pg_trgm machinery — keeps InMemory tests simple.
		if (Database.ProviderName != PostgreSQL)
		{
			return;
		}

		// Reconcile the ENTIRE touched set in two set-based round trips (one INSERT, one DELETE)
		// instead of a pair of round trips per description. Raw SQL keeps the reconciliation
		// atomic and idempotent under concurrent saves: the INSERT's ON CONFLICT DO NOTHING and
		// the DELETE's NOT EXISTS guard both race-safely converge on the invariant "a
		// DistinctDescriptions row exists iff an active ReceiptItem with that description exists".
		// The touched descriptions are passed as a single text[] parameter and expanded with
		// unnest / = ANY, so the number of round trips is constant regardless of set size.
		string[] descriptionArray = [.. descriptions];

		// INSERT a row for every touched description that still has at least one active
		// ReceiptItem. ON CONFLICT keeps it idempotent (and race-safe on the PK).
		int rowsInserted = await Database.ExecuteSqlRawAsync(
			"""
			INSERT INTO "matching"."DistinctDescriptions" ("Description", "ProcessedAt")
			SELECT d, NULL
			FROM unnest({0}::text[]) AS d
			WHERE EXISTS (SELECT 1 FROM "receipts"."ReceiptItems" AS ri WHERE ri."Description" = d AND ri."DeletedAt" IS NULL)
			ON CONFLICT ("Description") DO NOTHING;
			""",
			[descriptionArray],
			cancellationToken);

		// DELETE any touched description that no longer has an active ReceiptItem. The NOT EXISTS
		// guard makes it race-safe: a concurrent insert of a receipt item with that description
		// leaves the subquery non-empty, so the DELETE becomes a no-op for that row.
		int rowsDeleted = await Database.ExecuteSqlRawAsync(
			"""
			DELETE FROM "matching"."DistinctDescriptions" AS dd
			WHERE dd."Description" = ANY({0}::text[])
			  AND NOT EXISTS (SELECT 1 FROM "receipts"."ReceiptItems" AS ri WHERE ri."Description" = dd."Description" AND ri."DeletedAt" IS NULL);
			""",
			[descriptionArray],
			cancellationToken);

		if (rowsInserted > 0 || rowsDeleted > 0)
		{
			_descriptionChangeSignal?.NotifyDirty();
		}
	}

	private sealed class AuditEntry(AuditLogEntity auditLog, EntityEntry? trackedEntry = null)
	{
		public AuditLogEntity AuditLog { get; } = auditLog;
		public EntityEntry? TrackedEntry { get; } = trackedEntry;
	}

	private void HandleSoftDelete()
	{
		// Discover the cascade from a SINGLE snapshot of the change tracker.
		// Previously CollectOwnedChildren re-enumerated ChangeTracker.Entries() for every
		// (deleted parent × owned-child type) pair, and — with auto-detect on — each of those
		// enumerations triggered a full DetectChanges pass over every tracked property. That is
		// O(parents × childTypes × trackedEntities). Here we DetectChanges() once (which also
		// lets EF mark cascade-deleted children as Deleted), disable auto-detect for the
		// duration so the reads below are cheap, bucket the tracker once, and drive the cascade
		// from dictionary lookups. The flag is restored in the finally so base.SaveChangesAsync
		// still runs its normal DetectChanges.
		bool autoDetectChanges = ChangeTracker.AutoDetectChangesEnabled;
		ChangeTracker.DetectChanges();
		ChangeTracker.AutoDetectChangesEnabled = false;
		try
		{
			// Single enumeration: bucket every non-detached entry by runtime type and collect
			// the directly-/cascade-deleted soft-deletables (in tracker order, preserving the
			// exact ordering the old nested scan produced).
			List<EntityEntry> deletedSoftDeletables = [];
			Dictionary<Type, List<EntityEntry>> entriesByType = [];
			foreach (EntityEntry entry in ChangeTracker.Entries())
			{
				if (entry.State == EntityState.Detached)
				{
					continue;
				}

				Type type = entry.Entity.GetType();
				if (!entriesByType.TryGetValue(type, out List<EntityEntry>? bucket))
				{
					bucket = [];
					entriesByType[type] = bucket;
				}
				bucket.Add(entry);

				if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable)
				{
					deletedSoftDeletables.Add(entry);
				}
			}

			if (deletedSoftDeletables.Count == 0)
			{
				return;
			}

			// Snapshot entities that were already soft-deleted before this save.
			// EF Core cascade-delete marks ALL tracked children as Deleted — even
			// those that were independently soft-deleted earlier. We must not tag
			// those with CascadeDeletedByParentId.
			HashSet<ISoftDeletable> alreadySoftDeleted = new(
				deletedSoftDeletables
					.Select(e => (ISoftDeletable)e.Entity)
					.Where(e => e.DeletedAt is not null));

			// Build a per-owned-child FK index over the single snapshot: for each owned-child
			// relationship, group its tracked entries by FK value. The cascade then resolves
			// each parent's children with two dictionary lookups instead of a full re-scan.
			Dictionary<OwnedChildrenMapProvider.OwnedChildEntry, Dictionary<Guid, List<ISoftDeletable>>> childFkIndex =
				BuildOwnedChildFkIndex(entriesByType);

			// Identify cascade targets BEFORE changing any states, so the
			// collection does not depend on the iteration order of entries.
			List<(ISoftDeletable Target, Guid ParentId)> cascadeTargets = [];

			foreach (EntityEntry entry in deletedSoftDeletables)
			{
				Type parentType = entry.Entity.GetType();
				if (OwnedChildrenMapProvider.Map.TryGetValue(parentType, out OwnedChildrenMapProvider.ParentEntry? parentEntry))
				{
					Guid parentId = (Guid)parentEntry.IdProperty.GetValue(entry.Entity)!;
					foreach (OwnedChildrenMapProvider.OwnedChildEntry child in parentEntry.Children)
					{
						if (childFkIndex.TryGetValue(child, out Dictionary<Guid, List<ISoftDeletable>>? byFk)
							&& byFk.TryGetValue(parentId, out List<ISoftDeletable>? matches))
						{
							foreach (ISoftDeletable match in matches)
							{
								cascadeTargets.Add((match, parentId));
							}
						}
					}
				}
			}

			HashSet<ISoftDeletable> cascadeSet = new(cascadeTargets.Select(t => t.Target));

			// Soft-delete all directly-deleted entries.
			foreach (EntityEntry entry in deletedSoftDeletables)
			{
				ISoftDeletable entity = (ISoftDeletable)entry.Entity;

				// Cascade targets are handled below.
				if (cascadeSet.Contains(entity))
				{
					continue;
				}

				entry.State = EntityState.Modified;
				entity.DeletedAt = DateTimeOffset.UtcNow;
				entity.DeletedByUserId = _currentUserAccessor?.UserId;
				entity.DeletedByApiKeyId = _currentUserAccessor?.ApiKeyId;
			}

			// Soft-delete cascade targets and tag them with the parent ID.
			foreach ((ISoftDeletable target, Guid parentId) in cascadeTargets)
			{
				// Skip children that were independently soft-deleted before this save.
				if (alreadySoftDeleted.Contains(target))
				{
					Entry(target).State = EntityState.Modified;
					continue;
				}

				target.DeletedAt = DateTimeOffset.UtcNow;
				target.DeletedByUserId = _currentUserAccessor?.UserId;
				target.DeletedByApiKeyId = _currentUserAccessor?.ApiKeyId;
				target.CascadeDeletedByParentId = parentId;
				Entry(target).State = EntityState.Modified;
			}
		}
		finally
		{
			ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
		}
	}

	/// <summary>
	/// Groups the tracked entries of every owned-child relationship by FK value, from a single
	/// pre-bucketed snapshot of the change tracker. Keyed by <see cref="OwnedChildrenMapProvider.OwnedChildEntry"/>
	/// so a child type owned by multiple parents (via distinct FK columns) is indexed per relationship.
	/// Only <see cref="ISoftDeletable"/> entries are indexed — matching the original cascade filter.
	/// </summary>
	private static Dictionary<OwnedChildrenMapProvider.OwnedChildEntry, Dictionary<Guid, List<ISoftDeletable>>> BuildOwnedChildFkIndex(
		Dictionary<Type, List<EntityEntry>> entriesByType)
	{
		Dictionary<OwnedChildrenMapProvider.OwnedChildEntry, Dictionary<Guid, List<ISoftDeletable>>> index = [];

		foreach (OwnedChildrenMapProvider.ParentEntry parentEntry in OwnedChildrenMapProvider.Map.Values)
		{
			foreach (OwnedChildrenMapProvider.OwnedChildEntry child in parentEntry.Children)
			{
				if (index.ContainsKey(child) || !entriesByType.TryGetValue(child.ChildType, out List<EntityEntry>? bucket))
				{
					continue;
				}

				Dictionary<Guid, List<ISoftDeletable>> byFk = [];
				foreach (EntityEntry entry in bucket)
				{
					if (entry.Entity is not ISoftDeletable softDeletable)
					{
						continue;
					}

					Guid fkValue = (Guid)child.FkProperty.GetValue(entry.Entity)!;
					if (!byFk.TryGetValue(fkValue, out List<ISoftDeletable>? matches))
					{
						matches = [];
						byFk[fkValue] = matches;
					}
					matches.Add(softDeletable);
				}

				index[child] = byFk;
			}
		}

		return index;
	}

	private List<AuditEntry> CollectAuditEntries()
	{
		// ApiKeyEntity is excluded because the auth hot path stamps LastUsedAt on every
		// authenticated request (RECEIPTS-769). A LastUsedAt bump is telemetry, not an
		// audit-worthy mutation, and auditing it would grow AuditLogs unbounded — one row
		// per API request. The service stamps LastUsedAt via ExecuteUpdate (which bypasses
		// the change tracker and this interceptor entirely); this exclusion is belt-and-braces
		// so any future tracked write to an ApiKey (create/revoke) also stays out of the log.
		HashSet<Type> excludedTypes = [typeof(AuditLogEntity), typeof(AuthAuditLogEntity), typeof(SeedHistoryEntry), typeof(YnabSyncRecordEntity), typeof(YnabSelectedBudgetEntity), typeof(YnabAccountMappingEntity), typeof(YnabCategoryMappingEntity), typeof(YnabServerKnowledgeEntity), typeof(YnabSyncEventEntity), typeof(DistinctDescriptionEntity), typeof(ItemSimilarityEdgeEntity), typeof(ApiKeyEntity)];
		List<AuditEntry> auditEntries = [];
		DateTimeOffset now = DateTimeOffset.UtcNow;

		foreach (EntityEntry entry in ChangeTracker.Entries())
		{
			Type entryType = entry.Entity.GetType();

			if (excludedTypes.Contains(entryType))
			{
				continue;
			}

			// Skip ASP.NET Identity internal entities (IdentityRole, IdentityUserRole, etc.)
			// — they use composite keys and are not part of our domain audit trail.
			if (entryType.Namespace?.StartsWith("Microsoft.AspNetCore.Identity", StringComparison.Ordinal) == true)
			{
				continue;
			}

			if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
			{
				continue;
			}

			string entityType = entry.Entity.GetType().Name.Replace("Entity", "");
			AuditAction action = GetAuditAction(entry);
			List<FieldChange> changes = GetFieldChanges(entry, action);

			if (action == AuditAction.Update && changes.Count == 0)
			{
				continue;
			}

			object? entityId = entry.Property("Id").CurrentValue;
			AuditLogEntity auditLog = new()
			{
				Id = Guid.NewGuid(),
				EntityType = entityType,
				EntityId = action == AuditAction.Create ? "" : entityId?.ToString() ?? "",
				Action = action,
				ChangedByUserId = _currentUserAccessor?.UserId,
				ChangedByApiKeyId = _currentUserAccessor?.ApiKeyId,
				ChangedAt = now,
				IpAddress = _currentUserAccessor?.IpAddress,
			};
			auditLog.SetChanges(changes);

			auditEntries.Add(new AuditEntry(
				auditLog,
				action == AuditAction.Create ? entry : null));
		}

		return auditEntries;
	}

	private static AuditAction GetAuditAction(EntityEntry entry)
	{
		if (entry.State == EntityState.Added)
		{
			return AuditAction.Create;
		}

		if (entry.State == EntityState.Deleted)
		{
			return AuditAction.Delete;
		}

		// Modified — check for soft delete / restore
		if (entry.Entity is ISoftDeletable)
		{
			PropertyEntry deletedAtProp = entry.Property(nameof(ISoftDeletable.DeletedAt));
			object? originalValue = deletedAtProp.OriginalValue;
			object? currentValue = deletedAtProp.CurrentValue;

			if (originalValue is null && currentValue is not null)
			{
				return AuditAction.Delete;
			}

			if (originalValue is not null && currentValue is null)
			{
				return AuditAction.Restore;
			}
		}

		return AuditAction.Update;
	}

	private static List<FieldChange> GetFieldChanges(EntityEntry entry, AuditAction action)
	{
		List<FieldChange> changes = [];

		foreach (PropertyEntry property in entry.Properties)
		{
			string propertyName = property.Metadata.Name;

			if (action == AuditAction.Create)
			{
				changes.Add(new FieldChange
				{
					FieldName = propertyName,
					OldValue = null,
					NewValue = SerializeValue(property.CurrentValue),
				});
			}
			else if (entry.State == EntityState.Modified && property.IsModified)
			{
				string? oldValue = SerializeValue(property.OriginalValue);
				string? newValue = SerializeValue(property.CurrentValue);

				if (oldValue != newValue)
				{
					changes.Add(new FieldChange
					{
						FieldName = propertyName,
						OldValue = oldValue,
						NewValue = newValue,
					});
				}
			}
		}

		return changes;
	}

	private static string? SerializeValue(object? value)
	{
		if (value is null)
		{
			return null;
		}

		return value switch
		{
			string s => s,
			DateTime dt => dt.ToString("O"),
			DateTimeOffset dto => dto.ToString("O"),
			DateOnly d => d.ToString("O"),
			Guid g => g.ToString(),
			bool b => b.ToString(),
			_ => JsonSerializer.Serialize(value),
		};
	}

	private static void PrepareEntityTypesInModelBuilder(ModelBuilder modelBuilder, string? providerName)
	{
		if (providerName == InMemory)
		{
			return;
		}

		Dictionary<Type, string> columnTypes = new()
		{
			{ typeof(decimal), GetMoneyType(providerName) },
			{ typeof(DateTime), GetDateTimeType(providerName) },
			{ typeof(DateTimeOffset), GetDateOffsetType(providerName) },
			{ typeof(DateOnly), GetDateOnlyType(providerName) },
			{ typeof(bool), GetBoolType(providerName) },
			{ typeof(string), GetStringType(providerName) },
			{ typeof(Guid), GetGuidType(providerName) },
			{ typeof(int), GetIntType(providerName) },
			{ typeof(long), GetBigIntType(providerName) },
		};

		foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
		{
			LoopPropertiesAndSetColumnTypes(columnTypes, entityType);
		}
	}

	private static void LoopPropertiesAndSetColumnTypes(Dictionary<Type, string> columnTypes, IMutableEntityType entityType)
	{
		foreach (IMutableProperty property in entityType.GetProperties())
		{
			string? columnType = GetColumnType(property, columnTypes);
			if (columnType is not null)
			{
				property.SetColumnType(columnType);
			}
		}
	}

	private static string? GetColumnType(IMutableProperty property, Dictionary<Type, string> columnTypes)
	{
		Type clrType = property.ClrType;

		// Unwrap nullable types (e.g. DateTimeOffset? -> DateTimeOffset)
		Type baseType = Nullable.GetUnderlyingType(clrType) ?? clrType;

		if (columnTypes.TryGetValue(baseType, out string? columnType))
		{
			// Npgsql rejects DateTimeOffset with non-zero offset for timestamptz columns.
			// Normalize all DateTimeOffset values to UTC before persisting.
			if (baseType == typeof(DateTimeOffset))
			{
				property.SetValueConverter(new ValueConverter<DateTimeOffset, DateTimeOffset>(
					v => v.ToUniversalTime(),
					v => v.ToUniversalTime()));
			}

			return columnType;
		}

		if (baseType.IsEnum)
		{
			return SetEnumPropertyColumnType(property, columnTypes[typeof(string)]);
		}

		// Skip unknown types (e.g. byte[]) — let EF/provider handle them
		return null;
	}

	private static string SetEnumPropertyColumnType(IMutableProperty property, string stringType)
	{
		property.SetColumnType(stringType);
		Type enumType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
		Type converterType = typeof(EnumToStringConverter<>).MakeGenericType(enumType);
		ValueConverter converter = (ValueConverter)Activator.CreateInstance(converterType)!;
		property.SetValueConverter(converter);
		return stringType;
	}

	private static string GetMoneyType(string? providerName)
	{
		return providerName switch
		{
			PostgreSQL => "decimal(18,2)",
			_ => throw new NotImplementedException(string.Format(DatabaseProviderNotSupported, providerName))
		};
	}

	private static string GetDateTimeType(string? providerName)
	{
		return providerName switch
		{
			PostgreSQL => "timestamptz",
			_ => throw new NotImplementedException(string.Format(DatabaseProviderNotSupported, providerName))
		};
	}

	private static string GetDateOffsetType(string? providerName)
	{
		return providerName switch
		{
			PostgreSQL => "timestamptz",
			_ => throw new NotImplementedException(string.Format(DatabaseProviderNotSupported, providerName))
		};
	}

	private static string GetDateOnlyType(string? providerName)
	{
		return providerName switch
		{
			PostgreSQL => "date",
			_ => throw new NotImplementedException(string.Format(DatabaseProviderNotSupported, providerName))
		};
	}

	private static string GetBoolType(string? providerName)
	{
		return providerName switch
		{
			PostgreSQL => "boolean",
			_ => throw new NotImplementedException(string.Format(DatabaseProviderNotSupported, providerName))
		};
	}

	private static string GetStringType(string? providerName)
	{
		return providerName switch
		{
			PostgreSQL => "text",
			_ => throw new NotImplementedException(string.Format(DatabaseProviderNotSupported, providerName))
		};
	}

	private static string GetGuidType(string? providerName)
	{
		return providerName switch
		{
			PostgreSQL => "uuid",
			_ => throw new NotImplementedException(string.Format(DatabaseProviderNotSupported, providerName))
		};
	}

	private static string GetIntType(string? providerName)
	{
		return providerName switch
		{
			PostgreSQL => "integer",
			_ => throw new NotImplementedException(string.Format(DatabaseProviderNotSupported, providerName))
		};
	}

	private static string GetBigIntType(string? providerName)
	{
		return providerName switch
		{
			PostgreSQL => "bigint",
			_ => throw new NotImplementedException(string.Format(DatabaseProviderNotSupported, providerName))
		};
	}

}
