using Infrastructure.Entities.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Infrastructure.Configurations;

public sealed class OrganizationConfiguration : IEntityTypeConfiguration<OrganizationRunEntity>,
	IEntityTypeConfiguration<OrganizationSourceEntity>, IEntityTypeConfiguration<OrganizationItemEntity>,
	IEntityTypeConfiguration<OrganizationDirtyItemEntity>, IEntityTypeConfiguration<OrganizationDirtySourceEntity>
{
	public void Configure(EntityTypeBuilder<OrganizationRunEntity> b)
	{
		b.ToTable("Runs", "organization", t =>
		{
			t.HasCheckConstraint("CK_OrganizationRun_Version", "\"FormatVersion\" = 1");
			t.HasCheckConstraint("CK_OrganizationRun_State", "(\"Active\" AND \"State\" IN ('scanning','watching')) OR (NOT \"Active\" AND \"State\" = 'stale')");
		});
		b.HasKey(x => x.Id);
		b.Property(x => x.Id).ValueGeneratedNever();
		b.HasIndex(x => x.Active).IsUnique().HasFilter("\"Active\"");
	}
	public void Configure(EntityTypeBuilder<OrganizationSourceEntity> b)
	{
		b.ToTable("Sources", "organization", t => t.HasCheckConstraint("CK_OrganizationSource_Version", "\"FormatVersion\" = 1"));
		b.HasKey(x => x.Id);
		b.Property(x => x.Id).ValueGeneratedNever();
		b.HasAlternateKey(x => new { x.RunId, x.Id });
		b.HasIndex(x => new { x.RunId, x.RawHash }).IsUnique();
		b.Property(x => x.RawHash).HasMaxLength(64);
		b.Property(x => x.NormalizedHash).HasMaxLength(64);
		b.Property(x => x.Store).UseCollation("C");
		b.Property(x => x.Description).UseCollation("C");
		b.Property(x => x.ItemCode).UseCollation("C");
		b.HasOne<OrganizationRunEntity>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
	}
	public void Configure(EntityTypeBuilder<OrganizationItemEntity> b)
	{
		b.ToTable("Items", "organization", t =>
		{
			t.HasCheckConstraint("CK_OrganizationItem_Version", "\"FormatVersion\" = 1");
			t.HasCheckConstraint("CK_OrganizationItem_State", "\"State\" IN ('observed','protected','unavailable','invalid_source')");
		});
		b.HasKey(x => new { x.RunId, x.ItemId });
		b.Property(x => x.ItemId).ValueGeneratedNever();
		b.HasIndex(x => new { x.RunId, x.State, x.ItemId });
		b.HasOne<OrganizationRunEntity>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
		b.HasOne<OrganizationSourceEntity>().WithMany().HasForeignKey(x => new { x.RunId, x.SourceId })
			.HasPrincipalKey(x => new { x.RunId, x.Id }).OnDelete(DeleteBehavior.Restrict);
		// No business-row FKs: restore/deletion must not be blocked by this local cache.
	}
	public void Configure(EntityTypeBuilder<OrganizationDirtyItemEntity> b)
	{
		b.ToTable("DirtyItems", "organization");
		b.HasKey(x => x.ItemId);
		b.Property(x => x.ItemId).ValueGeneratedNever();
	}
	public void Configure(EntityTypeBuilder<OrganizationDirtySourceEntity> b)
	{
		b.ToTable("DirtySources", "organization", t =>
		{
			t.HasCheckConstraint("CK_OrganizationDirtySource_Version", "\"FormatVersion\" = 1");
			t.HasCheckConstraint("CK_OrganizationDirtySource_Kind", "\"Kind\" IN ('raw','normalized')");
		});
		b.HasKey(x => x.Id);
		b.Property(x => x.Id).ValueGeneratedNever();
		b.Property(x => x.Store).UseCollation("C");
		b.Property(x => x.Description).UseCollation("C");
		b.Property(x => x.ItemCode).UseCollation("C");
	}
}
