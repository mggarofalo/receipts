using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	public DbSet<NormalizationSourceMappingEntity> NormalizationSourceMappings { get; set; } = null!;
	public DbSet<NormalizationCatalogOperationEntity> NormalizationCatalogOperations { get; set; } = null!;
	public DbSet<NormalizationCatalogMemberEntity> NormalizationCatalogMembers { get; set; } = null!;

	private void PrepareNormalizationProvenance()
	{
		ChangeTracker.DetectChanges();
		// Load changed-location assignments before audit collection. The database trigger remains
		// a defense for direct SQL; ordinary application writes produce child audit entries too.
		Guid[] movedReceipts = ChangeTracker.Entries<ReceiptEntity>().Where(entry => entry.State == EntityState.Modified
			&& entry.Property(receipt => receipt.Location).IsModified
			&& entry.Property(receipt => receipt.Location).OriginalValue != entry.Entity.Location).Select(entry => entry.Entity.Id).ToArray();
		if (movedReceipts.Length > 0)
		{
			ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().Where(item => movedReceipts.Contains(item.ReceiptId)
				&& (item.NormalizationOrigin == "mapped" || item.NormalizationOrigin == "excluded")).Load();
			foreach (var entry in ChangeTracker.Entries<ReceiptItemEntity>().Where(entry => movedReceipts.Contains(entry.Entity.ReceiptId)
				&& entry.Entity.NormalizationOrigin is "mapped" or "excluded").ToArray())
			{
				entry.Entity.NormalizedDescriptionId = null;
				entry.Entity.NormalizationOrigin = "unassigned";
				entry.Entity.NormalizationMappingId = null;
				entry.Entity.NormalizationAssignmentId = Guid.NewGuid();
			}
			ChangeTracker.DetectChanges();
		}
		foreach (var entry in ChangeTracker.Entries<ReceiptItemEntity>().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
		{
			ReceiptItemEntity item = entry.Entity;
			bool linkChanged = entry.State == EntityState.Added
				? item.NormalizedDescriptionId.HasValue
				: entry.Property(value => value.NormalizedDescriptionId).IsModified;
			bool explicitAssignment = entry.State == EntityState.Added
				? item.NormalizationOrigin != "unassigned"
				: entry.Property(value => value.NormalizationAssignmentId).IsModified;
			if (linkChanged && !explicitAssignment)
			{
				// Unstamped links retain descriptive provenance only; they never establish reviewed evidence.
				item.NormalizationOrigin = item.NormalizedDescriptionId.HasValue ? "manual" : "unassigned";
				item.NormalizationMappingId = null;
				item.NormalizationAssignmentId = Guid.NewGuid();
			}
			if (entry.State == EntityState.Modified
				&& ((entry.Property(value => value.Description).IsModified && entry.Property(value => value.Description).OriginalValue != item.Description)
					|| (entry.Property(value => value.ReceiptItemCode).IsModified && entry.Property(value => value.ReceiptItemCode).OriginalValue != item.ReceiptItemCode))
				&& item.NormalizationOrigin is "mapped" or "excluded")
			{
				item.NormalizedDescriptionId = null;
				item.NormalizationOrigin = "unassigned";
				item.NormalizationMappingId = null;
				item.NormalizationAssignmentId = Guid.NewGuid();
			}
		}
	}
}
