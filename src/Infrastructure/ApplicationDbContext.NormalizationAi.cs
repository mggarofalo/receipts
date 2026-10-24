using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	// Production revisions are database generated, including raw SQL and FK actions.
	// This fallback only supports deterministic nonrelational fixtures.
	private void PrepareNormalizationRevisions()
	{
		if (Database.IsNpgsql()) { return; }
		ChangeTracker.DetectChanges();
		foreach (var entry in ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
		{
			if (entry.Entity is ReceiptItemEntity item) { item.NormalizationRevision = Guid.NewGuid(); }
			if (entry.Entity is ReceiptEntity receipt) { receipt.NormalizationRevision = Guid.NewGuid(); }
			if (entry.Entity is NormalizedDescriptionEntity description)
			{
				// Rebuildable vectors do not change the identity observed by reviewed choices.
				// Compare every other mapped property so future business fields stay protected.
				bool projectionChanged = entry.Properties.Any(property =>
					property.Metadata.Name is nameof(NormalizedDescriptionEntity.Embedding) or nameof(NormalizedDescriptionEntity.EmbeddingModelVersion)
					&& !Equals(property.OriginalValue, property.CurrentValue));
				bool businessChanged = entry.Properties.Any(property =>
					property.Metadata.Name is not (nameof(NormalizedDescriptionEntity.Embedding)
						or nameof(NormalizedDescriptionEntity.EmbeddingModelVersion) or nameof(NormalizedDescriptionEntity.NormalizationRevision))
					&& !Equals(property.OriginalValue, property.CurrentValue));
				description.NormalizationRevision = entry.State == EntityState.Modified && projectionChanged && !businessChanged
					? (Guid)entry.Property(nameof(NormalizedDescriptionEntity.NormalizationRevision)).OriginalValue!
					: Guid.NewGuid();
			}
		}
	}
}
