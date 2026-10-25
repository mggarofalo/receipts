using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class LegacyMigrationConfiguration : IEntityTypeConfiguration<LegacyMigrationEntity>,
	IEntityTypeConfiguration<LegacyMigrationCrosswalkEntity>, IEntityTypeConfiguration<LegacyMigrationItemEntity>
{
	public void Configure(EntityTypeBuilder<LegacyMigrationEntity> b)
	{
		b.ToTable("LegacyMigrations", "taxonomy");
		b.HasKey(x => new { x.Actor, x.OperationId, x.OperationKind });
		b.HasIndex(x => x.RevisionId).IsUnique();
		b.HasOne<TaxonomyRevisionEntity>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
	}
	public void Configure(EntityTypeBuilder<LegacyMigrationCrosswalkEntity> b)
	{
		b.ToTable("LegacyCrosswalks", "taxonomy");
		b.HasKey(x => new { x.Actor, x.OperationId, x.SourceTable, x.SourceId });
	}
	public void Configure(EntityTypeBuilder<LegacyMigrationItemEntity> b)
	{
		b.ToTable("LegacyItemOutcomes", "taxonomy");
		b.HasKey(x => new { x.Actor, x.OperationId, x.ReceiptItemId });
		b.HasOne<PurchaseClassificationEntity>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Restrict);
	}
}
