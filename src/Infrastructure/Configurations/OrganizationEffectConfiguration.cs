using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class OrganizationEffectConfiguration : IEntityTypeConfiguration<OrganizationEffectManifestEntity>,
	IEntityTypeConfiguration<OrganizationEffectItemEntity>, IEntityTypeConfiguration<OrganizationEffectOutcomeEntity>, IEntityTypeConfiguration<OrganizationEffectOperationEntity>
{
	public void Configure(EntityTypeBuilder<OrganizationEffectManifestEntity> b)
	{
		b.ToTable("OrganizationEffectManifests", "taxonomy");
		b.HasKey(x => x.Id);
		b.Property(x => x.Id).ValueGeneratedNever();
		b.Property(x => x.Actor).UseCollation("C");
		b.HasAlternateKey(x => new { x.Id, x.TaxonomyRevisionId });
		b.Property<long>("WriteTransaction").HasDefaultValueSql("txid_current()");
		b.HasOne<TaxonomyRevisionEntity>().WithMany().HasForeignKey(x => x.TaxonomyRevisionId).OnDelete(DeleteBehavior.Restrict);
	}

	public void Configure(EntityTypeBuilder<OrganizationEffectItemEntity> b)
	{
		b.ToTable("OrganizationEffectItems", "taxonomy", t => t.HasCheckConstraint("CK_EffectItem_Operations", "\"ApplyOperationId\" <> \"UndoOperationId\""));
		b.HasKey(x => new { x.ManifestId, x.ItemId });
		b.Property(x => x.ItemId).ValueGeneratedNever();
		b.Property(x => x.Store).UseCollation("C");
		b.Property(x => x.RawDescription).UseCollation("C");
		b.Property(x => x.ItemCode).UseCollation("C");
		b.Property(x => x.DecisionEvidenceJson).HasColumnType("jsonb");
		b.HasIndex(x => x.ApplyOperationId).IsUnique();
		b.HasIndex(x => x.UndoOperationId).IsUnique();
		b.HasOne<OrganizationEffectManifestEntity>().WithMany().HasForeignKey(x => new { x.ManifestId, x.TaxonomyRevisionId })
			.HasPrincipalKey(x => new { x.Id, x.TaxonomyRevisionId }).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<TaxonomyNodeEntity>().WithMany().HasForeignKey(x => new { x.TaxonomyRevisionId, CategoryId = x.TargetCategoryId })
			.HasPrincipalKey(x => new { x.RevisionId, x.CategoryId }).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<PurchaseClassificationEntity>().WithMany().HasForeignKey(x => new { ReceiptItemId = x.ItemId, x.ExpectedAssignmentId })
			.HasPrincipalKey(x => new { x.ReceiptItemId, x.Id }).OnDelete(DeleteBehavior.Restrict);
		// No FK to live sources: their deletion must not delete frozen evidence.
	}

	public void Configure(EntityTypeBuilder<OrganizationEffectOutcomeEntity> b)
	{
		b.ToTable("OrganizationEffectOutcomes", "taxonomy", t =>
		{
			t.HasCheckConstraint("CK_EffectOutcome_Phase", "\"Phase\" IN ('apply', 'undo')");
			t.HasCheckConstraint("CK_EffectOutcome_Disposition", "\"Disposition\" IN ('applied', 'skipped')");
			t.HasCheckConstraint("CK_EffectOutcome_Evidence", "(\"Disposition\" = 'applied' AND \"AssignmentId\" IS NOT NULL AND \"ItemRevision\" IS NOT NULL AND \"ReceiptRevision\" IS NOT NULL) OR (\"Disposition\" = 'skipped' AND \"AssignmentId\" IS NULL)");
		});
		b.HasKey(x => new { x.ManifestId, x.ItemId, x.Phase });
		b.Property(x => x.Phase).UseCollation("C");
		b.Property(x => x.Disposition).UseCollation("C");
		b.HasOne<OrganizationEffectItemEntity>().WithMany().HasForeignKey(x => new { x.ManifestId, x.ItemId }).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<PurchaseClassificationEntity>().WithMany().HasForeignKey(x => new { ReceiptItemId = x.ItemId, x.AssignmentId })
			.HasPrincipalKey(x => new { x.ReceiptItemId, x.Id }).OnDelete(DeleteBehavior.Restrict);
	}
	public void Configure(EntityTypeBuilder<OrganizationEffectOperationEntity> b)
	{
		b.ToTable("OrganizationEffectOperations", "taxonomy", t =>
			t.HasCheckConstraint("CK_EffectOperation_Phase", "\"Phase\" IN ('apply', 'undo')"));
		b.HasKey(x => x.OperationId);
		b.Property(x => x.OperationId).ValueGeneratedNever();
		b.Property(x => x.Phase).UseCollation("C");
		b.HasIndex(x => new { x.ManifestId, x.ItemId, x.Phase }).IsUnique();
		b.HasOne<OrganizationEffectItemEntity>().WithMany().HasForeignKey(x => new { x.ManifestId, x.ItemId })
			.OnDelete(DeleteBehavior.Restrict);
	}

}
