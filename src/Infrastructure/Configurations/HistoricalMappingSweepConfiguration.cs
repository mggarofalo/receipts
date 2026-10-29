using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

internal sealed class HistoricalMappingSweepConfiguration : IEntityTypeConfiguration<HistoricalMappingSweepEntity>
{
	public void Configure(EntityTypeBuilder<HistoricalMappingSweepEntity> b)
	{
		b.ToTable("HistoricalMappingSweeps", "taxonomy");
		b.HasKey(x => x.Id);
		b.Property(x => x.Id).ValueGeneratedNever();
	}
}
