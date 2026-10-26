using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class CategoryMappingConfiguration : IEntityTypeConfiguration<CategoryMappingHeadEntity>,
	IEntityTypeConfiguration<CategoryMappingRevisionEntity>
{
	public void Configure(EntityTypeBuilder<CategoryMappingHeadEntity> b)
	{
		b.ToTable("CategoryMappingHeads", "taxonomy");
		b.HasKey(x => x.SourceFingerprint);
		b.Property(x => x.SourceFingerprint).HasMaxLength(64);
		b.Property(x => x.RevisionId).IsConcurrencyToken();
		b.HasOne<CategoryMappingRevisionEntity>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
	}
	public void Configure(EntityTypeBuilder<CategoryMappingRevisionEntity> b)
	{
		b.ToTable("CategoryMappingRevisions", "taxonomy");
		b.HasKey(x => x.Id);
		b.Property(x => x.Id).ValueGeneratedNever();
		b.Property(x => x.SourceFingerprint).HasMaxLength(64);
		b.Property(x => x.SemanticFingerprint).HasMaxLength(64);
		b.Property(x => x.RequestHash).HasMaxLength(64);
		b.Property(x => x.Location).HasMaxLength(1000);
		b.Property(x => x.Description).HasMaxLength(2000);
		b.Property(x => x.ItemCode).HasMaxLength(200);
		b.Property(x => x.Actor).HasMaxLength(450);
		b.Property(x => x.Reason).HasMaxLength(4000);
		b.HasIndex(x => new { x.Actor, x.OperationId }).IsUnique();
		b.HasIndex(x => x.SourceFingerprint);
		b.HasOne<CategoryMappingRevisionEntity>().WithMany().HasForeignKey(x => x.PreviousRevisionId).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<TaxonomyNodeEntity>().WithMany().HasForeignKey(x => new { x.TaxonomyRevisionId, x.CategoryId })
			.HasPrincipalKey(x => new { x.RevisionId, x.CategoryId }).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<TaxonomyRevisionEntity>().WithMany().HasForeignKey(x => x.TaxonomyRevisionId).OnDelete(DeleteBehavior.Restrict);
	}
}
