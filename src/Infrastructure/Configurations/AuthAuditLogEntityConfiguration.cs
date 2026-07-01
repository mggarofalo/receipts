using Infrastructure.Entities.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class AuthAuditLogEntityConfiguration : IEntityTypeConfiguration<AuthAuditLogEntity>
{
	public void Configure(EntityTypeBuilder<AuthAuditLogEntity> builder)
	{
		builder.ToTable("AuthAuditLogs", "audit");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.HasIndex(e => new { e.UserId, e.Timestamp });
		builder.HasIndex(e => new { e.EventType, e.Timestamp });
		builder.HasIndex(e => e.Timestamp);
		builder.HasIndex(e => e.IpAddress);
	}
}
