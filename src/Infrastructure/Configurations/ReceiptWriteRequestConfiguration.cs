using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class ReceiptWriteRequestConfiguration : IEntityTypeConfiguration<ReceiptWriteRequestEntity>
{
	public void Configure(EntityTypeBuilder<ReceiptWriteRequestEntity> builder)
	{
		builder.ToTable("ReceiptWriteRequests", "receipts");
		builder.HasKey(value => new { value.Actor, value.RequestId });
		builder.Property(value => value.Actor).HasMaxLength(450);
		builder.Property(value => value.RequestHash).HasMaxLength(64);
		builder.Property(value => value.Kind).HasMaxLength(32);
		// Keep tombstones after purge: retrying a lost response must never recreate deleted purchases.
	}
}
