using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	public DbSet<OrganizationEffectManifestEntity> OrganizationEffectManifests => Set<OrganizationEffectManifestEntity>();
	public DbSet<OrganizationEffectItemEntity> OrganizationEffectItems => Set<OrganizationEffectItemEntity>();
	public DbSet<OrganizationEffectOutcomeEntity> OrganizationEffectOutcomes => Set<OrganizationEffectOutcomeEntity>();
	public DbSet<OrganizationEffectOperationEntity> OrganizationEffectOperations => Set<OrganizationEffectOperationEntity>();
}
