using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class YnabOAuthConnectionEntityConfiguration : IEntityTypeConfiguration<YnabOAuthConnectionEntity>
{
	public void Configure(EntityTypeBuilder<YnabOAuthConnectionEntity> builder)
	{
		builder.ToTable("YnabOAuthConnections", "ynab");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Status)
			.HasConversion<string>()
			.HasMaxLength(32);

		builder.Property(e => e.ConnectedByUserId)
			.HasMaxLength(450);

		builder.Property(e => e.ConcurrencyStamp)
			.IsConcurrencyToken();
	}
}
