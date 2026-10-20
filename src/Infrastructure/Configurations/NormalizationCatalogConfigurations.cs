using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class NormalizationSourceMappingConfiguration : IEntityTypeConfiguration<NormalizationSourceMappingEntity>
{
	public void Configure(EntityTypeBuilder<NormalizationSourceMappingEntity> builder)
	{
		builder.ToTable("NormalizationSourceMappings", "matching");
		builder.HasKey(e => e.Id);
		builder.HasIndex(e => e.SourceHash).IsUnique().HasFilter("\"State\" = 'active'");
		builder.Property(e => e.SourceHash).HasMaxLength(64);
		builder.Property(e => e.State).HasMaxLength(24);
		// Historical target references are intentionally retained through merges/deletions.
		// Resolution checks the live target rather than cascading away approval evidence.
	}
}

public sealed class NormalizationCatalogOperationConfiguration : IEntityTypeConfiguration<NormalizationCatalogOperationEntity>
{
	public void Configure(EntityTypeBuilder<NormalizationCatalogOperationEntity> builder)
	{
		builder.ToTable("NormalizationCatalogOperations", "matching");
		builder.HasKey(e => e.Id);
		builder.HasIndex(e => new { e.Actor, e.ClientRequestId }).IsUnique();
		builder.HasMany(e => e.Members).WithOne().HasForeignKey(e => e.OperationId).OnDelete(DeleteBehavior.Cascade);
	}
}

public sealed class NormalizationCatalogMemberConfiguration : IEntityTypeConfiguration<NormalizationCatalogMemberEntity>
{
	public void Configure(EntityTypeBuilder<NormalizationCatalogMemberEntity> builder)
	{
		builder.ToTable("NormalizationCatalogMembers", "matching");
		builder.HasKey(e => e.Id);
		builder.HasIndex(e => new { e.OperationId, e.ItemId }).IsUnique();
		builder.HasIndex(e => new { e.OperationId, e.State });
		builder.HasIndex(e => e.ItemId).HasFilter("\"State\" = 'pending'");
	}
}
