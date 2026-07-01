using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class AccountEntityConfiguration : IEntityTypeConfiguration<AccountEntity>
{
	public void Configure(EntityTypeBuilder<AccountEntity> builder)
	{
		builder.ToTable("Accounts", "library");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();
	}
}
