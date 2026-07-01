using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class YnabSyncRecordEntityConfiguration : IEntityTypeConfiguration<YnabSyncRecordEntity>
{
	public void Configure(EntityTypeBuilder<YnabSyncRecordEntity> builder)
	{
		builder.ToTable("YnabSyncRecords", "ynab");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.Property(e => e.LastError)
			.HasMaxLength(2000);

		builder.HasIndex(e => new { e.LocalTransactionId, e.SyncType })
			.IsUnique()
			.HasFilter("\"DeletedAt\" IS NULL");

		// ClientCascade: EF only cascades when related entities are loaded in the
		// same change tracker. Hard-delete paths (TrashService.PurgeAllDeletedAsync)
		// must delete YnabSyncRecords before Transactions to respect FK order.
		// Soft-delete cascades via OwnedByFk attribute + HandleSoftDelete().
		builder.HasOne(e => e.Transaction)
			.WithMany()
			.HasForeignKey(e => e.LocalTransactionId)
			.OnDelete(DeleteBehavior.ClientCascade);

		builder.HasQueryFilter(e => e.DeletedAt == null);
	}
}
