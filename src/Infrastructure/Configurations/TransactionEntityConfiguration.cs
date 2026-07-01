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

		builder.HasOne(e => e.Account)
			.WithMany()
			.HasForeignKey(e => e.AccountId)
			.OnDelete(DeleteBehavior.Cascade);

		builder.Navigation(e => e.Account)
			.AutoInclude();

		// RECEIPTS-574: CardId is NOT NULL end-to-end. Restrict (not Cascade) on delete —
		// hard-deleting a Card must not silently destroy transactions; soft-delete is the
		// normal flow. Dropping AccountId is still a later phase.
		builder.HasOne(e => e.Card)
			.WithMany()
			.HasForeignKey(e => e.CardId)
			.OnDelete(DeleteBehavior.Restrict);

		builder.Navigation(e => e.Card)
			.AutoInclude();

		builder.HasQueryFilter(e => e.DeletedAt == null);
	}
}
