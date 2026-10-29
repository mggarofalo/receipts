using Infrastructure.Entities.Organization;
using Microsoft.EntityFrameworkCore;
namespace Infrastructure;

public partial class ApplicationDbContext
{
	public DbSet<OrganizationDirtySourceEntity> OrganizationDirtySources => Set<OrganizationDirtySourceEntity>();
	public DbSet<OrganizationRunEntity> OrganizationRuns => Set<OrganizationRunEntity>();
	public DbSet<OrganizationSourceEntity> OrganizationSources => Set<OrganizationSourceEntity>();
	public DbSet<OrganizationItemEntity> OrganizationItems => Set<OrganizationItemEntity>();
	public DbSet<OrganizationDirtyItemEntity> OrganizationDirtyItems => Set<OrganizationDirtyItemEntity>();
}
