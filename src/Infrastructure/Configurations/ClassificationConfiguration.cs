using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class ClassificationConfiguration : IEntityTypeConfiguration<PurchaseClassificationEntity>,
	IEntityTypeConfiguration<CurrentClassificationEntity>, IEntityTypeConfiguration<ClassificationOperationEntity>
{
	public void Configure(EntityTypeBuilder<PurchaseClassificationEntity> b)
	{
		b.ToTable("Assignments", "taxonomy", t => t.HasCheckConstraint("CK_Assignment_Sequence", "\"Sequence\" > 0"));
		b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
		b.HasIndex(x => new { x.ReceiptItemId, x.Sequence }).IsUnique();
		b.HasAlternateKey(x => new { x.ReceiptItemId, x.Id });
		b.HasOne<ReceiptItemEntity>().WithMany().HasForeignKey(x => x.ReceiptItemId).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<PurchaseClassificationEntity>().WithMany().HasForeignKey(x => new { x.ReceiptItemId, x.PreviousAssignmentId })
			.HasPrincipalKey(x => new { x.ReceiptItemId, x.Id }).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<TaxonomyRevisionEntity>().WithMany().HasForeignKey(x => x.TaxonomyRevisionId).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<TaxonomyNodeEntity>().WithMany().HasForeignKey(x => new { x.TaxonomyRevisionId, x.CategoryId })
			.HasPrincipalKey(x => new { x.RevisionId, x.CategoryId }).OnDelete(DeleteBehavior.Restrict);
	}
	public void Configure(EntityTypeBuilder<CurrentClassificationEntity> b)
	{
		b.ToTable("CurrentAssignments", "taxonomy"); b.HasKey(x => x.ReceiptItemId);
		b.HasOne<PurchaseClassificationEntity>().WithMany().HasForeignKey(x => new { x.ReceiptItemId, x.AssignmentId })
			.HasPrincipalKey(x => new { x.ReceiptItemId, x.Id }).OnDelete(DeleteBehavior.Restrict);
	}
	public void Configure(EntityTypeBuilder<ClassificationOperationEntity> b)
	{
		b.ToTable("AssignmentOperations", "taxonomy"); b.HasKey(x => new { x.Actor, x.OperationId });
		b.HasOne<PurchaseClassificationEntity>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Restrict);
	}
}
