using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class RestructureConfiguration : IEntityTypeConfiguration<RestructureApplicationEntity>,
	IEntityTypeConfiguration<RestructureItemEntity>, IEntityTypeConfiguration<RestructureChangeEntity>
{
	public void Configure(EntityTypeBuilder<RestructureApplicationEntity> b)
	{
		b.ToTable("RestructureApplications", "taxonomy");
		b.HasKey(x => x.Id);
		b.Property(x => x.Id).ValueGeneratedNever();
		b.Property(x => x.Actor).UseCollation("C");
		b.Property(x => x.PlanJson).HasColumnType("jsonb");
		b.Property(x => x.ReportJson).HasColumnType("jsonb");
		b.HasIndex(x => x.PlanId);
		// The application's purchase reassignments are the historical operation with the same ID.
		b.HasOne<HistoricalOperationEntity>().WithOne().HasForeignKey<RestructureApplicationEntity>(x => x.Id).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<TaxonomyRevisionEntity>().WithMany().HasForeignKey(x => x.BaseRevisionId).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<TaxonomyRevisionEntity>().WithMany().HasForeignKey(x => x.AppliedRevisionId).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<TaxonomyRevisionEntity>().WithMany().HasForeignKey(x => x.UndoRevisionId).OnDelete(DeleteBehavior.Restrict);
	}

	public void Configure(EntityTypeBuilder<RestructureItemEntity> b)
	{
		b.ToTable("RestructureItems", "taxonomy");
		b.HasKey(x => new { x.ApplicationId, x.ItemId });
		b.Property(x => x.Rule).HasMaxLength(64);
		b.HasOne<RestructureApplicationEntity>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<HistoricalOperationItemEntity>().WithOne().HasForeignKey<RestructureItemEntity>(x => new { x.ApplicationId, x.ItemId })
			.OnDelete(DeleteBehavior.Restrict);
	}

	public void Configure(EntityTypeBuilder<RestructureChangeEntity> b)
	{
		b.ToTable("RestructureChanges", "taxonomy");
		b.HasKey(x => new { x.ApplicationId, x.Ordinal });
		b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
		b.Property(x => x.UndoOutcome).HasMaxLength(64);
		b.HasOne<RestructureApplicationEntity>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
	}
}
