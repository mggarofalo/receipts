using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class YnabSyncRecordEntityConfiguration : IEntityTypeConfiguration<YnabSyncRecordEntity>
{
	public void Configure(EntityTypeBuilder<YnabSyncRecordEntity> builder)
	{
		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.Property(e => e.LastError)
			.HasMaxLength(2000);

		builder.HasIndex(e => new { e.LocalTransactionId, e.SyncType })
			.IsUnique();

		builder.HasOne(e => e.Transaction)
			.WithMany()
			.HasForeignKey(e => e.LocalTransactionId)
			.OnDelete(DeleteBehavior.Restrict);

		builder.HasQueryFilter(e => e.DeletedAt == null);
	}
}
