using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class YnabSelectedBudgetEntityConfiguration : IEntityTypeConfiguration<YnabSelectedBudgetEntity>
{
	public void Configure(EntityTypeBuilder<YnabSelectedBudgetEntity> builder)
	{
		builder.HasKey(e => e.Id);

		builder.Property(e => e.BudgetId)
			.HasMaxLength(36);
	}
}
