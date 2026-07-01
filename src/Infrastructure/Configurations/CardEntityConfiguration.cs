using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class CardEntityConfiguration : IEntityTypeConfiguration<CardEntity>
{
	public void Configure(EntityTypeBuilder<CardEntity> builder)
	{
		builder.ToTable("Cards", "library");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.Property(e => e.AccountId)
			.IsRequired();

		builder.HasOne(e => e.ParentAccount)
			.WithMany()
			.HasForeignKey(e => e.AccountId)
			.OnDelete(DeleteBehavior.Restrict);
	}
}
