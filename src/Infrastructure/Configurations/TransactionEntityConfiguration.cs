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

		// RECEIPTS-754: AccountId is NOT NULL. Restrict (not Cascade) on delete —
		// hard-deleting an Account must NOT silently cascade-destroy its transactions.
		// The DeleteAccount guard rejects the delete while any transaction (active or
		// soft-deleted) still references the account; merges repoint transactions first.
		builder.HasOne(e => e.Account)
			.WithMany()
			.HasForeignKey(e => e.AccountId)
			.OnDelete(DeleteBehavior.Restrict);

		builder.Navigation(e => e.Account)
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
