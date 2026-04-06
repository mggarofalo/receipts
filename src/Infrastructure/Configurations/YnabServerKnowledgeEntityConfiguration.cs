using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class YnabServerKnowledgeEntityConfiguration : IEntityTypeConfiguration<YnabServerKnowledgeEntity>
{
	public void Configure(EntityTypeBuilder<YnabServerKnowledgeEntity> builder)
	{
		builder.HasKey(e => e.BudgetId);

		builder.Property(e => e.BudgetId)
			.HasMaxLength(36);
	}
}
