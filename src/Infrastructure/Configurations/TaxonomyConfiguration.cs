using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class TaxonomyConfiguration : IEntityTypeConfiguration<TaxonomyIdentityEntity>,
	IEntityTypeConfiguration<TaxonomyHeadEntity>, IEntityTypeConfiguration<TaxonomyRevisionEntity>,
	IEntityTypeConfiguration<TaxonomyNodeEntity>, IEntityTypeConfiguration<TaxonomyOperationEntity>
{
	public static readonly Guid GenesisId = new("e0450000-0000-0000-0000-000000000000");
	public void Configure(EntityTypeBuilder<TaxonomyIdentityEntity> b) { b.ToTable("Identities", "taxonomy"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); }
	public void Configure(EntityTypeBuilder<TaxonomyHeadEntity> b)
	{
		b.ToTable("Head", "taxonomy", table => table.HasCheckConstraint("CK_TaxonomyHead_Singleton", "\"Id\" = 1"));
		b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
		b.HasOne<TaxonomyRevisionEntity>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
		b.HasData(new TaxonomyHeadEntity { Id = 1, RevisionId = GenesisId });
	}
	public void Configure(EntityTypeBuilder<TaxonomyRevisionEntity> b)
	{
		b.ToTable("Revisions", "taxonomy"); b.HasKey(x => x.Id); b.HasIndex(x => x.Sequence).IsUnique();
		b.Property(x => x.Id).ValueGeneratedNever();
		b.Property<long>("WriteTransaction").HasDefaultValueSql("txid_current()");
		b.HasOne<TaxonomyRevisionEntity>().WithMany().HasForeignKey(x => x.PreviousRevisionId).OnDelete(DeleteBehavior.Restrict);
		// Genesis is empty and permanently sealed, including during migration. Supply the
		// shadow property explicitly so providers without SQL defaults can validate the seed.
		b.HasData(new { Id = GenesisId, Sequence = 0L, WriteTransaction = 0L, AffectedCount = 0, Actor = "system", Reason = "Empty taxonomy foundation; legacy import is separate.", RecordedAt = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero), OperationId = GenesisId });
	}
	public void Configure(EntityTypeBuilder<TaxonomyNodeEntity> b)
	{
		b.ToTable("Nodes", "taxonomy", table =>
		{
			table.HasCheckConstraint("CK_TaxonomyNode_NameLength", "char_length(\"Name\") BETWEEN 1 AND 200");
			table.HasCheckConstraint("CK_TaxonomyNode_DefinitionLength", "char_length(\"Definition\") <= 4000");
		}); b.HasKey(x => new { x.RevisionId, x.CategoryId });
		b.Property(x => x.Name).UseCollation("C");
		b.HasOne<TaxonomyRevisionEntity>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<TaxonomyIdentityEntity>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<TaxonomyNodeEntity>().WithMany().HasForeignKey(x => new { x.RevisionId, x.ParentId }).HasPrincipalKey(x => new { x.RevisionId, x.CategoryId }).OnDelete(DeleteBehavior.Restrict);
		b.HasIndex(x => new { x.RevisionId, x.ParentId, x.Name }).IsUnique().HasFilter("NOT \"Archived\" AND \"ParentId\" IS NOT NULL");
		b.HasIndex(x => new { x.RevisionId, x.Name }).IsUnique().HasFilter("NOT \"Archived\" AND \"ParentId\" IS NULL");
	}
	public void Configure(EntityTypeBuilder<TaxonomyOperationEntity> b)
	{
		b.ToTable("Operations", "taxonomy"); b.HasKey(x => new { x.Actor, x.OperationId });
		b.HasOne<TaxonomyRevisionEntity>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
	}
}
