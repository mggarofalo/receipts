using Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class ApiKeyEntityConfiguration : IEntityTypeConfiguration<ApiKeyEntity>
{
	public void Configure(EntityTypeBuilder<ApiKeyEntity> builder)
	{
		builder.ToTable("ApiKeys", "identity");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedNever();

		builder.HasIndex(e => e.KeyHash)
			.IsUnique();

		builder.HasOne(e => e.User)
			.WithMany(u => u.ApiKeys)
			.HasForeignKey(e => e.UserId)
			.OnDelete(DeleteBehavior.Cascade);
	}
}
