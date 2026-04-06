using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class YnabAccountMappingEntityConfiguration : IEntityTypeConfiguration<YnabAccountMappingEntity>
{
	public void Configure(EntityTypeBuilder<YnabAccountMappingEntity> builder)
	{
		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.Property(e => e.YnabAccountId)
			.HasMaxLength(256);

		builder.Property(e => e.YnabAccountName)
			.HasMaxLength(500);

		builder.Property(e => e.YnabBudgetId)
			.HasMaxLength(256);

		builder.HasIndex(e => e.ReceiptsAccountId)
			.IsUnique();

		builder.HasOne(e => e.Account)
			.WithMany()
			.HasForeignKey(e => e.ReceiptsAccountId)
			.OnDelete(DeleteBehavior.Cascade);
	}
}
