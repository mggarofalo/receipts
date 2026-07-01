using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class YnabSyncEventEntityConfiguration : IEntityTypeConfiguration<YnabSyncEventEntity>
{
	public void Configure(EntityTypeBuilder<YnabSyncEventEntity> builder)
	{
		builder.ToTable("YnabSyncEvents", "ynab");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		// Primary access pattern: a user's most-recent events first. Descending on OccurredAt
		// so the paginated activity feed reads straight off the index.
		builder.HasIndex(e => new { e.UserId, e.OccurredAt })
			.IsDescending(false, true);
	}
}
