using Infrastructure.Entities.CategoryAssistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class CategoryAssistanceConfiguration : IEntityTypeConfiguration<CategoryAssistanceSettingsEntity>,
	IEntityTypeConfiguration<CategoryAssistanceSettingsAuditEntity>, IEntityTypeConfiguration<CategoryAssistanceObservationEntity>
{
	public void Configure(EntityTypeBuilder<CategoryAssistanceSettingsEntity> b)
	{
		b.ToTable("Settings", "category_assistance", t =>
		{
			t.HasCheckConstraint("CK_CategoryAssistance_Singleton", "\"Id\" = 1");
			t.HasCheckConstraint("CK_CategoryAssistance_Limits", "\"DailyLimit\" BETWEEN 1 AND 5000 AND \"PendingLimit\" BETWEEN 1 AND 20");
		});
		b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
		b.Property(x => x.Revision).IsConcurrencyToken();
		b.HasData(new CategoryAssistanceSettingsEntity { Revision = Guid.Parse("4bb4909f-fb40-433c-a98d-d48a2bd0cd48"), RestoreGeneration = Guid.Parse("5c5ebfbf-0833-432d-aefc-c388be8e0460") });
	}
	public void Configure(EntityTypeBuilder<CategoryAssistanceSettingsAuditEntity> b)
	{
		b.ToTable("SettingsAudit", "category_assistance"); b.HasKey(x => x.Id);
		b.Property(x => x.Actor).HasMaxLength(450); b.HasIndex(x => x.Revision).IsUnique();
	}
	public void Configure(EntityTypeBuilder<CategoryAssistanceObservationEntity> b)
	{
		b.ToTable("Observations", "category_assistance"); b.HasKey(x => new { x.SaveId, x.ReceiptItemId });
		b.Property(x => x.Actor).HasMaxLength(450);
		b.HasIndex(x => x.SourceHash);
		b.HasIndex(x => x.AttemptId);
		b.HasOne<CategoryAssistanceAttemptEntity>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Restrict);
	}
}

public sealed class CategoryAssistanceConnectionConfiguration : IEntityTypeConfiguration<CategoryAssistanceConnectionEntity>
{
	public void Configure(EntityTypeBuilder<CategoryAssistanceConnectionEntity> b)
	{
		b.ToTable("Connection", "category_assistance", t =>
				t.HasCheckConstraint("CK_CategoryAssistance_ConnectionSingleton", "\"Id\" = 1"));
		b.HasKey(x => x.Id);
		b.Property(x => x.Id).ValueGeneratedNever();
		b.Property(x => x.Provider).HasMaxLength(100);
		b.Property(x => x.Model).HasMaxLength(200);
		b.Property(x => x.ConfigurationFingerprint).HasMaxLength(64);
		b.HasData(new CategoryAssistanceConnectionEntity());
	}
}
