using Infrastructure.Entities.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class AuditLogEntityConfiguration : IEntityTypeConfiguration<AuditLogEntity>
{
	public void Configure(EntityTypeBuilder<AuditLogEntity> builder)
	{
		builder.ToTable("AuditLogs", "audit");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.HasIndex(e => new { e.EntityType, e.EntityId });
		builder.HasIndex(e => e.ChangedAt);
		builder.HasIndex(e => e.ChangedByUserId);
		builder.HasIndex(e => e.ChangedByApiKeyId);
	}
}
