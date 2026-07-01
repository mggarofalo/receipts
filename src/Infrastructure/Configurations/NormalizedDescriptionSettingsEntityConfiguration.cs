using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class NormalizedDescriptionSettingsEntityConfiguration : IEntityTypeConfiguration<NormalizedDescriptionSettingsEntity>
{
	// Fixed singleton ID for the one-row settings table. All reads and writes target this ID;
	// HasData seeds a single row on migration so the service can always resolve thresholds.
	public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");

	// Initial threshold defaults, mirrored from the (now-legacy) hardcoded constants on
	// NormalizedDescriptionService. Retained here so the migration seed is self-contained
	// and can be updated in isolation when we re-calibrate.
	public const double InitialAutoAcceptThreshold = 0.81;
	public const double InitialPendingReviewThreshold = 0.68;

	// The seed timestamp is fixed so EF migrations are deterministic (DateTimeOffset.UtcNow
	// would produce a different SQL on every `migrations add`). Runtime updates overwrite
	// this value via UpdateSettingsAsync.
	public static readonly DateTimeOffset SeedUpdatedAt = new(2026, 4, 19, 0, 0, 0, TimeSpan.Zero);

	public void Configure(EntityTypeBuilder<NormalizedDescriptionSettingsEntity> builder)
	{
		builder.ToTable("NormalizedDescriptionSettings", "matching");

		builder.HasKey(e => e.Id);

		// ValueGeneratedNever — singleton row identity is fixed at seed time.
		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedNever();

		builder.HasData(
			new NormalizedDescriptionSettingsEntity
			{
				Id = SingletonId,
				AutoAcceptThreshold = InitialAutoAcceptThreshold,
				PendingReviewThreshold = InitialPendingReviewThreshold,
				UpdatedAt = SeedUpdatedAt,
			});
	}
}
