using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class TransactionEntityConfiguration : IEntityTypeConfiguration<TransactionEntity>
{
	public void Configure(EntityTypeBuilder<TransactionEntity> builder)
	{
		builder.ToTable("Transactions", "receipts");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.Navigation(e => e.Receipt)
			.AutoInclude();

		// RECEIPTS-574: CardId is NOT NULL end-to-end. Restrict (not Cascade) on delete —
		// hard-deleting a Card must not silently destroy transactions; soft-delete is the
		// normal flow.
		builder.HasOne(e => e.Card)
			.WithMany()
			.HasForeignKey(e => e.CardId)
			.OnDelete(DeleteBehavior.Restrict);

		builder.Navigation(e => e.Card)
			.AutoInclude();

		builder.HasQueryFilter(e => e.DeletedAt == null);
	}
}
