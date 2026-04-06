using System.Text.Json;
using Application.Interfaces.Services;
using Common;
using Infrastructure.Entities;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces;
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

	public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, ICurrentUserAccessor currentUserAccessor)
		: base(options)
	{
		_currentUserAccessor = currentUserAccessor;
	}

	[ActivatorUtilitiesConstructor]
	public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
		: base(options)
	{
	}

	public virtual DbSet<AccountEntity> Accounts { get; set; } = null!;
	public virtual DbSet<CategoryEntity> Categories { get; set; } = null!;
	public virtual DbSet<SubcategoryEntity> Subcategories { get; set; } = null!;
	public virtual DbSet<ReceiptEntity> Receipts { get; set; } = null!;
	public virtual DbSet<TransactionEntity> Transactions { get; set; } = null!;
	public virtual DbSet<ReceiptItemEntity> ReceiptItems { get; set; } = null!;
	public virtual DbSet<AdjustmentEntity> Adjustments { get; set; } = null!;
	public virtual DbSet<ApiKeyEntity> ApiKeys { get; set; } = null!;
	public virtual DbSet<ItemTemplateEntity> ItemTemplates { get; set; } = null!;
	public virtual DbSet<ItemEmbeddingEntity> ItemEmbeddings { get; set; } = null!;
	public virtual DbSet<AuditLogEntity> AuditLogs { get; set; } = null!;
	public virtual DbSet<AuthAuditLogEntity> AuthAuditLogs { get; set; } = null!;
	public virtual DbSet<SeedHistoryEntry> SeedHistory { get; set; } = null!;
	public virtual DbSet<YnabSyncRecordEntity> YnabSyncRecords { get; set; } = null!;
	public virtual DbSet<YnabSelectedBudgetEntity> YnabSelectedBudgets { get; set; } = null!;
	public virtual DbSet<YnabAccountMappingEntity> YnabAccountMappings { get; set; } = null!;
	public virtual DbSet<YnabCategoryMappingEntity> YnabCategoryMappings { get; set; } = null!;

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);
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
		}
	}

	public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
	{
		HandleSoftDelete();

		List<AuditEntry> auditEntries = CollectAuditEntries();

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

		return result;
	}

	private sealed class AuditEntry(AuditLogEntity auditLog, EntityEntry? trackedEntry = null)
	{
		public AuditLogEntity AuditLog { get; } = auditLog;
		public EntityEntry? TrackedEntry { get; } = trackedEntry;
	}

	private void HandleSoftDelete()
	{
		List<EntityEntry<ISoftDeletable>> entries = ChangeTracker
			.Entries<ISoftDeletable>()
			.Where(e => e.State == EntityState.Deleted)
			.ToList();

		if (entries.Count == 0)
		{
			return;
		}

		// Snapshot entities that were already soft-deleted before this save.
		// EF Core cascade-delete marks ALL tracked children as Deleted — even
		// those that were independently soft-deleted earlier. We must not tag
		// those with CascadeDeletedByParentId.
		HashSet<ISoftDeletable> alreadySoftDeleted = new(
			entries
				.Where(e => e.Entity.DeletedAt is not null)
				.Select(e => e.Entity));

		// Identify cascade targets BEFORE changing any states, so the
		// collection does not depend on the iteration order of entries.
		List<(ISoftDeletable Target, Guid ParentId)> cascadeTargets = [];

		foreach (EntityEntry<ISoftDeletable> entry in entries)
		{
			Type parentType = entry.Entity.GetType();
			if (OwnedChildrenMapProvider.Map.TryGetValue(parentType, out OwnedChildrenMapProvider.ParentEntry? parentEntry))
			{
				Guid parentId = (Guid)parentEntry.IdProperty.GetValue(entry.Entity)!;
				CollectOwnedChildren(parentId, parentEntry.Children, cascadeTargets);
			}
		}

		HashSet<ISoftDeletable> cascadeSet = new(cascadeTargets.Select(t => t.Target));

		// Soft-delete all directly-deleted entries.
		foreach (EntityEntry<ISoftDeletable> entry in entries)
		{
			// Cascade targets are handled below.
			if (cascadeSet.Contains(entry.Entity))
			{
				continue;
			}

			entry.State = EntityState.Modified;
			entry.Entity.DeletedAt = DateTimeOffset.UtcNow;
			entry.Entity.DeletedByUserId = _currentUserAccessor?.UserId;
			entry.Entity.DeletedByApiKeyId = _currentUserAccessor?.ApiKeyId;
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

	private void CollectOwnedChildren(
		Guid parentId,
		List<OwnedChildrenMapProvider.OwnedChildEntry> children,
		List<(ISoftDeletable Target, Guid ParentId)> targets)
	{
		foreach (OwnedChildrenMapProvider.OwnedChildEntry child in children)
		{
			foreach (EntityEntry entry in ChangeTracker.Entries())
			{
				if (entry.Entity.GetType() != child.ChildType || entry.State == EntityState.Detached)
				{
					continue;
				}

				Guid fkValue = (Guid)child.FkProperty.GetValue(entry.Entity)!;
				if (fkValue == parentId && entry.Entity is ISoftDeletable softDeletable)
				{
					targets.Add((softDeletable, parentId));
				}
			}
		}
	}

	private List<AuditEntry> CollectAuditEntries()
	{
		HashSet<Type> excludedTypes = [typeof(AuditLogEntity), typeof(AuthAuditLogEntity), typeof(SeedHistoryEntry), typeof(YnabSyncRecordEntity), typeof(YnabSelectedBudgetEntity), typeof(YnabAccountMappingEntity), typeof(YnabCategoryMappingEntity)];
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
