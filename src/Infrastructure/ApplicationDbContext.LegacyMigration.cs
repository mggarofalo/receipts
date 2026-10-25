using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	public DbSet<LegacyMigrationEntity> LegacyMigrations => Set<LegacyMigrationEntity>();
	public DbSet<LegacyMigrationCrosswalkEntity> LegacyMigrationCrosswalks => Set<LegacyMigrationCrosswalkEntity>();
	public DbSet<LegacyMigrationItemEntity> LegacyMigrationItems => Set<LegacyMigrationItemEntity>();
}
