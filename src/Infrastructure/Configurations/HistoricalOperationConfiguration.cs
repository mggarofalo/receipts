using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class HistoricalOperationConfiguration : IEntityTypeConfiguration<HistoricalOperationEntity>,
		IEntityTypeConfiguration<HistoricalOperationItemEntity>, IEntityTypeConfiguration<HistoricalGroupDispositionEntity>,
		IEntityTypeConfiguration<HistoricalDispositionOperationEntity>
{
	public void Configure(EntityTypeBuilder<HistoricalOperationEntity> b)
	{
		b.ToTable("HistoricalOperations", "taxonomy");
		b.HasKey(x => x.Id);
		b.Property(x => x.Id).ValueGeneratedNever();
		b.Property(x => x.Actor).UseCollation("C");
		b.Property(x => x.SelectionJson).HasColumnType("jsonb");
		b.HasOne<TaxonomyRevisionEntity>().WithMany().HasForeignKey(x => x.TaxonomyRevisionId).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<TaxonomyRevisionEntity>().WithMany().HasForeignKey(x => x.UndoTaxonomyRevisionId).OnDelete(DeleteBehavior.Restrict);
	}

	public void Configure(EntityTypeBuilder<HistoricalOperationItemEntity> b)
	{
		b.ToTable("HistoricalOperationItems", "taxonomy");
		b.HasKey(x => new { x.OperationId, x.ItemId });
		b.Property(x => x.ItemId).ValueGeneratedNever();
		b.Property(x => x.Store).UseCollation("C");
		b.Property(x => x.RawDescription).UseCollation("C");
		b.Property(x => x.ItemCode).UseCollation("C");
		b.HasIndex(x => x.ApplyOperationId).IsUnique();
		b.HasIndex(x => x.UndoOperationId).IsUnique();
		b.HasIndex(x => new { x.OperationId, x.State, x.ItemId });
		b.HasIndex(x => new { x.OperationId, x.State, x.UndoState, x.ItemId });
		b.HasOne<HistoricalOperationEntity>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
		// Intentionally no source-row FK: deleted sources must remain visible as frozen evidence.
		b.HasOne<PurchaseClassificationEntity>().WithMany().HasForeignKey(x => x.ExpectedAssignmentId).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<PurchaseClassificationEntity>().WithMany().HasForeignKey(x => x.AppliedAssignmentId).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<PurchaseClassificationEntity>().WithMany().HasForeignKey(x => x.UndoAssignmentId).OnDelete(DeleteBehavior.Restrict);
	}

	public void Configure(EntityTypeBuilder<HistoricalGroupDispositionEntity> b)
	{
		b.ToTable("HistoricalGroupDispositions", "taxonomy");
		b.HasKey(x => x.Id);
		b.Property(x => x.Id).ValueGeneratedNever();
		b.Property(x => x.Actor).UseCollation("C");
		b.Property(x => x.Store).UseCollation("C");
		b.Property(x => x.RawDescription).UseCollation("C");
		b.Property(x => x.ItemCode).UseCollation("C");
		b.Property(x => x.Disposition).HasConversion<string>();
		b.HasIndex(x => new { x.Actor, x.SourceHash }).IsUnique();
	}

	public void Configure(EntityTypeBuilder<HistoricalDispositionOperationEntity> b)
	{
		b.ToTable("HistoricalDispositionOperations", "taxonomy");
		b.HasKey(x => x.OperationId);
		b.Property(x => x.OperationId).ValueGeneratedNever();
		b.Property(x => x.Actor).UseCollation("C");
		b.Property(x => x.Store).UseCollation("C");
		b.Property(x => x.RawDescription).UseCollation("C");
		b.Property(x => x.ItemCode).UseCollation("C");
		b.Property(x => x.Disposition).HasConversion<string>();
	}
}
