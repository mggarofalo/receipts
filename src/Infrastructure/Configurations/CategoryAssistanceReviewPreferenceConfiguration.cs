using Infrastructure.Entities.CategoryAssistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class CategoryAssistanceReviewPreferenceConfiguration :
	IEntityTypeConfiguration<CategoryAssistanceReviewPreferenceEntity>,
	IEntityTypeConfiguration<CategoryAssistanceReviewOperationEntity>
{
	public void Configure(EntityTypeBuilder<CategoryAssistanceReviewPreferenceEntity> b)
	{
		b.ToTable("ReviewPreferences", "category_assistance", t =>
			t.HasCheckConstraint("CK_CategoryAssistance_ReviewState", "\"State\" IN ('active','later','left_as_is')"));
		b.HasKey(x => new { x.Actor, x.AttemptId });
		b.Property(x => x.Actor).HasMaxLength(450);
		b.Property(x => x.State).HasMaxLength(20);
		b.Property(x => x.Revision).IsConcurrencyToken();
		b.HasOne<CategoryAssistanceAttemptEntity>().WithMany().HasForeignKey(x => x.AttemptId)
			.OnDelete(DeleteBehavior.Restrict);
	}

	public void Configure(EntityTypeBuilder<CategoryAssistanceReviewOperationEntity> b)
	{
		b.ToTable("ReviewOperations", "category_assistance");
		b.HasKey(x => new { x.Actor, x.OperationId });
		b.Property(x => x.Actor).HasMaxLength(450);
		b.Property(x => x.RequestHash).HasMaxLength(64);
		b.HasOne<CategoryAssistanceAttemptEntity>().WithMany().HasForeignKey(x => x.AttemptId)
			.OnDelete(DeleteBehavior.Restrict);
	}
}
