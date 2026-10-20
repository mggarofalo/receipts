using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	// Production revisions are database generated, including raw SQL and FK actions.
	// This fallback only supports deterministic nonrelational fixtures.
	private void PrepareNormalizationRevisions()
	{
		PrepareNormalizationProvenance();
		if (Database.IsNpgsql()) { return; }
		ChangeTracker.DetectChanges();
		foreach (var entry in ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
		{
			if (entry.Entity is ReceiptItemEntity item) { item.NormalizationRevision = Guid.NewGuid(); }
			if (entry.Entity is ReceiptEntity receipt) { receipt.NormalizationRevision = Guid.NewGuid(); }
			if (entry.Entity is NormalizedDescriptionEntity description) { description.NormalizationRevision = Guid.NewGuid(); }
		}
	}
}
