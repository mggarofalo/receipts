using Infrastructure.Entities.Envelopes;
using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class EnvelopeEntityConfiguration : IEntityTypeConfiguration<EnvelopeEntity>
{
	public void Configure(EntityTypeBuilder<EnvelopeEntity> builder)
	{
		builder.ToTable("Envelopes", "budget");
		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).IsRequired().ValueGeneratedOnAdd();
		builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
		builder.HasIndex(e => e.Name).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
		builder.HasQueryFilter(e => e.DeletedAt == null);
	}
}

public class OccasionEntityConfiguration : IEntityTypeConfiguration<OccasionEntity>
{
	public void Configure(EntityTypeBuilder<OccasionEntity> builder)
	{
		builder.ToTable("Occasions", "budget");
		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).IsRequired().ValueGeneratedOnAdd();
		builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
		builder.HasIndex(e => e.Name).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
		builder.HasQueryFilter(e => e.DeletedAt == null);
	}
}

public class ItemTypeEnvelopeDefaultEntityConfiguration : IEntityTypeConfiguration<ItemTypeEnvelopeDefaultEntity>
{
	public void Configure(EntityTypeBuilder<ItemTypeEnvelopeDefaultEntity> builder)
	{
		builder.ToTable("ItemTypeEnvelopeDefaults", "budget");
		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).ValueGeneratedNever();
		builder.HasIndex(e => e.CategoryId).IsUnique();
		builder.HasIndex(e => e.EnvelopeId);
		builder.HasOne<TaxonomyIdentityEntity>().WithMany().HasForeignKey(e => e.CategoryId).OnDelete(DeleteBehavior.Restrict);
		builder.HasOne<EnvelopeEntity>().WithMany().HasForeignKey(e => e.EnvelopeId).OnDelete(DeleteBehavior.Restrict);
	}
}

public class YnabEnvelopeMappingEntityConfiguration : IEntityTypeConfiguration<YnabEnvelopeMappingEntity>
{
	public void Configure(EntityTypeBuilder<YnabEnvelopeMappingEntity> builder)
	{
		builder.ToTable("YnabEnvelopeMappings", "ynab");
		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).ValueGeneratedNever();
		builder.Property(e => e.YnabBudgetId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.YnabCategoryId).IsRequired().HasMaxLength(100);
		builder.Property(e => e.YnabCategoryName).IsRequired().HasMaxLength(200);
		builder.Property(e => e.YnabCategoryGroupName).IsRequired().HasMaxLength(200);
		builder.HasIndex(e => new { e.EnvelopeId, e.YnabBudgetId }).IsUnique();
		builder.HasOne<EnvelopeEntity>().WithMany().HasForeignKey(e => e.EnvelopeId).OnDelete(DeleteBehavior.Cascade);
	}
}

public class EnvelopeBackfillEntityConfiguration : IEntityTypeConfiguration<EnvelopeBackfillEntity>
{
	public void Configure(EntityTypeBuilder<EnvelopeBackfillEntity> builder)
	{
		builder.ToTable("EnvelopeBackfill", "budget", table => table.HasCheckConstraint("CK_EnvelopeBackfill_Singleton", "\"Id\" = 1"));
		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).ValueGeneratedNever();
	}
}
