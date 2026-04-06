using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class YnabCategoryMappingEntityConfiguration : IEntityTypeConfiguration<YnabCategoryMappingEntity>
{
	public void Configure(EntityTypeBuilder<YnabCategoryMappingEntity> builder)
	{
		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.Property(e => e.ReceiptsCategory)
			.IsRequired()
			.HasMaxLength(200);

		builder.Property(e => e.YnabCategoryId)
			.IsRequired()
			.HasMaxLength(100);

		builder.Property(e => e.YnabCategoryName)
			.IsRequired()
			.HasMaxLength(200);

		builder.Property(e => e.YnabCategoryGroupName)
			.IsRequired()
			.HasMaxLength(200);

		builder.Property(e => e.YnabBudgetId)
			.IsRequired()
			.HasMaxLength(100);

		builder.HasIndex(e => e.ReceiptsCategory)
			.IsUnique();
	}
}
