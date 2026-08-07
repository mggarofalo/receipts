using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class DistinctDescriptionEntityConfiguration : IEntityTypeConfiguration<DistinctDescriptionEntity>
{
	public void Configure(EntityTypeBuilder<DistinctDescriptionEntity> builder)
	{
		builder.ToTable("DistinctDescriptions", "matching");

		builder.HasKey(e => e.Description);

		builder.Property(e => e.Description)
			.IsRequired();

		// No trigram index on this table any more (RECEIPTS-859). The GIN index existed to serve
		// ItemSimilarityEdgeRefresher's `%` similarity join, which went with the refresher in
		// RECEIPTS-836. Every remaining trigram query runs against library.ItemTemplates or
		// receipts.ReceiptItems, each of which has its own index; the only access to this table is
		// the reconciliation INSERT/DELETE in ApplicationDbContext, both keyed on the primary key.
		// Keeping it would cost a GIN write on every receipt save and buy nothing.
	}
}
